using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace MarketHelper.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("Market Helper###MarketHelperMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
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
                ImGui.TextWrapped("Coming in step 2: every item across your characters and retainers, sorted into Sell on MB, Vendor and Keep.");
        }

        using (var tab = ImRaii.TabItem("Retainers"))
        {
            if (tab.Success)
                ImGui.TextWrapped("Coming in step 3: rules for which retainer holds what, and a move list.");
        }

        using (var tab = ImRaii.TabItem("Listing"))
        {
            if (tab.Success)
                ImGui.TextWrapped("Coming in step 4: batch listing and repricing at the current lowest price minus your undercut.");
        }
    }
}
