using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// A club's Division Rivals record under the retail rules (migration 058): Skill Rating and placement, the weekly
/// competition's fixed division, the Weekly Score, FUT Champions Points, the last five results and the last week's result.
/// </summary>
public sealed class RivalsRecord
{
    public long RatingVersion { get; set; }
    public long? WeekId { get; set; }
    public long SkillRating { get; set; }
    public bool Placed { get; set; }
    public long PlacementPlayed { get; set; }
    public long Wins { get; set; }
    public long Draws { get; set; }
    public long Losses { get; set; }
    public long MatchesPlayed { get; set; }
    public long TitleWins { get; set; }
    public long WeeklyWins { get; set; }
    public long WeeklyDraws { get; set; }
    public long WeeklyGames { get; set; }

    /// <summary>The weekly competition's own division, fixed for the week.</summary>
    public int WeekDivision { get; set; }

    /// <summary>The best division ever entered (11 before placement): promotion coins are paid once per division.</summary>
    public int BestDivision { get; set; }

    public long WeeklyScore { get; set; }

    /// <summary>The week's matches that counted toward the Weekly Score (at most the match cap).</summary>
    public long WeeklyCounted { get; set; }

    public long FcPoints { get; set; }

    /// <summary>The last five results, oldest first: "W", "D" or "L" each.</summary>
    public string Form { get; set; } = string.Empty;

    public long LastDelta { get; set; }
    public long? LastWeekId { get; set; }
    public int LastWeekDivision { get; set; }
    public long LastWeekScore { get; set; }
    public long LastWeekGames { get; set; }

    /// <summary>
    /// The division the club stands in (10 during placement): reached by Skill Rating during the week and kept until the
    /// rating falls below its relegation line; next week's division.
    /// </summary>
    public int Division { get; set; }

    public static RivalsRecord Fresh(RivalsRules rules, long ratingVersion) => new()
    {
        RatingVersion = ratingVersion,
        SkillRating = rules.PlacementStart,
        WeekDivision = rules.DivisionCount,
        BestDivision = rules.DivisionCount + 1,
        Division = rules.DivisionCount,
    };
}

public static class RivalsRepository
{
    internal const string Columns = """
        rating_version, season_id, skill_rating, placed, placement_played, wins, draws, losses, matches_played, title_wins,
        weekly_wins, weekly_draws, weekly_games, week_division, best_division, weekly_score, weekly_counted, fc_points, form,
        last_delta, last_week_id, last_week_division, last_week_score, last_week_games, division
        """;

    /// <summary>This club's stored record, or null when it has none.</summary>
    public static async ValueTask<RivalsRecord?> RivalsRecordAsync(this IUnitOfWork work) =>
        (await work.QueryAsync($"SELECT {Columns} FROM ultimateteam.rivals_progress WHERE club_id = @club", reader => ReadRecord(reader, 0))).FirstOrDefault();

    /// <summary>A record from its <see cref="Columns"/>, read from column <paramref name="at"/> on.</summary>
    internal static RivalsRecord ReadRecord(NpgsqlDataReader reader, int at) => new()
    {
        RatingVersion = NullableLong(reader, at + 0) ?? 0,
        WeekId = NullableLong(reader, at + 1),
        SkillRating = NullableLong(reader, at + 2) ?? 0,
        Placed = reader.GetBoolean(at + 3),
        PlacementPlayed = reader.GetInt64(at + 4),
        Wins = NullableLong(reader, at + 5) ?? 0,
        Draws = NullableLong(reader, at + 6) ?? 0,
        Losses = NullableLong(reader, at + 7) ?? 0,
        MatchesPlayed = NullableLong(reader, at + 8) ?? 0,
        TitleWins = NullableLong(reader, at + 9) ?? 0,
        WeeklyWins = NullableLong(reader, at + 10) ?? 0,
        WeeklyDraws = NullableLong(reader, at + 11) ?? 0,
        WeeklyGames = NullableLong(reader, at + 12) ?? 0,
        WeekDivision = (int)reader.GetInt64(at + 13),
        BestDivision = (int)reader.GetInt64(at + 14),
        WeeklyScore = reader.GetInt64(at + 15),
        WeeklyCounted = reader.GetInt64(at + 16),
        FcPoints = reader.GetInt64(at + 17),
        Form = reader.GetString(at + 18),
        LastDelta = reader.GetInt64(at + 19),
        LastWeekId = NullableLong(reader, at + 20),
        LastWeekDivision = (int)(NullableLong(reader, at + 21) ?? 0),
        LastWeekScore = NullableLong(reader, at + 22) ?? 0,
        LastWeekGames = NullableLong(reader, at + 23) ?? 0,
        Division = reader.IsDBNull(at + 24) ? (int)reader.GetInt64(at + 13) : System.Convert.ToInt32(reader.GetValue(at + 24), CultureInfo.InvariantCulture),
    };

