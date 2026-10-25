using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketHelper.Listing;

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

    // Records what's open right now instead of waiting for the next tick
    public void ScanNow()
    {
        try
        {
            Scan(IsRetainerInventoryOpen());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Inventory scan failed");
        }
    }

    public static bool RetainerPagesLoaded() => IsLoaded(GameInventoryType.RetainerPage1);

    public static bool SaddlebagLoaded() => IsLoaded(GameInventoryType.SaddleBag1);

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events) => dirty = true;

    private void OnLogin() => dirty = true;

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextScan)
            return;
        nextScan = now + ScanInterval;

        DetectRetainers(Plugin.Instance.Configuration.DetectRetainers);

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

    // Adds every retainer the game knows about for this character, so they show up
    // in the Retainers tab before their inventory has been opened
    // Records the game's retainer order, and adds retainers not seen yet when asked to
    private void DetectRetainers(bool addNew)
    {
        var player = Plugin.PlayerState;
        var manager = RetainerManager.Instance();
        if (!player.IsLoaded || player.ContentId == 0 || manager == null || !manager->IsReady)
            return;

        var record = store.GetOrAdd(player.ContentId);
        var changed = false;

        // The retainer list greys out retainers that aren't paid for
        var list = GameUi.GetReady("RetainerList");
        for (uint order = 0; order < 10; order++)
        {
            var retainer = manager->GetRetainerBySortedIndex(order);
            if (retainer == null || retainer->RetainerId == 0)
                continue;

            var name = retainer->NameString;
            if (name.Length == 0)
                continue;

            var key = CharacterRecord.LocationKey(LocationKind.Retainer, retainer->RetainerId);
            if (!record.Locations.TryGetValue(key, out var location))
            {
                if (!addNew)
                    continue;

                record.Locations[key] = new Location
                {
                    Kind = LocationKind.Retainer,
                    RetainerId = retainer->RetainerId,
                    RetainerName = name,
                    Order = (int)order,
                    UpdatedUtc = DateTime.MinValue,
                };
                changed = true;
            }
            else if (location.RetainerName != name || location.Order != order)
            {
                location.RetainerName = name;
                location.Order = (int)order;
                changed = true;
            }

            if (list != null && record.Locations.TryGetValue(key, out var seen))
            {
                var locked = GameUi.IsRetainerRowLocked(list, (int)order, manager->MaxRetainerEntitlement);
                if (seen.Locked != locked)
                {
                    seen.Locked = locked;
                    changed = true;
                }
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
        var now = DateTime.UtcNow;
        if (record.Locations.TryGetValue(key, out var existing) &&
            existing.RetainerName == retainerName && existing.UpdatedUtc != DateTime.MinValue &&
            SameItems(existing.Items, items))
        {
            // Nothing changed; note that it was looked at, without saving every second
            if (now - existing.CheckedUtc < TimeSpan.FromMinutes(1))
                return false;
            existing.CheckedUtc = now;
            return true;
        }

        record.Locations[key] = new Location
        {
            Kind = kind,
            RetainerId = retainerId,
            RetainerName = retainerName,
            Order = existing?.Order ?? 99,
            Locked = existing?.Locked ?? false,
            CheckedUtc = now,
            UpdatedUtc = now,
            Items = items,
        };
        return true;
    }

    private static bool SameItems(List<ItemStack> a, List<ItemStack> b) =>
        a.Count == b.Count &&
        a.Zip(b).All(p => p.First.ItemId == p.Second.ItemId &&
                          p.First.IsHq == p.Second.IsHq &&
                          p.First.Quantity == p.Second.Quantity);

    // The retainer whose inventory or sell list is open right now, or 0
    public static ulong OpenRetainerId()
    {
        if (!IsRetainerInventoryOpen())
            return 0;
        var retainer = RetainerManager.Instance()->GetActiveRetainer();
        return retainer == null ? 0 : retainer->RetainerId;
    }

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
