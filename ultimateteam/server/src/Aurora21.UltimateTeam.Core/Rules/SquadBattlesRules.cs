using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class MatchVenue
{
    public required long StadiumId { get; set; }
    public required long ResourceId { get; set; }
    public required string Name { get; set; }
}

/// <summary>How a Squad Battles fixture's home side is chosen (see rules/squad_battles.json _homeAwayComment).</summary>
public sealed class SquadBattlesHomeAway
{
    public required long OpponentBadgeAssetId { get; set; }
    public required long FormationEnum { get; set; }
    public required long EstDateBase { get; set; }
    public required long CpuPersonaBase { get; set; }
}

public sealed class OpponentKit
{
    public required string ItemState { get; set; }
    public required long ResourceId { get; set; }
    public required long TeamId { get; set; }
    public required long KitType { get; set; }
    public required string Name { get; set; }
}

public sealed class SquadBattlesOpponent
{
    public required string ClubName { get; set; }
    public required string ClubAbbr { get; set; }
    public required long Rating { get; set; }
    public required long Chemistry { get; set; }
    public required string Difficulty { get; set; }
}

public sealed class LegacyFeedRules
{
    public required int Squads { get; set; }
    public required long SquadIdBase { get; set; }
    public required string NameFormat { get; set; }
    public required long RatingBase { get; set; }
    public required long RatingStep { get; set; }
    public required long ChemistryBase { get; set; }
    public required long ChemistryStep { get; set; }
}

public sealed class OpponentItemIds
{
    public required long OpponentPlayers { get; set; }
    public required long OpponentPlayersPerSlot { get; set; }
    public required long LegacyPlayers { get; set; }
    public required long OpponentKits { get; set; }
    public required long OpponentKitsPerSlot { get; set; }
    public required long OpponentStadiums { get; set; }
    public required long MatchOpponentStadium { get; set; }
}

/// <summary>rules/squad_battles.json: opponents, generated squad positions, the match venue and the event window.</summary>
public sealed class SquadBattlesRules
{
    public required MatchVenue Venue { get; set; }
    public required SquadBattlesHomeAway HomeAway { get; set; }
    public required List<OpponentKit> OpponentKits { get; set; }
    public required List<SquadBattlesOpponent> Opponents { get; set; }
    public required string Formation { get; set; }
    public required long FiveStarFromRating { get; set; }
    public required List<List<string>> SlotPositions { get; set; }
    public required long CandidateMinRating { get; set; }
    public required int ShortlistSize { get; set; }
    public required double RatingDistanceExponent { get; set; }
    public required int TopPlayersShown { get; set; }
    public required long MatchesPerEvent { get; set; }
    public required long EventStartedDaysAgo { get; set; }
    public required long EventEndsInDays { get; set; }
    public required LegacyFeedRules LegacyFeed { get; set; }
    public required OpponentItemIds ItemIds { get; set; }
    public required SquadBattlesCompetitionRules Competition { get; set; }
    public required SquadBattlesSetRules Sets { get; set; }
    public required SquadBattlesTotwRules Totw { get; set; }
}

/// <summary>One part of the skill bonus or penalty: <see cref="Points"/> per unit of the summed stats, at most <see cref="MaxUnits"/>.</summary>
public sealed class SquadBattlesSkillRule
{
    public required List<string> Stats { get; set; }
    public required long Points { get; set; }
    public required long MaxUnits { get; set; }
}

public sealed class SquadBattlesGoalsAgainstRule
{
    public required long Points { get; set; }
    public required long MaxGoals { get; set; }
}

/// <summary>A scored Squad Battles match: the parts DestroyMatch's squadBattlesScore carries.</summary>
public sealed record SquadBattlesPoints(long Final, long TeamRating, long MatchResult, long Skill, long Goals, double Modifier);

