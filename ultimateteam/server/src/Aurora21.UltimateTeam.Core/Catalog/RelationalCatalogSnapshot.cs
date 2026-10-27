using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>One immutable, ordered catalog revision shared by player contexts.</summary>
public sealed class RelationalCatalogSnapshot
{
    internal RelationalCatalogSnapshot(
        IReadOnlyList<FootballerRecord> footballers,
        IReadOnlyList<NationRecord> nations,
        IReadOnlyList<LeagueRecord> leagues,
        IReadOnlyList<TeamRecord> teams,
        IReadOnlyList<ItemFamilyRecord> families,
        IReadOnlyList<ItemDefinitionRecord> definitions,
        IReadOnlyList<CardTypeRecord> cardtypes,
        IReadOnlyList<PromoRecord> promos,
        IReadOnlyList<PromoCardRecord> promocards,
        IReadOnlyList<PromoBucketRecord> promoBuckets,
        IReadOnlyList<PromoEventRecord> promoEvents,
        IReadOnlyList<PromoEventPromoRecord> promoEventPromos,
        IReadOnlyList<CardRarityGroupRecord> raritygroups,
        IReadOnlyList<PlayerCardDefinitionRecord> players,
        IReadOnlyList<ManagerDefinitionRecord> managers,
        IReadOnlyList<ConsumableDefinitionRecord> consumables,
        IReadOnlyList<CustomisationDefinitionRecord> customisations,
        IReadOnlyList<DefinitionExtensionRecord> extensions,
        IReadOnlyList<IconRotationRecord>? iconRotations = null, long databaseRevision = 0)
    {
        Footballers = Array.AsReadOnly(footballers.ToArray());
        Nations = Array.AsReadOnly(nations.ToArray());
        Leagues = Array.AsReadOnly(leagues.ToArray());
        Teams = Array.AsReadOnly(teams.ToArray());
        Families = Array.AsReadOnly(families.ToArray());
        Definitions = Array.AsReadOnly(definitions.ToArray());
        CardTypes = Array.AsReadOnly(cardtypes.ToArray());
        Promos = Array.AsReadOnly(promos.ToArray());
        PromoCards = Array.AsReadOnly(promocards.ToArray());
        PromoBuckets = Array.AsReadOnly(promoBuckets.ToArray());
        PromoEvents = Array.AsReadOnly(promoEvents.ToArray());
        PromoEventPromos = Array.AsReadOnly(promoEventPromos.ToArray());
        RarityGroups = Array.AsReadOnly(raritygroups.ToArray());
        Players = Array.AsReadOnly(players.ToArray());
        Managers = Array.AsReadOnly(managers.ToArray());
        Consumables = Array.AsReadOnly(consumables.ToArray());
        Customisations = Array.AsReadOnly(customisations.ToArray());
        Extensions = Array.AsReadOnly(extensions.ToArray());
        IconRotations = Array.AsReadOnly((iconRotations ?? []).ToArray());
        DatabaseRevision = databaseRevision;
        DefinitionsById = Definitions.ToFrozenDictionary(d => d.DefinitionId);
        FootballersById = Footballers.ToFrozenDictionary(f => f.FootballerId);
        PlayersById = Players.ToFrozenDictionary(p => p.DefinitionId);
        ConsumablesById = Consumables.ToFrozenDictionary(c => c.DefinitionId);
        CustomisationsById = Customisations.ToFrozenDictionary(c => c.DefinitionId);
        _nativeDefinitions = Definitions.Where(d => d.NativeResourceId.HasValue).ToFrozenDictionary(
            d => (d.GameYear, d.FamilyId, d.NativeResourceId!.Value));
        _variants = Players.GroupBy(p => p.FootballerId).ToFrozenDictionary(g => g.Key,
            g => (IReadOnlyList<PlayerCardDefinitionRecord>)Array.AsReadOnly(g.OrderBy(p => DefinitionsById[p.DefinitionId].CatalogOrder).ToArray()));
        RevisionSha256 = ComputeRevision();
    }

