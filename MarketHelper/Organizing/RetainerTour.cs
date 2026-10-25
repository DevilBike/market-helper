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

    // Slots already used during the current visit, in case the game hasn't shown the last move yet
    private readonly HashSet<(GameInventoryType, int)> taken = new();

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
            Visit(id, null);
    }

    // Two rounds: take out of every retainer whatever belongs elsewhere, then put it all away
    public void SortAll()
    {
        if (IsRunning || !CanStart)
            return;

        var me = Plugin.PlayerState.ContentId;
        var order = RetainerIdsInListOrder();
        var plan = plugin.Organizer.Plan(plugin.Store.Characters.Values, plugin.Configuration);
        var moves = plan.Moves
            .Where(m => m.From.Owner.ContentId == me && m.To.Owner.ContentId == me)
            .ToList();

        // Where each item ends up; items going back to the bags just get taken out
        var destination = new Dictionary<(uint, bool), ulong>();
        foreach (var move in moves)
            destination[(move.Item.Id, move.IsHq)] = move.To.IsBags ? 0 : move.To.Id;

        var takeFrom = moves.Where(m => !m.From.IsBags).Select(m => m.From.Id).Distinct().ToHashSet();
        var putInto = moves.Where(m => !m.To.IsBags).Select(m => m.To.Id).Distinct().ToHashSet();

        var withdrawals = order.Where(takeFrom.Contains).ToList();
        var deposits = order.Where(putInto.Contains).ToList();
        Begin("Sorting", withdrawals.Count + deposits.Count);
        if (total == 0)
        {
            failure = "Nothing to move for this character.";
            return;
        }

        foreach (var id in withdrawals)
        {
            var retainerId = id;
            Visit(retainerId, () => Withdraw(retainerId, destination));
        }

        foreach (var id in deposits)
        {
            var retainerId = id;
            Visit(retainerId, () => Deposit(retainerId, destination));
        }
    }

    private void Visit(ulong retainerId, Func<IEnumerable<(string, Func<StepResult>)>>? whileOpen)
    {
        runner.Add("Picking a retainer", () =>
        {
            var list = GameUi.GetReady(ListAddon);
            if (list == null)
                return StepResult.Wait;

            var index = RetainerIdsInListOrder().IndexOf(retainerId);
            if (index < 0)
                return StepResult.Fail;

            GameUi.SelectRetainer(list, index);
            return StepResult.Done;
        });

        runner.Add("Waiting for the retainer menu", () => PastTalk(MenuAddon), false);
        // Entrust is the first choice in the retainer menu, Quit the last, if the text can't be matched
        runner.Add("Choosing Entrust or withdraw items", () => Choose(entrustText, first: true));

        runner.Add("Waiting for the retainer inventory", () =>
        {
            if (OpenInventory() == null || !InventoryScanner.RetainerPagesLoaded())
                return StepResult.Wait;

            plugin.Scanner.ScanNow();
            taken.Clear();
            if (whileOpen != null)
                runner.AddNext(whileOpen());
            return StepResult.Done;
        }, false);

        runner.Add("Closing the retainer inventory", () =>
        {
            plugin.Scanner.ScanNow();
            var inventory = OpenInventory();
            if (inventory == null)
                return StepResult.Wait;
            inventory->Close(true);
            return StepResult.Done;
        });

        runner.Add("Waiting for the retainer menu", () => PastTalk(MenuAddon), false);
        runner.Add("Choosing Quit", () => Choose(quitText, first: false));

        runner.Add("Waiting for the retainer list", () =>
        {
            var result = PastTalk(ListAddon);
            if (result == StepResult.Done)
                done++;
            return result;
        }, false);
    }

    // Retainer stacks that belong somewhere else go to empty bag slots
    private IEnumerable<(string, Func<StepResult>)> Withdraw(ulong retainerId, Dictionary<(uint, bool), ulong> destination)
    {
        var steps = new List<(string, Func<StepResult>)>();
        foreach (var slot in Read(RetainerPages))
        {
            if (!destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) || target == retainerId)
                continue;

            var name = plugin.Valuator.GetItem(slot.ItemId).Name;
            steps.Add(($"Taking out {name}", () => MoveTo(slot, Bags, true)));
        }

        return steps;
    }

    // Bag stacks that belong in this retainer go to its empty slots
    private IEnumerable<(string, Func<StepResult>)> Deposit(ulong retainerId, Dictionary<(uint, bool), ulong> destination)
    {
        var steps = new List<(string, Func<StepResult>)>();
        foreach (var slot in Read(Bags))
        {
            if (!destination.TryGetValue((slot.ItemId, slot.IsHq), out var target) || target != retainerId)
                continue;

            var name = plugin.Valuator.GetItem(slot.ItemId).Name;
            steps.Add(($"Putting in {name}", () => MoveTo(slot, RetainerPages, false)));
        }

        return steps;
    }

    private StepResult MoveTo(BagSlot slot, GameInventoryType[] containers, bool toBags)
    {
        // Skip it if the stack has moved since the list was made
        var now = Plugin.GameInventory.GetInventoryItems(slot.Container)[slot.Slot];
        if (now.IsEmpty || now.BaseItemId != slot.ItemId || now.IsHq != slot.IsHq)
            return StepResult.Done;

        if (FirstEmpty(containers) is not { } target)
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