/// <summary>
/// The weekly Squad Battles competition as in FIFA 21 retail (squad_battles.json <c>competition</c>, Python server f354894):
/// the retail Battle Points formula and the week's clock. See its _competitionComment.
/// </summary>
public sealed class SquadBattlesCompetitionRules : IJsonOnDeserialized
{
    public required long WeekOriginSeconds { get; set; }
    public required long WeekSeconds { get; set; }
    public required int BoardSize { get; set; }
    public required string BoardSku { get; set; }
    public required long LeaderboardRefreshSeconds { get; set; }
    public required long StandingsCacheSeconds { get; set; }
    public required long PendingMaxAgeSeconds { get; set; }
    public required long FirstRetailWeek { get; set; }
    public required long PrizeWeeksBack { get; set; }
    public required int MaxRegularGames { get; set; }
    public required List<string> DifficultyNames { get; set; }
    public required List<double> WinModifiers { get; set; }
    public required List<double> LossModifiers { get; set; }
    public required Dictionary<string, long> MatchResultPoints { get; set; }
    public required List<long> GoalPoints { get; set; }
    public required long GoalBonusCap { get; set; }
    public required List<SquadBattlesSkillRule> SkillBonuses { get; set; }
    public required List<SquadBattlesSkillRule> SkillPenalties { get; set; }
    public required SquadBattlesGoalsAgainstRule GoalsAgainstPenalty { get; set; }
    public required long BaseSkillAllowance { get; set; }

    public long WeekId(long now) => (long)Math.Floor((now - WeekOriginSeconds) / (double)WeekSeconds);

    public long WeekStart(long week) => WeekOriginSeconds + week * WeekSeconds;

    public long WeekEnd(long week) => WeekStart(week + 1);

    /// <summary>CreateMatch's sqbtMatchDifficulty (0 Beginner to 6 Ultimate), clamped.</summary>
    public int ClampDifficulty(long value) => (int)Math.Clamp(value, 0, DifficultyNames.Count - 1);

    private long Result(bool win) => MatchResultPoints[win ? "win" : "loss"];

    /// <summary>The retail team rating score: rating x 3 + chemistry / 2.</summary>
    public static long TeamRatingScore(long rating, long chemistry) => (long)Math.Floor(rating * 3 + chemistry / 2.0);

    /// <summary>The difficulty screen's base points: the client's own fallback (0x1802f971e), so the preview is what it would compute.</summary>
    public long BasePoints(long rating, long chemistry, bool win) =>
        (long)Math.Floor(3.0 * rating + 0.5 * chemistry + Result(win) + BaseSkillAllowance + 0.5);

    public double Modifier(bool win, int difficulty) => (win ? WinModifiers : LossModifiers)[ClampDifficulty(difficulty)];

    public long SkillBonus(JsonObject stats, long goalsAgainst)
    {
        long Units(SquadBattlesSkillRule rule) =>
            Math.Min(rule.MaxUnits, (long)Math.Floor(rule.Stats.Sum(stat => Math.Max(0, JsonValues.Float(stats.Get(stat))))));
        return SkillBonuses.Sum(rule => rule.Points * Units(rule)) - SkillPenalties.Sum(rule => rule.Points * Units(rule))
            - GoalsAgainstPenalty.Points * Math.Min(GoalsAgainstPenalty.MaxGoals, Math.Max(0, goalsAgainst));
    }

    /// <summary>
    /// One scored match: modifier x (team rating score + result + skill), never below 0, rounded as Python's round() does
    /// (halves to even), plus the goal bonus.
    /// </summary>
    public SquadBattlesPoints MatchPoints(bool win, int difficulty, long rating, long chemistry, JsonObject stats, long goalsFor, long goalsAgainst)
    {
        var level = ClampDifficulty(difficulty);
        var team = TeamRatingScore(rating, chemistry);
        var result = Result(win);
        var skill = SkillBonus(stats, goalsAgainst);
        var factor = Modifier(win, level);
        var goals = Math.Min(GoalBonusCap, Math.Max(0, goalsFor)) * GoalPoints[level];
        var final = Math.Max(0, (long)Math.Round(factor * Math.Max(0, team + result + skill), MidpointRounding.ToEven)) + goals;
        return new SquadBattlesPoints(final, team, result, skill, goals, factor);
    }

    /// <summary>The parts of a match that scores nothing.</summary>
    public SquadBattlesPoints Zero(int difficulty) => new(0, 0, 0, 0, 0, Modifier(false, difficulty));

