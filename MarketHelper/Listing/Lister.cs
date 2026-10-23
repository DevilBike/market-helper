using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using Dalamud.Game.Network.Structures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using AddonSheet = Lumina.Excel.Sheets.Addon;

namespace MarketHelper.Listing;

public record BagSlot(GameInventoryType Container, int Slot, uint ItemId, bool IsHq, int Quantity);

public record PriceDecision(int? NewPrice, string Note);

// Lists bag items and reprices existing listings through the retainer's sell windows.
public sealed unsafe class Lister : IDisposable
{
    public const int MaxListings = 20;

    private const string SellListAddon = "RetainerSellList";
    private const string SellAddon = "RetainerSell";
    private const string SearchResultAddon = "ItemSearchResult";
    private const string ContextMenuAddon = "ContextMenu";

    // RetainerSell: value 5 is the asking price, node 4 "Compare Prices", 21 "Confirm", 22 "Cancel"
    private const int AskingPriceValue = 5;
    private const uint ComparePricesButton = 4;
    private const uint ConfirmButton = 21;
    private const uint CancelButton = 22;

    private static readonly GameInventoryType[] Bags =
    [
        GameInventoryType.Inventory1, GameInventoryType.Inventory2,
        GameInventoryType.Inventory3, GameInventoryType.Inventory4,
    ];

    private readonly Plugin plugin;
    private readonly StepRunner runner;
    private readonly OfferingsWatcher offerings = new();
    private readonly string putUpForSaleText;

    // State of the item currently in the sell window
    private uint itemId;
    private bool itemHq;
    private int currentPrice;
    private bool isNewListing;
    private PriceDecision decision = new(null, string.Empty);

    public List<string> Log { get; } = new();
    public int Total { get; private set; }
    public int Finished { get; private set; }

    public bool IsRunning => runner.IsRunning;
    public string? CurrentStep => runner.CurrentStep;

    public Lister(Plugin plugin)
    {
        this.plugin = plugin;
        runner = new StepRunner(() => (plugin.Configuration.MinActionDelayMs, plugin.Configuration.MaxActionDelayMs));
        runner.Failed += OnFailed;

        // Row 99 of the Addon sheet is the "Put Up for Sale" menu entry, in the client's language
        putUpForSaleText = Plugin.DataManager.GetExcelSheet<AddonSheet>().TryGetRow(99, out var row)
            ? row.Text.ToString()
            : "Put Up for Sale";
    }

    public void Dispose()
    {
        runner.Dispose();
        offerings.Dispose();
    }

    public static bool SellListOpen => GameUi.IsOpen(SellListAddon);

