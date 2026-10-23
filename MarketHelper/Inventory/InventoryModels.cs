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

    public DateTime UpdatedUtc { get; set; }
    public List<ItemStack> Items { get; set; } = new();
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
