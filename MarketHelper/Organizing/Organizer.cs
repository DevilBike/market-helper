using System.Collections.Generic;
using System.Linq;
using MarketHelper.Inventory;
using MarketHelper.Market;

namespace MarketHelper.Organizing;

// A place items can be moved between: one of a character's retainers, or their bags
public record RetainerRef(CharacterRecord Owner, Location Location)
{
    public ulong Id => Location.RetainerId;
    public bool IsBags => Location.Kind == LocationKind.Bags;
    public string Name => IsBags ? "your bags" : Location.RetainerName;
    public string Label => IsBags ? $"Bags ({Owner.Name})" : $"{Location.RetainerName} ({Owner.Name})";
    public string Key => $"{Owner.ContentId}:{Location.Kind}:{Location.RetainerId}";
    public bool Scanned => Location.UpdatedUtc != System.DateTime.MinValue;
}

public record Move(RetainerRef From, RetainerRef To, ItemInfo Item, bool IsHq, int Quantity)
{
    // Different characters own the two retainers, so it needs a trade
    public bool NeedsTrade => From.Owner.ContentId != To.Owner.ContentId;
}

public record MovePlan(List<Move> Moves, Dictionary<ulong, int> StacksAfter);

// Works out which retainer each item belongs in, from the rules in settings.
public class Organizer
{
    public const int RetainerSlots = 175;

    private readonly Valuator valuator;

    public Organizer(Valuator valuator)
    {
        this.valuator = valuator;
    }

    public static List<RetainerRef> Retainers(IEnumerable<CharacterRecord> characters) =>
        characters
            .SelectMany(c => c.Locations.Values
                .Where(l => l.Kind == LocationKind.Retainer)
                .Select(l => new RetainerRef(c, l)))
            .OrderBy(r => r.Owner.Name).ThenBy(r => r.Name)
            .ToList();

    public static bool IsCarried(ItemInfo item, Configuration config) =>
        config.CarryItemIds.Contains(item.Id) ||
        (item.Group != ItemGroup.None && config.CarryGroups.Contains(item.Group));

    // Most specific wins: single item, then category, then broad group, then the catch-all
    public static ulong TargetFor(ItemInfo item, Configuration config)
    {
        foreach (var rule in config.RetainerRules)
        {
            if (rule.ItemIds.Contains(item.Id))
                return rule.RetainerId;
        }

        foreach (var rule in config.RetainerRules)
        {
            if (rule.UiCategories.Contains(item.UiCategory))
                return rule.RetainerId;
        }

        if (item.Group != ItemGroup.None)
        {
            foreach (var rule in config.RetainerRules)
            {
                if (rule.Groups.Contains(item.Group))
                    return rule.RetainerId;
            }
        }

        return config.CatchAllRetainerId;
    }

    public MovePlan Plan(IEnumerable<CharacterRecord> characters, Configuration config)
    {
        var characterList = characters.ToList();
        var retainers = Retainers(characterList);
        var byId = retainers.ToDictionary(r => r.Id);
        var moves = new List<Move>();

        // Items on the keep-on-me list come back to their owner's bags
        RetainerRef BagsOf(CharacterRecord owner) => new(owner,
            owner.Locations.GetValueOrDefault(CharacterRecord.LocationKey(LocationKind.Bags))
            ?? new Location { Kind = LocationKind.Bags });

        foreach (var from in retainers)
        {
            foreach (var group in from.Location.Items.GroupBy(s => (s.ItemId, s.IsHq)))
            {
                var item = valuator.GetItem(group.Key.ItemId);
                if (IsCarried(item, config))
                {
                    moves.Add(new Move(from, BagsOf(from.Owner), item, group.Key.IsHq, group.Sum(s => s.Quantity)));
                    continue;
                }

                var target = TargetFor(item, config);
                if (target == 0 || target == from.Id || !byId.TryGetValue(target, out var to))
                    continue;

                // Items that can't be traded can't change owners
                if (from.Owner.ContentId != to.Owner.ContentId && item.Untradable)
                    continue;

                moves.Add(new Move(from, to, item, group.Key.IsHq, group.Sum(s => s.Quantity)));
            }
        }

        // Everything else in your bags goes to the retainer its rules name
        if (config.PlanBagMoves)
        {
            foreach (var owner in characterList)
            {
                if (!owner.Locations.TryGetValue(CharacterRecord.LocationKey(LocationKind.Bags), out var bags))
                    continue;

                var from = new RetainerRef(owner, bags);
                foreach (var group in bags.Items.GroupBy(s => (s.ItemId, s.IsHq)))
                {
                    var item = valuator.GetItem(group.Key.ItemId);
                    if (IsCarried(item, config))
                        continue;

                    var target = TargetFor(item, config);
                    if (target == 0 || !byId.TryGetValue(target, out var to))
                        continue;
                    if (from.Owner.ContentId != to.Owner.ContentId && item.Untradable)
                        continue;

                    moves.Add(new Move(from, to, item, group.Key.IsHq, group.Sum(s => s.Quantity)));
                }
            }
        }

        // Rough stack count per retainer after the moves, to warn about full retainers
        var stacksAfter = retainers.ToDictionary(r => r.Id, r => r.Location.Items.Count);
        var arrived = new HashSet<(ulong, uint, bool)>();
        foreach (var move in moves)
        {
            if (!move.From.IsBags)
                stacksAfter[move.From.Id] -= move.From.Location.Items.Count(s => s.ItemId == move.Item.Id && s.IsHq == move.IsHq);

            var alreadyThere = move.To.Location.Items.Any(s => s.ItemId == move.Item.Id && s.IsHq == move.IsHq);
            if (!move.To.IsBags && !alreadyThere && arrived.Add((move.To.Id, move.Item.Id, move.IsHq)))
                stacksAfter[move.To.Id] += 1;
        }

        return new MovePlan(moves, stacksAfter);
    }
}