    public static int CurrentListingCount()
    {
        var count = 0;
        foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.RetainerMarket))
        {
            if (!item.IsEmpty)
                count++;
        }

        return count;
    }

    public static List<BagSlot> ReadBags()
    {
        var slots = new List<BagSlot>();
        foreach (var container in Bags)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
            {
                if (!item.IsEmpty && item.BaseItemId != 0)
                    slots.Add(new BagSlot(container, (int)item.InventorySlot, item.BaseItemId, item.IsHq, item.Quantity));
            }
        }

        return slots;
    }

    public void Stop()
    {
        runner.Stop();
        CloseWindows();
        AddLog("Stopped.");
    }

    public void RepriceAll()
    {
        if (IsRunning || !SellListOpen)
            return;

        var count = CurrentListingCount();
        StartBatch(count);
        for (var i = 0; i < count; i++)
        {
            var row = i;
            runner.Add($"Open listing {row + 1}", () => OpenListingMenu(row));
            runner.Add("Choose Adjust Price", ChooseAdjustPrice);
            AddPricingSteps(isNew: false);
        }

        runner.Add("Finish", () => AddLog($"Done. Checked {Finished} of {Total} listings."), input: false);
    }

    public void ListItems(IReadOnlyList<BagSlot> slots)
    {
        if (IsRunning || !SellListOpen)
            return;

        var room = MaxListings - CurrentListingCount();
        var toList = slots.Take(Math.Max(0, room)).ToList();
        if (toList.Count < slots.Count)
            AddLog($"Only {room} market slots free, listing the first {toList.Count}.");

        StartBatch(toList.Count);
        foreach (var slot in toList)
        {
            runner.Add($"Open {plugin.Valuator.GetItem(slot.ItemId).Name}", () => OpenBagItemMenu(slot));
            runner.Add("Choose Put Up for Sale", ChoosePutUpForSale);
            AddPricingSteps(isNew: true);
        }

        runner.Add("Finish", () => AddLog($"Done. Handled {Finished} of {Total} items."), input: false);
    }

    private void StartBatch(int total)
    {
        Total = total;
        Finished = 0;
        Log.Clear();
    }

    private void AddPricingSteps(bool isNew)
    {
        runner.Add("Wait for the sell window", () => ReadSellWindow(isNew), input: false);
        runner.Add("Compare prices", ComparePrices);
        runner.Add("Read market prices", ReadOffers, input: false);
        runner.Add("Close the price list", ClosePriceList);
        runner.Add("Set the price", SetPrice);
        runner.Add("Confirm", Confirm);
        runner.Add("Wait for the sell window to close", () => GameUi.IsOpen(SellAddon) ? StepResult.Wait : StepResult.Done, input: false);
    }

    private StepResult OpenListingMenu(int row)
    {
        var sellList = GameUi.GetReady(SellListAddon);
        if (sellList == null)
            return StepResult.Fail;

        GameUi.Fire(sellList, true, 0, row, 1);
        return StepResult.Done;
    }

    // "Adjust Price" is always the first entry when a listing's menu opens
    private StepResult ChooseAdjustPrice()
    {
        var menu = GameUi.GetReady(ContextMenuAddon);
        if (menu == null)
            return StepResult.Wait;

        GameUi.SelectContextEntry(menu, 0);
        return StepResult.Done;
    }

    private StepResult OpenBagItemMenu(BagSlot slot)
    {
        // Make sure the item is still where we saw it
        var items = Plugin.GameInventory.GetInventoryItems(slot.Container);
        if (slot.Slot >= items.Length || items[slot.Slot].BaseItemId != slot.ItemId)
        {
            AddLog($"{plugin.Valuator.GetItem(slot.ItemId).Name}: moved, skipped.");
            SkipCurrentItem();
            return StepResult.Done;
        }

        var inventoryAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        AgentInventoryContext.Instance()->OpenForItemSlot((InventoryType)slot.Container, slot.Slot, 0, inventoryAgent->GetAddonId());
        return StepResult.Done;
    }

    private StepResult ChoosePutUpForSale()
    {
        var menu = GameUi.GetReady(ContextMenuAddon);
        if (menu == null)
            return StepResult.Wait;

        var index = GameUi.FindContextEntry(menu, putUpForSaleText);
        if (index < 0)
        {
            AddLog("The item menu had no \"Put Up for Sale\" entry.");
            menu->Close(true);
            SkipCurrentItem();
            return StepResult.Done;
        }

        GameUi.SelectContextEntry(menu, index);
        return StepResult.Done;
    }

    private StepResult ReadSellWindow(bool isNew)
    {
        var sell = GameUi.GetReady(SellAddon);
        if (sell == null)
            return StepResult.Wait;

        // The item being priced sits in a hidden container while the window is open
        var blocked = Plugin.GameInventory.GetInventoryItems(GameInventoryType.BlockedItems);
        if (blocked.Length == 0 || blocked[0].BaseItemId == 0)
            return StepResult.Wait;

        itemId = blocked[0].BaseItemId;
        itemHq = blocked[0].IsHq;
        currentPrice = sell->AtkValues[AskingPriceValue].Int;
        isNewListing = isNew;
        return StepResult.Done;
    }

    private StepResult ComparePrices()
    {
        var sell = GameUi.GetReady(SellAddon);
        if (sell == null)
            return StepResult.Fail;

        offerings.Arm();
        return GameUi.ClickButton(sell, ComparePricesButton) ? StepResult.Done : StepResult.Fail;
    }

    private StepResult ReadOffers()
    {
        if (offerings.TryGet() is not { } listings)
            return StepResult.Wait;

        decision = Decide(listings);
        return StepResult.Done;
    }

    private StepResult ClosePriceList()
    {
        var results = GameUi.GetReady(SearchResultAddon);
        if (results != null)
            results->Close(true);
        return StepResult.Done;
    }

    private StepResult SetPrice()
    {
        var sell = GameUi.GetReady(SellAddon);
        if (sell == null)
            return StepResult.Wait;

        if (decision.NewPrice is { } price && price != currentPrice)
            GameUi.Fire(sell, true, 2, price);
        return StepResult.Done;
    }

    private StepResult Confirm()
    {
        var sell = GameUi.GetReady(SellAddon);
        if (sell == null)
            return StepResult.Wait;

        var name = plugin.Valuator.GetItem(itemId).Name + (itemHq ? " (HQ)" : "");
        var listIt = decision.NewPrice is { } price && (isNewListing || price != currentPrice);
        if (listIt)
        {
            if (!GameUi.ClickButton(sell, ConfirmButton))
                return StepResult.Fail;

            AddLog(isNewListing
                ? $"{name}: listed at {decision.NewPrice:N0}. {decision.Note}"
                : $"{name}: {currentPrice:N0} → {decision.NewPrice:N0}. {decision.Note}");
        }
        else
        {
            GameUi.ClickButton(sell, CancelButton);
            AddLog($"{name}: left alone. {decision.Note}");
        }

        Finished++;
        return StepResult.Done;
    }

    private PriceDecision Decide(List<IMarketBoardItemListing> listings)
    {
        var config = plugin.Configuration;
        var item = plugin.Valuator.GetItem(itemId);
        var world = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
        var average = plugin.Universalis.Get(world, itemId)?.AveragePrice(itemHq) ?? 0;

        // Never undercut our own retainers on any character
        var own = plugin.Store.Characters.Values
            .SelectMany(c => c.Locations.Values)
            .Where(l => l.RetainerId != 0)
            .Select(l => l.RetainerId)
            .ToHashSet();

        // HQ only competes with HQ; NQ competes with everything
        var competitors = listings
            .Where(l => !own.Contains(l.RetainerId))
            .Where(l => !itemHq || l.IsHq)
            .ToList();

        int price;
        if (competitors.Count == 0)
        {
            if (!isNewListing)
                return new PriceDecision(null, "No other sellers.");
            if (average <= 0)
                return new PriceDecision(null, "No other sellers and no price history.");
            price = (int)Math.Round(average);
        }
        else
        {
            var lowest = (int)competitors.Min(l => l.PricePerUnit);
            if (!isNewListing && currentPrice > 0 && currentPrice <= lowest)
                return new PriceDecision(null, $"Already the cheapest (next is {lowest:N0}).");
            price = lowest - config.UndercutAmount;
        }

        if (price <= item.VendorPrice)
            return new PriceDecision(null, $"Market ({price:N0}) is at or below the vendor price.");

        if (average > 0 && price < average * config.MinPriceRatio)
            return new PriceDecision(null, $"{price:N0} looks too cheap next to the {average:N0} average, check it yourself.");

        return new PriceDecision(Math.Max(1, price), competitors.Count == 0 ? "Priced at the recent average." : string.Empty);
    }

    // Drops the rest of this item's steps, up to the next item's first step
    private void SkipCurrentItem()
    {
        runner.SkipUntilNext(name => name.StartsWith("Open ") || name == "Finish");
        Finished++;
    }

    private void OnFailed(string step)
    {
        AddLog($"Stopped: \"{step}\" didn't work. Nothing else was changed.");
        CloseWindows();
    }

    private void CloseWindows()
    {
        var results = GameUi.GetReady(SearchResultAddon);
        if (results != null)
            results->Close(true);

        var sell = GameUi.GetReady(SellAddon);
        if (sell != null)
            GameUi.ClickButton(sell, CancelButton);

        var menu = GameUi.GetReady(ContextMenuAddon);
        if (menu != null)
            menu->Close(true);
    }

    private void AddLog(string line)
    {
        Log.Add(line);
        Plugin.Log.Information($"[Listing] {line}");
    }
}
