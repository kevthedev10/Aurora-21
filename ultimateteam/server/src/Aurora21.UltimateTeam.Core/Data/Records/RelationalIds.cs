namespace Aurora21.UltimateTeam.Core.Data.Records;

// Internal keys and client numbers intentionally have no implicit conversions.
public readonly record struct DefinitionId(long Value);
public readonly record struct FootballerId(long Value);
public readonly record struct ItemId(long Value);
public readonly record struct SquadId(long Value);
public readonly record struct NativeResourceId(long Value);
public readonly record struct NativeItemId(long Value);
public readonly record struct NativeSquadId(long Value);

/// <summary>A wire item number is only unique within its owning club.</summary>
public readonly record struct OwnedItemKey(long ClubId, NativeItemId NativeId);

/// <summary>A wire squad number is only unique within its owning club.</summary>
public readonly record struct OwnedSquadKey(long ClubId, NativeSquadId NativeId);

/// <summary>Maps a persistence identity to its separate client-visible resource.</summary>
public sealed record DefinitionIdentity(DefinitionId Id, NativeResourceId ResourceId, long FamilyId, long Revision);
