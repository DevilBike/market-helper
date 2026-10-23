using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ItemUICategory = Lumina.Excel.Sheets.ItemUICategory;
using MarketHelper.Inventory;
using MarketHelper.Organizing;

namespace MarketHelper.Windows;

public class RetainersTab
{
    private static readonly TimeSpan PlanRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly Vector4 Warning = new(1f, 0.6f, 0.3f, 1f);
    private static readonly Vector4 Highlight = new(0.5f, 0.9f, 0.5f, 1f);

    private readonly Plugin plugin;
    private readonly List<(uint Id, string Name)> categories;

    private List<RetainerRef> retainers = new();
    private MovePlan plan = new(new List<Move>(), new Dictionary<ulong, int>());
    private DateTime nextRefresh = DateTime.MinValue;

    private ulong selected;
    private string categorySearch = string.Empty;

    public RetainersTab(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.Store.Changed += Invalidate;

        categories = Plugin.DataManager.GetExcelSheet<ItemUICategory>()
            .Where(c => c.RowId != 0)
            .Select(c => (c.RowId, c.Name.ToString()))
            .Where(c => c.Item2.Length > 0)
            .OrderBy(c => c.Item2)
            .ToList();
    }

    private void Invalidate() => nextRefresh = DateTime.MinValue;

    public void Draw()
    {
        if (DateTime.UtcNow >= nextRefresh)
        {
            nextRefresh = DateTime.UtcNow + PlanRefreshInterval;
            retainers = Organizer.Retainers(plugin.Store.Characters.Values);
            plan = plugin.Organizer.Plan(plugin.Store.Characters.Values, plugin.Configuration);
        }

        if (retainers.Count == 0)
        {
            ImGui.TextWrapped("No retainers recorded yet. Open each retainer's inventory once, on every character, and they'll show up here.");
            return;
        }

        DrawOpenRetainer();

        using (var tabs = ImRaii.TabBar("RetainerSubTabs"))
        {
            if (!tabs.Success)
                return;

            using (var tab = ImRaii.TabItem("Rules"))
            {
                if (tab.Success)
                    DrawRules();
            }

            using (var tab = ImRaii.TabItem($"Moves ({plan.Moves.Count})###Moves"))
            {
                if (tab.Success)
                    DrawMoves(plan.Moves);
            }
        }
    }

    // While a retainer is open, show exactly what to take out of it and put into it
    private void DrawOpenRetainer()
    {
        var open = InventoryScanner.OpenRetainerId();
        if (open == 0)
            return;

        var takeOut = plan.Moves.Where(m => m.From.Id == open).ToList();
        var putIn = plan.Moves.Where(m => m.To.Id == open && !m.NeedsTrade).ToList();
        if (takeOut.Count == 0 && putIn.Count == 0)
        {
            Colored(Highlight, "This retainer is already organized.");
            return;
        }

        using var node = ImRaii.TreeNode("This retainer", ImGuiTreeNodeFlags.DefaultOpen);
        if (!node.Success)
            return;

        if (takeOut.Count > 0)
        {
            Colored(Highlight, "Take out:");
            foreach (var move in takeOut)
                ImGui.BulletText($"{ItemLabel(move)} → {move.To.Label}{(move.NeedsTrade ? ", trade" : "")}");
        }

        if (putIn.Count > 0)
        {
            Colored(Highlight, "Put in (from your bags after taking them out of the others):");
            foreach (var move in putIn)
                ImGui.BulletText($"{ItemLabel(move)} from {move.From.Name}");
        }

        ImGui.Separator();
    }

