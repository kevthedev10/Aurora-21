namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>The immutable contents stored in a Match Pack earned before its retirement.</summary>
public sealed record LegacyMatchPackContents(string Tier, bool Highlight, IReadOnlyList<long> Players,
    long? ChemistryStyle, long? PositionModifier, long? Kit, long? ManagerLeagueCard = null);
