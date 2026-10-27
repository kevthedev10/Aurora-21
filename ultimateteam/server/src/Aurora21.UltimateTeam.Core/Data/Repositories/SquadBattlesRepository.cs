using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// A club's Squad Battles week: points, community matches played and skipped by a refresh, TOTW matches, results, the
/// slots of replaced sets that already scored ("setWeek:setIndex:slot") and this week's TOTW result.
/// </summary>
public sealed record SquadBattlesWeek(long Week, long Points, int Games, int Skipped, int SpecialGames, int Wins, int Draws, int Losses,
    string[] Played, JsonObject? Totw)
{
    public static SquadBattlesWeek Blank(long week) => new(week, 0, 0, 0, 0, 0, 0, 0, [], null);

    /// <summary>The hub's gamesPlayed: community matches plus skipped squads, at most the week's count.</summary>
    public int Counted(int maxRegularGames) => Math.Min(maxRegularGames, Games + Skipped);
}

/// <summary>A club's current opponents: which draw of which week, when it may refresh, the clubs faced and the four squads.</summary>
public sealed record SquadBattlesSet(long Week, int Index, long CreatedAt, long NextRefreshAt, long[] Faced, JsonArray Squads);

/// <summary>The Squad Battles match a CreateMatch named: a slot of the set drawn then, or the TOTW (featuredSquadId).</summary>
public sealed record SquadBattlesPending(string Kind, int Slot, int Difficulty, long Week, long CreatedAt, long? FeaturedSquadId, long? SetWeek, int? SetIndex);

/// <summary>A club's settled weekly result with a prize.</summary>
public sealed record SquadBattlesResult(long Week, int Position, int Rank, string TierId, int TierLevel, long Points, List<SquadBattlesAward> Awards);

/// <summary>One club in the opponent pool: its active XI's average rating and the XI's cards, to tell copies apart.</summary>
public sealed record SquadBattlesPoolEntry(long ClubId, string Name, double Rating, string Signature);

public static class SquadBattlesRepository
{
    public static async ValueTask<SquadBattlesWeek> SquadBattlesWeekAsync(this IUnitOfWork work, long week) =>
        (await work.QueryAsync("""
            -- query: sqbt.week.read
            SELECT points, games, skipped, special_games, wins, draws, losses, played, totw::text
            FROM ultimateteam.squad_battles_weeks WHERE club_id = @club AND week_id = @week
            """, reader => new SquadBattlesWeek(week, reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetFieldValue<string[]>(7), reader.IsDBNull(8) ? null : FutJson.ParseObject(reader.GetString(8))),
            ("week", week))).FirstOrDefault() ?? SquadBattlesWeek.Blank(week);

    public static ValueTask<int> SaveSquadBattlesWeekAsync(this IUnitOfWork work, SquadBattlesWeek record) =>
        work.ExecuteAsync("""
            -- query: sqbt.week.save
            INSERT INTO ultimateteam.squad_battles_weeks(club_id, week_id, points, games, skipped, special_games, wins, draws, losses, played, totw)
            VALUES (@club, @week, @points, @games, @skipped, @special, @wins, @draws, @losses, @played, @totw)
            ON CONFLICT (club_id, week_id) DO UPDATE SET points = EXCLUDED.points, games = EXCLUDED.games, skipped = EXCLUDED.skipped,
                special_games = EXCLUDED.special_games, wins = EXCLUDED.wins, draws = EXCLUDED.draws, losses = EXCLUDED.losses,
                played = EXCLUDED.played, totw = EXCLUDED.totw
            """, ("week", record.Week), ("points", record.Points), ("games", record.Games), ("skipped", record.Skipped),
            ("special", record.SpecialGames), ("wins", record.Wins), ("draws", record.Draws), ("losses", record.Losses), ("played", record.Played),
            ("totw", record.Totw is null ? null : Json(record.Totw)));

