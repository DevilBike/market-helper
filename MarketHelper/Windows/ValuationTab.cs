using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MarketHelper.Inventory;
using MarketHelper.Market;

namespace MarketHelper.Windows;

public class ValuationTab
{
    private static readonly string[] VerdictFilters = ["All", "Sell on MB", "Vendor", "Keep", "Listed", "Waiting for prices"];
    private static readonly TimeSpan RowRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly Plugin plugin;

    private List<ValuationRow> rows = new();
    private DateTime nextRowRefresh = DateTime.MinValue;
    private DateTime nextPriceCheck = DateTime.MinValue;

    private int verdictFilter = 1;
    private ulong characterFilter;
    private string search = string.Empty;

    public ValuationTab(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.Store.Changed += () => nextRowRefresh = DateTime.MinValue;
    }

    public void Draw()
    {
        var now = DateTime.UtcNow;
        if (now >= nextPriceCheck)
        {
            nextPriceCheck = now + TimeSpan.FromMinutes(1);
            RefreshPrices(false);
        }

        if (now >= nextRowRefresh)
        {
            nextRowRefresh = now + RowRefreshInterval;
            rows = plugin.Valuator.Evaluate(plugin.Store.Characters.Values, plugin.Configuration);
        }

        DrawToolbar();
        DrawTotals();
        DrawTable();
    }

    private void RefreshPrices(bool force)
    {
        var maxAge = TimeSpan.FromMinutes(plugin.Configuration.PriceCacheMinutes);
        var requests = plugin.Valuator.PriceRequests(plugin.Store.Characters.Values);
        Task.Run(async () =>
        {
            foreach (var (world, ids) in requests)
                await plugin.Universalis.RefreshAsync(world, ids, maxAge, force);
            nextRowRefresh = DateTime.MinValue;
        });
    }

    private void DrawToolbar()
    {
        using (ImRaii.Disabled(plugin.Universalis.IsFetching))
        {
            if (ImGui.Button(plugin.Universalis.IsFetching ? "Fetching prices..." : "Refresh prices"))
                RefreshPrices(true);
        }

        if (plugin.Universalis.LastError is { } error)
        {
            ImGui.SameLine();
            var red = new Vector4(1f, 0.4f, 0.4f, 1f);
            ImGui.TextColored(in red, $"Price lookup failed: {error}");
        }

        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("##verdict", ref verdictFilter, (ReadOnlySpan<string>)VerdictFilters, VerdictFilters.Length);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        var characterLabel = characterFilter == 0 ? "All characters"
            : plugin.Store.Characters.TryGetValue(characterFilter, out var c) ? c.Name : "All characters";
        using (var combo = ImRaii.Combo("##character", characterLabel))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("All characters", characterFilter == 0))
                    characterFilter = 0;
                foreach (var character in plugin.Store.Characters.Values.OrderBy(ch => ch.Name))
                {
                    if (ImGui.Selectable($"{character.Name} ({character.World})##{character.ContentId}", characterFilter == character.ContentId))
                        characterFilter = character.ContentId;
                }
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search items", ref search, 100);
    }

    private IEnumerable<ValuationRow> FilteredRows()
    {
        var filtered = rows.AsEnumerable();
        if (verdictFilter > 0)
        {
            var verdict = verdictFilter switch
            {
                1 => Verdict.Sell,
                2 => Verdict.Vendor,
                3 => Verdict.Keep,
                4 => Verdict.Listed,
                _ => Verdict.Pending,
            };
            filtered = filtered.Where(r => r.Verdict == verdict);
        }

        if (characterFilter != 0)
            filtered = filtered.Where(r => r.Character.ContentId == characterFilter);

        if (search.Length > 0)
            filtered = filtered.Where(r => r.Item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        return filtered.OrderByDescending(r => r.StackValue).ThenByDescending(r => r.VendorValue);
    }

    private void DrawTotals()
    {
        var scoped = characterFilter == 0 ? rows : rows.Where(r => r.Character.ContentId == characterFilter).ToList();
        var sell = scoped.Where(r => r.Verdict == Verdict.Sell).ToList();
        var vendor = scoped.Where(r => r.Verdict == Verdict.Vendor).ToList();
        ImGui.TextUnformatted(
            $"Sell on MB: {sell.Count} stacks, about {sell.Sum(r => r.StackValue):N0} gil    " +
            $"Vendor: {vendor.Count} stacks, {vendor.Sum(r => r.VendorValue):N0} gil");

        if (plugin.Store.Characters.Count == 0)
            ImGui.TextWrapped("Nothing recorded yet. Your bags are read automatically; open each retainer's inventory once to record it.");
    }

    private void DrawTable()
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("valuation", 8, flags, new Vector2(0, -1));
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Where");
        ImGui.TableSetupColumn("Unit price");
        ImGui.TableSetupColumn("Stack value");
        ImGui.TableSetupColumn("Sales/day");
        ImGui.TableSetupColumn("Vendor");
        ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var index = 0;
        foreach (var row in FilteredRows())
        {
            index++;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Selectable($"{row.Item.Name}{(row.IsHq ? " (HQ)" : "")}##{index}", false, ImGuiSelectableFlags.SpanAllColumns);
            DrawContextMenu(row, index);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.Character.Name} · {LocationLabel(row.Location)}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.UnitPrice > 0 ? $"{row.UnitPrice:N0}" : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.StackValue > 0 ? $"{row.StackValue:N0}" : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Item.MarketSellable ? $"{row.SalesPerDay:0.#}" : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Item.VendorPrice > 0 ? $"{row.VendorValue:N0}" : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Reason);
        }
    }

    private void DrawContextMenu(ValuationRow row, int index)
    {
        using var popup = ImRaii.ContextPopupItem($"rowmenu{index}");
        if (!popup.Success)
            return;

        var keep = plugin.Configuration.KeepItemIds;
        if (keep.Contains(row.Item.Id))
        {
            if (ImGui.Selectable("Remove from keep list"))
            {
                keep.Remove(row.Item.Id);
                plugin.Configuration.Save();
                nextRowRefresh = DateTime.MinValue;
            }
        }
        else if (ImGui.Selectable("Always keep this item"))
        {
            keep.Add(row.Item.Id);
            plugin.Configuration.Save();
            nextRowRefresh = DateTime.MinValue;
        }
    }

    public static string LocationLabel(Location location) => location.Kind switch
    {
        LocationKind.Retainer => location.RetainerName,
        LocationKind.RetainerMarket => $"{location.RetainerName} (market)",
        _ => location.Kind.ToString(),
    };
}
