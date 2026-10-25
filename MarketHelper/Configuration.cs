using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using MarketHelper.Market;

namespace MarketHelper;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    // Valuation thresholds
    public int MinMarketValue { get; set; } = 1000;
    public float MinSalesPerDay { get; set; } = 0.5f;
    public bool VendorUntradables { get; set; } = false;
    public HashSet<uint> KeepItemIds { get; set; } = new();
    public int PriceCacheMinutes { get; set; } = 30;

    // Retainer organizing
    public List<RetainerRule> RetainerRules { get; set; } = new();

    // Only read when moving old settings over; each rule has its own CatchAll now
    public ulong CatchAllRetainerId { get; set; }

    // Off: each character's items stay with that character's retainers. On: rules apply across characters
    public bool CombineCharacters { get; set; } = false;

    // Rules of this character are copied, by retainer order, to characters that have none yet
    public ulong TemplateOwnerId { get; set; }

    // Sorting also puts your shards and crystals into retainers, top of the list first
    public bool StoreCrystals { get; set; } = true;

    // Items and groups that stay in your bags
    public HashSet<uint> CarryItemIds { get; set; } = new();
    public HashSet<ItemGroup> CarryGroups { get; set; } = new();
    public bool PlanBagMoves { get; set; } = true;

    // Items and groups that live in the saddlebag
    public HashSet<uint> SaddlebagItemIds { get; set; } = new();
    public HashSet<ItemGroup> SaddlebagGroups { get; set; } = new();

    // Armory items that stay put even though no gearset uses them
    public HashSet<uint> ArmoryKeepIds { get; set; } = new();

    // Scanning
    public bool IncludeArmory { get; set; } = false;
    public bool DetectRetainers { get; set; } = true;

    // Listing
    public int UndercutAmount { get; set; } = 1;
    public float MinPriceRatio { get; set; } = 0.5f;
    public int MinActionDelayMs { get; set; } = 400;
    public int MaxActionDelayMs { get; set; } = 1200;

    public void Migrate()
    {
        // Version 0 had slower listing delays; move people still on those defaults to the new ones
        if (Version < 1)
        {
            if (MinActionDelayMs == 800 && MaxActionDelayMs == 2500)
            {
                MinActionDelayMs = 400;
                MaxActionDelayMs = 1200;
            }

            Version = 1;
            Save();
        }

        // Version 1 had one catch-all retainer for everyone
        if (Version < 2)
        {
            if (CatchAllRetainerId != 0)
                GetRule(CatchAllRetainerId).CatchAll = true;
            CatchAllRetainerId = 0;
            Version = 2;
            Save();
        }
    }

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
    public HashSet<ItemGroup> Groups { get; set; } = new();
    public HashSet<uint> UiCategories { get; set; } = new();
    public HashSet<uint> ItemIds { get; set; } = new();

    // Gets anything from its character without a more specific rule
    public bool CatchAll { get; set; }

    // Left out of organizing entirely
    public bool Ignored { get; set; }

    // Where new stacks go once this retainer is full
    public ulong OverflowRetainerId { get; set; }

    public bool HasRules => Groups.Count > 0 || UiCategories.Count > 0 || ItemIds.Count > 0 || CatchAll || Ignored;

    public RetainerRule CopyFor(ulong retainerId, ulong overflowRetainerId) => new()
    {
        RetainerId = retainerId,
        Groups = new HashSet<ItemGroup>(Groups),
        UiCategories = new HashSet<uint>(UiCategories),
        ItemIds = new HashSet<uint>(ItemIds),
        CatchAll = CatchAll,
        Ignored = Ignored,
        OverflowRetainerId = overflowRetainerId,
    };
}
