using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketHelper.Inventory;
using MarketHelper.Listing;
using AddonSheet = Lumina.Excel.Sheets.Addon;

namespace MarketHelper.Organizing;

// What a Sort run will do: retainers to empty and fill, in list order
public record SortPreview(
    List<ulong> TakeFrom,
    List<ulong> PutInto,
    List<ulong> Order,
    Dictionary<(uint, bool), ulong> Destination,
    Dictionary<ulong, HashSet<ulong>> Outgoing,
    int Stacks,
    bool Crystals);

// At a summoning bell, opens each retainer's inventory in turn, to record it
// or to carry out the planned moves for the current character
public sealed unsafe class RetainerTour : IDisposable
{
    private const string ListAddon = "RetainerList";
    private const string MenuAddon = "SelectString";
    private const string TalkAddon = "Talk";
    private static readonly string[] InventoryAddons = ["InventoryRetainerLarge", "InventoryRetainer"];

    private static readonly GameInventoryType[] Bags =
    [
        GameInventoryType.Inventory1, GameInventoryType.Inventory2,
        GameInventoryType.Inventory3, GameInventoryType.Inventory4,
    ];

    private static readonly GameInventoryType[] RetainerPages =
    [
        GameInventoryType.RetainerPage1, GameInventoryType.RetainerPage2,
        GameInventoryType.RetainerPage3, GameInventoryType.RetainerPage4,
        GameInventoryType.RetainerPage5, GameInventoryType.RetainerPage6,
        GameInventoryType.RetainerPage7,
    ];

    private readonly Plugin plugin;
    private readonly StepRunner runner;
    private readonly string entrustText;
    private readonly string quitText;

    private const int MaxCrystals = 9999;
    private const int MaxRounds = 20;

    private static readonly GameInventoryType[] Saddlebags =
    [
        GameInventoryType.SaddleBag1, GameInventoryType.SaddleBag2,
        GameInventoryType.PremiumSaddleBag1, GameInventoryType.PremiumSaddleBag2,
    ];

    // Slots already used during the current visit, in case the game hasn't shown the last move yet
    private readonly HashSet<(GameInventoryType, int)> taken = new();
    private readonly Dictionary<(GameInventoryType, int), int> added = new();

    private string task = string.Empty;
    private int total;
    private int done;
    private int moved;
    private bool bagsFull;
    private int round;
    private int movedAtRoundStart;
    private int remaining;
    private string? failure;

    public bool IsRunning => runner.IsRunning;

    public string Status => IsRunning
        ? $"{task}{(round > 1 ? $" (round {round})" : string.Empty)}: {done} of {total} visits done. {runner.CurrentStep}..."
        : failure ?? (task.Length == 0 ? string.Empty : Summary());

    public RetainerTour(Plugin plugin)
    {
        this.plugin = plugin;
        runner = new StepRunner(() => (plugin.Configuration.MinActionDelayMs, plugin.Configuration.MaxActionDelayMs));
        runner.Failed += step =>
        {
            failure = $"{task} stopped after {done} of {total}: \"{step}\" didn't work.";
            Plugin.Log.Warning($"Retainer tour stopped at \"{step}\"");
        };

        // Addon rows 2378 "Entrust or withdraw items." and 2383 "Quit.", in the client's language
        var sheet = Plugin.DataManager.GetExcelSheet<AddonSheet>();
        entrustText = sheet.TryGetRow(2378, out var entrust) ? entrust.Text.ExtractText() : "Entrust or withdraw items";
        quitText = sheet.TryGetRow(2383, out var quit) ? quit.Text.ExtractText() : "Quit";
    }

    public void Dispose() => runner.Dispose();

    public static bool CanStart => GameUi.IsOpen(ListAddon);

    public void Stop()
    {
        runner.Stop();
        failure = $"{task} stopped after {done} of {total}.";
    }