    /// <summary>Writes the whole record; <c>weekly_points</c> carries the Weekly Score for the old readers.</summary>
    public static ValueTask<int> SaveRivalsRecordAsync(this IUnitOfWork work, RivalsRecord record) =>
        work.ExecuteAsync($"""
            INSERT INTO ultimateteam.rivals_progress(club_id, {Columns}, weekly_points)
            VALUES (@club, @version, @week, @rating, @placed, @placementPlayed, @wins, @draws, @losses, @matches, @titles,
                @weeklyWins, @weeklyDraws, @weeklyGames, @weekDivision, @bestDivision, @weeklyScore, @weeklyCounted, @fcPoints, @form,
                @lastDelta, @lastWeek, @lastWeekDivision, @lastWeekScore, @lastWeekGames, @division, @weeklyScore)
            ON CONFLICT (club_id) DO UPDATE SET rating_version = EXCLUDED.rating_version, season_id = EXCLUDED.season_id,
                skill_rating = EXCLUDED.skill_rating, placed = EXCLUDED.placed, placement_played = EXCLUDED.placement_played,
                wins = EXCLUDED.wins, draws = EXCLUDED.draws, losses = EXCLUDED.losses, matches_played = EXCLUDED.matches_played,
                title_wins = EXCLUDED.title_wins, weekly_wins = EXCLUDED.weekly_wins, weekly_draws = EXCLUDED.weekly_draws,
                weekly_games = EXCLUDED.weekly_games, week_division = EXCLUDED.week_division, best_division = EXCLUDED.best_division,
                weekly_score = EXCLUDED.weekly_score, weekly_counted = EXCLUDED.weekly_counted, fc_points = EXCLUDED.fc_points,
                form = EXCLUDED.form, last_delta = EXCLUDED.last_delta, last_week_id = EXCLUDED.last_week_id,
                last_week_division = EXCLUDED.last_week_division, last_week_score = EXCLUDED.last_week_score,
                last_week_games = EXCLUDED.last_week_games, division = EXCLUDED.division, weekly_points = EXCLUDED.weekly_points,
                placement_matches_played = NULL
            """,
            ("version", record.RatingVersion), ("week", record.WeekId), ("rating", record.SkillRating), ("placed", record.Placed),
            ("placementPlayed", record.PlacementPlayed), ("wins", record.Wins), ("draws", record.Draws), ("losses", record.Losses),
            ("matches", record.MatchesPlayed), ("titles", record.TitleWins), ("weeklyWins", record.WeeklyWins),
            ("weeklyDraws", record.WeeklyDraws), ("weeklyGames", record.WeeklyGames), ("weekDivision", (long)record.WeekDivision),
            ("bestDivision", (long)record.BestDivision), ("weeklyScore", record.WeeklyScore), ("weeklyCounted", record.WeeklyCounted),
            ("fcPoints", record.FcPoints), ("form", record.Form), ("lastDelta", record.LastDelta), ("lastWeek", record.LastWeekId),
            ("lastWeekDivision", record.LastWeekId is null ? null : (long?)record.LastWeekDivision),
            ("lastWeekScore", record.LastWeekId is null ? null : (long?)record.LastWeekScore),
            ("lastWeekGames", record.LastWeekId is null ? null : (long?)record.LastWeekGames), ("division", (long)record.Division));

