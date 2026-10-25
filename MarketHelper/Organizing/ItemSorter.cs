using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using MarketHelper.Listing;

namespace MarketHelper.Organizing;

// Runs the game's own /itemsort: on a retainer's inventory as it closes,
// and on your bags once you've left the summoning bell
public sealed class ItemSorter : IDisposable
{
    private static readonly string[] RetainerInventories = ["InventoryRetainer", "InventoryRetainerLarge"];

    // Windows that mean you're still busy with retainers
    private static readonly string[] AtTheBell =
        ["RetainerList", "SelectString", "Talk", "InventoryRetainer", "InventoryRetainerLarge", "RetainerSellList", "RetainerSell"];

    // Item category first, then by item within a category
    private static readonly string[] Conditions = ["category asc", "id asc"];

    private readonly Plugin plugin;
    private DateTime bagsSortAt = DateTime.MaxValue;

    public ItemSorter(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, RetainerInventories, OnRetainerInventoryClosing);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerList", OnRetainerListClosing);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnRetainerInventoryClosing, OnRetainerListClosing);
        Plugin.Framework.Update -= OnUpdate;
    }

    private void OnRetainerInventoryClosing(AddonEvent type, AddonArgs args)
    {
        if (plugin.Configuration.AutoItemSort)
            Sort("retainer");
    }

    // The list also closes while talking to a retainer, so wait and see whether you've really left
    private void OnRetainerListClosing(AddonEvent type, AddonArgs args)
    {
        if (plugin.Configuration.AutoItemSort)
            bagsSortAt = DateTime.UtcNow.AddSeconds(2);
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < bagsSortAt)
            return;

        foreach (var name in AtTheBell)
        {
            if (GameUi.IsOpen(name))
            {
                // Still at the bell; the list closing again will set a new time
                bagsSortAt = DateTime.MaxValue;
                return;
            }
        }

        bagsSortAt = DateTime.MaxValue;
        if (plugin.RetainerTour.IsRunning)
            return;
        Sort("inventory");
    }

    private void Sort(string target)
    {
        try
        {
            GameUi.RunCommand($"/itemsort clear {target}");
            foreach (var condition in Conditions)
                GameUi.RunCommand($"/itemsort condition {target} {condition}");
            GameUi.RunCommand($"/itemsort execute {target}");
            plugin.SortLog.Note($"Ran the game's item sort on {target}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Item sort on {target} failed");
        }
    }
}