    private string Summary() => task switch
    {
        "Sorting" when remaining == 0 => $"Done: moved {moved} stacks in {round} rounds.",
        "Sorting" => $"Moved {moved} stacks in {round} rounds. {remaining} kinds of items still need moving; the retainers they belong in may be full, or need a trade.",
        "Saddlebag" when bagsFull => $"Moved {moved} stacks, then ran out of room.",
        "Saddlebag" => $"Moved {moved} stacks.",
        _ => $"Checked {done} of {total} retainers.",
    };

    // The retainer list shows retainers in the game's sorted order
    private static List<ulong> RetainerIdsInListOrder()
    {
        var ids = new List<ulong>();
        var manager = RetainerManager.Instance();
        if (manager == null)
            return ids;

        for (uint i = 0; i < 10; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer != null && retainer->RetainerId != 0)
                ids.Add(retainer->RetainerId);
        }

        return ids;
    }

    private void Begin(string name, int visits)
    {
        task = name;
        total = visits;
        done = 0;
        moved = 0;
        round = 0;
        remaining = 0;
        bagsFull = false;
        failure = null;
    }

    public void CheckAll()
    {
        if (IsRunning || !CanStart)
            return;

        var ids = RetainerIdsInListOrder();
        Begin("Checking", ids.Count);
        if (ids.Count == 0)
        {
            failure = "The game hasn't loaded your retainer list yet. Try again in a moment.";
            return;
        }

        foreach (var id in ids)
            Gate(id, () => true, null);
    }

    // Works out what Sort would do, so it can be shown before anything moves
    public SortPreview? PreviewSort()
    {
        failure = null;
        var me = Plugin.PlayerState.ContentId;
        var config = plugin.Configuration;
        var ignored = config.RetainerRules.Where(r => r.Ignored).Select(r => r.RetainerId).ToHashSet();
        if (plugin.Store.Characters.TryGetValue(me, out var character))
            ignored.UnionWith(character.Locations.Values.Where(l => l.Kind == LocationKind.Retainer && l.Locked).Select(l => l.RetainerId));
        var order = RetainerIdsInListOrder().Where(id => !ignored.Contains(id)).ToList();
        var plan = plugin.Organizer.Plan(plugin.Store.Characters.Values, config);
        var moves = plan.Moves
            .Where(m => m.From.Owner.ContentId == me && m.To.Owner.ContentId == me)
            .ToList();

        // Where each item ends up; items going back to the bags just get taken out
        var destination = new Dictionary<(uint, bool), ulong>();
        foreach (var move in moves)
            destination[(move.Item.Id, move.IsHq)] = move.To.IsRetainer ? move.To.Id : 0;

        var outgoing = moves.Where(m => m.From.IsRetainer)
            .GroupBy(m => m.From.Id)
            .ToDictionary(g => g.Key, g => g.Select(m => m.To.IsRetainer ? m.To.Id : 0).ToHashSet());
        var putInto = moves.Where(m => m.To.IsRetainer).Select(m => m.To.Id).ToHashSet();
        var crystals = config.StoreCrystals && PlayerHasCrystals();

        var preview = new SortPreview(
            order.Where(outgoing.ContainsKey).ToList(),
            order.Where(putInto.Contains).ToList(),
            order,
            destination,
            outgoing,
            moves.Count,
            crystals);

        if (preview.Stacks == 0 && !crystals)
        {
            failure = "Nothing to move for this character.";
            return null;
        }

        return preview;
    }

    // Goes down the retainer list once per round, visiting each retainer at most once.
    // At each one: put away what's in the bags for it, take out what belongs further along
    // (or in the bags) until the bag limit, and if the retainer is full, take out whatever
    // belongs elsewhere to make room. Rounds alternate direction, so things that belong
    // in a retainer already passed are on the way next time. Crystals go top retainer first.
    public void RunSort(SortPreview preview)
    {
        if (IsRunning || !CanStart)
            return;

        Begin("Sorting", 0);
        round = 0;
        remaining = 0;
        runner.Add("Emptying the saddlebag", () =>
        {
            runner.AddNext([EmptySaddlebag(item => preview.Destination.ContainsKey(item), BagLimit)]);
            return StepResult.Done;
        }, false);
        QueueRound(preview);
    }

    private int BagLimit => Math.Clamp(plugin.Configuration.BagStackLimit, 1, 140);

    private void QueueRound(SortPreview preview)
    {
        round++;
        movedAtRoundStart = moved;
        var forward = round % 2 == 1;
        var order = forward ? preview.Order : Enumerable.Reverse(preview.Order).ToList();
        var crystals = forward && preview.Crystals;
        total += order.Count;

        for (var i = 0; i < order.Count; i++)
        {
            var retainerId = order[i];
            var ahead = order.Skip(i + 1).ToHashSet();
            bool Wanted(ulong target) => target == 0 || ahead.Contains(target);

            Gate(retainerId,
                 () => (crystals && PlayerHasCrystals())
                       || BagsHoldItemsFor(retainerId, preview.Destination)
                       || (preview.Outgoing.TryGetValue(retainerId, out var targets) && targets.Any(Wanted) && UsedSlots(Bags) < BagLimit),
                 () =>
                 [
                     Deposit(retainerId, preview.Destination),
                     .. crystals ? StoreCrystals() : Enumerable.Empty<(string, Func<StepResult>, bool)>(),
                     MakeRoom(retainerId, preview.Destination),
                     Withdraw(retainerId, preview.Destination, Wanted),
                 ]);
        }

        runner.Add("Checking what's left", () =>
        {
            plugin.Scanner.ScanNow();
            var next = PreviewSort();
            failure = null;
            remaining = next?.Stacks ?? 0;
            if (next != null && next.Stacks > 0 && moved > movedAtRoundStart && round < MaxRounds)
                QueueRound(next with { Crystals = false });
            else
                runner.AddNext([FillSaddlebag()]);
            return StepResult.Done;
        }, false);
    }

    // Can be used anywhere: kept items from the bags into the saddlebag
    public void FillSaddlebagNow()
    {
        if (IsRunning)
            return;

        Begin("Saddlebag", 0);
        runner.Add("Filling the saddlebag", () =>
        {
            runner.AddNext([FillSaddlebag()]);
            return StepResult.Done;
        }, false);
    }

    // Can be used anywhere: everything not kept in the saddlebag comes out into the bags
    public void EmptySaddlebagNow()
    {
        if (IsRunning)
            return;

        Begin("Saddlebag", 0);
        runner.Add("Emptying the saddlebag", () =>
        {
            runner.AddNext([EmptySaddlebag(_ => true, 140)]);
            return StepResult.Done;
        }, false);
    }

    private (string, Func<StepResult>, bool) FillSaddlebag() =>
        MoveLoop("Putting items in the saddlebag", Bags, Saddlebags, false, () => InventoryScanner.SaddlebagLoaded(),
                 slot => Organizer.IsSaddlebagged(plugin.Valuator.GetItem(slot.ItemId), plugin.Configuration));

    // Saddlebag stacks that aren't kept there and match the filter go to the bags, up to the limit
    private (string, Func<StepResult>, bool) EmptySaddlebag(Func<(uint, bool), bool> wanted, int limit) =>
        MoveLoop("Taking items out of the saddlebag", Saddlebags, Bags, true,
                 () => InventoryScanner.SaddlebagLoaded() && UsedSlots(Bags) < limit,
                 slot => !Organizer.IsSaddlebagged(plugin.Valuator.GetItem(slot.ItemId), plugin.Configuration) && wanted((slot.ItemId, slot.IsHq)));

    // Moves one stack per step, looking at what's there each time, until nothing matches,
    // the destination is full, or keepGoing says stop
    private (string, Func<StepResult>, bool) MoveLoop(
        string name, GameInventoryType[] from, GameInventoryType[] to, bool toBags,
        Func<bool> keepGoing, Func<BagSlot, bool> matches, Func<List<BagSlot>, IEnumerable<BagSlot>>? order = null)
    {
        var tried = new HashSet<(GameInventoryType, int)>();
        StepResult Run()
        {
            if (!keepGoing())
                return StepResult.Done;

            var candidates = Read(from).Where(s => !tried.Contains((s.Container, s.Slot)) && matches(s)).ToList();
            var slot = (order?.Invoke(candidates) ?? candidates).FirstOrDefault();
            if (slot == null)
                return StepResult.Done;

            tried.Add((slot.Container, slot.Slot));
            if (!MoveTo(slot, to, toBags))
                return StepResult.Done;

            var (min, max) = (plugin.Configuration.MinActionDelayMs, plugin.Configuration.MaxActionDelayMs);
            runner.Pause(min, max);
            runner.AddNext([(name, Run, false)]);
            return StepResult.Done;
        }

        return (name, Run, false);
    }

    // Decides when its turn comes whether the retainer still needs a visit
    private void Gate(ulong retainerId, Func<bool> needed, Func<IEnumerable<(string, Func<StepResult>, bool)>>? whileOpen)
    {
        runner.Add("Next retainer", () =>
        {
            // Retainers that aren't paid for can't be opened
            if (IsLocked(retainerId))
            {
                done++;
                return StepResult.Done;
            }

            if (needed())
                runner.AddNext(Visit(retainerId, whileOpen));
            else
                done++;
            return StepResult.Done;
        }, false);
    }

    private static bool IsLocked(ulong retainerId)
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return false;

        var ids = RetainerIdsInListOrder();
        var index = ids.IndexOf(retainerId);
        if (index < 0)
            return false;

        var list = GameUi.GetReady(ListAddon);
        return InventoryScanner.IsRetainerLocked(manager, manager->GetRetainerBySortedIndex((uint)index), index, list);
    }

    private List<(string, Func<StepResult>, bool)> Visit(ulong retainerId, Func<IEnumerable<(string, Func<StepResult>, bool)>>? whileOpen) =>
    [
        ("Picking a retainer", () =>
        {
            var list = GameUi.GetReady(ListAddon);
            if (list == null)
                return StepResult.Wait;

            var index = RetainerIdsInListOrder().IndexOf(retainerId);
            if (index < 0)
                return StepResult.Fail;

            GameUi.SelectRetainer(list, index);
            return StepResult.Done;
        }, true),

        ("Waiting for the retainer menu", () => PastTalk(MenuAddon), false),

        // Entrust is the first choice in the retainer menu, Quit the last, if the text can't be matched
        ("Choosing Entrust or withdraw items", () => Choose(entrustText, first: true), true),

        ("Waiting for the retainer inventory", () =>
        {
            if (OpenInventory() == null || !InventoryScanner.RetainerPagesLoaded())
                return StepResult.Wait;

            plugin.Scanner.ScanNow();
            taken.Clear();
            added.Clear();
            if (whileOpen != null)
                runner.AddNext(whileOpen());
            return StepResult.Done;
        }, false),

        ("Closing the retainer inventory", () =>
        {
            plugin.Scanner.ScanNow();
            var inventory = OpenInventory();
            if (inventory == null)
                return StepResult.Wait;
            inventory->Close(true);
            return StepResult.Done;
        }, true),

        ("Waiting for the retainer menu", () => PastTalk(MenuAddon), false),
        ("Choosing Quit", () => Choose(quitText, first: false), true),

        ("Waiting for the retainer list", () =>
        {
            var result = PastTalk(ListAddon);
            if (result == StepResult.Done)
                done++;
            return result;
        }, false),
    ];

    private static int UsedSlots(GameInventoryType[] containers) => Read(containers).Count;

    private static bool HasFreeSlot(GameInventoryType[] containers)
    {
        foreach (var container in containers)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
            {
                if (item.IsEmpty)
                    return true;
            }
        }

        return false;
    }

    private static bool BagsHoldItemsFor(ulong retainerId, Dictionary<(uint, bool), ulong> destination) =>
        Read(Bags).Any(s => destination.TryGetValue((s.ItemId, s.IsHq), out var target) && target == retainerId);

    // Retainer stacks wanted now (ahead in this round, or kept in the bags) go to the bags, until the bag limit
    private (string, Func<StepResult>, bool) Withdraw(ulong retainerId, Dictionary<(uint, bool), ulong> destination, Func<ulong, bool> wanted) =>
        MoveLoop("Taking out items that belong elsewhere", RetainerPages, Bags, true,
            () => UsedSlots(Bags) < BagLimit,
            slot => destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) && target != retainerId && wanted(target),
            // Things for other retainers first; things kept in the bags take up room for good, so last
            candidates => candidates.OrderBy(s => destination[(s.ItemId, s.IsHq)] == 0));

    // When the retainer is full but bag items still belong in it: take out one stack that
    // belongs elsewhere, put one in, and repeat, using the room left above the bag limit
    private (string, Func<StepResult>, bool) MakeRoom(ulong retainerId, Dictionary<(uint, bool), ulong> destination)
    {
        const string name = "Making room in a full retainer";
        var tried = new HashSet<(GameInventoryType, int)>();
        StepResult Run()
        {
            var incoming = Read(Bags).FirstOrDefault(s => !tried.Contains((s.Container, s.Slot)) && destination.TryGetValue((s.ItemId, s.IsHq), out var target) && target == retainerId);
            if (incoming == null)
                return StepResult.Done;

            BagSlot? slot = incoming;
            var to = RetainerPages;
            var toBags = false;
            if (!HasFreeSlot(RetainerPages))
            {
                slot = Read(RetainerPages).FirstOrDefault(s => !tried.Contains((s.Container, s.Slot)) && destination.TryGetValue((s.ItemId, s.IsHq), out var target) && target != retainerId);
                to = Bags;
                toBags = true;
            }

            if (slot == null)
                return StepResult.Done;

            tried.Add((slot.Container, slot.Slot));
            if (!MoveTo(slot, to, toBags))
                return StepResult.Done;

            runner.Pause(plugin.Configuration.MinActionDelayMs, plugin.Configuration.MaxActionDelayMs);
            runner.AddNext([(name, Run, false)]);
            return StepResult.Done;
        }

        return (name, Run, false);
    }

    // Bag stacks that belong in this retainer go into it, looking at the bags afresh each time
    private (string, Func<StepResult>, bool) Deposit(ulong retainerId, Dictionary<(uint, bool), ulong> destination) =>
        MoveLoop("Putting items in", Bags, RetainerPages, false, () => true,
                 slot => destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) && target == retainerId);

    private static bool PlayerHasCrystals()
    {
        foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.Crystals))
        {
            if (!item.IsEmpty && item.Quantity > 0)
                return true;
        }

        return false;
    }

    // Each kind of shard, crystal and cluster has the same slot in your crystals and the retainer's.
    // A kind goes in only if all of it fits, otherwise it waits for the next retainer.
    private IEnumerable<(string, Func<StepResult>, bool)> StoreCrystals()
    {
        var steps = new List<(string, Func<StepResult>, bool)>();
        var mine = Plugin.GameInventory.GetInventoryItems(GameInventoryType.Crystals);
        for (var i = 0; i < mine.Length; i++)
        {
            if (mine[i].IsEmpty || mine[i].Quantity == 0)
                continue;

            var slot = i;
            var name = plugin.Valuator.GetItem(mine[i].BaseItemId).Name;
            steps.Add(($"Storing {name}", () =>
            {
                var have = Plugin.GameInventory.GetInventoryItems(GameInventoryType.Crystals);
                var stored = Plugin.GameInventory.GetInventoryItems(GameInventoryType.RetainerCrystals);
                if (slot >= have.Length || slot >= stored.Length || have[slot].IsEmpty)
                    return StepResult.Done;

                var already = stored[slot].IsEmpty ? 0 : stored[slot].Quantity;
                if (already + have[slot].Quantity > MaxCrystals)
                    return StepResult.Done;

                InventoryManager.Instance()->MoveItemSlot(
                    InventoryType.Crystals, (ushort)slot,
                    InventoryType.RetainerCrystals, (ushort)slot, true);
                moved++;
                return StepResult.Done;
            }, true));
        }

        return steps;
    }

    // False when there's no room for it
    private bool MoveTo(BagSlot slot, GameInventoryType[] containers, bool toBags)
    {
        // Nothing to do if the stack has moved since it was picked
        var now = Plugin.GameInventory.GetInventoryItems(slot.Container)[slot.Slot];
        if (now.IsEmpty || now.BaseItemId != slot.ItemId || now.IsHq != slot.IsHq)
            return true;

        var moving = slot with { Quantity = (int)now.Quantity };
        if (SlotFor(containers, moving) is not { } target)
        {
            if (toBags)
                bagsFull = true;
            return false;
        }

        InventoryManager.Instance()->MoveItemSlot(
            (InventoryType)slot.Container, (ushort)slot.Slot,
            (InventoryType)target.Container, (ushort)target.Slot, true);
        moved++;
        return true;
    }

    // Tops up a partial stack of the same item when the whole stack fits, otherwise an empty slot
    private (GameInventoryType Container, int Slot)? SlotFor(GameInventoryType[] containers, BagSlot moving)
    {
        var stackSize = plugin.Valuator.GetItem(moving.ItemId).StackSize;
        if (stackSize > 1)
        {
            foreach (var container in containers)
            {
                foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
                {
                    if (item.IsEmpty || item.BaseItemId != moving.ItemId || item.IsHq != moving.IsHq)
                        continue;

                    var key = (container, (int)item.InventorySlot);
                    var planned = item.Quantity + added.GetValueOrDefault(key);
                    if (planned + moving.Quantity > stackSize)
                        continue;

                    added[key] = added.GetValueOrDefault(key) + moving.Quantity;
                    return key;
                }
            }
        }

        return FirstEmpty(containers);
    }

    private static List<BagSlot> Read(GameInventoryType[] containers)
    {
        var slots = new List<BagSlot>();
        foreach (var container in containers)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
            {
                if (!item.IsEmpty && item.BaseItemId != 0)
                    slots.Add(new BagSlot(container, (int)item.InventorySlot, item.BaseItemId, item.IsHq, item.Quantity));
            }
        }

        return slots;
    }

    private (GameInventoryType Container, int Slot)? FirstEmpty(GameInventoryType[] containers)
    {
        foreach (var container in containers)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
            {
                if (item.IsEmpty && taken.Add((container, (int)item.InventorySlot)))
                    return (container, (int)item.InventorySlot);
            }
        }

        return null;
    }

    private static AtkUnitBase* OpenInventory()
    {
        foreach (var name in InventoryAddons)
        {
            var addon = GameUi.GetReady(name);
            if (addon != null)
                return addon;
        }

        return null;
    }

    // Clicks through the retainer's greeting until the wanted window shows up
    private StepResult PastTalk(string target)
    {
        if (GameUi.IsOpen(target))
            return StepResult.Done;

        var talk = GameUi.GetReady(TalkAddon);
        if (talk != null)
        {
            GameUi.ClickTalk(talk);
            runner.Pause(250, 600);
        }

        return StepResult.Wait;
    }

    private static StepResult Choose(string text, bool first)
    {
        var menu = GameUi.GetReady(MenuAddon);
        if (menu == null)
            return StepResult.Wait;

        var index = GameUi.FindSelectStringEntry(menu, text);
        if (index < 0)
        {
            var entries = GameUi.SelectStringEntries(menu);
            Plugin.Log.Warning($"Retainer menu has no \"{text}\"; entries: {string.Join(" | ", entries)}");
            if (entries.Count == 0)
                return StepResult.Wait;
            index = first ? 0 : entries.Count - 1;
        }

        GameUi.Fire(menu, true, index);
        return StepResult.Done;
    }
}
