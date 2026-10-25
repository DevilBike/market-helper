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
    public bool IsSaddlebag => Location.Kind == LocationKind.Saddlebag;
    public bool IsRetainer => Location.Kind == LocationKind.Retainer;
    public string Name => IsBags ? "your bags" : IsSaddlebag ? "your saddlebag" : Location.RetainerName;
    public string Label => IsBags ? $"Bags ({Owner.Name})" : IsSaddlebag ? $"Saddlebag ({Owner.Name})" : $"{Location.RetainerName} ({Owner.Name})";
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
            .OrderBy(r => r.Owner.Name).ThenBy(r => r.Location.Order).ThenBy(r => r.Name)
            .ToList();

    public static bool IsCarried(ItemInfo item, Configuration config) =>
        config.CarryItemIds.Contains(item.Id) ||
        (item.Group != ItemGroup.None && config.CarryGroups.Contains(item.Group));

    public static bool IsSaddlebagged(ItemInfo item, Configuration config) =>
        !IsCarried(item, config) &&
        (config.SaddlebagItemIds.Contains(item.Id) ||
         (item.Group != ItemGroup.None && config.SaddlebagGroups.Contains(item.Group)));

    // Most specific wins: single item, then category, then broad group, then a catch-all.
    // Among equally specific rules, a retainer of the item's own character wins.
    public static ulong TargetFor(ItemInfo item, Configuration config, ICollection<ulong> allowed, ICollection<ulong> own)
    {
        var levels = new System.Func<RetainerRule, bool>[]
        {
            r => r.ItemIds.Contains(item.Id),
            r => r.UiCategories.Contains(item.UiCategory),
            r => item.Group != ItemGroup.None && r.Groups.Contains(item.Group),
            r => r.CatchAll,
        };

        foreach (var matches in levels)
        {
            ulong elsewhere = 0;
            foreach (var rule in config.RetainerRules)
            {
                if (!allowed.Contains(rule.RetainerId) || !matches(rule))
                    continue;
                if (own.Contains(rule.RetainerId))
                    return rule.RetainerId;
                if (elsewhere == 0)
                    elsewhere = rule.RetainerId;
            }

            if (elsewhere != 0)
                return elsewhere;
        }

        return 0;
    }

    public MovePlan Plan(IEnumerable<CharacterRecord> characters, Configuration config)
    {
        var characterList = characters.ToList();
        var retainers = Retainers(characterList);
        var byId = retainers.ToDictionary(r => r.Id);
        var moves = new List<Move>();

        // Ignored retainers are neither emptied nor filled
        // Retainers that aren't paid for can't be opened, so they count as ignored too
        var ignored = config.RetainerRules.Where(r => r.Ignored).Select(r => r.RetainerId).ToHashSet();
        ignored.UnionWith(retainers.Where(r => r.Location.Locked).Select(r => r.Id));
        var active = retainers.Where(r => !ignored.Contains(r.Id)).ToList();
        var allIds = active.Select(r => r.Id).ToHashSet();
        var idsByOwner = active.GroupBy(r => r.Owner.ContentId).ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToHashSet());
        ulong Target(ItemInfo item, CharacterRecord owner)
        {
            var own = idsByOwner.GetValueOrDefault(owner.ContentId) ?? new HashSet<ulong>();
            return TargetFor(item, config, config.CombineCharacters ? allIds : own, own);
        }

        // Items on the keep-on-me list come back to their owner's bags
        RetainerRef PlaceOf(CharacterRecord owner, LocationKind kind) => new(owner,
            owner.Locations.GetValueOrDefault(CharacterRecord.LocationKey(kind))
            ?? new Location { Kind = kind });
        RetainerRef BagsOf(CharacterRecord owner) => PlaceOf(owner, LocationKind.Bags);

        foreach (var from in active)
        {
            foreach (var group in from.Location.Items.GroupBy(s => (s.ItemId, s.IsHq)))
            {
                var item = valuator.GetItem(group.Key.ItemId);
                if (IsCarried(item, config) || IsSaddlebagged(item, config))
                {
                    moves.Add(new Move(from, BagsOf(from.Owner), item, group.Key.IsHq, group.Sum(s => s.Quantity)));
                    continue;
                }

                var target = Target(item, from.Owner);
                if (target == 0 || target == from.Id || !byId.TryGetValue(target, out var to))
                    continue;

                // Items that can't be traded can't change owners
                if (from.Owner.ContentId != to.Owner.ContentId && item.Untradable)
                    continue;

                moves.Add(new Move(from, to, item, group.Key.IsHq, group.Sum(s => s.Quantity)));
            }
        }

        // Everything else in your bags and saddlebag goes to the retainer its rules name;
        // items kept in the saddlebag go there from your bags
        if (config.PlanBagMoves)
        {
            foreach (var owner in characterList)
            {
                foreach (var kind in new[] { LocationKind.Bags, LocationKind.Saddlebag })
                {
                    if (!owner.Locations.TryGetValue(CharacterRecord.LocationKey(kind), out var place))
                        continue;

                    var from = new RetainerRef(owner, place);
                    foreach (var group in place.Items.GroupBy(s => (s.ItemId, s.IsHq)))
                    {
                        var item = valuator.GetItem(group.Key.ItemId);
                        var quantity = group.Sum(s => s.Quantity);
                        if (IsCarried(item, config))
                        {
                            if (kind == LocationKind.Saddlebag)
                                moves.Add(new Move(from, BagsOf(owner), item, group.Key.IsHq, quantity));
                            continue;
                        }

                        if (IsSaddlebagged(item, config))
                        {
                            if (kind == LocationKind.Bags)
                                moves.Add(new Move(from, PlaceOf(owner, LocationKind.Saddlebag), item, group.Key.IsHq, quantity));
                            continue;
                        }

                        var target = Target(item, owner);
                        if (target == 0 || !byId.TryGetValue(target, out var to))
                            continue;
                        if (from.Owner.ContentId != to.Owner.ContentId && item.Untradable)
                            continue;

                        moves.Add(new Move(from, to, item, group.Key.IsHq, quantity));
                    }
                }
            }
        }

        ApplyOverflow(retainers, byId, moves, config);
        return new MovePlan(moves, CountStacks(retainers, moves));
    }

    // Rough stack count per retainer after the moves, to warn about full retainers
    private static Dictionary<ulong, int> CountStacks(List<RetainerRef> retainers, List<Move> moves)
    {
        var stacksAfter = retainers.ToDictionary(r => r.Id, r => r.Location.Items.Count);
        var arrived = new HashSet<(ulong, uint, bool)>();
        foreach (var move in moves)
        {
            if (move.From.IsRetainer)
                stacksAfter[move.From.Id] -= move.From.Location.Items.Count(s => s.ItemId == move.Item.Id && s.IsHq == move.IsHq);

            if (move.To.IsRetainer && !AlreadyThere(move) && arrived.Add((move.To.Id, move.Item.Id, move.IsHq)))
                stacksAfter[move.To.Id] += 1;
        }

        return stacksAfter;
    }

    private static bool AlreadyThere(Move move) =>
        move.To.Location.Items.Any(s => s.ItemId == move.Item.Id && s.IsHq == move.IsHq);

    // A full retainer with an overflow retainer leaves what's already in the overflow there,
    // then sends new stacks to the overflow until it fits
    private static void ApplyOverflow(List<RetainerRef> retainers, Dictionary<ulong, RetainerRef> byId, List<Move> moves, Configuration config)
    {
        foreach (var rule in config.RetainerRules.Where(r => r.OverflowRetainerId != 0 && !r.Ignored))
        {
            if (!byId.TryGetValue(rule.RetainerId, out var full) || !byId.TryGetValue(rule.OverflowRetainerId, out var overflow))
                continue;
            if (CountStacks(retainers, moves).GetValueOrDefault(full.Id) <= RetainerSlots)
                continue;

            moves.RemoveAll(m => m.To.IsRetainer && m.To.Id == full.Id && m.From.IsRetainer && m.From.Id == overflow.Id);

            var excess = CountStacks(retainers, moves)[full.Id] - RetainerSlots;
            for (var i = moves.Count - 1; i >= 0 && excess > 0; i--)
            {
                var move = moves[i];
                if (!move.To.IsRetainer || move.To.Id != full.Id || AlreadyThere(move))
                    continue;
                if (move.From.Owner.ContentId != overflow.Owner.ContentId && move.Item.Untradable)
                    continue;

                moves[i] = move with { To = overflow };
                excess--;
            }
        }
    }
}
