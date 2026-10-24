using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace MarketHelper.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ValuationTab valuationTab;
    private readonly RetainersTab retainersTab;
    private readonly ListingTab listingTab;
    private readonly InventoryTab inventoryTab;

    public MainWindow(Plugin plugin) : base("Market Helper###MarketHelperMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        valuationTab = new ValuationTab(plugin);
        retainersTab = new RetainersTab(plugin);
        listingTab = new ListingTab(plugin);
        inventoryTab = new InventoryTab(plugin);
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            ImGui.TextUnformatted("Log in to a character to use Market Helper.");
            return;
        }

        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();

        using var tabs = ImRaii.TabBar("MarketHelperTabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("Valuation"))
        {
            if (tab.Success)
                valuationTab.Draw();
        }

        using (var tab = ImRaii.TabItem("Inventory"))
        {
            if (tab.Success)
                inventoryTab.Draw();
        }

        using (var tab = ImRaii.TabItem("Retainers"))
        {
            if (tab.Success)
                retainersTab.Draw();
        }

        using (var tab = ImRaii.TabItem("Listing"))
        {
            if (tab.Success)
                listingTab.Draw();
        }
    }
}
