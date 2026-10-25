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

    private static readonly string[] MoveFilters = ["All moves", "Between retainers", "From my bags or saddlebag", "To my bags or saddlebag", "Trades only"];

    private ulong selected;
    private string categorySearch = string.Empty;
    private string retainerSearch = string.Empty;
    private string moveSearch = string.Empty;
    private int moveFilter;

    // Whose retainers the tab shows; 0 means whoever is logged in
    private ulong viewOwner;
    private string spreadSearch = string.Empty;
    private int spreadMax = 99;

    // Sort waits on this preview until Go is pressed
    private SortPreview? pendingSort;
    private bool confirmCopy;

    // How many stacks the viewed character has per group and category, and the biggest categories without a rule
    private ulong sizesOwner;
    private Dictionary<ItemGroup, int> groupSizes = new();
    private Dictionary<uint, int> categorySizes = new();
    private List<(uint Id, int Stacks)> suggestions = new();

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
            CopyTemplateToNewCharacters();
            plan = plugin.Organizer.Plan(plugin.Store.Characters.Values, plugin.Configuration);
            sizesOwner = 0;
        }

        DrawTour();

        if (retainers.Count == 0)
        {
            ImGui.TextWrapped("No retainers recorded yet. Open each retainer's inventory once, on every character, and they'll show up here.");
            return;
        }

        DrawOpenRetainer();

        var owner = DrawCharacterPicker();
        if (sizesOwner != owner)
            ComputeSizes(owner);

        // In game order, with ignored retainers at the bottom
        var shown = retainers.Where(r => r.Owner.ContentId == owner)
            .OrderBy(r => IsIgnored(r.Id) || r.Location.Locked).ThenBy(r => r.Location.Order).ThenBy(r => r.Name)
            .ToList();
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

        if (pendingSort is { } preview)
        {
            DrawSortPreview(preview);
            return;
        }

        if (RetainerTour.CanStart)
        {
            if (ImGui.Button("Check all retainers"))
                tour.CheckAll();
            ImGui.SameLine();
            if (ImGui.Button("Sort this character's retainers"))
                pendingSort = tour.PreviewSort();
            ImGui.SameLine();
            var crystals = plugin.Configuration.StoreCrystals;
            if (ImGui.Checkbox("Store crystals too", ref crystals))
            {
                plugin.Configuration.StoreCrystals = crystals;
                plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shards, crystals and clusters go into the first retainer in the list, then the next once it's full.");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
            var limit = plugin.Configuration.BagStackLimit;
            if (ImGui.SliderInt("Bag limit", ref limit, 20, 140))
            {
                plugin.Configuration.BagStackLimit = limit;
                plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Sort stops taking items out of a retainer once this many bag slots are used, then goes to put them away. Your bags hold 140.");

            if (ImGui.Button("Top up crystals"))
                tour.TopUpCrystals();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Takes crystals back from the retainers holding the fewest of each kind first, until you have the amount set here.\nWhole stacks only, never past 9,999.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
            var crystalTarget = plugin.Configuration.CrystalTopUpTarget;
            if (ImGui.InputInt("of each##crystaltarget", ref crystalTarget, 100))
            {
                plugin.Configuration.CrystalTopUpTarget = Math.Clamp(crystalTarget, 1, 9999);
                plugin.Configuration.Save();
            }

            ImGui.SameLine();
            var topUpAfter = plugin.Configuration.TopUpCrystalsAfterSort;
            if (ImGui.Checkbox("Top up after sorting", ref topUpAfter))
            {
                plugin.Configuration.TopUpCrystalsAfterSort = topUpAfter;
                plugin.Configuration.Save();
            }

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

        if (ImGui.SmallButton("Copy sorting log"))
            ImGui.SetClipboardText(plugin.SortLog.Read());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"The last 10 runs, every visit and move with times. Paste it to Claude to tune the next version.\nSaved at {plugin.SortLog.FilePath}");
    }

    private void DrawSortPreview(SortPreview preview)
    {
        string Names(IEnumerable<ulong> ids) =>
            string.Join(", ", ids.Select(id => retainers.Find(r => r.Id == id)?.Name ?? "?"));

        Colored(Highlight, $"Sort will move {preview.Stacks} kinds of items for this character:");
        if (preview.TakeFrom.Count > 0)
            ImGui.TextWrapped($"Take out of: {Names(preview.TakeFrom)}");
        if (preview.PutInto.Count > 0)
            ImGui.TextWrapped($"Put into: {Names(preview.PutInto)}");
        if (preview.TopUps > 0)
            ImGui.TextWrapped($"Top up {preview.TopUps} stacks you keep one of on you.");
        if (preview.Crystals)
            ImGui.TextWrapped($"Crystals: into {Names(preview.Order.Take(1))} first, then down the list as retainers fill.");

        using (ImRaii.Disabled(!RetainerTour.CanStart))
        {
            if (ImGui.Button("Go"))
            {
                plugin.RetainerTour.RunSort(preview);
                pendingSort = null;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            pendingSort = null;
        ImGui.Separator();
    }

    // While a retainer is open, show exactly what to take out of it and put into it
    private void DrawOpenRetainer()
    {
        var open = InventoryScanner.OpenRetainerId();
        if (open == 0)
            return;

        var takeOut = plan.Moves.Where(m => m.From.IsRetainer && m.From.Id == open).ToList();
        var putIn = plan.Moves.Where(m => m.To.IsRetainer && m.To.Id == open && !m.NeedsTrade).ToList();
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
                ImGui.BulletText($"{ItemLabel(move)} from {move.From.Name}{(move.From.IsRetainer ? " (withdraw it there first)" : "")}");
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
                    var seen = retainer.Location.Locked ? " (locked: not paid for)"
                        : retainer.Scanned ? $" ({Age(retainer.Location.LastSeenUtc)})" : " (not opened yet)";
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), IsIgnored(retainer.Id) || retainer.Location.Locked))
                    {
                        if (ImGui.Selectable($"{retainer.Name}{catchAll}{seen}##{retainer.Id}", selected == retainer.Id))
                            selected = retainer.Id;
                    }

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

        var ignore = rule.Ignored;
        if (ImGui.Checkbox("Ignore this retainer (nothing is taken out or put in)", ref ignore))
        {
            rule.Ignored = ignore;
            Save();
        }

        if (rule.Ignored)
            return;

        DrawOverflow(current, rule);

        var full = after > Organizer.RetainerSlots;
        if (ImGui.CollapsingHeader($"Make room{(full ? $" ({after - Organizer.RetainerSlots} stacks too many)" : string.Empty)}###makeroom", full ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            DrawMakeRoom(current, rule, after);

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
        {
            DrawRuleCategories(config, current, rule);
            DrawSuggestions(current, rule);
        }

        if (ImGui.CollapsingHeader($"Single items stored here ({rule.ItemIds.Count})###ruleitems"))
            DrawRuleItems(rule);

        if (ImGui.CollapsingHeader("Copy to other characters###copyrules"))
            DrawCopyRules(current.Owner);
    }

    // What ends up in a retainer after the moves, split into the groups and categories that
    // could be handed to another retainer, biggest first; the best fit for the excess is marked
    private void DrawMakeRoom(RetainerRef current, RetainerRule rule, int after)
    {
        var leaving = plan.Moves.Where(m => m.From.IsRetainer && m.From.Id == current.Id)
            .Select(m => (m.Item.Id, m.IsHq)).ToHashSet();
        var staying = current.Location.Items
            .Where(s => !leaving.Contains((s.ItemId, s.IsHq)))
            .Select(s => plugin.Valuator.GetItem(s.ItemId));
        var arriving = plan.Moves.Where(m => m.To.IsRetainer && m.To.Id == current.Id)
            .SelectMany(m => Enumerable.Repeat(m.Item, Math.Max(1, m.From.Location.Items.Count(s => s.ItemId == m.Item.Id && s.IsHq == m.IsHq))));

        // Each option is a category or a whole group; single items are left alone
        var options = new Dictionary<(ItemGroup Group, uint Category), int>();
        foreach (var item in staying.Concat(arriving))
        {
            if (rule.ItemIds.Contains(item.Id))
                continue;

            if (!rule.UiCategories.Contains(item.UiCategory) && item.Group != ItemGroup.None && rule.Groups.Contains(item.Group))
                options[(item.Group, 0)] = options.GetValueOrDefault((item.Group, 0)) + 1;
            if (item.UiCategory != 0)
                options[(ItemGroup.None, item.UiCategory)] = options.GetValueOrDefault((ItemGroup.None, item.UiCategory)) + 1;
        }

        if (options.Count == 0)
        {
            ImGui.TextDisabled("Nothing here that a category or group rule could move.");
            return;
        }

        var excess = after - Organizer.RetainerSlots;
        var bestFit = excess > 0
            ? options.Where(o => o.Value >= excess).OrderBy(o => o.Value).Select(o => o.Key).FirstOrDefault((ItemGroup.None, uint.MaxValue))
            : (ItemGroup.None, uint.MaxValue);

        var others = retainers
            .Where(r => r.Owner.ContentId == current.Owner.ContentId && r.Id != current.Id && !IsIgnored(r.Id) && !r.Location.Locked)
            .OrderBy(r => r.Location.Order).ThenBy(r => r.Name)
            .ToList();

        ImGui.TextDisabled(excess > 0
            ? $"Move one of these to a retainer with room to free at least {excess} stacks. The marked one is the smallest that's enough."
            : "Biggest things stored here, if you want to free up space.");

        foreach (var ((group, category), stacks) in options.OrderByDescending(o => o.Value).Take(12))
        {
            var isGroup = category == 0;
            var name = isGroup ? $"{ItemGroups.Label(group)} (group)" : CategoryName(category);
            var groupNote = !isGroup && !rule.UiCategories.Contains(category) && ItemInGroupHere(category, rule) is { } g
                ? $", part of {ItemGroups.Label(g)}" : string.Empty;

            ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
            using (var combo = ImRaii.Combo($"##makeroom{group}{category}", "Move to..."))
            {
                if (combo.Success)
                {
                    foreach (var other in others)
                    {
                        var free = Organizer.RetainerSlots - plan.StacksAfter.GetValueOrDefault(other.Id, other.Location.Items.Count);
                        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), free < stacks);
                        if (!ImGui.Selectable($"{other.Name} ({Math.Max(0, free)} free)##makeroomto{other.Id}"))
                            continue;

                        // Categories come before groups and the catch-all, so a category rule there wins
                        var target = plugin.Configuration.GetRule(other.Id);
                        foreach (var owned in RulesOfOwner(current))
                        {
                            if (isGroup)
                                owned.Groups.Remove(group);
                            else
                                owned.UiCategories.Remove(category);
                        }

                        if (isGroup)
                            target.Groups.Add(group);
                        else
                            target.UiCategories.Add(category);
                        Save();
                    }
                }
            }

            ImGui.SameLine();
            var label = $"{name} ({stacks} stacks{groupNote})";
            if ((group, category) == bestFit)
                Colored(Highlight, $"{label}  <- enough on its own");
            else
                ImGui.TextUnformatted(label);
        }
    }

    private ItemGroup? ItemInGroupHere(uint category, RetainerRule rule)
    {
        var group = ItemGroups.GroupOfCategory(category, CategoryName(category));
        return group != ItemGroup.None && rule.Groups.Contains(group) ? group : null;
    }

    private void DrawOverflow(RetainerRef current, RetainerRule rule)
    {
        var others = retainers.Where(r => r.Owner.ContentId == current.Owner.ContentId && r.Id != current.Id && !IsIgnored(r.Id)).ToList();
        var label = others.Find(r => r.Id == rule.OverflowRetainerId)?.Name ?? "None";
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("When full, overflow into", label);
        if (!combo.Success)
            return;

        if (ImGui.Selectable("None", rule.OverflowRetainerId == 0))
        {
            rule.OverflowRetainerId = 0;
            Save();
        }

        foreach (var other in others)
        {
            if (ImGui.Selectable($"{other.Name}##overflow{other.Id}", rule.OverflowRetainerId == other.Id))
            {
                rule.OverflowRetainerId = other.Id;
                Save();
            }
        }
    }

    // Copies each retainer's rules to the retainer in the same list position on other characters
    private void DrawCopyRules(CharacterRecord source)
    {
        var config = plugin.Configuration;
        ImGui.TextWrapped($"The first retainer in {source.Name}'s list gets copied to the first retainer of each other character, the second to the second, and so on. This replaces their rules.");

        if (ImGui.Button(confirmCopy ? "Click again to replace the other characters' rules" : "Copy to all other characters"))
        {
            if (confirmCopy)
            {
                foreach (var target in retainers.Select(r => r.Owner).DistinctBy(o => o.ContentId).Where(o => o.ContentId != source.ContentId))
                    CopyRules(source, target);
                confirmCopy = false;
                Save();
            }
            else
            {
                confirmCopy = true;
            }
        }

        var auto = config.TemplateOwnerId == source.ContentId;
        if (ImGui.Checkbox("Also copy to characters that have no rules yet, automatically", ref auto))
        {
            config.TemplateOwnerId = auto ? source.ContentId : 0;
            Save();
        }
    }

    private void CopyRules(CharacterRecord source, CharacterRecord target)
    {
        var config = plugin.Configuration;
        var from = InOrder(source);
        var to = InOrder(target);
        for (var i = 0; i < to.Count; i++)
        {
            config.RetainerRules.RemoveAll(r => r.RetainerId == to[i].Id);
            if (i >= from.Count)
                continue;

            var rule = config.RetainerRules.Find(r => r.RetainerId == from[i].Id);
            if (rule == null)
                continue;

            var overflowIndex = from.FindIndex(r => r.Id == rule.OverflowRetainerId);
            var overflow = overflowIndex >= 0 && overflowIndex < to.Count ? to[overflowIndex].Id : 0;
            config.RetainerRules.Add(rule.CopyFor(to[i].Id, overflow));
        }
    }

    private void CopyTemplateToNewCharacters()
    {
        var config = plugin.Configuration;
        if (config.TemplateOwnerId == 0)
            return;

        var owners = retainers.Select(r => r.Owner).DistinctBy(o => o.ContentId).ToList();
        var source = owners.Find(o => o.ContentId == config.TemplateOwnerId);
        if (source == null)
            return;

        var copied = false;
        foreach (var target in owners.Where(o => o.ContentId != source.ContentId))
        {
            var ids = InOrder(target).Select(r => r.Id).ToHashSet();
            if (config.RetainerRules.Any(r => ids.Contains(r.RetainerId) && r.HasRules))
                continue;

            CopyRules(source, target);
            copied = true;
        }

        if (copied)
            config.Save();
    }

    private List<RetainerRef> InOrder(CharacterRecord owner) =>
        retainers.Where(r => r.Owner.ContentId == owner.ContentId).OrderBy(r => r.Location.Order).ThenBy(r => r.Name).ToList();

    private bool IsIgnored(ulong retainerId) =>
        plugin.Configuration.RetainerRules.Find(r => r.RetainerId == retainerId)?.Ignored == true;

    private static string Age(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age.TotalMinutes < 1)
            return "just now";
        if (age.TotalHours < 1)
            return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1)
            return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    // Stack counts per group and category over the character's bags, saddlebags and retainers
    private void ComputeSizes(ulong owner)
    {
        sizesOwner = owner;
        groupSizes = new Dictionary<ItemGroup, int>();
        categorySizes = new Dictionary<uint, int>();
        if (!plugin.Store.Characters.TryGetValue(owner, out var character))
        {
            suggestions = new List<(uint, int)>();
            return;
        }

        var kinds = new[] { LocationKind.Bags, LocationKind.Saddlebag, LocationKind.Retainer };
        var items = character.Locations.Values
            .Where(l => kinds.Contains(l.Kind))
            .SelectMany(l => l.Items)
            .Select(i => plugin.Valuator.GetItem(i.ItemId))
            .ToList();

        foreach (var item in items)
        {
            if (item.Group != ItemGroup.None)
                groupSizes[item.Group] = groupSizes.GetValueOrDefault(item.Group) + 1;
            categorySizes[item.UiCategory] = categorySizes.GetValueOrDefault(item.UiCategory) + 1;
        }

        var ids = retainers.Where(r => r.Owner.ContentId == owner).Select(r => r.Id).ToHashSet();
        var rules = plugin.Configuration.RetainerRules.Where(r => ids.Contains(r.RetainerId) && !r.Ignored).ToList();
        suggestions = items
            .Where(i => i.UiCategory != 0 && !Organizer.IsCarried(i, plugin.Configuration))
            .Where(i => !rules.Any(r => r.ItemIds.Contains(i.Id) || r.UiCategories.Contains(i.UiCategory) ||
                                        (i.Group != ItemGroup.None && r.Groups.Contains(i.Group))))
            .GroupBy(i => i.UiCategory)
            .Select(g => (g.Key, g.Count()))
            .Where(c => c.Item2 >= 3)
            .OrderByDescending(c => c.Item2)
            .Take(8)
            .ToList();
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
            var size = groupSizes.GetValueOrDefault(group);
            if (ImGui.Checkbox($"{ItemGroups.Label(group)} ({size} stacks){elsewhere}##group{group}", ref here))
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
            ImGui.TextUnformatted($"{CategoryName(id)} ({categorySizes.GetValueOrDefault(id)} stacks)");
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
                    var size = categorySizes.GetValueOrDefault(id);
                    if (ImGui.Selectable($"{name} ({size}){suffix}##addcat{id}", rule.UiCategories.Contains(id)))
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

    // The biggest categories this character has that no rule covers yet
    private void DrawSuggestions(RetainerRef current, RetainerRule rule)
    {
        if (suggestions.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextDisabled("Biggest categories without a rule:");
        foreach (var (id, stacks) in suggestions)
        {
            if (ImGui.SmallButton($"Add##suggest{id}"))
            {
                foreach (var other in RulesOfOwner(current))
                    other.UiCategories.Remove(id);
                rule.UiCategories.Add(id);
                Save();
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{CategoryName(id)} ({stacks} stacks)");
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
                1 => m.From.IsRetainer && m.To.IsRetainer,
                2 => !m.From.IsRetainer,
                3 => !m.To.IsRetainer,
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

        if (move.From.IsRetainer && ImGui.Selectable("Leave it wherever it is"))
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
        sizesOwner = 0;
        Invalidate();
    }

    private string CategoryName(uint id) =>
        categories.Find(c => c.Id == id).Name ?? $"Category {id}";

    private static void Colored(Vector4 color, string text) => ImGui.TextColored(in color, text);

    private static string ItemLabel(Move move) =>
        $"{move.Item.Name}{(move.IsHq ? " (HQ)" : "")} x{move.Quantity}";
}
