using Dalamud.Configuration;
using System;

namespace MarketHelper;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Valuation thresholds
    public int MinMarketValue { get; set; } = 1000;
    public float MinSalesPerDay { get; set; } = 0.5f;

    // Listing
    public int UndercutAmount { get; set; } = 1;
    public int MinActionDelayMs { get; set; } = 800;
    public int MaxActionDelayMs { get; set; } = 2500;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
