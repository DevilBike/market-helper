using System.Collections.Generic;
using System.Linq;
using MarketHelper.Inventory;
using MarketHelper.Market;

namespace MarketHelper.Organizing;

public record RetainerRef(CharacterRecord Owner, Location Location)
{
    public ulong Id => Location.RetainerId;
    public string Name => Location.RetainerName;
    public string Label => $"{Location.RetainerName} ({Owner.Name})";
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
        var retainers = Retainers(characters);
        var byId = retainers.ToDictionary(r => r.Id);
        var moves = new List<Move>();

        foreach (var from in retainers)
        {
            foreach (var group in from.Location.Items.GroupBy(s => (s.ItemId, s.IsHq)))
            {
                var item = valuator.GetItem(group.Key.ItemId);
                var target = TargetFor(item, config);
                if (target == 0 || target == from.Id || !byId.TryGetValue(target, out var to))
                    continue;

                // Items that can't be traded can't change owners
                if (from.Owner.ContentId != to.Owner.ContentId && item.Untradable)
                    continue;

                moves.Add(new Move(from, to, item, group.Key.IsHq, group.Sum(s => s.Quantity)));
            }
        }

        // Rough stack count per retainer after the moves, to warn about full retainers
        var stacksAfter = retainers.ToDictionary(r => r.Id, r => r.Location.Items.Count);
        var arrived = new HashSet<(ulong, uint, bool)>();
        foreach (var move in moves)
        {
            var leaving = move.From.Location.Items.Count(s => s.ItemId == move.Item.Id && s.IsHq == move.IsHq);
            stacksAfter[move.From.Id] -= leaving;

            var alreadyThere = move.To.Location.Items.Any(s => s.ItemId == move.Item.Id && s.IsHq == move.IsHq);
            if (!alreadyThere && arrived.Add((move.To.Id, move.Item.Id, move.IsHq)))
                stacksAfter[move.To.Id] += 1;
        }

        return new MovePlan(moves, stacksAfter);
    }
}