    /// <summary>
    /// Every club's result for one weekly competition under the current rules: the live week's columns while the club has
    /// not rolled over, otherwise its kept last week.
    /// </summary>
    public static ValueTask<List<RivalsWeekRow>> RivalsWeekRowsAsync(this IUnitOfWork work, long week, long ratingVersion) =>
        work.QueryAsync("""
            -- query: rivals.week-rows
            SELECT club_id,
                   CASE WHEN season_id = @week THEN week_division ELSE last_week_division END,
                   CASE WHEN season_id = @week THEN weekly_score ELSE last_week_score END,
                   CASE WHEN season_id = @week THEN coalesce(weekly_games, 0) ELSE last_week_games END
            FROM ultimateteam.rivals_progress
            WHERE rating_version >= @version AND (season_id = @week OR last_week_id = @week)
            """,
            reader => new RivalsWeekRow(reader.GetInt64(0), (int)reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
            ("week", week), ("version", ratingVersion));

    /// <summary>A settled week's thresholds per division and its award time, or null when it has not settled.</summary>
    public static async ValueTask<(long AwardTime, JsonObject Divisions)?> RivalsSettlementAsync(this IUnitOfWork work, long week) =>
        (await work.QueryAsync("SELECT award_time, divisions::text FROM ultimateteam.rivals_week_settlements WHERE week_id = @week",
            reader => (reader.GetInt64(0), FutJson.ParseObject(reader.GetString(1)) ?? new JsonObject()), ("week", week)))
        .Select(row => ((long, JsonObject)?)row).FirstOrDefault();

    /// <summary>Claims a week's settlement; false when another settlement already holds it (it then waits for that one).</summary>
    public static async ValueTask<bool> TryBeginRivalsSettlementAsync(this IUnitOfWork work, long week, long awardTime, JsonObject divisions) =>
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.rivals_week_settlements(week_id, settled_at, award_time, divisions) VALUES (@week, @now, @award, @divisions)
            ON CONFLICT (week_id) DO NOTHING
            """, ("week", week), ("now", work.NowSeconds), ("award", awardTime), ("divisions", Json(divisions))) == 1;

    public static ValueTask<int> AddRivalsWeekResultAsync(this IUnitOfWork work, long week, long clubId, int division, int rank, long score, JsonArray options) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.rivals_week_results(week_id, club_id, division, rank, score, options)
            VALUES (@week, @clubId, @division, @rank, @score, @options)
            """, ("week", week), ("clubId", clubId), ("division", division), ("rank", rank), ("score", score), ("options", Json(options)));

    /// <summary>Which of these weeks have settled.</summary>
    public static async ValueTask<HashSet<long>> RivalsSettledWeeksAsync(this IUnitOfWork work, IReadOnlyList<long> weeks) =>
        (await work.QueryAsync("-- query: rivals.settled\nSELECT week_id FROM ultimateteam.rivals_week_settlements WHERE week_id = ANY(@weeks)",
            reader => reader.GetInt64(0), ("weeks", weeks.ToArray()))).ToHashSet();

