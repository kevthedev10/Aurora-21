using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// One item a pack can hand out besides a player. <see cref="Kind"/> is the FIFA item type (a badge is <c>custom</c>),
/// <see cref="Pool"/> the drop pool, <see cref="Tier"/> the pack tier its rating falls in.
/// </summary>
public sealed record NonPlayerDrop(string Pool, string Kind, long Resource, long Rating, long Rareflag, string Tier, string Name,
    long Asset = 0, long Team = 0, long KitType = 0);

/// <summary>
/// Every non-player item packs hand out, and the one way they are drawn (rules/pack_odds.json nonPlayerDrops): a pool by
/// its weight among the pools asked for that have a candidate, then an item, a rare one at rareWeight of a common one's
/// chance. Rarity only sets how often an item drops and how it looks; a pack's rare slots are its rare players.
/// </summary>
public sealed class NonPlayerDropTable
{
    public const string ChemistryStyle = "chemistry_style";
    public const string PositionModifier = "position_modifier";
    public const string ManagerLeague = "manager_league";
    public const string Kit = "kit";
    public const string Ball = "ball";
    public const string Badge = "badge";
    public const string Stadium = "stadium";
    public const string Manager = "manager";

    public static readonly string[] Consumables = [ChemistryStyle, PositionModifier, ManagerLeague];
    public static readonly string[] Cosmetics = [Kit, Ball, Badge, Stadium];
    public static readonly string[] Staff = [Manager];

    private readonly NonPlayerDropRules _rules;
    private readonly Dictionary<string, NonPlayerDrop[]> _pools;

    public NonPlayerDropTable(NonPlayerDropRules rules, IEnumerable<NonPlayerDrop> items)
    {
        _rules = rules;
        _pools = items.GroupBy(item => item.Pool).ToDictionary(group => group.Key, group => group.OrderBy(item => item.Resource).ToArray());
    }

    public static NonPlayerDropTable Build(ContentSnapshot snapshot)
    {
        var rules = snapshot.Rules;
        string Tier(long rating) => rules.Packs.TierFor(rating);
        var items = new List<NonPlayerDrop>();
        var styles = rules.Cards.ChemistryStyles;
        // Basic and GK Basic are what a player plays as without a style card, so no pack hands them out.
        foreach (var style in snapshot.Cards.ChemistryStyles.Where(style => style.PlayStyle != styles.OutfieldDefault && style.PlayStyle != styles.GoalkeeperDefault))
            items.Add(new(ChemistryStyle, "consumable", style.ResourceId, styles.Rating, styles.RareStyles.Contains(style.PlayStyle) ? 1 : 0,
                Tier(styles.Rating), style.Name));
        foreach (var modifier in snapshot.Cards.PositionModifiers)
            items.Add(new(PositionModifier, "consumable", modifier.ResourceId, modifier.Rating, 1, Tier(modifier.Rating), $"{modifier.Source} → {modifier.Target}"));
        var leagueRating = rules.Cards.ManagerLeagueCards.Rating;
        foreach (var card in snapshot.Cards.ManagerLeagueCards)
            items.Add(new(ManagerLeague, "consumable", card.ResourceId, leagueRating, 1, Tier(leagueRating), card.Name));
        foreach (var (resource, manager) in snapshot.Managers.Entries.Where(entry => entry.Value.Int("leagueId") > 0))
        {
            var rating = manager.Int("rating");
            items.Add(new(Manager, "manager", resource, rating, manager.IntOr("rareflag", rating >= rules.Cards.ManagerRareFromRating ? 1 : 0),
                Tier(rating), manager.Text("name")));
        }
        foreach (var cosmetic in snapshot.Catalog.Customisations.Where(c => (c.CustomisationKind != "kit" || c.PackEligible) && c.Rating is not null && c.NativeRareflag is not null))
        {
            var definition = snapshot.Catalog.DefinitionsById[cosmetic.DefinitionId];
            var pool = cosmetic.CustomisationKind switch { "kit" => Kit, "ball" => Ball, "custom" => Badge, "stadium" => Stadium, _ => null };
            if (pool is null || definition.GameYear != 2021 || definition.NativeResourceId is null) continue;
            var resource = definition.NativeResourceId.Value.Value;
            if (pool == Ball && rules.Club.UnknownBalls.Contains(resource)) continue;
            items.Add(new(pool, cosmetic.CustomisationKind, resource, cosmetic.Rating!.Value, cosmetic.NativeRareflag!.Value, Tier(cosmetic.Rating.Value),
                cosmetic.Name, definition.NativeAssetId ?? 0, cosmetic.NativeTeamId ?? 0, cosmetic.NativeKitType ?? 0));
        }
        return new NonPlayerDropTable(rules.PackOdds.NonPlayerDrops, items);
    }

