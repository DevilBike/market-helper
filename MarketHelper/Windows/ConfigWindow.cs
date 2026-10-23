using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MarketHelper.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Market Helper Settings###MarketHelperConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(380, 260);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextUnformatted("Valuation");
        ImGui.Separator();

        var minValue = configuration.MinMarketValue;
        if (ImGui.InputInt("Min market value (gil)", ref minValue, 100, 1000))
        {
            configuration.MinMarketValue = Math.Max(0, minValue);
            configuration.Save();
        }

        var minSales = configuration.MinSalesPerDay;
        if (ImGui.InputFloat("Min sales per day", ref minSales, 0.1f, 1f, "%.1f"))
        {
            configuration.MinSalesPerDay = Math.Max(0f, minSales);
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Listing");
        ImGui.Separator();

        var undercut = configuration.UndercutAmount;
        if (ImGui.InputInt("Undercut by (gil)", ref undercut))
        {
            configuration.UndercutAmount = Math.Max(0, undercut);
            configuration.Save();
        }

        // Each listing action waits a random time between these two values
        var minDelay = configuration.MinActionDelayMs;
        if (ImGui.SliderInt("Min delay (ms)", ref minDelay, 200, 10000))
        {
            configuration.MinActionDelayMs = minDelay;
            configuration.MaxActionDelayMs = Math.Max(minDelay, configuration.MaxActionDelayMs);
            configuration.Save();
        }

        var maxDelay = configuration.MaxActionDelayMs;
        if (ImGui.SliderInt("Max delay (ms)", ref maxDelay, 200, 10000))
        {
            configuration.MaxActionDelayMs = maxDelay;
            configuration.MinActionDelayMs = Math.Min(maxDelay, configuration.MinActionDelayMs);
            configuration.Save();
        }
    }
}
