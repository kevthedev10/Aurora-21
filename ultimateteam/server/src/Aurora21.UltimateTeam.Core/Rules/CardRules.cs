using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// A quick-sell multiplier from a rating up (to the next band's rating): the value is the rating times <see cref="PerRating"/>,
/// at least <see cref="Minimum"/> and, when <see cref="Maximum"/> is above 0, at most <see cref="Maximum"/> (an economy cap
/// where the retail value would pay more than the cheapest market buy-in).
/// </summary>
public sealed class QuickSellBand
{
    public required long FromRating { get; set; }
    public required decimal PerRating { get; set; }
    public long Minimum { get; set; }
    public long Maximum { get; set; }

    /// <summary>The value at <paramref name="rating"/>: rating times the multiplier, rounded half up.</summary>
    public long ValueAt(long rating) => Capped(Math.Max(Minimum, (long)Math.Floor(rating * PerRating + 0.5m)), Maximum);

    internal static long Capped(long value, long maximum) => maximum > 0 ? Math.Min(value, maximum) : value;
}

/// <summary>A tier's base-card quick-sell multipliers (value = rating times it, rounded half up), common and rare.</summary>
public sealed class TierQuickSell
{
    public required decimal CommonPerRating { get; set; }
    public required decimal RarePerRating { get; set; }

    /// <summary>The most a rare of this tier sells for (an economy cap), or 0 for none.</summary>
    public long RareMaximum { get; set; }
}

/// <summary>
/// rules/cards.json quickSell: retail FIFA 21's discard values, which are the card's rating times a multiplier of its kind (base
/// cards by tier and rarity, every special by the <see cref="InForm"/> bands), plus the item-level reward rules: a reward-untradeable item sells for at most
/// <see cref="RewardUntradeableCap"/>, an item of a <see cref="ZeroValueRewardSources"/> source for nothing.
/// </summary>
public sealed class QuickSellRules : IJsonOnDeserialized
{
    public required Dictionary<string, TierQuickSell> Tiers { get; set; }

    /// <summary>The multiplier bands of a Team of the Week in-form and of every special without a multiplier of its own.</summary>
    public required List<QuickSellBand> InForm { get; set; }

    /// <summary>Rarities that sell for nothing (the Icon Swaps and FGS Swaps tokens: only worth swapping).</summary>
    public required HashSet<long> ZeroValueRareflags { get; set; }
    public required long RewardUntradeableCap { get; set; }
    public required HashSet<string> ZeroValueRewardSources { get; set; }
    public required long ManagerCommon { get; set; }
    public required long ManagerRare { get; set; }
    public required HashSet<long> BaseCompetitionRareflags { get; set; }

    /// <summary>A base card's value: its tier's common or rare multiplier at its rating (an unknown tier sells as bronze).</summary>
    public long BaseValue(string tier, long rating, bool rare)
    {
        var bands = Tiers.GetValueOrDefault(tier) ?? Tiers["bronze"];
        return rare ? QuickSellBand.Capped(Round(rating, bands.RarePerRating), bands.RareMaximum) : Round(rating, bands.CommonPerRating);
    }

    /// <summary>
    /// A manager's value: the flat managerCommon/managerRare, but never more than a base player of the manager's tier and
    /// rating. Flat everywhere, a bronze manager sold for 8-17x a bronze player and made the 400-coin Bronze Pack return
    /// 97% of its price in quick sell alone (pack-odds-2026-10 bronze/silver audit).
    /// </summary>
    public long ManagerValue(string tier, long rating, bool rare) => Math.Min(rare ? ManagerRare : ManagerCommon, BaseValue(tier, rating, rare));

    /// <summary>A special's value: nothing for a token rarity, otherwise the in-form bands at its rating.</summary>
    public long SpecialValue(long rareflag, long rating) =>
        ZeroValueRareflags.Contains(rareflag) ? 0 : InFormBand(rating).ValueAt(rating);

    /// <summary>The in-form band a rating falls in.</summary>
    public QuickSellBand InFormBand(long rating) => InForm.LastOrDefault(band => rating >= band.FromRating) ?? InForm[0];

    /// <summary>A value after the item's own reward rules: nothing for a zero-value source, at most the cap when untradeable.</summary>
    public long ForItem(long value, bool untradeable, string? rewardSource) =>
        rewardSource is not null && ZeroValueRewardSources.Contains(rewardSource) ? 0
        : untradeable ? Math.Min(value, RewardUntradeableCap)
        : value;

    private static long Round(long rating, decimal perRating) => (long)Math.Floor(rating * perRating + 0.5m);

