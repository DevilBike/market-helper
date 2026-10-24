using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketHelper.Inventory;

namespace MarketHelper.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Market Helper Settings###MarketHelperConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(420, 420);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.CollapsingHeader("Valuation", ImGuiTreeNodeFlags.DefaultOpen))
            DrawValuation();

        if (ImGui.CollapsingHeader("Listing and automation", ImGuiTreeNodeFlags.DefaultOpen))
            DrawListing();

        if (ImGui.CollapsingHeader("Recorded characters"))
            DrawCharacters();
    }

    private void DrawValuation()
    {
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

        var vendorUntradables = configuration.VendorUntradables;
        if (ImGui.Checkbox("Mark untradable items with a vendor price as Vendor", ref vendorUntradables))
        {
            configuration.VendorUntradables = vendorUntradables;
            configuration.Save();
        }

        var includeArmory = configuration.IncludeArmory;
        if (ImGui.Checkbox("Include the armory chest", ref includeArmory))
        {
            configuration.IncludeArmory = includeArmory;
            configuration.Save();
            plugin.Scanner.RequestScan();
        }

        var detect = configuration.DetectRetainers;
        if (ImGui.Checkbox("Find my retainers automatically at a summoning bell", ref detect))
        {
            configuration.DetectRetainers = detect;
            configuration.Save();
        }

        var cacheMinutes = configuration.PriceCacheMinutes;
        if (ImGui.SliderInt("Refresh prices after (min)", ref cacheMinutes, 5, 240))
        {
            configuration.PriceCacheMinutes = cacheMinutes;
            configuration.Save();
        }
    }

    private void DrawListing()
    {
        var undercut = configuration.UndercutAmount;
        if (ImGui.InputInt("Undercut by (gil)", ref undercut))
        {
            configuration.UndercutAmount = Math.Max(0, undercut);
            configuration.Save();
        }

        var ratio = configuration.MinPriceRatio * 100f;
        if (ImGui.SliderFloat("Skip if below % of average", ref ratio, 0f, 100f, "%.0f%%"))
        {
            configuration.MinPriceRatio = ratio / 100f;
            configuration.Save();
        }

        // Each listing, armory and retainer-check action waits a random time between these two values
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

    private void DrawCharacters()
    {
        ulong? forget = null;
        foreach (var character in plugin.Store.Characters.Values.OrderBy(c => c.Name))
        {
            var retainers = character.Locations.Values.Count(l => l.Kind == LocationKind.Retainer);
            ImGui.TextUnformatted($"{character.Name} ({character.World}), {retainers} retainers");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Forget##{character.ContentId}"))
                forget = character.ContentId;
        }

        if (forget is { } id)
            plugin.Store.Remove(id);
    }
}
