using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MarketHelper.Inventory;
using MarketHelper.Listing;
using MarketHelper.Market;
using MarketHelper.Organizing;

namespace MarketHelper.Windows;

public class InventoryTab
{
    private static readonly string[] SortNames = ["Name", "Group", "Quantity", "Kept first"];

    private readonly Plugin plugin;
    private string bagSearch = string.Empty;
    private string armorySearch = string.Empty;
    private string saddleSearch = string.Empty;
    private int bagSort;

    public InventoryTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private Configuration Config => plugin.Configuration;

    public void Draw()
    {
        using var tabs = ImRaii.TabBar("InventorySubTabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("Keep on me"))
        {
            if (tab.Success)
                DrawCarry();
        }

        using (var tab = ImRaii.TabItem("Cleanup ideas"))
        {
            if (tab.Success)
                DrawCleanup();
        }

        using (var tab = ImRaii.TabItem("Armory cleanup"))
        {
            if (tab.Success)
                DrawArmory();
        }
    }

    private void DrawCarry()
    {
        ImGui.TextWrapped("Items on this list stay in your bags. Any of them found in a retainer show up in Retainers > Moves to be taken out.");

        var planBags = Config.PlanBagMoves;
        if (ImGui.Checkbox("Plan moves for everything else in my bags", ref planBags))
        {
            Config.PlanBagMoves = planBags;
            Config.Save();
        }

        if (ImGui.CollapsingHeader($"Groups to keep on me ({Config.CarryGroups.Count})###carrygroups"))
        {
            foreach (var group in ItemGroups.All)
            {
                var carried = Config.CarryGroups.Contains(group);
                if (ImGui.Checkbox($"{ItemGroups.Label(group)}##carry{group}", ref carried))
                {
                    if (carried)
                        Config.CarryGroups.Add(group);
                    else
                        Config.CarryGroups.Remove(group);
                    Config.Save();
                }
            }
        }

        if (ImGui.CollapsingHeader($"Single items to keep on me ({Config.CarryItemIds.Count})###carryitems"))
        {
            if (DrawRemovableList(Config.CarryItemIds, "carry"))
                Config.Save();

            if (Config.CarryItemIds.Count == 0)
                ImGui.TextDisabled("None yet. Tick items in your bags below, or right-click an item on the Valuation or Moves list.");
        }

        if (ImGui.CollapsingHeader($"One stack on me, topped up ({Config.TopUpItemIds.Count})###topupitems"))
        {
            ImGui.TextWrapped("One stack of each stays in your bags and Sort tops it up from your retainers. Extra stacks go to the retainer their rules name.");
            if (DrawRemovableList(Config.TopUpItemIds, "topup"))
                Config.Save();

            if (Config.TopUpItemIds.Count == 0)
                ImGui.TextDisabled("None yet. Tick \"1 stack\" next to an item in your bags below.");
        }

        if (ImGui.CollapsingHeader("In your bags now", ImGuiTreeNodeFlags.DefaultOpen))
            DrawBags();
    }

    private void DrawBags()
    {
        var player = Plugin.PlayerState;
        if (!plugin.Store.Characters.TryGetValue(player.ContentId, out var character) ||
            !character.Locations.TryGetValue(CharacterRecord.LocationKey(LocationKind.Bags), out var bags))
        {
            ImGui.TextDisabled("Your bags haven't been recorded yet.");
            return;
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##bagsearch", "Search", ref bagSearch, 100);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("Sort by##bagsort", ref bagSort, (ReadOnlySpan<string>)SortNames, SortNames.Length);

        var rows = bags.Items
            .GroupBy(s => s.ItemId)
            .Select(g => (Item: plugin.Valuator.GetItem(g.Key), Quantity: g.Sum(s => s.Quantity)))
            .Where(r => bagSearch.Length == 0 || r.Item.Name.Contains(bagSearch, StringComparison.OrdinalIgnoreCase));

        rows = bagSort switch
        {
            1 => rows.OrderBy(r => r.Item.Group == ItemGroup.None ? "~" : ItemGroups.Label(r.Item.Group)).ThenBy(r => r.Item.Name),
            2 => rows.OrderByDescending(r => r.Quantity).ThenBy(r => r.Item.Name),
            3 => rows.OrderByDescending(r => Organizer.IsCarried(r.Item, Config)).ThenBy(r => r.Item.Name),
            _ => rows.OrderBy(r => r.Item.Name),
        };

        using var child = ImRaii.Child("bagitems");
        foreach (var (item, quantity) in rows)
        {
            var byGroup = item.Group != ItemGroup.None && Config.CarryGroups.Contains(item.Group);
            var carried = Organizer.IsCarried(item, Config);
            var label = $"{item.Name} x{quantity}{(byGroup ? $" (kept with {ItemGroups.Label(item.Group)})" : "")}";
            using (ImRaii.Disabled(byGroup))
            {
                if (ImGui.Checkbox($"{label}##bag{item.Id}", ref carried))
                {
                    if (carried)
                        Config.CarryItemIds.Add(item.Id);
                    else
                        Config.CarryItemIds.Remove(item.Id);
                    Config.Save();
                }
            }

            if (item.StackSize <= 1 || carried)
                continue;

            ImGui.SameLine();
            var topUp = Config.TopUpItemIds.Contains(item.Id);
            if (ImGui.Checkbox($"1 stack##topup{item.Id}", ref topUp))
            {
                if (topUp)
                    Config.TopUpItemIds.Add(item.Id);
                else
                    Config.TopUpItemIds.Remove(item.Id);
                Config.Save();
            }
        }
    }

    public void DrawSaddlebag()
    {
        ImGui.TextWrapped("Items on this list live in your saddlebag. Sort takes them out of retainers and puts them in the saddlebag at the end; everything else in the saddlebag goes to its retainer.");

        var tour = plugin.RetainerTour;
        if (!InventoryScanner.SaddlebagLoaded())
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.3f, 1f), "Open your saddlebag once this session so it can be read and filled.");
        }
        else if (tour.IsRunning)
        {
            ImGui.TextUnformatted(tour.Status);
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##saddle"))
                tour.Stop();
        }
        else if (!GameUi.SaddlebagOpen())
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.3f, 1f), "Open your saddlebag to move items in or out of it. Moving them with it closed disconnects you.");
            if (tour.Status.Length > 0)
                ImGui.TextUnformatted(tour.Status);
        }
        else
        {
            if (ImGui.Button("Put kept items in the saddlebag"))
                tour.FillSaddlebagNow();
            ImGui.SameLine();
            if (ImGui.Button("Take everything else out"))
                tour.EmptySaddlebagNow();
            if (tour.Status.Length > 0)
                ImGui.TextUnformatted(tour.Status);
        }

        if (ImGui.CollapsingHeader($"Groups kept in the saddlebag ({Config.SaddlebagGroups.Count})###saddlegroups"))
        {
            foreach (var group in ItemGroups.All)
            {
                var kept = Config.SaddlebagGroups.Contains(group);
                if (ImGui.Checkbox($"{ItemGroups.Label(group)}##saddle{group}", ref kept))
                {
                    if (kept)
                        Config.SaddlebagGroups.Add(group);
                    else
                        Config.SaddlebagGroups.Remove(group);
                    Config.Save();
                }
            }
        }

        if (ImGui.CollapsingHeader($"Single items kept in the saddlebag ({Config.SaddlebagItemIds.Count})###saddleitems"))
        {
            if (DrawRemovableList(Config.SaddlebagItemIds, "saddle"))
                Config.Save();
            if (Config.SaddlebagItemIds.Count == 0)
                ImGui.TextDisabled("None yet. Tick items below.");
        }

        if (!plugin.Store.Characters.TryGetValue(Plugin.PlayerState.ContentId, out var character))
            return;

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##saddlesearch", "Search", ref saddleSearch, 100);

        if (ImGui.CollapsingHeader("In your saddlebag now", ImGuiTreeNodeFlags.DefaultOpen))
            DrawSaddlebagRows(character, LocationKind.Saddlebag);
        if (ImGui.CollapsingHeader("In your bags now###saddlebags"))
            DrawSaddlebagRows(character, LocationKind.Bags);
    }

    private void DrawSaddlebagRows(CharacterRecord character, LocationKind kind)
    {
        var place = character.Locations.GetValueOrDefault(CharacterRecord.LocationKey(kind));
        if (place == null)
        {
            ImGui.TextDisabled(kind == LocationKind.Saddlebag ? "Your saddlebag hasn't been recorded yet." : "Your bags haven't been recorded yet.");
            return;
        }

        if (kind == LocationKind.Saddlebag)
            ImGui.TextDisabled($"{place.Items.Count} of 70 slots used");

        var rows = place.Items
            .GroupBy(s => s.ItemId)
            .Select(g => (Item: plugin.Valuator.GetItem(g.Key), Quantity: g.Sum(s => s.Quantity)))
            .Where(r => saddleSearch.Length == 0 || r.Item.Name.Contains(saddleSearch, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => Organizer.IsSaddlebagged(r.Item, Config)).ThenBy(r => r.Item.Name);

        foreach (var (item, quantity) in rows)
        {
            var byGroup = item.Group != ItemGroup.None && Config.SaddlebagGroups.Contains(item.Group);
            var carried = Organizer.IsCarried(item, Config);
            var kept = Organizer.IsSaddlebagged(item, Config);
            var note = carried ? " (kept on you)" : byGroup ? $" (kept with {ItemGroups.Label(item.Group)})" : "";
            using (ImRaii.Disabled(byGroup || carried))
            {
                if (ImGui.Checkbox($"{item.Name} x{quantity}{note}##saddle{kind}{item.Id}", ref kept))
                {
                    if (kept)
                        Config.SaddlebagItemIds.Add(item.Id);
                    else
                        Config.SaddlebagItemIds.Remove(item.Id);
                    Config.Save();
                }
            }
        }
    }

    // Things worth getting rid of to free slots: kinds you only have a few of, and old items.
    // Items on any keep list are left out.
    private void DrawCleanup()
    {
        if (!plugin.Store.Characters.TryGetValue(Plugin.PlayerState.ContentId, out var character))
        {
            ImGui.TextDisabled("Log in to see suggestions for this character.");
            return;
        }

        ImGui.TextWrapped("Suggestions for freeing slots on this character. Sell, vendor or discard what you don't need; nothing here is moved for you.");

        var kinds = new[] { LocationKind.Bags, LocationKind.Saddlebag, LocationKind.Retainer };
        var held = character.Locations.Values
            .Where(l => kinds.Contains(l.Kind))
            .SelectMany(l => l.Items.Select(s => (Location: l, Stack: s)))
            .GroupBy(x => x.Stack.ItemId)
            .Select(g => (
                Item: plugin.Valuator.GetItem(g.Key),
                Quantity: g.Sum(x => x.Stack.Quantity),
                Stacks: g.Count(),
                First: g.First(),
                Where: string.Join(", ", g.Select(x => ValuationTab.LocationLabel(x.Location)).Distinct())))
            .Where(r => !IsKept(r.Item))
            .ToList();

        var small = Config.SmallQuantity;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Few: this many or fewer##smallqty", ref small, 1, 50))
        {
            Config.SmallQuantity = small;
            Config.Save();
        }

        var smallRows = held.Where(r => r.Item.StackSize > 1 && r.Quantity <= small)
            .OrderBy(r => r.Quantity).ThenBy(r => r.Item.Name).ToList();
        if (ImGui.CollapsingHeader($"Small amounts ({smallRows.Count} slots)###cleansmall", ImGuiTreeNodeFlags.DefaultOpen))
            DrawCleanupRows(character, smallRows.Select(r => (r.Item, r.Quantity, r.Stacks, r.First, r.Where)), "small");

        var level = Config.OldItemLevel;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Old: below item level##oldlevel", ref level, 50, 800))
        {
            Config.OldItemLevel = level;
            Config.Save();
        }

        var oldRows = held.Where(r => r.Item.ItemLevel > 1 && r.Item.ItemLevel < level)
            .OrderBy(r => r.Item.ItemLevel).ThenBy(r => r.Item.Name).ToList();
        if (ImGui.CollapsingHeader($"Old items ({oldRows.Sum(r => r.Stacks)} slots)###cleanold"))
            DrawCleanupRows(character, oldRows.Select(r => (r.Item, r.Quantity, r.Stacks, r.First, r.Where)), "old");
    }

    private bool IsKept(ItemInfo item) =>
        Config.KeepItemIds.Contains(item.Id) || Config.TopUpItemIds.Contains(item.Id) ||
        Organizer.IsCarried(item, Config) || Organizer.IsSaddlebagged(item, Config);

    private void DrawCleanupRows(CharacterRecord character,
        IEnumerable<(ItemInfo Item, int Quantity, int Stacks, (Location Location, ItemStack Stack) First, string Where)> rows, string id)
    {
        using var table = ImRaii.Table($"cleanup{id}", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                                       new System.Numerics.Vector2(0, 250 * ImGuiHelpers.GlobalScale));
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.WidthFixed, 40 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Worth");
        ImGui.TableSetupColumn("Where");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var (item, quantity, stacks, first, where) in rows)
        {
            var value = plugin.Valuator.EvaluateStack(character, first.Location, item, first.Stack.IsHq, quantity, Config);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stacks > 1 ? $"{quantity} ({stacks} slots)" : quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.ItemLevel.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(value.UnitPrice > 0 ? $"~{value.UnitPrice:N0} each ({value.Verdict})" : $"{value.Verdict}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(where);
        }
    }

    private void DrawArmory()
    {
        var cleaner = plugin.ArmoryCleaner;
        ImGui.TextWrapped("Items in your armory chest that none of your gearsets use. Soul crystals and items you keep in the armory are left alone.");

        var unused = ArmoryCleaner.Unused(Config.ArmoryKeepIds);
        var free = ArmoryCleaner.FreeBagSlots();
        ImGui.TextUnformatted($"{unused.Count} items not in a gearset. {free} free slots in your bags.");

        if (cleaner.IsRunning)
        {
            if (ImGui.Button("Stop"))
                cleaner.Stop();
        }
        else
        {
            using (ImRaii.Disabled(unused.Count == 0 || free == 0))
            {
                var count = Math.Min(unused.Count, free);
                if (ImGui.Button($"Move {count} to my bags"))
                    cleaner.MoveToBags(unused.Take(count));
            }
        }

        if (cleaner.Status.Length > 0)
            ImGui.TextUnformatted(cleaner.Status);

        if (ImGui.CollapsingHeader($"Kept in the armory ({Config.ArmoryKeepIds.Count})###armorykeep"))
        {
            if (DrawRemovableList(Config.ArmoryKeepIds, "armorykeep"))
                Config.Save();

            if (Config.ArmoryKeepIds.Count == 0)
                ImGui.TextDisabled("None yet. Right-click an item below to keep it in the armory.");
        }

        if (!ImGui.CollapsingHeader("To move", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##armorysearch", "Search", ref armorySearch, 100);

        using var child = ImRaii.Child("armoryitems");
        var index = 0;
        foreach (var slot in unused)
        {
            var name = plugin.Valuator.GetItem(slot.ItemId).Name;
            if (armorySearch.Length > 0 && !name.Contains(armorySearch, StringComparison.OrdinalIgnoreCase))
                continue;

            index++;
            ImGui.Selectable($"{name}{(slot.IsHq ? " (HQ)" : "")}##armory{index}");
            using var popup = ImRaii.ContextPopupItem($"armorymenu{index}");
            if (popup.Success && ImGui.Selectable("Keep this in the armory"))
            {
                Config.ArmoryKeepIds.Add(slot.ItemId);
                Config.Save();
            }
        }

        if (index == 0)
            ImGui.TextDisabled("Nothing to move.");
    }

    // Item names with a Remove button each; true when one was removed
    private bool DrawRemovableList(HashSet<uint> ids, string idPrefix)
    {
        uint? remove = null;
        foreach (var id in ids.OrderBy(id => plugin.Valuator.GetItem(id).Name))
        {
            if (ImGui.SmallButton($"Remove##{idPrefix}{id}"))
                remove = id;
            ImGui.SameLine();
            ImGui.TextUnformatted(plugin.Valuator.GetItem(id).Name);
        }

        return remove is { } removed && ids.Remove(removed);
    }
}
