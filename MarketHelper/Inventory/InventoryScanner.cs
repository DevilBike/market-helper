using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketHelper.Inventory;

// Takes snapshots of the logged-in character's containers whenever they change,
// and of a retainer's containers while that retainer's inventory is open.
public sealed unsafe class InventoryScanner : IDisposable
{
    private static readonly GameInventoryType[] Bags =
    [
        GameInventoryType.Inventory1, GameInventoryType.Inventory2,
        GameInventoryType.Inventory3, GameInventoryType.Inventory4,
    ];

    private static readonly GameInventoryType[] Saddlebags =
    [
        GameInventoryType.SaddleBag1, GameInventoryType.SaddleBag2,
        GameInventoryType.PremiumSaddleBag1, GameInventoryType.PremiumSaddleBag2,
    ];

    private static readonly GameInventoryType[] Armory =
    [
        GameInventoryType.ArmoryMainHand, GameInventoryType.ArmoryOffHand,
        GameInventoryType.ArmoryHead, GameInventoryType.ArmoryBody,
        GameInventoryType.ArmoryHands, GameInventoryType.ArmoryWaist,
        GameInventoryType.ArmoryLegs, GameInventoryType.ArmoryFeets,
        GameInventoryType.ArmoryEar, GameInventoryType.ArmoryNeck,
        GameInventoryType.ArmoryWrist, GameInventoryType.ArmoryRings,
    ];

    private static readonly GameInventoryType[] RetainerPages =
    [
        GameInventoryType.RetainerPage1, GameInventoryType.RetainerPage2,
        GameInventoryType.RetainerPage3, GameInventoryType.RetainerPage4,
        GameInventoryType.RetainerPage5, GameInventoryType.RetainerPage6,
        GameInventoryType.RetainerPage7,
    ];

    // Addons that are only open once a retainer's inventory has finished loading
    private static readonly string[] RetainerAddons =
        ["InventoryRetainer", "InventoryRetainerLarge", "RetainerSellList"];

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    private readonly InventoryStore store;
    private bool dirty = true;
    private DateTime nextScan = DateTime.MinValue;

    public InventoryScanner(InventoryStore store)
    {
        this.store = store;

        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;
        Plugin.ClientState.Login += OnLogin;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
        Plugin.ClientState.Login -= OnLogin;
        Plugin.Framework.Update -= OnUpdate;
    }

    public void RequestScan() => dirty = true;

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events) => dirty = true;

    private void OnLogin() => dirty = true;

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextScan)
            return;
        nextScan = now + ScanInterval;

        // Retainer windows don't always raise inventory events when they open
        var retainerOpen = IsRetainerInventoryOpen();
        if (!dirty && !retainerOpen)
            return;
        dirty = false;

        try
        {
            Scan(retainerOpen);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Inventory scan failed");
        }
    }

    private void Scan(bool retainerOpen)
    {
        var player = Plugin.PlayerState;
        if (!Plugin.ClientState.IsLoggedIn || !player.IsLoaded || player.ContentId == 0)
            return;

        var record = store.GetOrAdd(player.ContentId);
        var changed = false;

        if (record.Name != player.CharacterName || record.WorldId != player.HomeWorld.RowId)
        {
            record.Name = player.CharacterName;
            record.WorldId = player.HomeWorld.RowId;
            record.World = player.HomeWorld.Value.Name.ToString();
            changed = true;
        }

        changed |= Snapshot(record, LocationKind.Bags, Bags);
        changed |= Snapshot(record, LocationKind.Saddlebag, Saddlebags);
        if (Plugin.Instance.Configuration.IncludeArmory)
            changed |= Snapshot(record, LocationKind.Armory, Armory);

        if (retainerOpen)
        {
            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            if (retainer != null && retainer->RetainerId != 0)
            {
                var name = retainer->NameString;
                changed |= Snapshot(record, LocationKind.Retainer, RetainerPages, retainer->RetainerId, name);
                changed |= Snapshot(record, LocationKind.RetainerMarket, [GameInventoryType.RetainerMarket], retainer->RetainerId, name);
            }
        }

        if (changed)
            store.Save();
    }

    // Returns true if the stored items for this location changed
    private static bool Snapshot(CharacterRecord record, LocationKind kind, GameInventoryType[] containers,
                                 ulong retainerId = 0, string retainerName = "")
    {
        // An unloaded container reads as empty, which would wipe what we know
        if (!IsLoaded(containers[0]))
            return false;

        var items = new List<ItemStack>();
        foreach (var type in containers)
        {
            if (!IsLoaded(type))
                continue;

            foreach (var item in Plugin.GameInventory.GetInventoryItems(type))
            {
                if (item.IsEmpty || item.BaseItemId == 0)
                    continue;
                items.Add(new ItemStack { ItemId = item.BaseItemId, IsHq = item.IsHq, Quantity = item.Quantity });
            }
        }

        var key = CharacterRecord.LocationKey(kind, retainerId);
        if (record.Locations.TryGetValue(key, out var existing) &&
            existing.RetainerName == retainerName && SameItems(existing.Items, items))
            return false;

        record.Locations[key] = new Location
        {
            Kind = kind,
            RetainerId = retainerId,
            RetainerName = retainerName,
            UpdatedUtc = DateTime.UtcNow,
            Items = items,
        };
        return true;
    }

    private static bool SameItems(List<ItemStack> a, List<ItemStack> b) =>
        a.Count == b.Count &&
        a.Zip(b).All(p => p.First.ItemId == p.Second.ItemId &&
                          p.First.IsHq == p.Second.IsHq &&
                          p.First.Quantity == p.Second.Quantity);

    private static bool IsLoaded(GameInventoryType type)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer((InventoryType)type);
        return container != null && container->IsLoaded;
    }

    private static bool IsRetainerInventoryOpen()
    {
        foreach (var name in RetainerAddons)
        {
            var addon = Plugin.GameGui.GetAddonByName(name, 1);
            if (!addon.IsNull && addon.IsVisible)
                return true;
        }

        return false;
    }
}