    /// <summary>A pool's items in resource id order; empty for an unknown pool.</summary>
    public IReadOnlyList<NonPlayerDrop> Pool(string pool) => _pools.TryGetValue(pool, out var items) ? items : [];

    /// <summary>An item's weight within its pool.</summary>
    public double Weight(NonPlayerDrop item) => item.Rareflag != 0 ? _rules.RareWeight : 1.0;

    /// <summary>Whether any of <paramref name="pools"/> can drop an item that passes <paramref name="where"/>.</summary>
    public bool CanDraw(IEnumerable<string> pools, Func<NonPlayerDrop, bool>? where = null) =>
        pools.Any(pool => _rules.PoolWeight(pool) > 0 && Pool(pool).Any(item => Weight(item) > 0 && (where?.Invoke(item) ?? true)));

    /// <summary>One item from <paramref name="pools"/> that passes <paramref name="where"/>, or null when none can drop.</summary>
    public NonPlayerDrop? Draw(GameRandom random, IEnumerable<string> pools, Func<NonPlayerDrop, bool>? where = null)
    {
        var options = pools.Distinct()
            .Select(pool => (Weight: _rules.PoolWeight(pool), Items: Pool(pool).Where(item => Weight(item) > 0 && (where?.Invoke(item) ?? true)).ToArray()))
            .Where(option => option.Weight > 0 && option.Items.Length > 0)
            .ToArray();
        if (options.Length == 0) return null;
        var items = random.WeightedChoice(options, options.Select(option => option.Weight).ToArray()).Items;
        return random.WeightedChoice(items, items.Select(Weight).ToArray());
    }

    /// <summary>
    /// <paramref name="count"/> items for one pack, drawn as <see cref="Draw"/> does but from the pools this pack has not drawn
    /// from yet, starting over once every pool that can drop has: two consumables are never two chemistry styles. Empty when
    /// no pool can drop.
    /// </summary>
    public List<NonPlayerDrop> DrawBlended(GameRandom random, IReadOnlyCollection<string> pools, int count, Func<NonPlayerDrop, bool>? where = null)
    {
        var drawn = new List<NonPlayerDrop>();
        var live = pools.Distinct().Where(pool => CanDraw([pool], where)).ToArray();
        if (live.Length == 0) return drawn;
        var unused = new List<string>(live);
        while (drawn.Count < count)
        {
            if (unused.Count == 0) unused.AddRange(live);
            var item = Draw(random, unused, where)!;
            unused.Remove(item.Pool);
            drawn.Add(item);
        }
        return drawn;
    }

    /// <summary>Up to <paramref name="count"/> different items, each drawn as <see cref="Draw"/> does from those not yet drawn.</summary>
    public List<NonPlayerDrop> DrawDistinct(GameRandom random, IReadOnlyCollection<string> pools, int count, Func<NonPlayerDrop, bool>? where = null)
    {
        var drawn = new List<NonPlayerDrop>();
        var taken = new HashSet<NonPlayerDrop>();
        while (drawn.Count < count && Draw(random, pools, item => !taken.Contains(item) && (where?.Invoke(item) ?? true)) is { } item)
        {
            taken.Add(item);
            drawn.Add(item);
        }
        return drawn;
    }
}