    private void DrawRules()
    {
        var config = plugin.Configuration;

        using (var list = ImRaii.Child("RetainerList", new Vector2(240 * ImGuiHelpers.GlobalScale, 0), true))
        {
            if (list.Success)
            {
                foreach (var retainer in retainers)
                {
                    var stacks = plan.StacksAfter.GetValueOrDefault(retainer.Id, retainer.Location.Items.Count);
                    var catchAll = config.CatchAllRetainerId == retainer.Id ? " *" : "";
                    if (ImGui.Selectable($"{retainer.Label}{catchAll}##{retainer.Id}", selected == retainer.Id))
                        selected = retainer.Id;

                    if (stacks > Organizer.RetainerSlots)
                    {
                        ImGui.SameLine();
                        Colored(Warning, "full");
                    }
                }
            }
        }

        ImGui.SameLine();

        using var detail = ImRaii.Child("RetainerDetail", Vector2.Zero, false);
        if (!detail.Success)
            return;

        var current = retainers.Find(r => r.Id == selected);
        if (current == null)
        {
            ImGui.TextWrapped("Pick a retainer to choose what it stores. * marks the catch-all retainer that gets anything without a rule.");
            return;
        }

        ImGui.TextUnformatted(current.Label);
        var after = plan.StacksAfter.GetValueOrDefault(current.Id, current.Location.Items.Count);
        ImGui.TextUnformatted($"{current.Location.Items.Count} stacks now, about {after} after moves (of {Organizer.RetainerSlots})");
        if (after > Organizer.RetainerSlots)
            Colored(Warning, "These rules send more here than fits.");

        var isCatchAll = config.CatchAllRetainerId == current.Id;
        if (ImGui.Checkbox("Catch-all: store anything without a rule here", ref isCatchAll))
        {
            config.CatchAllRetainerId = isCatchAll ? current.Id : 0;
            Save();
        }

        var rule = config.GetRule(current.Id);

        ImGui.Spacing();
        ImGui.TextUnformatted("Categories stored here");
        ImGui.Separator();
        uint? removeCategory = null;
        foreach (var id in rule.UiCategories)
        {
            if (ImGui.SmallButton($"x##cat{id}"))
                removeCategory = id;
            ImGui.SameLine();
            ImGui.TextUnformatted(CategoryName(id));
        }

        if (removeCategory is { } rc)
        {
            rule.UiCategories.Remove(rc);
            Save();
        }

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##addcategory", "Add a category..."))
        {
            if (combo.Success)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##catsearch", "Search", ref categorySearch, 50);
                foreach (var (id, name) in categories)
                {
                    if (categorySearch.Length > 0 && !name.Contains(categorySearch, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var owner = config.RetainerRules.Find(r => r.UiCategories.Contains(id));
                    var suffix = owner != null && owner.RetainerId != current.Id
                        ? $" (now {retainers.Find(r => r.Id == owner.RetainerId)?.Name ?? "another retainer"})"
                        : "";
                    if (ImGui.Selectable($"{name}{suffix}##addcat{id}", rule.UiCategories.Contains(id)))
                    {
                        // A category lives in one retainer only
                        foreach (var other in config.RetainerRules)
                            other.UiCategories.Remove(id);
                        rule.UiCategories.Add(id);
                        Save();
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Single items stored here");
        ImGui.Separator();
        if (rule.ItemIds.Count == 0)
            ImGui.TextWrapped("Right-click an item in the Moves list to send it to a specific retainer.");

        uint? removeItem = null;
        foreach (var id in rule.ItemIds)
        {
            if (ImGui.SmallButton($"x##item{id}"))
                removeItem = id;
            ImGui.SameLine();
            ImGui.TextUnformatted(plugin.Valuator.GetItem(id).Name);
        }

        if (removeItem is { } ri)
        {
            rule.ItemIds.Remove(ri);
            Save();
        }
    }

    private void DrawMoves(List<Move> moves)
    {
        if (moves.Count == 0)
        {
            ImGui.TextWrapped("Nothing to move. Add rules on the Rules tab, or everything is already where it belongs.");
            return;
        }

        var trades = moves.Count(m => m.NeedsTrade);
        if (trades > 0)
            ImGui.TextWrapped($"{trades} moves go to another character's retainer. Withdraw them, then trade them over with both characters on the same world.");

        var index = 0;
        foreach (var group in moves.GroupBy(m => (m.From.Id, m.To.Id)).OrderBy(g => g.First().From.Label))
        {
            var first = group.First();
            var header = $"{first.From.Label} → {first.To.Label}{(first.NeedsTrade ? " (trade)" : "")}  [{group.Count()}]";
            using var node = ImRaii.TreeNode($"{header}##{first.From.Id}-{first.To.Id}");
            if (!node.Success)
                continue;

            foreach (var move in group.OrderBy(m => m.Item.Name))
            {
                index++;
                ImGui.Selectable($"{ItemLabel(move)}##move{index}");
                DrawMoveMenu(move, index);
            }
        }
    }

    private void DrawMoveMenu(Move move, int index)
    {
        using var popup = ImRaii.ContextPopupItem($"movemenu{index}");
        if (!popup.Success)
            return;

        ImGui.TextDisabled("Always store this item in:");
        foreach (var retainer in retainers)
        {
            if (ImGui.Selectable($"{retainer.Label}##storein{retainer.Id}"))
            {
                foreach (var rule in plugin.Configuration.RetainerRules)
                    rule.ItemIds.Remove(move.Item.Id);
                plugin.Configuration.GetRule(retainer.Id).ItemIds.Add(move.Item.Id);
                Save();
            }
        }

        if (ImGui.Selectable("Leave it wherever it is"))
        {
            foreach (var rule in plugin.Configuration.RetainerRules)
                rule.ItemIds.Remove(move.Item.Id);
            plugin.Configuration.GetRule(move.From.Id).ItemIds.Add(move.Item.Id);
            Save();
        }
    }

    private void Save()
    {
        plugin.Configuration.Save();
        Invalidate();
    }

    private string CategoryName(uint id) =>
        categories.Find(c => c.Id == id).Name ?? $"Category {id}";

    private static void Colored(Vector4 color, string text) => ImGui.TextColored(in color, text);

    private static string ItemLabel(Move move) =>
        $"{move.Item.Name}{(move.IsHq ? " (HQ)" : "")} x{move.Quantity}";
}
