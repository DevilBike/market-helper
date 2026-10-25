using System;
using System.Collections.Generic;

namespace MarketHelper.Inventory;

public enum LocationKind
{
    Bags,
    Saddlebag,
    Armory,
    Retainer,
    RetainerMarket,
}

public class ItemStack
{
    public uint ItemId { get; set; }
    public bool IsHq { get; set; }
    public int Quantity { get; set; }
}

public class Location
{
    public LocationKind Kind { get; set; }

    // Retainer id for retainer locations, 0 otherwise
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;

    // Position in the game's retainer list, for showing retainers in the same order
    public int Order { get; set; } = 99;

    // The retainer list shows it greyed out because it isn't paid for
    public bool Locked { get; set; }

    // When the items last changed, and when they were last looked at
    public DateTime UpdatedUtc { get; set; }
    public DateTime CheckedUtc { get; set; }

    public DateTime LastSeenUtc => CheckedUtc > UpdatedUtc ? CheckedUtc : UpdatedUtc;
    public List<ItemStack> Items { get; set; } = new();

    // Retainers only: shards, crystals and clusters by item id, null until the retainer is opened
    public Dictionary<uint, int>? Crystals { get; set; }
}

public class CharacterRecord
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public uint WorldId { get; set; }

    // Keyed by LocationKey()
    public Dictionary<string, Location> Locations { get; set; } = new();

    public static string LocationKey(LocationKind kind, ulong retainerId = 0) =>
        retainerId == 0 ? kind.ToString() : $"{kind}:{retainerId}";
}
