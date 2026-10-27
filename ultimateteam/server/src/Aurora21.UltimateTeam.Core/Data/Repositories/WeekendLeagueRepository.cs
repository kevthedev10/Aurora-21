using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>A club's record in one Weekend League (form -10 to +15).</summary>
public sealed record WeekendLeagueRecord(long EventId, long Games, long Wins, long Draws, long Losses, long Form);

/// <summary>A club's settled Weekend League result: its final position and rank, and the reward fixed at settlement.</summary>
public sealed record ChampionResult(long EventId, long Position, int TierLevel, string RankName, long Wins, long Games, RewardOption Reward);

/// <summary>One club on a FUT Champions leaderboard: its record summed over the board's Weekend Leagues.</summary>
public sealed record ChampionBoardRow(long ClubId, long PersonaId, long NucleusId, string Name, long Wins, long Draws, long Losses, long Games, long Form,
    long Rating, string Region);

/// <summary>The FUT Champions Weekend League tables (migration 058).</summary>
public static class WeekendLeagueRepository
{
    public static async ValueTask<List<long>> WeekendLeagueEntriesAsync(this IUnitOfWork work) =>
        await work.QueryAsync("SELECT event_id FROM ultimateteam.weekend_league_entries WHERE club_id = @club ORDER BY event_id", reader => reader.GetInt64(0));

    /// <summary>Records a redeemed entry and forgets the <paramref name="expired"/> ones (Weekend Leagues long finished).</summary>
    public static async ValueTask AddWeekendLeagueEntryAsync(this IUnitOfWork work, long eventId, long points, IEnumerable<long> expired)
    {
        await work.ExecuteAsync("DELETE FROM ultimateteam.weekend_league_entries WHERE club_id = @club AND event_id = ANY(@expired)",
            ("expired", expired.ToArray()));
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.weekend_league_entries(club_id, event_id, redeemed_at, points) VALUES (@club, @event, @now, @points)
            ON CONFLICT (club_id, event_id) DO NOTHING
            """, ("event", eventId), ("now", work.NowSeconds), ("points", points));
    }

    public static async ValueTask<(string Country, string Region)?> ChampionRegistrationAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("SELECT country, region FROM ultimateteam.champion_registrations WHERE club_id = @club",
            reader => (reader.GetString(0), reader.GetString(1)))).Select(row => ((string, string)?)row).FirstOrDefault();

    public static ValueTask<int> SaveChampionRegistrationAsync(this IUnitOfWork work, string country, string region) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.champion_registrations(club_id, country, region) VALUES (@club, @country, @region)
            ON CONFLICT (club_id) DO UPDATE SET country = EXCLUDED.country, region = EXCLUDED.region
            """, ("country", country), ("region", region));

    /// <summary>Remembers a Weekend League CreateMatch for the next match end; null clears it (another mode's CreateMatch).</summary>
    public static ValueTask<int> SetChampionPendingAsync(this IUnitOfWork work, long? leagueId) =>
        leagueId is { } league
            ? work.ExecuteAsync("""
                INSERT INTO ultimateteam.champion_pending(club_id, league_id, created_at) VALUES (@club, @league, @now)
                ON CONFLICT (club_id) DO UPDATE SET league_id = EXCLUDED.league_id, created_at = EXCLUDED.created_at
                """, ("league", league), ("now", work.NowSeconds))
            : work.ExecuteAsync("DELETE FROM ultimateteam.champion_pending WHERE club_id = @club");