    public static async ValueTask<SquadBattlesSet?> SquadBattlesSetAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("""
            -- query: sqbt.set.read
            SELECT week_id, set_index, created_at, next_refresh_at, faced, squads::text FROM ultimateteam.squad_battles_sets WHERE club_id = @club
            """, reader => new SquadBattlesSet(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetFieldValue<long[]>(4),
                FutJson.Parse(reader.GetString(5)) as JsonArray ?? new JsonArray()))).FirstOrDefault();

    public static ValueTask<int> SaveSquadBattlesSetAsync(this IUnitOfWork work, SquadBattlesSet set) =>
        work.ExecuteAsync("""
            -- query: sqbt.set.save
            INSERT INTO ultimateteam.squad_battles_sets(club_id, week_id, set_index, created_at, next_refresh_at, faced, squads)
            VALUES (@club, @week, @index, @created, @refresh, @faced, @squads)
            ON CONFLICT (club_id) DO UPDATE SET week_id = EXCLUDED.week_id, set_index = EXCLUDED.set_index, created_at = EXCLUDED.created_at,
                next_refresh_at = EXCLUDED.next_refresh_at, faced = EXCLUDED.faced, squads = EXCLUDED.squads
            """, ("week", set.Week), ("index", set.Index), ("created", set.CreatedAt), ("refresh", set.NextRefreshAt), ("faced", set.Faced),
            ("squads", Json(set.Squads)));

    /// <summary>Remembers a Squad Battles CreateMatch; null clears it (another mode's CreateMatch).</summary>
    public static ValueTask<int> SetSquadBattlesPendingAsync(this IUnitOfWork work, SquadBattlesPending? pending) =>
        pending is null
            ? work.ExecuteAsync("-- query: sqbt.pending.clear\nDELETE FROM ultimateteam.squad_battles_pending WHERE club_id = @club")
            : work.ExecuteAsync("""
                -- query: sqbt.pending.record
                INSERT INTO ultimateteam.squad_battles_pending(club_id, slot, difficulty, cycle, week_id, created_at, kind, featured_squad_id, set_week, set_index)
                VALUES (@club, @slot, @difficulty, 0, @week, @now, @kind, @featured, @setWeek, @setIndex)
                ON CONFLICT (club_id) DO UPDATE SET slot = EXCLUDED.slot, difficulty = EXCLUDED.difficulty, cycle = EXCLUDED.cycle,
                    week_id = EXCLUDED.week_id, created_at = EXCLUDED.created_at, kind = EXCLUDED.kind, featured_squad_id = EXCLUDED.featured_squad_id,
                    set_week = EXCLUDED.set_week, set_index = EXCLUDED.set_index
                """, ("slot", pending.Slot), ("difficulty", pending.Difficulty), ("week", pending.Week), ("now", pending.CreatedAt),
                ("kind", pending.Kind), ("featured", pending.FeaturedSquadId), ("setWeek", pending.SetWeek), ("setIndex", pending.SetIndex));

    public static async ValueTask<SquadBattlesPending?> TakeSquadBattlesPendingAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("""
            -- query: sqbt.pending.take
            DELETE FROM ultimateteam.squad_battles_pending WHERE club_id = @club
            RETURNING kind, slot, difficulty, week_id, created_at, featured_squad_id, set_week, set_index
            """, reader => new SquadBattlesPending(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4),
                NullableLong(reader, 5), NullableLong(reader, 6), reader.IsDBNull(7) ? null : reader.GetInt32(7)))).FirstOrDefault();

    /// <summary>Every named club's week with points, for the board and the standings.</summary>
    public static ValueTask<List<SquadBattlesWeekRow>> SquadBattlesWeekRowsAsync(this IUnitOfWork work, long week) =>
        work.QueryAsync("""
            -- query: sqbt.week.rows
            SELECT c.id, c.display_name, w.points, w.wins, w.games FROM ultimateteam.squad_battles_weeks w
            JOIN ultimateteam.clubs c ON c.id = w.club_id
            WHERE w.week_id = @week AND w.points > 0 AND c.display_name IS NOT NULL AND btrim(c.display_name) <> ''
            """, reader => new SquadBattlesWeekRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4)),
            ("week", week));

    /// <summary>
    /// Every other named club whose active squad has eleven owned starters and none of <paramref name="excludedResources"/>
    /// anywhere in it: its average starter rating and the XI's cards (the same XI on two clubs is one candidate).
    /// </summary>
    public static ValueTask<List<SquadBattlesPoolEntry>> SquadBattlesPoolAsync(this IUnitOfWork work, long[] excludedResources) =>
        work.QueryAsync("""
            -- query: sqbt.pool
            SELECT DISTINCT ON (s.club_id) s.club_id, c.display_name, avg(d.rating)::float8,
                   string_agg(p.definition_id::text, ',' ORDER BY p.definition_id)
            FROM ultimateteam.squads s
            JOIN ultimateteam.clubs c ON c.id = s.club_id
            JOIN ultimateteam.squad_players p ON p.squad_id = s.id AND p.slot_index < 11
            JOIN ultimateteam.player_card_definitions d ON d.definition_id = p.definition_id
            WHERE s.active AND s.club_id <> @club AND c.display_name IS NOT NULL AND btrim(c.display_name) <> ''
            GROUP BY s.club_id, s.id, c.display_name
            HAVING count(*) = 11 AND bool_and(p.reference_kind = 'owned')
               AND NOT EXISTS (SELECT 1 FROM ultimateteam.squad_players x JOIN ultimateteam.item_definitions xd ON xd.definition_id = x.definition_id
                               WHERE x.squad_id = s.id AND xd.native_resource_id = ANY(@excluded))
            ORDER BY s.club_id, s.id
            """, reader => new SquadBattlesPoolEntry(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetString(3)),
            ("excluded", excludedResources));

    /// <summary>Which of these weeks have settled.</summary>
    public static async ValueTask<HashSet<long>> SquadBattlesSettledWeeksAsync(this IUnitOfWork work, IReadOnlyList<long> weeks) =>
        (await work.QueryAsync("-- query: sqbt.settled\nSELECT week_id FROM ultimateteam.squad_battles_settlements WHERE week_id = ANY(@weeks)",
            reader => reader.GetInt64(0), ("weeks", weeks.ToArray()))).ToHashSet();

    /// <summary>Claims a week's settlement; false when another settlement already holds it (it then waits for that one).</summary>
    public static async ValueTask<bool> TryBeginSquadBattlesSettlementAsync(this IUnitOfWork work, long week, long participants, bool ranked) =>
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.squad_battles_settlements(week_id, settled_at, participants, ranked) VALUES (@week, @now, @participants, @ranked)
            ON CONFLICT (week_id) DO NOTHING
            """, ("week", week), ("now", work.NowSeconds), ("participants", participants), ("ranked", ranked)) == 1;

    public static ValueTask<int> AddSquadBattlesResultAsync(this IUnitOfWork work, long week, SquadBattlesStanding standing) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.squad_battles_results(week_id, club_id, position, rank, tier_id, tier_level, points, awards)
            VALUES (@week, @clubId, @position, @rank, @tier, @level, @points, @awards)
            """, ("week", week), ("clubId", standing.Row.ClubId), ("position", standing.Position), ("rank", standing.Rank),
            ("tier", standing.Tier!.Id), ("level", standing.TierLevel), ("points", standing.Row.Points),
            ("awards", Json(new JsonArray(standing.Tier.Awards.Select(award => (JsonNode?)award.ToJson()).ToArray()))));

    /// <summary>This club's settled, unclaimed prizes between two weeks, oldest first.</summary>
    public static ValueTask<List<SquadBattlesResult>> UnclaimedSquadBattlesResultsAsync(this IUnitOfWork work, long from, long to) =>
        work.QueryAsync("""
            -- query: sqbt.unclaimed
            SELECT week_id, position, rank, tier_id, tier_level, points, awards::text FROM ultimateteam.squad_battles_results
            WHERE club_id = @club AND claimed_at IS NULL AND week_id BETWEEN @from AND @to
            ORDER BY week_id
            """, reader => new SquadBattlesResult(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetInt64(5), (FutJson.Parse(reader.GetString(6)) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(SquadBattlesAward.FromJson).ToList()),
            ("from", from), ("to", to));

    /// <summary>Marks the prizes claimed; returns the weeks actually claimed now (a concurrent claim takes none twice).</summary>
    public static async ValueTask<HashSet<long>> ClaimSquadBattlesResultsAsync(this IUnitOfWork work, IEnumerable<long> weeks) =>
        new(await work.QueryAsync("""
            UPDATE ultimateteam.squad_battles_results SET claimed_at = @now
            WHERE club_id = @club AND claimed_at IS NULL AND week_id = ANY(@weeks)
            RETURNING week_id
            """, reader => reader.GetInt64(0), ("now", work.NowSeconds), ("weeks", weeks.ToArray())));
}
