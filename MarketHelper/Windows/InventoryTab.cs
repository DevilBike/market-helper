using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using MarketHelper.Inventory;
using MarketHelper.Market;
using MarketHelper.Organizing;

namespace MarketHelper.Windows;

public class InventoryTab
{
    private readonly Plugin plugin;
    private string search = string.Empty;

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

        ImGui.Spacing();
        ImGui.TextUnformatted("Groups to keep on me");
        ImGui.Separator();
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

        ImGui.Spacing();
        ImGui.TextUnformatted("Single items to keep on me");
        ImGui.Separator();
        uint? remove = null;
        foreach (var id in Config.CarryItemIds.OrderBy(id => plugin.Valuator.GetItem(id).Name))
        {
            if (ImGui.SmallButton($"Remove##carry{id}"))
                remove = id;
            ImGui.SameLine();
            ImGui.TextUnformatted(plugin.Valuator.GetItem(id).Name);
        }

        if (remove is { } removed)
        {
            Config.CarryItemIds.Remove(removed);
            Config.Save();
        }

        if (Config.CarryItemIds.Count == 0)
            ImGui.TextDisabled("None yet. Tick items in your bags below, or right-click an item on the Valuation or Moves list.");

        ImGui.Spacing();
        ImGui.TextUnformatted("In your bags now");
        ImGui.Separator();
        ImGui.InputTextWithHint("##bagsearch", "Search", ref search, 100);

        var player = Plugin.PlayerState;
        if (!plugin.Store.Characters.TryGetValue(player.ContentId, out var character) ||
            !character.Locations.TryGetValue(CharacterRecord.LocationKey(LocationKind.Bags), out var bags))
        {
            ImGui.TextDisabled("Your bags haven't been recorded yet.");
            return;
        }

        using var child = ImRaii.Child("bagitems");
        foreach (var id in bags.Items.Select(s => s.ItemId).Distinct())
        {
            var item = plugin.Valuator.GetItem(id);
            if (search.Length > 0 && !item.Name.Contains(search, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var byGroup = item.Group != ItemGroup.None && Config.CarryGroups.Contains(item.Group);
            var carried = Organizer.IsCarried(item, Config);
            var label = byGroup ? $"{item.Name} (kept with {ItemGroups.Label(item.Group)})" : item.Name;
            using (ImRaii.Disabled(byGroup))
            {
                if (ImGui.Checkbox($"{label}##bag{id}", ref carried))
                {
                    if (carried)
                        Config.CarryItemIds.Add(id);
                    else
                        Config.CarryItemIds.Remove(id);
                    Config.Save();
                }
            }
        }
    }

    private void DrawArmory()
    {
        var cleaner = plugin.ArmoryCleaner;
        ImGui.TextWrapped("Items in your armory chest that none of your gearsets use. Soul crystals are left alone.");

        var unused = ArmoryCleaner.Unused();
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
                var count = System.Math.Min(unused.Count, free);
                if (ImGui.Button($"Move {count} to my bags"))
                    cleaner.MoveToBags(unused.Take(count));
            }
        }

        if (cleaner.Status.Length > 0)
            ImGui.TextUnformatted(cleaner.Status);

        ImGui.Separator();
        using var child = ImRaii.Child("armoryitems");
        foreach (var slot in unused)
        {
            var name = plugin.Valuator.GetItem(slot.ItemId).Name;
            ImGui.BulletText(slot.IsHq ? $"{name} (HQ)" : name);
        }
    }
}