    /// <summary>When the pending Weekend League CreateMatch was created, without taking it; null when there is none.</summary>
    public static async ValueTask<long?> ChampionPendingSinceAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("SELECT created_at FROM ultimateteam.champion_pending WHERE club_id = @club",
            reader => reader.GetInt64(0))).Select(at => (long?)at).FirstOrDefault();

    /// <summary>Takes the pending Weekend League CreateMatch: its league id and when it was created.</summary>
    public static async ValueTask<(long LeagueId, long CreatedAt)?> TakeChampionPendingAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("DELETE FROM ultimateteam.champion_pending WHERE club_id = @club RETURNING league_id, created_at",
            reader => (reader.GetInt64(0), reader.GetInt64(1)))).Select(row => ((long, long)?)row).FirstOrDefault();

    /// <summary>Holds a lost-connection claim for a game (migration 061), replacing any earlier one.</summary>
    public static ValueTask<int> SaveChampionClaimAsync(this IUnitOfWork work, string gameId, long matchAt) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.champion_connection_claims(club_id, game_id, created_at, match_at) VALUES (@club, @game, @now, @matchAt)
            ON CONFLICT (club_id) DO UPDATE SET game_id = EXCLUDED.game_id, created_at = EXCLUDED.created_at, match_at = EXCLUDED.match_at
            """, ("game", gameId), ("now", work.NowSeconds), ("matchAt", matchAt));

    /// <summary>Takes this club's claim when it was held at or before <paramref name="dueBy"/>; null otherwise.</summary>
    public static async ValueTask<(string GameId, long MatchAt)?> TakeDueChampionClaimAsync(this IUnitOfWork work, long dueBy) =>
        (await work.QueryAsync("""
            -- query: champions.claim.take
            DELETE FROM ultimateteam.champion_connection_claims WHERE club_id = @club AND created_at <= @due RETURNING game_id, match_at
            """, reader => (reader.GetString(0), reader.GetInt64(1)), ("due", dueBy))).Select(row => ((string, long)?)row).FirstOrDefault();

    public static async ValueTask<WeekendLeagueRecord> WeekendLeagueRecordAsync(this IUnitOfWork work, long eventId) =>
        (await work.QueryAsync("""
            SELECT games, wins, draws, losses, form FROM ultimateteam.weekend_league_records WHERE club_id = @club AND event_id = @event
            """, reader => new WeekendLeagueRecord(eventId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
            ("event", eventId))).FirstOrDefault() ?? new WeekendLeagueRecord(eventId, 0, 0, 0, 0, 0);

    public static ValueTask<int> SaveWeekendLeagueRecordAsync(this IUnitOfWork work, WeekendLeagueRecord record) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.weekend_league_records(club_id, event_id, games, wins, draws, losses, form)
            VALUES (@club, @event, @games, @wins, @draws, @losses, @form)
            ON CONFLICT (club_id, event_id) DO UPDATE SET games = EXCLUDED.games, wins = EXCLUDED.wins, draws = EXCLUDED.draws,
                losses = EXCLUDED.losses, form = EXCLUDED.form
            """, ("event", record.EventId), ("games", record.Games), ("wins", record.Wins), ("draws", record.Draws),
            ("losses", record.Losses), ("form", record.Form));

    /// <summary>This club's records in the given Weekend Leagues, by event.</summary>
    public static async ValueTask<Dictionary<long, WeekendLeagueRecord>> WeekendLeagueRecordsAsync(this IUnitOfWork work, IEnumerable<long> events) =>
        (await work.QueryAsync("""
            -- query: weekend-league.records
            SELECT event_id, games, wins, draws, losses, form FROM ultimateteam.weekend_league_records WHERE club_id = @club AND event_id = ANY(@events)
            """, reader => new WeekendLeagueRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)),
            ("events", events.ToArray()))).ToDictionary(record => record.EventId);

    /// <summary>
    /// Every named club with a match in the given Weekend Leagues, its record summed over them (form for a single one), its
    /// ranked Rivals Skill Rating (0 until placed) and its region (<paramref name="defaultRegion"/> when never registered).
    /// </summary>
    public static ValueTask<List<ChampionBoardRow>> ChampionBoardRowsAsync(this IUnitOfWork work, long[] events, long ratingVersion, string defaultRegion) =>
        work.QueryAsync("""
            -- query: champions.board
            SELECT c.id, c.persona_id, c.nucleus_id, c.display_name, sum(r.wins)::bigint, sum(r.draws)::bigint, sum(r.losses)::bigint,
                   sum(r.games)::bigint, CASE WHEN cardinality(@events) = 1 THEN max(r.form) ELSE 0 END,
                   max(CASE WHEN p.rating_version >= @version AND p.placed THEN coalesce(p.skill_rating, 0) ELSE 0 END),
                   coalesce(max(g.region), @region)
            FROM ultimateteam.weekend_league_records r
            JOIN ultimateteam.clubs c ON c.id = r.club_id
            LEFT JOIN ultimateteam.rivals_progress p ON p.club_id = c.id
            LEFT JOIN ultimateteam.champion_registrations g ON g.club_id = c.id
            WHERE r.event_id = ANY(@events) AND c.display_name IS NOT NULL AND btrim(c.display_name) <> ''
            GROUP BY c.id, c.persona_id, c.nucleus_id, c.display_name
            HAVING sum(r.games) > 0
            """, reader => new ChampionBoardRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10)),
            ("events", events), ("version", ratingVersion), ("region", defaultRegion));

    /// <summary>Every club's record in one Weekend League, with its ranked Rivals Skill Rating (0 until placed) for tie-breaks.</summary>
    public static ValueTask<List<(long ClubId, long Wins, long Games, long Rating)>> WeekendLeagueRowsAsync(this IUnitOfWork work, long eventId, long ratingVersion) =>
        work.QueryAsync("""
            -- query: weekend-league.rows
            SELECT r.club_id, r.wins, r.games,
                   CASE WHEN p.rating_version >= @version AND p.placed THEN coalesce(p.skill_rating, 0) ELSE 0 END
            FROM ultimateteam.weekend_league_records r LEFT JOIN ultimateteam.rivals_progress p ON p.club_id = r.club_id
            WHERE r.event_id = @event
            """, reader => (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
            ("event", eventId), ("version", ratingVersion));

    /// <summary>Which of these Weekend Leagues have settled.</summary>
    public static async ValueTask<HashSet<long>> ChampionSettledEventsAsync(this IUnitOfWork work, IReadOnlyList<long> events) =>
        (await work.QueryAsync("-- query: champions.settled\nSELECT event_id FROM ultimateteam.champion_settlements WHERE event_id = ANY(@events)",
            reader => reader.GetInt64(0), ("events", events.ToArray()))).ToHashSet();

    /// <summary>Claims a Weekend League's settlement; false when another settlement already holds it.</summary>
    public static async ValueTask<bool> TryBeginChampionSettlementAsync(this IUnitOfWork work, long eventId, long participants) =>
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.champion_settlements(event_id, settled_at, participants) VALUES (@event, @now, @participants)
            ON CONFLICT (event_id) DO NOTHING
            """, ("event", eventId), ("now", work.NowSeconds), ("participants", participants)) == 1;

    public static ValueTask<int> AddChampionResultAsync(this IUnitOfWork work, long eventId, ChampionsStanding standing) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.champion_results(event_id, club_id, position, tier_level, rank_name, wins, games, reward)
            VALUES (@event, @clubId, @position, @tier, @name, @wins, @games, @reward)
            """, ("event", eventId), ("clubId", standing.ClubId), ("position", standing.Position), ("tier", standing.Rank.TierLevel),
            ("name", standing.Rank.Name), ("wins", standing.Wins), ("games", standing.Games),
            ("reward", Json(standing.Reward.ToJson())));

    /// <summary>This club's unclaimed results in the given Weekend Leagues, oldest first.</summary>
    public static ValueTask<List<ChampionResult>> UnclaimedChampionResultsAsync(this IUnitOfWork work, long fromEvent, long toEvent) =>
        work.QueryAsync("""
            SELECT event_id, position, tier_level, rank_name, wins, games, reward::text FROM ultimateteam.champion_results
            WHERE club_id = @club AND claimed_at IS NULL AND event_id BETWEEN @from AND @to
            ORDER BY event_id
            """, reader => new ChampionResult(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt64(4),
                reader.GetInt64(5), RewardOption.FromJson(FutJson.ParseObject(reader.GetString(6)))),
            ("from", fromEvent), ("to", toEvent));

    /// <summary>Marks the results claimed; returns the events actually claimed now (a concurrent claim takes none twice).</summary>
    public static async ValueTask<HashSet<long>> ClaimChampionResultsAsync(this IUnitOfWork work, IEnumerable<long> events) =>
        new(await work.QueryAsync("""
            UPDATE ultimateteam.champion_results SET claimed_at = @now
            WHERE club_id = @club AND claimed_at IS NULL AND event_id = ANY(@events)
            RETURNING event_id
            """, reader => reader.GetInt64(0), ("now", work.NowSeconds), ("events", events.ToArray())));
}
