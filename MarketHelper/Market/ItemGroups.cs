using System.Collections.Generic;

namespace MarketHelper.Market;

// Broad groups built from the game's item categories (ItemUICategory rows).
public static class ItemGroups
{
    public static readonly ItemGroup[] All =
    [
        ItemGroup.Consumables, ItemGroup.CraftingMaterials, ItemGroup.Equipment, ItemGroup.Tokens,
        ItemGroup.Housing, ItemGroup.Collectibles, ItemGroup.Materia, ItemGroup.Seasonal, ItemGroup.Fishing,
    ];

    // Medicine, Meal
    public static readonly HashSet<uint> ConsumableCategories = [44, 46];

    // Ingredient, Seafood, Stone, Metal, Lumber, Cloth, Leather, Bone, Reagent, Dye, Part, Crystal, Catalyst
    public static readonly HashSet<uint> CraftingCategories = [45, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 59, 60];

    private const uint Currency = 100;

    // Newer categories are matched by their English names, so these groups need an English client
    public static readonly HashSet<string> HousingCategories =
    [
        "Furnishing", "Construction Permit", "Roof", "Exterior Wall", "Window", "Door", "Roof Decoration",
        "Exterior Wall Decoration", "Placard", "Fence", "Interior Wall", "Flooring", "Ceiling Light",
        "Outdoor Furnishing", "Table", "Tabletop", "Wall-mounted", "Rug", "Gardening", "Painting",
    ];

    public static readonly HashSet<string> CollectibleCategories = ["Minion", "Orchestrion Roll", "Triple Triad Card"];
    public static readonly HashSet<string> MateriaCategories = ["Materia"];
    public static readonly HashSet<string> SeasonalCategories = ["Seasonal Miscellany"];
    public static readonly HashSet<string> FishingCategories = ["Fishing Tackle"];

    // Each item belongs to the group of its game category (ItemUICategory) and nothing else,
    // so an item never lands in two places; gear goes by whether the game lets you equip it.
    // Only Currency items count as tokens; Miscellany stays Miscellany.
    public static ItemGroup Classify(uint uiCategory, string categoryName, bool equippable, bool untradable, bool unlockable)
    {
        if (equippable)
            return ItemGroup.Equipment;

        return GroupOfCategory(uiCategory, categoryName);
    }

    // The group a whole category belongs to, for labelling the category picker
    public static ItemGroup GroupOfCategory(uint uiCategory, string categoryName)
    {
        if (ConsumableCategories.Contains(uiCategory))
            return ItemGroup.Consumables;
        if (CraftingCategories.Contains(uiCategory))
            return ItemGroup.CraftingMaterials;
        if (MateriaCategories.Contains(categoryName))
            return ItemGroup.Materia;
        if (HousingCategories.Contains(categoryName))
            return ItemGroup.Housing;
        if (CollectibleCategories.Contains(categoryName))
            return ItemGroup.Collectibles;
        if (SeasonalCategories.Contains(categoryName))
            return ItemGroup.Seasonal;
        if (FishingCategories.Contains(categoryName))
            return ItemGroup.Fishing;
        if (uiCategory == Currency)
            return ItemGroup.Tokens;
        return ItemGroup.None;
    }

    public static string Label(ItemGroup group) => group switch
    {
        ItemGroup.Consumables => "Consumables",
        ItemGroup.CraftingMaterials => "Crafting materials",
        ItemGroup.Equipment => "Equipment",
        ItemGroup.Tokens => "Tokens (currency)",
        ItemGroup.Housing => "Housing",
        ItemGroup.Collectibles => "Collectibles (minions, rolls, cards)",
        ItemGroup.Materia => "Materia",
        ItemGroup.Seasonal => "Seasonal",
        ItemGroup.Fishing => "Fishing tackle",
        _ => "Other",
    };
}