    public IReadOnlyList<FootballerRecord> Footballers { get; }
    public IReadOnlyList<NationRecord> Nations { get; }
    public IReadOnlyList<LeagueRecord> Leagues { get; }
    public IReadOnlyList<TeamRecord> Teams { get; }
    public IReadOnlyList<ItemFamilyRecord> Families { get; }
    public IReadOnlyList<ItemDefinitionRecord> Definitions { get; }
    public IReadOnlyList<CardTypeRecord> CardTypes { get; }
    public IReadOnlyList<PromoRecord> Promos { get; }
    public IReadOnlyList<PromoCardRecord> PromoCards { get; }
    public IReadOnlyList<PromoBucketRecord> PromoBuckets { get; }
    public IReadOnlyList<PromoEventRecord> PromoEvents { get; }
    public IReadOnlyList<PromoEventPromoRecord> PromoEventPromos { get; }
    public IReadOnlyList<IconRotationRecord> IconRotations { get; }
    public long DatabaseRevision { get; }
    public IReadOnlyList<CardRarityGroupRecord> RarityGroups { get; }
    public IReadOnlyList<PlayerCardDefinitionRecord> Players { get; }
    public IReadOnlyList<ManagerDefinitionRecord> Managers { get; }
    public IReadOnlyList<ConsumableDefinitionRecord> Consumables { get; }
    public IReadOnlyList<CustomisationDefinitionRecord> Customisations { get; }
    public IReadOnlyList<DefinitionExtensionRecord> Extensions { get; }
    public FrozenDictionary<DefinitionId, ItemDefinitionRecord> DefinitionsById { get; }
    public FrozenDictionary<FootballerId, FootballerRecord> FootballersById { get; }
    public FrozenDictionary<DefinitionId, PlayerCardDefinitionRecord> PlayersById { get; }
    public FrozenDictionary<DefinitionId, ConsumableDefinitionRecord> ConsumablesById { get; }
    public FrozenDictionary<DefinitionId, CustomisationDefinitionRecord> CustomisationsById { get; }
    /// <summary>Identity of all loaded catalog values; relational maintenance combines this with rules and initial-roster settings.</summary>
    public string RevisionSha256 { get; }
    private readonly FrozenDictionary<(int Year, short Family, NativeResourceId Resource), ItemDefinitionRecord> _nativeDefinitions;
    private readonly FrozenDictionary<FootballerId, IReadOnlyList<PlayerCardDefinitionRecord>> _variants;

    public ItemDefinitionRecord? ResolveDefinition(int gameYear, short familyId, NativeResourceId resourceId) =>
        _nativeDefinitions.GetValueOrDefault((gameYear, familyId, resourceId));

    /// <summary>The manager league cards (migration 071): consumables of kind manager_league, whose effect is the native league.</summary>
    public IReadOnlyList<Rules.ManagerLeagueCard> ManagerLeagueCards(int gameYear = 2021) => Consumables
        .Where(c => c.ConsumableKind == "manager_league" && c.NativeEffectId is not null)
        .Select(c => (Consumable: c, Definition: DefinitionsById[c.DefinitionId]))
        .Where(pair => pair.Definition.GameYear == gameYear && pair.Definition.NativeResourceId is not null)
        .Select(pair => new Rules.ManagerLeagueCard(pair.Definition.NativeResourceId!.Value.Value, pair.Consumable.NativeEffectId!.Value,
            pair.Definition.DisplayName ?? string.Empty))
        .ToList();

    public IReadOnlyList<PlayerCardDefinitionRecord> Variants(FootballerId footballerId) =>
        _variants.GetValueOrDefault(footballerId) ?? Array.Empty<PlayerCardDefinitionRecord>();

    private string ComputeRevision()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var groups = new IEnumerable<object>[] { Footballers, Nations, Leagues, Teams, Families,
            Definitions, CardTypes, Promos, PromoCards, PromoBuckets, PromoEvents, PromoEventPromos, IconRotations,
            RarityGroups, Players,
            Managers, Consumables, Customisations, Extensions };
        foreach (var group in groups)
        {
            foreach (var row in group)
            {
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row, row.GetType()));
                hash.AppendData(new byte[] { 0 });
            }
            hash.AppendData(new byte[] { 1 });
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
