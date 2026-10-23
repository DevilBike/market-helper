using System.Collections.Generic;

namespace MarketHelper.Market;

// Broad groups built from the game's item categories (ItemUICategory rows).
public static class ItemGroups
{
    public static readonly ItemGroup[] All =
        [ItemGroup.Consumables, ItemGroup.CraftingMaterials, ItemGroup.Equipment, ItemGroup.Tokens];

    // Medicine, Meal
    public static readonly HashSet<uint> ConsumableCategories = [44, 46];

    // Ingredient, Seafood, Stone, Metal, Lumber, Cloth, Leather, Bone, Reagent, Dye, Part, Crystal, Catalyst
    public static readonly HashSet<uint> CraftingCategories = [45, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 59, 60];

    // Miscellany, Other, Currency: only untradable items in these count as tokens
    public static readonly HashSet<uint> TokenCategories = [61, 63, 100];

    public static ItemGroup Classify(uint uiCategory, bool equippable, bool untradable)
    {
        if (equippable)
            return ItemGroup.Equipment;
        if (ConsumableCategories.Contains(uiCategory))
            return ItemGroup.Consumables;
        if (CraftingCategories.Contains(uiCategory))
            return ItemGroup.CraftingMaterials;
        if (untradable && TokenCategories.Contains(uiCategory))
            return ItemGroup.Tokens;
        return ItemGroup.None;
    }

    // Categories that are entirely inside one group, for labelling the category picker
    public static ItemGroup GroupOfCategory(uint uiCategory) =>
        ConsumableCategories.Contains(uiCategory) ? ItemGroup.Consumables
        : CraftingCategories.Contains(uiCategory) ? ItemGroup.CraftingMaterials
        : ItemGroup.None;

    public static string Label(ItemGroup group) => group switch
    {
        ItemGroup.Consumables => "Consumables",
        ItemGroup.CraftingMaterials => "Crafting materials",
        ItemGroup.Equipment => "Equipment",
        ItemGroup.Tokens => "Tokens",
        _ => "Other",
    };
}
