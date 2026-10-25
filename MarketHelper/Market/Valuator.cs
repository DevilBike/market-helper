using System.Collections.Generic;
using System.Linq;
using MarketHelper.Inventory;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketHelper.Market;

public enum Verdict
{
    Sell,
    Vendor,
    Keep,
    Listed,
    Pending,
}

public enum ItemGroup
{
    None,
    Consumables,
    CraftingMaterials,
    Equipment,
    Tokens,
    Housing,
    Collectibles,
    Materia,
    Seasonal,
    Fishing,
}

public record ItemInfo(uint Id, string Name, uint VendorPrice, bool MarketSellable, bool Untradable, uint UiCategory, ItemGroup Group, uint StackSize = 1, bool Unique = false);

public record ValuationRow(
    CharacterRecord Character,
    Location Location,
    ItemInfo Item,
    bool IsHq,
    int Quantity,
    double UnitPrice,
    double SalesPerDay,
    Verdict Verdict,
    string Reason)
{
    public double StackValue => UnitPrice * Quantity;
    public long VendorValue => (long)Item.VendorPrice * Quantity;
}

// Sorts every stored item into Sell on MB, Vendor or Keep.
public class Valuator
{
    private readonly Dictionary<uint, ItemInfo> items = new();
    private readonly UniversalisClient universalis;

    public Valuator(UniversalisClient universalis)
    {
        this.universalis = universalis;
    }

    public ItemInfo GetItem(uint id)
    {
        if (items.TryGetValue(id, out var info))
            return info;

        if (Plugin.DataManager.GetExcelSheet<LuminaItem>().TryGetRow(id, out var row))
        {
            var categoryName = row.ItemUICategory.IsValid ? row.ItemUICategory.Value.Name.ToString() : string.Empty;
            var unlockable = row.IsUnique && row.ItemAction.RowId != 0;
            var group = ItemGroups.Classify(row.ItemUICategory.RowId, categoryName, row.EquipSlotCategory.RowId != 0, row.IsUntradable, unlockable);
            info = new ItemInfo(id, row.Name.ToString(), row.PriceLow, row.ItemSearchCategory.RowId != 0, row.IsUntradable, row.ItemUICategory.RowId, group, System.Math.Max(1u, row.StackSize), row.IsUnique);
        }
        else
        {
            info = new ItemInfo(id, $"Unknown item {id}", 0, false, true, 0, ItemGroup.None);
        }

        items[id] = info;
        return info;
    }

    // Item ids per world that need market prices
    public Dictionary<string, HashSet<uint>> PriceRequests(IEnumerable<CharacterRecord> characters)
    {
        var requests = new Dictionary<string, HashSet<uint>>();
        foreach (var character in characters.Where(c => c.World.Length > 0))
        {
            if (!requests.TryGetValue(character.World, out var ids))
                requests[character.World] = ids = new HashSet<uint>();

            foreach (var stack in character.Locations.Values.SelectMany(l => l.Items))
            {
                if (GetItem(stack.ItemId).MarketSellable)
                    ids.Add(stack.ItemId);
            }
        }

        return requests;
    }

    public List<ValuationRow> Evaluate(IEnumerable<CharacterRecord> characters, Configuration config)
    {
        var rows = new List<ValuationRow>();
        foreach (var character in characters)
        {
            foreach (var location in character.Locations.Values)
            {
                // One row per item and quality in each location
                var groups = location.Items.GroupBy(s => (s.ItemId, s.IsHq));
                foreach (var group in groups)
                {
                    var quantity = group.Sum(s => s.Quantity);
                    rows.Add(EvaluateStack(character, location, GetItem(group.Key.ItemId), group.Key.IsHq, quantity, config));
                }
            }
        }

        return rows;
    }

    public ValuationRow EvaluateStack(CharacterRecord character, Location location, ItemInfo item, bool hq, int quantity, Configuration config)
    {
        ValuationRow Row(double price, double velocity, Verdict verdict, string reason) =>
            new(character, location, item, hq, quantity, price, velocity, verdict, reason);

        var data = item.MarketSellable ? universalis.Get(character.World, item.Id) : null;
        var price = data?.UnitPrice(hq) ?? 0;
        var velocity = data?.SalesPerDay(hq) ?? 0;

        if (location.Kind == LocationKind.RetainerMarket)
            return Row(price, velocity, Verdict.Listed, "Already on the market board");

        if (config.KeepItemIds.Contains(item.Id))
            return Row(price, velocity, Verdict.Keep, "On your keep list");

        if (!item.MarketSellable)
        {
            if (item.VendorPrice > 0 && config.VendorUntradables)
                return Row(0, 0, Verdict.Vendor, "Can't be sold on the market board");
            return Row(0, 0, Verdict.Keep, item.VendorPrice > 0 ? "Untradable" : "Untradable, no vendor price");
        }

        if (data == null)
            return Row(0, 0, Verdict.Pending, "Waiting for prices");

        var stackValue = price * quantity;
        if (stackValue >= config.MinMarketValue && velocity >= config.MinSalesPerDay && price > item.VendorPrice)
            return Row(price, velocity, Verdict.Sell, $"{stackValue:N0} gil, sells {velocity:0.#}/day");

        if (item.VendorPrice > 0)
        {
            var why = price <= item.VendorPrice ? "Vendor pays as much as the market"
                : stackValue < config.MinMarketValue ? $"Only worth {stackValue:N0} gil on the market"
                : $"Sells slowly ({velocity:0.#}/day)";
            return Row(price, velocity, Verdict.Vendor, why);
        }

        return Row(price, velocity, Verdict.Keep, "Below your thresholds and no vendor price");
    }
}
