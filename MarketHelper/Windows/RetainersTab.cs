using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ItemUICategory = Lumina.Excel.Sheets.ItemUICategory;
using MarketHelper.Inventory;
using MarketHelper.Market;
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

    private static readonly string[] MoveFilters = ["All moves", "Between retainers", "From my bags", "Back to my bags", "Trades only"];

    private ulong selected;
    private string categorySearch = string.Empty;
    private string retainerSearch = string.Empty;
    private string moveSearch = string.Empty;
    private int moveFilter;

    // Whose retainers the tab shows; 0 means whoever is logged in
    private ulong viewOwner;
    private string spreadSearch = string.Empty;
    private int spreadMax = 99;

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

        DrawTour();

        if (retainers.Count == 0)
        {
            ImGui.TextWrapped("No retainers recorded yet. Open each retainer's inventory once, on every character, and they'll show up here.");
            return;
        }

        DrawOpenRetainer();

        var owner = DrawCharacterPicker();
        var shown = retainers.Where(r => r.Owner.ContentId == owner).ToList();
        var moves = plan.Moves.Where(m => m.From.Owner.ContentId == owner || m.To.Owner.ContentId == owner).ToList();

        using (var tabs = ImRaii.TabBar("RetainerSubTabs"))
        {
            if (!tabs.Success)
                return;

            using (var tab = ImRaii.TabItem("Rules"))
            {
                if (tab.Success)
                    DrawRules(shown);
            }

            using (var tab = ImRaii.TabItem($"Moves ({moves.Count})###Moves"))
            {
                if (tab.Success)
                    DrawMoves(moves);
            }

            using (var tab = ImRaii.TabItem("Across characters"))
            {
                if (tab.Success)
                    DrawSpread();
            }
        }
    }

    // Visits every retainer at the bell, to record them or to carry out this character's moves
    private void DrawTour()
    {
        var tour = plugin.RetainerTour;
        if (tour.IsRunning)
        {
            ImGui.TextUnformatted(tour.Status);
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##tour"))
                tour.Stop();
            return;
        }

        if (RetainerTour.CanStart)
        {
            if (ImGui.Button("Check all retainers"))
                tour.CheckAll();
            ImGui.SameLine();
            if (ImGui.Button("Sort this character's retainers"))
                tour.SortAll();
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Check opens each retainer's inventory to record it.\n" +
                                 "Sort takes out everything that belongs elsewhere, then puts it into the right retainers,\n" +
                                 "for this character only. Moves to other characters still need trades.");
        }
        else
        {
            ImGui.TextDisabled("Open the retainer list at a summoning bell to check or sort retainers automatically.");
        }

        if (tour.Status.Length > 0)
            ImGui.TextUnformatted(tour.Status);
    }

    // While a retainer is open, show exactly what to take out of it and put into it
    private void DrawOpenRetainer()
    {
        var open = InventoryScanner.OpenRetainerId();
        if (open == 0)
            return;

        var takeOut = plan.Moves.Where(m => !m.From.IsBags && m.From.Id == open).ToList();
        var putIn = plan.Moves.Where(m => !m.To.IsBags && m.To.Id == open && !m.NeedsTrade).ToList();
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
            Colored(Highlight, "Put in:");
            foreach (var move in putIn)
                ImGui.BulletText($"{ItemLabel(move)} from {move.From.Name}{(move.From.IsBags ? "" : " (withdraw it there first)")}");
        }

        ImGui.Separator();
    }

    // Picks which character's retainers to show; defaults to the one logged in
    private ulong DrawCharacterPicker()
    {
        var owners = retainers.Select(r => r.Owner).DistinctBy(o => o.ContentId).OrderBy(o => o.Name).ToList();
        var current = Plugin.PlayerState.ContentId;
        if (viewOwner == 0 || owners.All(o => o.ContentId != viewOwner))
            viewOwner = owners.Any(o => o.ContentId == current) ? current : owners[0].ContentId;

        var label = owners.Find(o => o.ContentId == viewOwner)?.Name ?? "?";
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Character##viewowner", label))
        {
            if (combo.Success)
            {
                foreach (var o in owners)
                {
                    var you = o.ContentId == current ? " (you)" : "";
                    if (ImGui.Selectable($"{o.Name}{you}##owner{o.ContentId}", o.ContentId == viewOwner))
                        viewOwner = o.ContentId;
                }
            }
        }

        ImGui.SameLine();
        var combine = plugin.Configuration.CombineCharacters;
        if (ImGui.Checkbox("Combine characters", ref combine))
        {
            plugin.Configuration.CombineCharacters = combine;
            Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: each character's items stay with that character's retainers.\nOn: rules can send items to other characters' retainers (those need trades).");

        return viewOwner;
    }

    // Items kept on several characters in small amounts, which could live on one
    private void DrawSpread()
    {
        ImGui.TextWrapped("Tradable items that more than one character holds, counting bags, saddlebags and retainers. Trade them to the character that has the most to bring them together.");

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##spreadsearch", "Search", ref spreadSearch, 100);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Only if each has at most", ref spreadMax, 10, 100))
            spreadMax = Math.Max(1, spreadMax);

        var kinds = new[] { LocationKind.Bags, LocationKind.Saddlebag, LocationKind.Retainer };
        var holdings = plugin.Store.Characters.Values
            .SelectMany(c => c.Locations.Values
                .Where(l => kinds.Contains(l.Kind))
                .SelectMany(l => l.Items)
                .Select(i => (Character: c, i.ItemId, i.Quantity)))
            .GroupBy(h => h.ItemId)
            .Select(g => (
                Item: plugin.Valuator.GetItem(g.Key),
                PerCharacter: g.GroupBy(h => h.Character.ContentId)
                    .Select(c => (Name: c.First().Character.Name, Quantity: c.Sum(h => h.Quantity)))
                    .OrderByDescending(c => c.Quantity)
                    .ToList()))
            .Where(r => r.PerCharacter.Count > 1 && !r.Item.Untradable)
            .Where(r => r.PerCharacter.All(c => c.Quantity <= spreadMax))
            .Where(r => spreadSearch.Length == 0 || r.Item.Name.Contains(spreadSearch, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.PerCharacter.Count)
            .ThenBy(r => r.Item.Name)
            .ToList();

        if (holdings.Count == 0)
        {
            ImGui.TextDisabled("Nothing is spread across characters.");
            return;
        }

        using var table = ImRaii.Table("spread", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Held by", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Bring to");
        ImGui.TableHeadersRow();
        foreach (var (item, perCharacter) in holdings)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Join(", ", perCharacter.Select(c => $"{c.Name} {c.Quantity}")));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(perCharacter[0].Name);
        }
    }

    private void DrawRules(List<RetainerRef> shown)
    {
        var config = plugin.Configuration;

        using (var list = ImRaii.Child("RetainerList", new Vector2(240 * ImGuiHelpers.GlobalScale, 0), true))
        {
            if (list.Success)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##retainersearch", "Search", ref retainerSearch, 50);
                foreach (var retainer in shown)
                {
                    if (retainerSearch.Length > 0 && !retainer.Label.Contains(retainerSearch, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var stacks = plan.StacksAfter.GetValueOrDefault(retainer.Id, retainer.Location.Items.Count);
                    var catchAll = config.RetainerRules.Find(r => r.RetainerId == retainer.Id)?.CatchAll == true ? " *" : "";
                    var unscanned = retainer.Scanned ? "" : " (not opened yet)";
                    if (ImGui.Selectable($"{retainer.Label}{catchAll}{unscanned}##{retainer.Id}", selected == retainer.Id))
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

        var current = shown.Find(r => r.Id == selected);
        if (current == null)
        {
            ImGui.TextWrapped("Pick a retainer to choose what it stores. Each character has its own rules; * marks a character's catch-all retainer that gets anything without a rule.");
            return;
        }

        ImGui.TextUnformatted(current.Label);
        var after = plan.StacksAfter.GetValueOrDefault(current.Id, current.Location.Items.Count);
        ImGui.TextUnformatted($"{current.Location.Items.Count} stacks now, about {after} after moves (of {Organizer.RetainerSlots})");
        if (after > Organizer.RetainerSlots)
            Colored(Warning, "These rules send more here than fits.");

        var rule = config.GetRule(current.Id);

        var isCatchAll = rule.CatchAll;
        if (ImGui.Checkbox($"Catch-all: store anything of {current.Owner.Name}'s without a rule here", ref isCatchAll))
        {
            // One catch-all per character
            foreach (var other in RulesOfOwner(current))
                other.CatchAll = false;
            rule.CatchAll = isCatchAll;
            Save();
        }

        if (ImGui.CollapsingHeader($"Groups stored here ({rule.Groups.Count})###rulegroups"))
            DrawRuleGroups(config, current, rule);

        if (ImGui.CollapsingHeader($"Categories stored here ({rule.UiCategories.Count})###rulecategories"))
            DrawRuleCategories(config, current, rule);

        if (ImGui.CollapsingHeader($"Single items stored here ({rule.ItemIds.Count})###ruleitems"))
            DrawRuleItems(rule);
    }

    private void DrawRuleGroups(Configuration config, RetainerRef current, RetainerRule rule)
    {
        ImGui.TextDisabled("Tick as many as you like.");
        foreach (var group in ItemGroups.All)
        {
            var owner = RulesOfOwner(current).FirstOrDefault(r => r.Groups.Contains(group));
            var here = owner?.RetainerId == current.Id;
            var elsewhere = owner != null && !here
                ? $" (now {retainers.Find(r => r.Id == owner.RetainerId)?.Name ?? "another retainer"})"
                : "";
            if (ImGui.Checkbox($"{ItemGroups.Label(group)}{elsewhere}##group{group}", ref here))
            {
                // A group lives in one retainer per character
                foreach (var other in RulesOfOwner(current))
                    other.Groups.Remove(group);
                if (here)
                    rule.Groups.Add(group);
                Save();
            }
        }
    }

    private void DrawRuleCategories(Configuration config, RetainerRef current, RetainerRule rule)
    {
        ImGui.TextDisabled("For what's left over, or to split a group.");
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

                    var owner = RulesOfOwner(current).FirstOrDefault(r => r.UiCategories.Contains(id));
                    var group = ItemGroups.GroupOfCategory(id, name);
                    var suffix = owner != null && owner.RetainerId != current.Id
                        ? $" (now {retainers.Find(r => r.Id == owner.RetainerId)?.Name ?? "another retainer"})"
                        : group != ItemGroup.None ? $" (in {ItemGroups.Label(group)})" : "";
                    if (ImGui.Selectable($"{name}{suffix}##addcat{id}", rule.UiCategories.Contains(id)))
                    {
                        // A category lives in one retainer per character
                        foreach (var other in RulesOfOwner(current))
                            other.UiCategories.Remove(id);
                        rule.UiCategories.Add(id);
                        Save();
                    }
                }
            }
        }
    }

    private void DrawRuleItems(RetainerRule rule)
    {
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

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##movesearch", "Search items or retainers", ref moveSearch, 100);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("##movefilter", ref moveFilter, (ReadOnlySpan<string>)MoveFilters, MoveFilters.Length);

        moves = moves.Where(m => moveFilter switch
            {
                1 => !m.From.IsBags && !m.To.IsBags,
                2 => m.From.IsBags,
                3 => m.To.IsBags,
                4 => m.NeedsTrade,
                _ => true,
            })
            .Where(m => moveSearch.Length == 0 ||
                        m.Item.Name.Contains(moveSearch, StringComparison.OrdinalIgnoreCase) ||
                        m.From.Label.Contains(moveSearch, StringComparison.OrdinalIgnoreCase) ||
                        m.To.Label.Contains(moveSearch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (moves.Count == 0)
            ImGui.TextDisabled("No moves match.");

        var index = 0;
        foreach (var group in moves.GroupBy(m => (m.From.Key, m.To.Key)).OrderBy(g => g.First().From.Label))
        {
            var first = group.First();
            var header = $"{first.From.Label} → {first.To.Label}{(first.NeedsTrade ? " (trade)" : "")}  [{group.Count()}]";
            using var node = ImRaii.TreeNode($"{header}##{first.From.Key}-{first.To.Key}");
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
        var choices = plugin.Configuration.CombineCharacters
            ? retainers
            : retainers.Where(r => r.Owner.ContentId == move.From.Owner.ContentId);
        foreach (var retainer in choices)
        {
            if (ImGui.Selectable($"{retainer.Label}##storein{retainer.Id}"))
            {
                foreach (var rule in RulesOfOwner(retainer))
                    rule.ItemIds.Remove(move.Item.Id);
                plugin.Configuration.GetRule(retainer.Id).ItemIds.Add(move.Item.Id);
                Save();
            }
        }

        if (ImGui.Selectable("Keep it on me"))
        {
            plugin.Configuration.CarryItemIds.Add(move.Item.Id);
            Save();
        }

        if (!move.From.IsBags && ImGui.Selectable("Leave it wherever it is"))
        {
            foreach (var rule in RulesOfOwner(move.From))
                rule.ItemIds.Remove(move.Item.Id);
            plugin.Configuration.CarryItemIds.Remove(move.Item.Id);
            plugin.Configuration.GetRule(move.From.Id).ItemIds.Add(move.Item.Id);
            Save();
        }
    }

    // Rules of every retainer that belongs to the same character as this one
    private IEnumerable<RetainerRule> RulesOfOwner(RetainerRef retainer)
    {
        var ids = retainers.Where(r => r.Owner.ContentId == retainer.Owner.ContentId).Select(r => r.Id).ToHashSet();
        return plugin.Configuration.RetainerRules.Where(r => ids.Contains(r.RetainerId)).ToList();
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
