namespace Aurora21.UltimateTeam.Core.Data.Records;

public sealed record RelationalItemHeader(
    ItemId Id,
    long ClubId,
    NativeItemId NativeItemId,
    DefinitionId DefinitionId,
    short FamilyId,
    long Timestamp,
    string? Pile,
    string? ItemState,
    bool Untradeable,
    long Owners,
    long LastSalePrice,
    // Where the item came from when a grant said so (items.reward_source, migration 069): its quick-sell rules read it.
    string? RewardSource = null);

public sealed record PlayerItemState(
    ItemId ItemId,
    long Contracts,
    long Fitness,
    string? InjuryType,
    long InjuryGames,
    long Suspension,
    long LoanGames,
    long Morale,
    long Training,
    string? PositionOverride,
    DefinitionId? StyleDefinitionId,
    long? NativePlayStyle,
    long Assists,
    long LifetimeAssists,
    long? LoyaltyBonus,
    bool DuplicateAcknowledged);

public sealed record PlayerStatValue(
    long Id,
    ItemId ItemId,
    string Scope,
    int StatIndex,
    long Value);

public sealed record ManagerItemState(
    ItemId ItemId,
    long Contracts,
    long? LeagueId);

public sealed record ConsumableItemState(
    ItemId ItemId,
    long Quantity,
    long UntradeableQuantity);

public sealed record CustomisationItemState(
    ItemId ItemId,
    bool? IsLockedOverride);

public sealed record PlayerPickItemState(
    ItemId ItemId,
    DefinitionId? PreviewDefinitionId,
    long MinRating,
    long ChoiceCount,
    long SourcePackId,
    string State,
    long? SelectedChoiceId,
    // The pick group's filters (store packs 48-65): specials only, and the rarities it is limited to (none: any).
    bool PromoOnly = false,
    IReadOnlyList<long>? Rareflags = null,
    // The token's own name when its pick group names it (pack 60: "1 of 3 FUT Champs Player Pick").
    string? Label = null,
    // The pick group's rating ceiling (pack 551: 84).
    long MaxRating = 99,
    // The pick group's named card pool (fixed TOTW 1–3 red copies: packs 550-553).
    string? ResourcePool = null,
    string? DrawConfig = null);

public sealed record ItemExtensionState(
    ItemId ItemId,
    long SchemaRevision,
    CatalogJson Payload,
    CatalogJson? WireRepresentation);

public sealed record UnopenedPackRecord(
    long Id,
    long ClubId,
    long PackId,
    string Source,
    long Created);

public sealed record PlayerPickChoiceRecord(
    long Id,
    ItemId TokenItemId,
    int Ordinal,
    DefinitionId DefinitionId,
    NativeItemId ReservedWireItemId,
    long Timestamp,
    bool IsDuplicate);

/// <summary>One complete owned instance. Child keys are normalized to the persisted header on save.</summary>
public sealed record RelationalItemAggregate(RelationalItemHeader Header,
    PlayerItemState? Player = null, ManagerItemState? Manager = null,
    ConsumableItemState? Consumable = null, CustomisationItemState? Customisation = null,
    PlayerPickItemState? Pick = null, ItemExtensionState? Extension = null,
    IReadOnlyList<PlayerStatValue>? Stats = null);

public sealed record RelationalOwnedPlayerIdentity(DefinitionId DefinitionId, FootballerId FootballerId,
    NativeResourceId? ResourceId, ItemId ItemId, NativeItemId NativeItemId);