    /// <summary>This club's oldest unclaimed weekly result between two weeks, with its week's award time and thresholds.</summary>
    public static async ValueTask<(RivalsWeekResult Result, long AwardTime, JsonObject Divisions)?> OldestUnclaimedRivalsResultAsync(this IUnitOfWork work,
        long from, long to) =>
        (await work.QueryAsync("""
            -- query: rivals.unclaimed
            SELECT r.week_id, r.division, r.rank, r.score, r.options::text, s.award_time, s.divisions::text
            FROM ultimateteam.rivals_week_results r JOIN ultimateteam.rivals_week_settlements s ON s.week_id = r.week_id
            WHERE r.club_id = @club AND r.claimed_at IS NULL AND r.week_id BETWEEN @from AND @to
            ORDER BY r.week_id
            LIMIT 1
            """,
            reader => (new RivalsWeekResult(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), Options(reader.GetString(4))),
                reader.GetInt64(5), FutJson.ParseObject(reader.GetString(6)) ?? new JsonObject()),
            ("from", from), ("to", to)))
        .Select(row => ((RivalsWeekResult, long, JsonObject)?)row).FirstOrDefault();

    internal static List<RewardOption> Options(string json) =>
        (FutJson.Parse(json) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(RewardOption.FromJson).ToList();

    /// <summary>This club's result in a settled week, claimed or not.</summary>
    public static async ValueTask<RivalsWeekResult?> RivalsResultAsync(this IUnitOfWork work, long week) =>
        (await work.QueryAsync("""
            SELECT week_id, division, rank, score, options::text FROM ultimateteam.rivals_week_results
            WHERE club_id = @club AND week_id = @week
            """,
            reader => new RivalsWeekResult(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), Options(reader.GetString(4))),
            ("week", week))).FirstOrDefault();

    /// <summary>Marks a week's reward claimed; false when it was already claimed.</summary>
    public static async ValueTask<bool> ClaimRivalsResultAsync(this IUnitOfWork work, long week) =>
        await work.ExecuteAsync("UPDATE ultimateteam.rivals_week_results SET claimed_at = @now WHERE club_id = @club AND week_id = @week AND claimed_at IS NULL",
            ("week", week), ("now", work.NowSeconds)) == 1;

    /// <summary>The club header's record (Python server 17172fc): one more win, draw or loss.</summary>
    public static ValueTask<int> AddClubRecordAsync(this IUnitOfWork work, long won, long draw, long loss) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.club_records(club_id, won, draw, loss) VALUES (@club, @won, @draw, @loss)
            ON CONFLICT (club_id) DO UPDATE SET won = club_records.won + EXCLUDED.won, draw = club_records.draw + EXCLUDED.draw,
                loss = club_records.loss + EXCLUDED.loss
            """, ("won", won), ("draw", draw), ("loss", loss));

    /// <summary>
    /// Every named club with its Rivals leaderboard values: the Skill Rating once placed under the current rules (0 until
    /// then), all-time wins, matches and losses, its nucleus id and its FUT Champions region (<paramref name="defaultRegion"/>
    /// when never registered).
    /// </summary>
    public static ValueTask<List<(long PersonaId, string Name, long Rating, long Wins, long Matches, long Losses, long NucleusId, string Region)>> RivalsBoardAsync(
        this IUnitOfWork work, long ratingVersion, string defaultRegion) =>
        work.QueryAsync("""
            -- query: rivals.board
            SELECT c.persona_id, c.display_name,
                   CASE WHEN p.rating_version >= @version AND p.placed THEN coalesce(p.skill_rating, 0) ELSE 0 END,
                   CASE WHEN p.rating_version >= @version THEN coalesce(p.wins, 0) ELSE 0 END,
                   CASE WHEN p.rating_version >= @version THEN coalesce(p.matches_played, 0) ELSE 0 END,
                   CASE WHEN p.rating_version >= @version THEN coalesce(p.losses, 0) ELSE 0 END,
                   c.nucleus_id, coalesce(g.region, @region)
            FROM ultimateteam.clubs c LEFT JOIN ultimateteam.rivals_progress p ON p.club_id = c.id
            LEFT JOIN ultimateteam.champion_registrations g ON g.club_id = c.id
            WHERE c.display_name IS NOT NULL AND btrim(c.display_name) <> ''
            ORDER BY c.id
            """,
            reader => (reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetString(7)),
            ("version", ratingVersion), ("region", defaultRegion));

    /// <summary>
    /// A club's Leaderboards details (the info panel's clubInfo): its all-mode record and Division Rivals titles, found by
    /// persona id; null when no club has it.
    /// </summary>
    public static async ValueTask<(long Won, long Draw, long Loss, long Titles)?> LeaderboardClubInfoAsync(this IUnitOfWork work, long personaId) =>
        (await work.QueryAsync("""
            -- query: rivals.board-club-info
            SELECT coalesce(r.won, 0), coalesce(r.draw, 0), coalesce(r.loss, 0), coalesce(p.title_wins, 0)
            FROM ultimateteam.clubs c
            LEFT JOIN ultimateteam.club_records r ON r.club_id = c.id
            LEFT JOIN ultimateteam.rivals_progress p ON p.club_id = c.id
            WHERE c.persona_id = @persona
            ORDER BY c.id
            LIMIT 1
            """,
            reader => ((long, long, long, long)?)(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
            ("persona", personaId))).FirstOrDefault();
}

/// <summary>A club's settled weekly result: its division, rank, Weekly Score and the reward options fixed at settlement.</summary>
public sealed record RivalsWeekResult(long Week, int Division, int Rank, long Score, List<RewardOption> Options);
