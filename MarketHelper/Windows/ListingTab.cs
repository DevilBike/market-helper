using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using MarketHelper.Inventory;
using MarketHelper.Listing;
using MarketHelper.Market;
using MarketHelper.Organizing;

namespace MarketHelper.Windows;

public class ListingTab
{
    private static readonly TimeSpan BagRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Plugin plugin;

    private List<(BagSlot Slot, ValuationRow Row)> candidates = new();
    private List<(BagSlot Slot, ValuationRow Row)> vendorCandidates = new();
    private readonly HashSet<(Dalamud.Game.Inventory.GameInventoryType, int)> deselected = new();
    private DateTime nextBagRefresh = DateTime.MinValue;
    private bool showAllTradable;
    private string search = string.Empty;

    public ListingTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var lister = plugin.Lister;

        if (lister.IsRunning)
        {
            ImGui.TextUnformatted($"Working: {lister.Finished} of {lister.Total} done. {lister.CurrentStep}");
            if (ImGui.Button("Stop"))
                lister.Stop();
            ImGui.Separator();
            DrawLog();
            return;
        }

        if (!Lister.SellListOpen)
        {
            ImGui.TextWrapped("Open a retainer at a summoning bell and choose \"Sell items in your inventory on the market\". " +
                              "Then you can reprice that retainer's listings or list items from your bags here.");
            DrawLog();
            return;
        }

        var listed = Lister.CurrentListingCount();
        ImGui.TextUnformatted($"This retainer has {listed} of {Lister.MaxListings} market slots used.");

        using (ImRaii.Disabled(listed == 0))
        {
            if (ImGui.Button($"Reprice all {listed} listings"))
                lister.RepriceAll();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"Undercut by {plugin.Configuration.UndercutAmount} gil, " +
                           $"{plugin.Configuration.MinActionDelayMs}-{plugin.Configuration.MaxActionDelayMs} ms between actions");

        ImGui.Spacing();
        DrawCandidates(Lister.MaxListings - listed);
        DrawVendor();
        DrawLog();
    }

    private void DrawCandidates(int room)
    {
        if (DateTime.UtcNow >= nextBagRefresh)
        {
            nextBagRefresh = DateTime.UtcNow + BagRefreshInterval;
            RefreshCandidates();
        }

        ImGui.TextUnformatted("List from your bags");
        ImGui.Separator();
        ImGui.Checkbox("Show every tradable item, not just Sell on MB", ref showAllTradable);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##listsearch", "Search", ref search, 100);

        // Only what's shown gets listed, so a search narrows the batch too
        var shown = candidates
            .Where(c => showAllTradable || c.Row.Verdict == Verdict.Sell)
            .Where(c => search.Length == 0 || c.Row.Item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (shown.Count == 0)
        {
            ImGui.TextWrapped("Nothing in your bags is marked Sell on MB. Withdraw items from your retainers first, or tick the box above.");
            return;
        }

        var selected = shown.Where(c => !deselected.Contains((c.Slot.Container, c.Slot.Slot))).Select(c => c.Slot).ToList();
        using (ImRaii.Disabled(selected.Count == 0 || room <= 0))
        {
            var count = Math.Min(selected.Count, room);
            if (ImGui.Button($"List {count} selected item{(count == 1 ? "" : "s")}"))
                plugin.Lister.ListItems(selected);
        }

        if (selected.Count > room)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Only {room} slots free; the first {room} get listed.");
        }

        using var table = ImRaii.Table("candidates", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                                       new Vector2(0, 220));
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("##pick");
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Est. value");
        ImGui.TableHeadersRow();

        foreach (var (slot, row) in shown)
        {
            var key = (slot.Container, slot.Slot);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var pick = !deselected.Contains(key);
            if (ImGui.Checkbox($"##pick{slot.Container}{slot.Slot}", ref pick))
            {
                if (pick)
                    deselected.Remove(key);
                else
                    deselected.Add(key);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.Item.Name}{(slot.IsHq ? " (HQ)" : "")}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.StackValue > 0 ? $"{row.StackValue:N0}" : "-");
        }
    }

    private void RefreshCandidates()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;

        var character = plugin.Store.GetOrAdd(Plugin.PlayerState.ContentId);
        var bags = new Location { Kind = LocationKind.Bags };
        var evaluated = Lister.ReadBags()
            .Select(slot => (slot, row: plugin.Valuator.EvaluateStack(character, bags, plugin.Valuator.GetItem(slot.ItemId), slot.IsHq, slot.Quantity, plugin.Configuration)))
            .ToList();
        candidates = evaluated
            .Where(c => c.row.Item.MarketSellable && !c.row.Item.Untradable)
            .OrderByDescending(c => c.row.StackValue)
            .ToList();
        vendorCandidates = evaluated
            .Where(c => c.row.Verdict == Verdict.Vendor && !Organizer.IsCarried(c.row.Item, plugin.Configuration))
            .OrderBy(c => c.row.Item.Name)
            .ToList();
    }

    // Items marked Vendor go to the vendor through the retainer, one click for all of them
    private void DrawVendor()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Sell to the vendor");
        ImGui.Separator();
        if (vendorCandidates.Count == 0)
        {
            ImGui.TextDisabled("Nothing in your bags is marked Vendor.");
            return;
        }

        var gil = vendorCandidates.Sum(c => (long)c.Row.VendorValue);
        if (ImGui.Button($"Have the retainer sell {vendorCandidates.Count} Vendor items (about {gil:N0} gil)"))
            plugin.Lister.VendorItems(vendorCandidates.Select(c => c.Slot).ToList());

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", vendorCandidates.Take(30).Select(c => $"{c.Row.Item.Name} x{c.Slot.Quantity}")) +
                             (vendorCandidates.Count > 30 ? $"\n...and {vendorCandidates.Count - 30} more" : ""));
    }

    private void DrawLog()
    {
        var log = plugin.Lister.Log;
        if (log.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted("Last run");
        ImGui.Separator();
        using var child = ImRaii.Child("listinglog", new Vector2(0, 0), true);
        if (!child.Success)
            return;
        foreach (var line in log)
            ImGui.TextWrapped(line);
    }
}
