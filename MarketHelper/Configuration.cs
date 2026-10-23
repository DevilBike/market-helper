using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace MarketHelper;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Valuation thresholds
    public int MinMarketValue { get; set; } = 1000;
    public float MinSalesPerDay { get; set; } = 0.5f;
    public bool VendorUntradables { get; set; } = false;
    public HashSet<uint> KeepItemIds { get; set; } = new();
    public int PriceCacheMinutes { get; set; } = 30;

    // Retainer organizing
    public List<RetainerRule> RetainerRules { get; set; } = new();
    public ulong CatchAllRetainerId { get; set; }

    // Scanning
    public bool IncludeArmory { get; set; } = false;

    // Listing
    public int UndercutAmount { get; set; } = 1;
    public int MinActionDelayMs { get; set; } = 800;
    public int MaxActionDelayMs { get; set; } = 2500;

    public RetainerRule GetRule(ulong retainerId)
    {
        var rule = RetainerRules.Find(r => r.RetainerId == retainerId);
        if (rule == null)
        {
            rule = new RetainerRule { RetainerId = retainerId };
            RetainerRules.Add(rule);
        }

        return rule;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

[Serializable]
public class RetainerRule
{
    public ulong RetainerId { get; set; }
    public HashSet<uint> UiCategories { get; set; } = new();
    public HashSet<uint> ItemIds { get; set; } = new();
}