    public void OnDeserialized()
    {
        foreach (var tier in new[] { "gold", "silver", "bronze" })
        {
            if (!Tiers.TryGetValue(tier, out var bands) || bands.CommonPerRating < 0 || bands.RarePerRating < 0 || bands.RareMaximum < 0)
            {
                throw new InvalidDataException($"rules/cards.json quickSell.tiers needs {tier} with multipliers of 0 or more.");
            }
        }
        if (InForm.Count == 0 || InForm.Zip(InForm.Skip(1)).Any(pair => pair.Second.FromRating <= pair.First.FromRating)
            || InForm.Any(band => band.PerRating < 0 || band.Minimum < 0 || band.Maximum < 0))
        {
            throw new InvalidDataException("rules/cards.json quickSell inForm needs bands in rising fromRating order with multipliers and minimums of 0 or more.");
        }
        if (RewardUntradeableCap < 0)
        {
            throw new InvalidDataException("rules/cards.json quickSell.rewardUntradeableCap must be 0 or more.");
        }
    }
}

public sealed class PlayerPickTokenRules
{
    public required long MinChoices { get; set; }
    public required long MaxChoices { get; set; }

    /// <summary>The most tokens one store pack pick group adds (Python server's limit).</summary>
    public required long MaxTokensPerGroup { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }

    public string NameFor(long choices, long rating) => RuleText.Format(Name, ("choices", choices), ("rating", rating));

    public string DescriptionFor(long choices, long rating) => RuleText.Format(Description, ("choices", choices), ("rating", rating));
}

/// <summary>A native position-change training card: source position to target position.</summary>
public sealed class PositionModifier
{
    public required long ResourceId { get; set; }
    public required long SubType { get; set; }
    public required string Source { get; set; }
    public required string Target { get; set; }
    public required long Rating { get; set; }
    public required long WeightRare { get; set; }
}

/// <summary>Values shared by every native position-change (development) card.</summary>
public sealed class PositionModifierCardRules
{
    public required long CardAssetId { get; set; }
    public required long ConsumableType { get; set; }
    public required long DiscardValue { get; set; }
}

public sealed class ResourceIdRange
{
    public required long First { get; set; }
    public required long Last { get; set; }

    public bool Contains(long resourceId) => resourceId >= First && resourceId <= Last;
}

/// <summary>
/// A chemistry style training card: its playStyle, card resource id, whether it is a goalkeeper style, and the position group
/// FIFA files it under (1 attackers, 2 midfielders, 3 defenders, 4 every position, 5 goalkeepers), sent as the card's amount.
/// </summary>
public sealed record ChemistryStyle(long PlayStyle, long ResourceId, bool Goalkeeper, string Name, long PositionGroup);

/// <summary>Chemistry style training cards; a card's cardsubtypeid is the playStyle it applies.</summary>
public sealed class ChemistryStyleRules : IJsonOnDeserialized
{
    public required long FirstResourceId { get; set; }
    public required long CardAssetId { get; set; }
    public required long ConsumableType { get; set; }
    public required long Rating { get; set; }
    public required long DiscardValue { get; set; }
    public required long OutfieldDefault { get; set; }
    public required long GoalkeeperDefault { get; set; }
    public required HashSet<long> GoalkeeperStyles { get; set; }
    public HashSet<long> RareStyles { get; set; } = [];
    public required SortedDictionary<long, string> Names { get; set; }

    /// <summary>
    /// playStyles per position group. FIFA 21 reads a chemistry style card's group from its amount (CardsDLL 0x31FCD0 keeps it
    /// at +0xEE) and lists the style for a player whose position group (table 0x419B10: GK 5, DEF 3, MID 2, ATT 1) matches it,
    /// or for anyone when it is 4.
    /// </summary>
    public required Dictionary<long, List<long>> PositionGroups { get; set; }

    /// <summary>Every style in playStyle order.</summary>
    [JsonIgnore]
    public IReadOnlyList<ChemistryStyle> Styles { get; private set; } = Array.Empty<ChemistryStyle>();

    public void OnDeserialized()
    {
        var groups = PositionGroups.SelectMany(group => group.Value.Select(style => (Style: style, Group: group.Key))).ToList();
        if (groups.Count != Names.Count || groups.Select(entry => entry.Style).Distinct().Count() != groups.Count || !groups.All(entry => Names.ContainsKey(entry.Style)))
        {
            throw new InvalidOperationException("chemistryStyles.positionGroups must name every style exactly once.");
        }
        var groupOf = groups.ToDictionary(entry => entry.Style, entry => entry.Group);
        Styles = Names.Select(entry => new ChemistryStyle(entry.Key, FirstResourceId + (entry.Key - OutfieldDefault), GoalkeeperStyles.Contains(entry.Key), entry.Value,
            groupOf[entry.Key])).ToList();
    }
}