    /// <summary>The Featured and TOTW tiles' difficultyBasedWinPoints / difficultyBasedLossPoints, one per difficulty.</summary>
    public (List<long> Win, List<long> Loss) DifficultyPoints(long rating, long chemistry)
    {
        var win = BasePoints(rating, chemistry, true);
        var loss = BasePoints(rating, chemistry, false);
        return (WinModifiers.Select(m => (long)Math.Floor(win * m + 0.5)).ToList(), LossModifiers.Select(m => (long)Math.Floor(loss * m + 0.5)).ToList());
    }

    public void OnDeserialized()
    {
        // Six Top reward slots share the board's places (migration 133), so it has at least six.
        LeaderboardSizes.Validate(BoardSize, LeaderboardSizes.ChampionsMinimum, "competition.boardSize");
        var levels = DifficultyNames.Count;
        if (levels == 0 || WinModifiers.Count != levels || LossModifiers.Count != levels || GoalPoints.Count != levels
            || !MatchResultPoints.ContainsKey("win") || !MatchResultPoints.ContainsKey("loss"))
        {
            throw new InvalidDataException("competition needs one win modifier, loss modifier and goal points per difficulty, and win and loss result points");
        }
    }
}

/// <summary>How opponents are drawn, refreshed and shown (squad_battles.json <c>sets</c>, Python server f354894). See its _setsComment.</summary>
public sealed class SquadBattlesSetRules
{
    public required int SquadsPerSet { get; set; }
    public required long RefreshSeconds { get; set; }
    public required Dictionary<string, string> BucketLabels { get; set; }
    public required int SnapshotTries { get; set; }
    public required int FacedMemory { get; set; }
    public required long SetPersonaBase { get; set; }
    public required long FallbackItemBase { get; set; }
    public required long FallbackSetsCycled { get; set; }
    public required long BadgeResourceBase { get; set; }
    public required List<long> DefaultTactics { get; set; }
    public required Dictionary<int, string> NationIso { get; set; }

    /// <summary>The Easy, Medium and Hard buckets, in that order.</summary>
    public static readonly string[] Buckets = ["EASY", "MEDIUM", "HARD"];

    /// <summary>The summary's nation: the most common nationality among the starters (the first seen wins a tie); empty when none is known.</summary>
    public string NationCode(IEnumerable<long> nationIds)
    {
        var known = nationIds.Where(id => NationIso.ContainsKey((int)id)).ToList();
        return known.Count == 0 ? string.Empty
            : NationIso[(int)known.GroupBy(id => id).OrderByDescending(group => group.Count()).ThenBy(group => known.IndexOf(group.Key)).First().Key];
    }

    /// <summary>The summary's <c>custom</c>: the squad's own 30-value tactics array, else Balanced (defaultTactics as elements 22-29).</summary>
    public string TacticsCustom(string? stored)
    {
        if (stored is not null && FutJson.Parse(stored) is JsonArray values && values.Count >= 30
            && values.Take(30).All(value => value is JsonValue number && number.GetValueKind() == System.Text.Json.JsonValueKind.Number))
        {
            return "[" + string.Join(",", values.Take(30).Select(value => value!.ToJsonString())) + "]";
        }
        return "[" + string.Join(",", Enumerable.Repeat(0L, 22).Concat(DefaultTactics)) + "]";
    }
}

/// <summary>The Team of the Week's ids and art (squad_battles.json <c>totw</c>, Python server a4fdc85). See its _totwComment.</summary>
public sealed class SquadBattlesTotwRules
{
    public required long IdBase { get; set; }
    public required long Crest { get; set; }
    public required long PersonaBase { get; set; }
    public required long ItemBase { get; set; }
    public required long StadiumItemBase { get; set; }
    public required long KitSlot { get; set; }
    public required string Difficulty { get; set; }
    public required string ArtFolder { get; set; }
    public required string BannerFormat { get; set; }
    public required long CacheSeconds { get; set; }

    public long SquadId(long week) => IdBase + week;

    /// <summary>The TOTW week a featured squad id names, or null for any other id.</summary>
    public long? WeekOf(long squadId) => squadId - IdBase is var week && week is > 0 and < 1000 ? week : null;

    public string BannerName(long week) => BannerFormat.Replace("{week}", week.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
