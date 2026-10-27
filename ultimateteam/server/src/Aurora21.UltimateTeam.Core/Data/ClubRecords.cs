using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Onboarding progress (country and club creation, starter loan, kit and badge choices).</summary>
public sealed record OnboardingState
{
    /// <summary>The nation of the country the player picked before creating the club (0 = none picked).</summary>
    public long NationId { get; init; }

    /// <summary>The club name and abbreviation the player typed at creation (null when FIFA did not create the club).</summary>
    public string? ClubName { get; init; }

    public string? ClubAbbr { get; init; }

    public bool Complete { get; init; }
    public long BadgeChoice { get; init; }
    public long BadgeItemId { get; init; }
    public long LoanOfferId { get; init; }
    public long LoanItemId { get; init; }
    public long LoanResourceId { get; init; }
    public IReadOnlyList<long> KitChoices { get; init; } = Array.Empty<long>();
    public JsonObject KitItemIds { get; init; } = new();
    public IReadOnlyList<long> StaleLoanItemIds { get; init; } = Array.Empty<long>();
}

/// <summary>The bounds of a client-data document (the game's per-screen key/value settings).</summary>
public static class ClientDataLimits
{
    public const int MaxEntries = 256;
    public const long MaxKey = 4096;

    /// <summary>Keys are 0..<see cref="MaxKey"/>; values fit a 32-bit signed integer.</summary>
    public static bool IsValid(long key, long value) => key >= 0 && key <= MaxKey && value >= int.MinValue && value <= int.MaxValue;
}

/// <summary>Seasonal campaign (Objectives) progress.</summary>
public sealed record ObjectivesState(long UserXp, bool RewardClaimed, IReadOnlyList<long> ClaimedLevels)
{
    /// <summary>Objective progress counters by stat name (matches, wins, goals, ...).</summary>
    public IReadOnlyDictionary<string, long> Stats { get; init; } = new Dictionary<string, long>();

    public IReadOnlyList<long> ClaimedObjectives { get; init; } = Array.Empty<long>();

    public IReadOnlyList<long> ClaimedGroups { get; init; } = Array.Empty<long>();

    /// <summary>
    /// Each live group's starting point per stat (migration 143): the club's counter when the group first went live. Read with
    /// the state, never saved with it.
    /// </summary>
    public IReadOnlyDictionary<(long Group, string Stat), long> Baselines { get; init; } = new Dictionary<(long, string), long>();

    /// <summary>The option a club picked for each claimed Season Pass level (migration 144); 0 when the level had no choice.</summary>
    public IReadOnlyDictionary<long, long> LevelOptions { get; init; } = new Dictionary<long, long>();

    /// <summary>
    /// The season the XP, level claims and level options belong to (migration 166): the season in force when read. Saving
    /// under another season than the stored one moves the stored season's progress to the club's season history.
    /// </summary>
    public long SeasonId { get; init; } = 1;

    /// <summary>Claims of scope level_option are stored as level * this + option id.</summary>
    public const long LevelOptionScale = 100;

    public long Stat(string name) => Stats.GetValueOrDefault(name);

    /// <summary>A group's progress on a stat: what the club counted since the group went live (all of it for older groups).</summary>
    public long Progress(long group, string stat) => Math.Max(0, Stat(stat) - Baselines.GetValueOrDefault((group, stat)));
}

/// <summary>Online Draft and single-player Draft keep separate runs, so switching modes never overwrites the other.</summary>
public enum DraftSlot
{
    SinglePlayer = 1,
    Online = 2,
}

/// <summary>A persisted 1-of-N player-pick token and its revealed choices.</summary>
public sealed record PlayerPickState(long MinRating, long ChoiceCount, JsonArray Choices);

/// <summary>The persona a club belongs to.</summary>
public sealed record ClubIdentity(long PersonaId, long NucleusId);