/// <summary>A manager league card (migration 071): its resource id, the native league it moves a manager to, and the league's name.</summary>
public sealed record ManagerLeagueCard(long ResourceId, long LeagueId, string Name);

/// <summary>Values shared by every manager league card; a card's resource id is <see cref="ResourceIdBase"/> plus its league id.</summary>
public sealed class ManagerLeagueCardRules
{
    public required long ResourceIdBase { get; set; }
    public required long FirstSubtype { get; set; }
    public required List<long> SubtypeLeagues { get; set; }
    public required long CardAssetId { get; set; }
    public required long ConsumableType { get; set; }
    public required long Rating { get; set; }
    public required long DiscardValue { get; set; }

    /// <summary>
    /// The cardsubtypeid FIFA files a league's card under: firstSubtype plus the league's fcc_leagues row, or null for a league
    /// the client has no row for.
    /// </summary>
    public long? Subtype(long leagueId) => SubtypeLeagues.IndexOf(leagueId) is var row and >= 0 ? FirstSubtype + row : null;
}

/// <summary>Which Club &gt; Consumables counter each stored consumable adds to.</summary>
public sealed class ConsumableCounterRules
{
    public required Dictionary<long, string> ByCardAssetId { get; set; }
    public required Dictionary<long, string> ByConsumableType { get; set; }
    public required long TeamFitnessSubType { get; set; }
}

/// <summary>A developer's exclusive Pro card (rules/cards.json aquaProCards).</summary>
public sealed class AquaProCard
{
    public required long CustomId { get; set; }
    public required long BaseId { get; set; }
    public required string Name { get; set; }

    /// <summary>The developer the card is reserved for; the operator panel grants it once.</summary>
    public required string Owner { get; set; }
}

/// <summary>rules/cards.json: quick-sell values, position modifiers, custom pro cards and attribute layout.</summary>
public sealed class CardRules
{
    public required long PermanentContracts { get; set; }
    public required QuickSellRules QuickSell { get; set; }

    /// <summary>Rarities only a pack or pick that names them reaches (the FUT Champions red picks, rarity 18, and the Icon Swaps tokens).</summary>
    public required List<long> RewardOnlyRareflags { get; set; }

    /// <summary>The rarities of FUT Champions red copies in the fixed TOTW reward pool.</summary>
    public required List<long> FutChampionsRedRareflags { get; set; }

    /// <summary>Cards only an explicit grant hands out (developer rewards; Python server 8d674f7).</summary>
    public required HashSet<long> GrantOnlyResourceIds { get; set; }

    /// <summary>Rarities whose cards (the swap tokens) are untradeable whatever grants them.</summary>
    public required HashSet<long> UntradeableRareflags { get; set; }
    public required long ManagerDefaultRating { get; set; }
    public required long ManagerRareFromRating { get; set; }
    public required PlayerPickTokenRules PlayerPickToken { get; set; }
    public required long RareRarityGroup { get; set; }
    public required Dictionary<long, List<long>> RarityGroupsByRareflag { get; set; }
    public required int PlayerStatSlots { get; set; }
    public required long ConceptItemIdBase { get; set; }
    public required long ConceptItemIdSpan { get; set; }
    public required PositionModifierCardRules PositionModifierCard { get; set; }
    public required ResourceIdRange LegacyPositionModifierIds { get; set; }
    public required ChemistryStyleRules ChemistryStyles { get; set; }
    public required ManagerLeagueCardRules ManagerLeagueCards { get; set; }
    public required ConsumableCounterRules ConsumableCounters { get; set; }
    public required List<PositionModifier> PositionModifiers { get; set; }
    public required List<AquaProCard> AquaProCards { get; set; }
    public required long AquaProAttributeValue { get; set; }
    public required List<List<string>> OutfieldAttributeGroups { get; set; }
    public required List<string> GoalkeeperAttributes { get; set; }
    public required List<string> InGameAttributeOrder { get; set; }
    public required Dictionary<string, long> NativeWorkRates { get; set; }
    public required long FaceAttributeTolerance { get; set; }
    public required FaceAttributeWeights FaceAttributeWeights { get; set; }
}

/// <summary>FIFA's face formulas: per face stat, the attribute weights that produce it.</summary>
public sealed class FaceAttributeWeights
{
    public required List<Dictionary<string, double>> Outfield { get; set; }
    public required List<Dictionary<string, double>> Goalkeeper { get; set; }
}
