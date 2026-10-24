using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using MarketHelper.Listing;

namespace MarketHelper.Organizing;

// Moves armory chest items that no gearset uses into the main inventory, one at a time
public sealed unsafe class ArmoryCleaner : IDisposable
{
    // Soul crystals are left alone on purpose
    private static readonly GameInventoryType[] Armory =
    [
        GameInventoryType.ArmoryMainHand, GameInventoryType.ArmoryOffHand,
        GameInventoryType.ArmoryHead, GameInventoryType.ArmoryBody,
        GameInventoryType.ArmoryHands, GameInventoryType.ArmoryWaist,
        GameInventoryType.ArmoryLegs, GameInventoryType.ArmoryFeets,
        GameInventoryType.ArmoryEar, GameInventoryType.ArmoryNeck,
        GameInventoryType.ArmoryWrist, GameInventoryType.ArmoryRings,
    ];

    private static readonly GameInventoryType[] Bags =
    [
        GameInventoryType.Inventory1, GameInventoryType.Inventory2,
        GameInventoryType.Inventory3, GameInventoryType.Inventory4,
    ];

    private readonly StepRunner runner;

    public string Status { get; private set; } = string.Empty;
    public bool IsRunning => runner.IsRunning;

    public ArmoryCleaner(Plugin plugin)
    {
        runner = new StepRunner(() => (plugin.Configuration.MinActionDelayMs, plugin.Configuration.MaxActionDelayMs));
        runner.Failed += step => Status = $"Stopped: {step} didn't work.";
    }

    public void Dispose() => runner.Dispose();

    // Item ids used by any saved gearset, without the HQ offset
    public static HashSet<uint> GearsetItemIds()
    {
        var ids = new HashSet<uint>();
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return ids;

        foreach (ref var entry in module->Entries)
        {
            if (!entry.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            foreach (ref var item in entry.Items)
            {
                if (item.ItemId != 0)
                    ids.Add(item.ItemId % 1_000_000);
            }
        }

        return ids;
    }

    public static List<BagSlot> Unused()
    {
        var inGearsets = GearsetItemIds();
        var slots = new List<BagSlot>();
        foreach (var container in Armory)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
            {
                if (!item.IsEmpty && item.BaseItemId != 0 && !inGearsets.Contains(item.BaseItemId))
                    slots.Add(new BagSlot(container, (int)item.InventorySlot, item.BaseItemId, item.IsHq, item.Quantity));
            }
        }

        return slots;
    }

    public static int FreeBagSlots() => Bags.Sum(c => CountEmpty(c));

    private static int CountEmpty(GameInventoryType container)
    {
        var count = 0;
        foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
        {
            if (item.IsEmpty)
                count++;
        }

        return count;
    }

    // Slots already used this run, in case the game hasn't shown the last move yet
    private readonly HashSet<(GameInventoryType, int)> taken = new();

    private (GameInventoryType Container, int Slot)? FirstEmptyBagSlot()
    {
        foreach (var container in Bags)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
            {
                if (item.IsEmpty && taken.Add((container, (int)item.InventorySlot)))
                    return (container, (int)item.InventorySlot);
            }
        }

        return null;
    }

    public void MoveToBags(IEnumerable<BagSlot> slots)
    {
        if (IsRunning)
            return;

        var list = slots.ToList();
        var moved = 0;
        taken.Clear();
        Status = $"Moving {list.Count} items...";
        foreach (var slot in list)
        {
            runner.Add($"Move {slot.ItemId}", () =>
            {
                // Skip it if the item has moved since the list was made
                var now = Plugin.GameInventory.GetInventoryItems(slot.Container)[slot.Slot];
                if (now.IsEmpty || now.BaseItemId != slot.ItemId)
                    return StepResult.Done;

                if (FirstEmptyBagSlot() is not { } target)
                {
                    Status = $"Moved {moved}, then your bags were full.";
                    runner.Stop();
                    return StepResult.Done;
                }

                InventoryManager.Instance()->MoveItemSlot(
                    (InventoryType)slot.Container, (ushort)slot.Slot,
                    (InventoryType)target.Container, (ushort)target.Slot, true);
                moved++;
                Status = $"Moved {moved} of {list.Count}.";
                return StepResult.Done;
            });
        }
    }

    public void Stop()
    {
        runner.Stop();
        Status = "Stopped.";
    }
}
