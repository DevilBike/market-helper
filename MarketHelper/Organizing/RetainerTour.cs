using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketHelper.Inventory;
using MarketHelper.Listing;
using MarketHelper.Market;
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
    int TopUps,
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
    private int idleRounds;
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
            Log.End($"Stopped: \"{step}\" didn't work (moved {moved} stacks)");
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
        Log.End($"Stopped by you after moving {moved} stacks");
    }

    private string Summary() => task switch
    {
        "Sorting" when remaining == 0 => $"Done: moved {moved} stacks in {round} rounds.",
        "Sorting" => $"Moved {moved} stacks in {round} rounds. {remaining} kinds of items still need moving; the retainers they belong in may be full, or need a trade.",
        "Saddlebag" when bagsFull => $"Moved {moved} stacks, then ran out of room.",
        "Saddlebag" => $"Moved {moved} stacks.",
        "Topping up crystals" => $"Took out {moved} kinds of crystals.",
        "Taking out equipment and coffers" when bagsFull => $"Took out {moved} stacks, then your bags were full.",
        "Taking out equipment and coffers" => $"Took out {moved} stacks.",
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
        idleRounds = 0;
        bagsFull = false;
        failure = null;
        Log.Begin($"{name} for {Plugin.PlayerState.CharacterName}");
    }

    private SortLog Log => plugin.SortLog;

    // Queued last: writes the run to the log
    private (string, Func<StepResult>, bool) Finish() => ("Finishing", () =>
    {
        Log.End(Summary());
        return StepResult.Done;
    }, false);

    private static string RetainerName(ulong retainerId)
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return retainerId.ToString();

        for (uint i = 0; i < 10; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer != null && retainer->RetainerId == retainerId)
                return retainer->NameString;
        }

        return retainerId.ToString();
    }

    private static string PlaceName(GameInventoryType container) => container switch
    {
        >= GameInventoryType.Inventory1 and <= GameInventoryType.Inventory4 => "bags",
        >= GameInventoryType.RetainerPage1 and <= GameInventoryType.RetainerPage7 => "retainer",
        GameInventoryType.Crystals => "crystals",
        GameInventoryType.RetainerCrystals => "retainer crystals",
        _ => container.ToString().Contains("SaddleBag") ? "saddlebag" : container.ToString(),
    };

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
        runner.Add(Finish());
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
        var topUps = TopUpsNeeded(order);

        var preview = new SortPreview(
            order.Where(outgoing.ContainsKey).ToList(),
            order.Where(putInto.Contains).ToList(),
            order,
            destination,
            outgoing,
            moves.Count,
            topUps,
            crystals);

        if (preview.Stacks == 0 && topUps == 0 && !crystals)
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
        Log.Add($"Plan: {preview.Stacks} kinds to move, {preview.TopUps} stacks to top up, crystals {(preview.Crystals ? "yes" : "no")}; bag limit {BagLimit}, bags {UsedSlots(Bags)}/140 used");
        Log.Add($"Take out of: {string.Join(", ", preview.TakeFrom.Select(RetainerName))}");
        Log.Add($"Put into: {string.Join(", ", preview.PutInto.Select(RetainerName))}");
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
        Log.Add($"Round {round}, {(forward ? "down" : "up")} the list; bags {UsedSlots(Bags)}/140 used");

        for (var i = 0; i < order.Count; i++)
        {
            var retainerId = order[i];
            var ahead = order.Skip(i + 1).ToHashSet();
            bool Wanted(ulong target) => target == 0 || ahead.Contains(target);

            Gate(retainerId,
                 () => (crystals && PlayerHasCrystals())
                       || BagsHoldItemsFor(retainerId, preview.Destination)
                       || NeedsTopUp(retainerId)
                       || (preview.Outgoing.TryGetValue(retainerId, out var targets) && targets.Any(Wanted) && UsedSlots(Bags) < BagLimit),
                 () =>
                 [
                     Deposit(retainerId, preview.Destination),
                     TopUpStacks(),
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
            Log.Add($"Round {round} done: moved {moved - movedAtRoundStart} stacks, {remaining} kinds left, bags {UsedSlots(Bags)}/140 used");
            // A round in one direction can find nothing to carry while the other direction still can,
            // so stop only after a round each way has moved nothing
            idleRounds = moved > movedAtRoundStart ? 0 : idleRounds + 1;
            if (next != null && next.Stacks > 0 && idleRounds < 2 && round < MaxRounds)
            {
                QueueRound(next with { Crystals = false });
                return StepResult.Done;
            }

            if (plugin.Configuration.TopUpCrystalsAfterSort)
                QueueCrystalTopUp();
            runner.Add(FillSaddlebag());
            runner.Add(Finish());
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
        runner.Add(Finish());
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
        runner.Add(Finish());
    }

    private (string, Func<StepResult>, bool) FillSaddlebag() =>
        MoveLoop("Putting items in the saddlebag", Bags, Saddlebags, false, () => SaddlebagUsable("fill"),
                 slot => Organizer.IsSaddlebagged(plugin.Valuator.GetItem(slot.ItemId), plugin.Configuration));

    // Saddlebag stacks that aren't kept there and match the filter go to the bags, up to the limit
    private (string, Func<StepResult>, bool) EmptySaddlebag(Func<(uint, bool), bool> wanted, int limit) =>
        MoveLoop("Taking items out of the saddlebag", Saddlebags, Bags, true,
                 () => SaddlebagUsable("empty") && UsedSlots(Bags) < limit,
                 slot => !Organizer.IsSaddlebagged(plugin.Valuator.GetItem(slot.ItemId), plugin.Configuration) && wanted((slot.ItemId, slot.IsHq)));

    // The server disconnects you (error 90002) for moving saddlebag items while its window is closed,
    // so saddlebag moves only happen with it open
    private bool SaddlebagUsable(string action)
    {
        if (!InventoryScanner.SaddlebagLoaded())
            return false;
        if (GameUi.SaddlebagOpen())
            return true;

        Log.Add($"Didn't {action} the saddlebag: its window isn't open");
        return false;
    }

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
                Log.Add($"Skipped {RetainerName(retainerId)}: locked (not paid for)");
                done++;
                return StepResult.Done;
            }

            if (needed())
            {
                Log.Add($"Visiting {RetainerName(retainerId)}; bags {UsedSlots(Bags)}/140 used");
                runner.AddNext(Visit(retainerId, whileOpen));
            }
            else
            {
                Log.Add($"Skipped {RetainerName(retainerId)}: nothing to do");
                done++;
            }
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
            Log.Add($"Opened {RetainerName(retainerId)}: {UsedSlots(RetainerPages)}/175 slots used");
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

    private bool BagsHoldItemsFor(ulong retainerId, Dictionary<(uint, bool), ulong> destination) =>
        Read(Bags).Any(s => BelongsIn(s, retainerId, destination));

    // A bag stack that goes into this retainer; the one kept stack of a topped-up item stays
    private bool BelongsIn(BagSlot slot, ulong retainerId, Dictionary<(uint, bool), ulong> destination) =>
        destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) && target == retainerId && !IsKeptStack(slot);

    // The biggest bag stack of an item kept topped up
    private bool IsKeptStack(BagSlot slot)
    {
        if (!Organizer.IsToppedUp(plugin.Valuator.GetItem(slot.ItemId), plugin.Configuration))
            return false;
        var kept = KeptStack(slot.ItemId);
        return kept != null && kept.Container == slot.Container && kept.Slot == slot.Slot;
    }

    private static BagSlot? KeptStack(uint itemId) =>
        Read(Bags).Where(s => s.ItemId == itemId).OrderByDescending(s => s.Quantity).FirstOrDefault();

    private IEnumerable<ItemInfo> ToppedUpItems() =>
        plugin.Configuration.TopUpItemIds
            .Select(id => plugin.Valuator.GetItem(id))
            .Where(item => Organizer.IsToppedUp(item, plugin.Configuration));

    // True when the bags' stack of the item is missing or short and this retainer has some to give
    private bool CanTopUpFrom(ItemInfo item, IEnumerable<(uint ItemId, bool IsHq)> retainerItems)
    {
        var kept = KeptStack(item.Id);
        if (kept != null && kept.Quantity >= item.StackSize)
            return false;
        return retainerItems.Any(s => s.ItemId == item.Id && (kept == null || s.IsHq == kept.IsHq));
    }

    private Location? RecordedRetainer(ulong retainerId) =>
        plugin.Store.Characters.TryGetValue(Plugin.PlayerState.ContentId, out var character)
            ? character.Locations.GetValueOrDefault(CharacterRecord.LocationKey(LocationKind.Retainer, retainerId))
            : null;

    private bool NeedsTopUp(ulong retainerId)
    {
        var location = RecordedRetainer(retainerId);
        if (location == null)
            return false;
        var items = location.Items.Select(s => (s.ItemId, s.IsHq)).ToList();
        return ToppedUpItems().Any(item => CanTopUpFrom(item, items));
    }

    private int TopUpsNeeded(List<ulong> retainers)
    {
        var items = retainers.Select(RecordedRetainer).Where(l => l != null)
            .SelectMany(l => l!.Items.Select(s => (s.ItemId, s.IsHq))).ToList();
        return ToppedUpItems().Count(item => CanTopUpFrom(item, items));
    }

    // Fills the bags' stack of each topped-up item from this retainer: a stack that fits
    // completely goes onto it, otherwise the smallest one (the game keeps what doesn't fit).
    // With none in the bags yet, the biggest stack comes out.
    private (string, Func<StepResult>, bool) TopUpStacks()
    {
        const string name = "Topping up stacks you keep on you";
        var tried = new HashSet<(GameInventoryType, int)>();
        StepResult Run()
        {
            foreach (var item in ToppedUpItems())
            {
                var kept = KeptStack(item.Id);
                if (kept != null && kept.Quantity >= item.StackSize)
                    continue;

                var sources = Read(RetainerPages)
                    .Where(s => s.ItemId == item.Id && (kept == null || s.IsHq == kept.IsHq) && !tried.Contains((s.Container, s.Slot)))
                    .ToList();
                if (sources.Count == 0)
                    continue;

                if (kept == null)
                {
                    var biggest = sources.MaxBy(s => s.Quantity)!;
                    tried.Add((biggest.Container, biggest.Slot));
                    if (!MoveTo(biggest, Bags, true))
                        return StepResult.Done;
                }
                else
                {
                    var room = (int)item.StackSize - kept.Quantity;
                    var source = sources.Where(s => s.Quantity <= room).MaxBy(s => s.Quantity)
                                 ?? sources.MinBy(s => s.Quantity)!;
                    tried.Add((source.Container, source.Slot));
                    InventoryManager.Instance()->MoveItemSlot(
                        (InventoryType)source.Container, (ushort)source.Slot,
                        (InventoryType)kept.Container, (ushort)kept.Slot, true);
                    moved++;
                    Log.Add($"Topped up {item.Name}: {kept.Quantity} + {source.Quantity} (stack holds {item.StackSize})");
                }

                runner.Pause(plugin.Configuration.MinActionDelayMs, plugin.Configuration.MaxActionDelayMs);
                runner.AddNext([(name, Run, false)]);
                return StepResult.Done;
            }

            return StepResult.Done;
        }

        return (name, Run, false);
    }

    // At the bell: takes crystals back from the retainers holding the fewest of each kind first,
    // until you have the target amount (whole stacks only, never past 9,999)
    public void TopUpCrystals()
    {
        if (IsRunning || !CanStart)
            return;

        Begin("Topping up crystals", 0);
        if (!QueueCrystalTopUp() && failure == null)
            failure = "Nothing to top up: you have enough of every kind, or the retainers holding more haven't been checked yet.";
        runner.Add(Finish());
    }

    private bool QueueCrystalTopUp()
    {
        var config = plugin.Configuration;
        var target = Math.Clamp(config.CrystalTopUpTarget, 1, MaxCrystals);
        var have = new Dictionary<uint, int>();
        foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.Crystals))
        {
            if (!item.IsEmpty && item.BaseItemId != 0)
                have[item.BaseItemId] = item.Quantity;
        }

        var ignored = config.RetainerRules.Where(r => r.Ignored).Select(r => r.RetainerId).ToHashSet();
        var order = RetainerIdsInListOrder().Where(id => !ignored.Contains(id) && !(RecordedRetainer(id)?.Locked ?? false)).ToList();
        var known = order.Select(id => (Id: id, Crystals: RecordedRetainer(id)?.Crystals)).Where(r => r.Crystals != null).ToList();
        if (known.Count < order.Count)
            Log.Add($"{order.Count - known.Count} retainers' crystals not recorded yet; run Check all retainers to include them");

        var plan = new Dictionary<ulong, HashSet<uint>>();
        foreach (var kind in known.SelectMany(r => r.Crystals!.Keys).Distinct())
        {
            var count = have.GetValueOrDefault(kind);
            foreach (var (id, crystals) in known.Where(r => r.Crystals!.GetValueOrDefault(kind) > 0).OrderBy(r => r.Crystals![kind]))
            {
                if (count >= target)
                    break;
                var amount = crystals![kind];
                if (count + amount > MaxCrystals)
                    continue;

                if (!plan.TryGetValue(id, out var kinds))
                    plan[id] = kinds = new HashSet<uint>();
                kinds.Add(kind);
                count += amount;
            }
        }

        Log.Add($"Crystal top-up to {target}: {string.Join("; ", plan.Select(p => $"{RetainerName(p.Key)}: {string.Join(", ", p.Value.Select(k => plugin.Valuator.GetItem(k).Name))}"))}");
        foreach (var id in order.Where(plan.ContainsKey))
        {
            var kinds = plan[id];
            total++;
            Gate(id, () => true, () => TakeCrystals(kinds, target));
        }

        return plan.Count > 0;
    }

    private IEnumerable<(string, Func<StepResult>, bool)> TakeCrystals(HashSet<uint> kinds, int target)
    {
        var steps = new List<(string, Func<StepResult>, bool)>();
        var stored = Plugin.GameInventory.GetInventoryItems(GameInventoryType.RetainerCrystals);
        for (var i = 0; i < stored.Length; i++)
        {
            if (stored[i].IsEmpty || !kinds.Contains(stored[i].BaseItemId))
                continue;

            var slot = i;
            var name = plugin.Valuator.GetItem(stored[i].BaseItemId).Name;
            steps.Add(($"Taking out {name}", () =>
            {
                var mine = Plugin.GameInventory.GetInventoryItems(GameInventoryType.Crystals);
                var theirs = Plugin.GameInventory.GetInventoryItems(GameInventoryType.RetainerCrystals);
                if (slot >= mine.Length || slot >= theirs.Length || theirs[slot].IsEmpty)
                    return StepResult.Done;

                var have = mine[slot].IsEmpty ? 0 : mine[slot].Quantity;
                if (have >= target || have + theirs[slot].Quantity > MaxCrystals)
                    return StepResult.Done;

                InventoryManager.Instance()->MoveItemSlot(
                    InventoryType.RetainerCrystals, (ushort)slot,
                    InventoryType.Crystals, (ushort)slot, true);
                moved++;
                Log.Add($"Took {theirs[slot].Quantity} {name} (had {have})");
                return StepResult.Done;
            }, true));
        }

        return steps;
    }

    // Retainer stacks wanted now (ahead in this round, or kept in the bags) go to the bags, until the bag limit
    private (string, Func<StepResult>, bool) Withdraw(ulong retainerId, Dictionary<(uint, bool), ulong> destination, Func<ulong, bool> wanted) =>
        MoveLoop("Taking out items that belong elsewhere", RetainerPages, Bags, true,
            () => UsedSlots(Bags) < BagLimit,
            slot => destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) && target != retainerId && wanted(target) && !ListedHere(slot.ItemId),
            // Things for other retainers first; things kept in the bags take up room for good, so last
            candidates => candidates.OrderBy(s => destination[(s.ItemId, s.IsHq)] == 0));

    // Items the open retainer has on the market board; its stock of them stays for relisting
    private static bool ListedHere(uint itemId) =>
        Read([GameInventoryType.RetainerMarket]).Any(s => s.ItemId == itemId);

    // At the bell: every retainer's equipment and coffers come out into your bags, up to 140 slots
    public void WithdrawEquipmentAndCoffers()
    {
        if (IsRunning || !CanStart)
            return;

        Begin("Taking out equipment and coffers", 0);
        var ignored = plugin.Configuration.RetainerRules.Where(r => r.Ignored).Select(r => r.RetainerId).ToHashSet();
        foreach (var id in RetainerIdsInListOrder().Where(id => !ignored.Contains(id)))
        {
            var retainerId = id;
            total++;
            Gate(retainerId,
                 () => HasFreeSlot(Bags) && (RecordedRetainer(retainerId)?.Items.Any(s => IsGearOrCoffer(s.ItemId)) ?? true),
                 () => [MoveLoop("Taking out equipment and coffers", RetainerPages, Bags, true,
                                 () => HasFreeSlot(Bags),
                                 slot => IsGearOrCoffer(slot.ItemId) && !ListedHere(slot.ItemId))]);
        }

        runner.Add(Finish());
    }

    private bool IsGearOrCoffer(uint itemId)
    {
        var item = plugin.Valuator.GetItem(itemId);
        return item.Group == ItemGroup.Equipment ||
               item.Name.Contains("Coffer", StringComparison.OrdinalIgnoreCase) ||
               item.Name.Contains("Container", StringComparison.OrdinalIgnoreCase);
    }

    // When the retainer is full but bag items still belong in it: take out one stack that
    // belongs elsewhere, put one in, and repeat, using the room left above the bag limit
    private (string, Func<StepResult>, bool) MakeRoom(ulong retainerId, Dictionary<(uint, bool), ulong> destination)
    {
        const string name = "Making room in a full retainer";
        var tried = new HashSet<(GameInventoryType, int)>();
        StepResult Run()
        {
            var incoming = Read(Bags).FirstOrDefault(s => !tried.Contains((s.Container, s.Slot)) && BelongsIn(s, retainerId, destination));
            if (incoming == null)
                return StepResult.Done;

            BagSlot? slot = incoming;
            var to = RetainerPages;
            var toBags = false;
            if (!HasFreeSlot(RetainerPages))
            {
                slot = Read(RetainerPages).FirstOrDefault(s => !tried.Contains((s.Container, s.Slot)) && destination.TryGetValue((s.ItemId, s.IsHq), out var target) && target != retainerId && !ListedHere(s.ItemId));
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
                 slot => BelongsIn(slot, retainerId, destination));

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
        var info = plugin.Valuator.GetItem(slot.ItemId);
        var name = info.Name;

        // A container holds only one of a unique item; it waits for the next retainer instead
        if (info.Unique && Read(containers).Any(s => s.ItemId == slot.ItemId))
        {
            Log.Add($"{name} is unique and the {PlaceName(containers[0])} already has one");
            return true;
        }

        if (SlotFor(containers, moving) is not { } target)
        {
            if (toBags)
                bagsFull = true;
            Log.Add($"No room in the {PlaceName(containers[0])} for {name}");
            return false;
        }

        var merging = !Plugin.GameInventory.GetInventoryItems(target.Container)[target.Slot].IsEmpty;
        InventoryManager.Instance()->MoveItemSlot(
            (InventoryType)slot.Container, (ushort)slot.Slot,
            (InventoryType)target.Container, (ushort)target.Slot, true);
        moved++;
        Log.Add($"{name} x{moving.Quantity}: {PlaceName(slot.Container)} -> {PlaceName(target.Container)}{(merging ? " (onto a partial stack)" : string.Empty)}");
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
