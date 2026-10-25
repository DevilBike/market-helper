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

    // Slots already used during the current visit, in case the game hasn't shown the last move yet
    private readonly HashSet<(GameInventoryType, int)> taken = new();
    private readonly Dictionary<(GameInventoryType, int), int> added = new();

    private string task = string.Empty;
    private int total;
    private int done;
    private int moved;
    private bool bagsFull;
    private string? failure;

    public bool IsRunning => runner.IsRunning;

    public string Status => IsRunning
        ? $"{task}: {done} of {total} visits done. {runner.CurrentStep}..."
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
        "Sorting" when bagsFull => $"Moved {moved} stacks, then your bags were full. Run Sort again to carry on.",
        "Sorting" => $"Moved {moved} stacks. Anything left on the Moves list needs a trade or a free slot.",
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
        var order = RetainerIdsInListOrder().Where(id => !ignored.Contains(id)).ToList();
        var plan = plugin.Organizer.Plan(plugin.Store.Characters.Values, config);
        var moves = plan.Moves
            .Where(m => m.From.Owner.ContentId == me && m.To.Owner.ContentId == me)
            .ToList();

        // Where each item ends up; items going back to the bags just get taken out
        var destination = new Dictionary<(uint, bool), ulong>();
        foreach (var move in moves)
            destination[(move.Item.Id, move.IsHq)] = move.To.IsBags ? 0 : move.To.Id;

        var takeFrom = moves.Where(m => !m.From.IsBags).Select(m => m.From.Id).ToHashSet();
        var putInto = moves.Where(m => !m.To.IsBags).Select(m => m.To.Id).ToHashSet();
        var crystals = config.StoreCrystals && PlayerHasCrystals();

        var preview = new SortPreview(
            order.Where(takeFrom.Contains).ToList(),
            order.Where(putInto.Contains).ToList(),
            order,
            destination,
            moves.Count,
            crystals);

        if (preview.Stacks == 0 && !crystals)
        {
            failure = "Nothing to move for this character.";
            return null;
        }

        return preview;
    }

    // Two rounds: take out of every retainer whatever belongs elsewhere, then put it all away.
    // Crystals go into the first retainer in the list that has room, then the next.
    public void RunSort(SortPreview preview)
    {
        if (IsRunning || !CanStart)
            return;

        var deposits = preview.Crystals ? preview.Order : preview.PutInto;
        Begin("Sorting", preview.TakeFrom.Count + deposits.Count);

        foreach (var id in preview.TakeFrom)
        {
            var retainerId = id;
            Gate(retainerId, () => true, () => Withdraw(retainerId, preview.Destination));
        }

        var putInto = preview.PutInto.ToHashSet();
        foreach (var id in deposits)
        {
            var retainerId = id;
            Gate(retainerId,
                 () => putInto.Contains(retainerId) || (preview.Crystals && PlayerHasCrystals()),
                 () => Deposit(retainerId, preview.Destination).Concat(preview.Crystals ? StoreCrystals() : Enumerable.Empty<(string, Func<StepResult>, bool)>()));
        }
    }

    // Decides when its turn comes whether the retainer still needs a visit
    private void Gate(ulong retainerId, Func<bool> needed, Func<IEnumerable<(string, Func<StepResult>, bool)>>? whileOpen)
    {
        runner.Add("Next retainer", () =>
        {
            if (needed())
                runner.AddNext(Visit(retainerId, whileOpen));
            else
                done++;
            return StepResult.Done;
        }, false);
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

    // Retainer stacks that belong somewhere else go to the bags
    private IEnumerable<(string, Func<StepResult>, bool)> Withdraw(ulong retainerId, Dictionary<(uint, bool), ulong> destination)
    {
        var steps = new List<(string, Func<StepResult>, bool)>();
        foreach (var slot in Read(RetainerPages))
        {
            if (!destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) || target == retainerId)
                continue;

            var name = plugin.Valuator.GetItem(slot.ItemId).Name;
            steps.Add(($"Taking out {name}", () => MoveTo(slot, Bags, true), true));
        }

        return steps;
    }

    // Bag stacks that belong in this retainer go into it
    private IEnumerable<(string, Func<StepResult>, bool)> Deposit(ulong retainerId, Dictionary<(uint, bool), ulong> destination)
    {
        var steps = new List<(string, Func<StepResult>, bool)>();
        foreach (var slot in Read(Bags))
        {
            if (!destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) || target != retainerId)
                continue;

            var name = plugin.Valuator.GetItem(slot.ItemId).Name;
            steps.Add(($"Putting in {name}", () => MoveTo(slot, RetainerPages, false), true));
        }

        return steps;
    }

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

    private StepResult MoveTo(BagSlot slot, GameInventoryType[] containers, bool toBags)
    {
        // Skip it if the stack has moved since the list was made
        var now = Plugin.GameInventory.GetInventoryItems(slot.Container)[slot.Slot];
        if (now.IsEmpty || now.BaseItemId != slot.ItemId || now.IsHq != slot.IsHq)
            return StepResult.Done;

        var moving = slot with { Quantity = (int)now.Quantity };
        if (SlotFor(containers, moving) is not { } target)
        {
            if (toBags)
                bagsFull = true;
            return StepResult.Done;
        }

        InventoryManager.Instance()->MoveItemSlot(
            (InventoryType)slot.Container, (ushort)slot.Slot,
            (InventoryType)target.Container, (ushort)target.Slot, true);
        moved++;
        return StepResult.Done;
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
