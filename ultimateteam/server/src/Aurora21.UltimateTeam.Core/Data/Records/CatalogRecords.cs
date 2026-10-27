// Columns mapped from the frozen relational schema; updates must follow its contract.
namespace Aurora21.UltimateTeam.Core.Data.Records;

/// <summary>Immutable opaque JSON text for provenance and explicitly retained extensions.</summary>
public sealed record CatalogJson(string Text);



public sealed record FootballerRecord(
    FootballerId FootballerId,
    long? NativeFootballerId,
    string FirstName,
    string LastName,
    string? CommonName);

public sealed record NationRecord(
    long NationId,
    long NativeNationId,
    string Name);

public sealed record LeagueRecord(
    long LeagueId,
    long NativeLeagueId,
    string Name);

public sealed record TeamRecord(
    long TeamId,
    long NativeTeamId,
    string Name);

public sealed record ItemFamilyRecord(
    short FamilyId,
    string Code,
    string Name);

public sealed record ItemDefinitionRecord(
    DefinitionId DefinitionId,
    short FamilyId,
    int GameYear,
    NativeResourceId? NativeResourceId,
    long? NativeAssetId,
    long? NativeCardDbId,
    long? NativeCardAssetId,
    string NativeItemType,
    long? NativeSubtype,
    long CatalogOrder,
    long Revision,
    string Status,
    string? DisplayName);

public sealed record CardTypeRecord(
    long CardTypeId,
    string Name,
    string? CardLevel,
    long? NativeRareflag,
    bool IsSpecial);

public sealed record PromoRecord(
    long PromoId,
    string Code,
    string Name,
    DateTime? StartsAt,
    DateTime? EndsAt,
    string Status,
    bool Enabled,
    string OddsCategory,
    decimal DrawWeight);

public sealed record PromoCardRecord(
    long Id,
    long PromoId,
    DefinitionId PlayerCardDefinitionId,
    long BucketId,
    decimal DrawReduction = 1);

public sealed record PromoBucketRecord(
    long Id,
    long PromoId,
    string Code,
    string Name,
    decimal DrawWeight,
    bool Enabled = true);

public sealed record IconRotationRecord(long Id, string Code, string Name, long PromoId, bool Enabled, DateTime StartsAt, DateTime EndsAt);

public sealed record PromoEventRecord(
    long EventId,
    string Code,
    string Name,
    bool Enabled,
    DateTime StartsAt,
    DateTime EndsAt);

public sealed record PromoEventPromoRecord(
    long EventId,
    long PromoId,
    bool Enabled);

public sealed record CardRarityGroupRecord(
    long Id,
    long CardTypeId,
    long NativeGroupId);

public sealed record PlayerCardDefinitionRecord(
    DefinitionId DefinitionId,
    FootballerId FootballerId,
    long CardTypeId,
    long Rating,
    string Position,
    long? TeamId,
    long? LeagueId,
    long? NationId,
    long? NativeTeamId,
    long? NativeLeagueId,
    long? NativeNationId,
    string? PreferredFoot,
    long? WeakFoot,
    long? SkillMoves,
    string? WorkRateAttack,
    string? WorkRateDefense,
    long? NativeTrait1,
    long? NativeTrait2,
    long? NativeVersion,
    long? FaceSlot0,
    long? FaceSlot1,
    long? FaceSlot2,
    long? FaceSlot3,
    long? FaceSlot4,
    long? FaceSlot5,
    long? Acceleration,
    long? SprintSpeed,
    long? Positioning,
    long? Finishing,
    long? ShotPower,
    long? LongShots,
    long? Volleys,
    long? Penalties,
    long? Vision,
    long? Crossing,
    long? FreeKickAccuracy,
    long? ShortPassing,
    long? LongPassing,
    long? Curve,
    long? Agility,
    long? Balance,
    long? Reactions,
    long? BallControl,
    long? Dribbling,
    long? Composure,
    long? Interceptions,
    long? HeadingAccuracy,
    long? Marking,
    long? StandingTackle,
    long? SlidingTackle,
    long? Jumping,
    long? Stamina,
    long? Strength,
    long? Aggression,
    long? GkDiving,
    long? GkHandling,
    long? GkKicking,
    long? GkReflexes,
    long? GkSpeed,
    long? GkPositioning,
    // The in-game head an operator picked (migration 074): another card of the player's dynamic head, or an uploaded head.
    long? HeadResourceId = null,
    long? CustomHeadId = null);

public sealed record ManagerDefinitionRecord(
    DefinitionId DefinitionId,
    string FirstName,
    string LastName,
    decimal? Rating,
    long? NativeRareflag,
    long? NativeLeagueId,
    long? NativeNationId,
    long? LeagueId,
    long? NationId,
    long? Negotiation);

public sealed record ConsumableDefinitionRecord(
    DefinitionId DefinitionId,
    string ConsumableKind,
    long? Rating,
    long? NativeConsumableType,
    long? NativeEffectId,
    string? SourcePosition,
    string? TargetPosition,
    bool? GoalkeeperOnly);

public sealed record CustomisationDefinitionRecord(
    DefinitionId DefinitionId,
    string CustomisationKind,
    string Name,
    long? NativeTeamId,
    long? TeamId,
    long? NativeKitType,
    long? NativeCategory,
    long? Rating,
    long? NativeRareflag,
    bool PackEligible);

public sealed record DefinitionExtensionRecord(
    DefinitionId DefinitionId,
    long SchemaRevision,
    CatalogJson Payload);
