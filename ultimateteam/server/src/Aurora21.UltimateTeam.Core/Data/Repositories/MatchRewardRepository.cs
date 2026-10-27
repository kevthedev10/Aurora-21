using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// When this club's current match was created and kicked off, and who it is against (migration 070 match_clock): the
/// opponent's persona, and its player id (migration 124), which opens the opponent's club when it leaves.
/// </summary>
public sealed record MatchClock(long CreatedAt, long? KickoffAt, long? OpponentPersona, string? OpponentPlayer = null)
{
    /// <summary>Real seconds since kickoff (the first PUT /match), else since the CreateMatch POST.</summary>
    public long RealSeconds(long now) => Math.Max(0, now - (KickoffAt ?? CreatedAt));
}

/// <summary>One settled match the reward gate paid, flagged or granted a Match Pack for (migration 070 match_rewards).</summary>
public sealed record MatchRewardRow(string Mode, string Verdict, long? OpponentPersona, long? WeekendLeague, long Coins, string? PackTier, bool PackHighlight);

/// <summary>The reward gate's per-club state: the match clock, the rewarded-match log and pre-rolled Match Packs.</summary>
public static class MatchRewardRepository
{
    /// <summary>A CreateMatch POST starts a new match clock; a PUT (kickoff, then substitutions) sets the kickoff once.</summary>
    public static ValueTask<int> NoteMatchCreateAsync(this IUnitOfWork work, bool post) =>
        post
            ? work.ExecuteAsync("""
                -- query: match-clock.create
                INSERT INTO ultimateteam.match_clock(club_id, created_at, kickoff_at, opponent_persona, opponent_player) VALUES (@club, @now, NULL, NULL, NULL)
                ON CONFLICT (club_id) DO UPDATE SET created_at = EXCLUDED.created_at, kickoff_at = NULL, opponent_persona = NULL, opponent_player = NULL
                """, ("now", work.NowSeconds))
            : work.ExecuteAsync("""
                -- query: match-clock.kickoff
                UPDATE ultimateteam.match_clock SET kickoff_at = @now WHERE club_id = @club AND kickoff_at IS NULL
                """, ("now", work.NowSeconds));

    /// <summary>The opponent of an online pairing (Rivals, Weekend League, online Draft): its persona and player id.</summary>
    public static ValueTask<int> NoteMatchOpponentAsync(this IUnitOfWork work, long opponentPersona, string? opponentPlayer = null) =>
        work.ExecuteAsync("""
            -- query: match-clock.opponent
            INSERT INTO ultimateteam.match_clock(club_id, created_at, kickoff_at, opponent_persona, opponent_player) VALUES (@club, @now, NULL, @opponent, @player)
            ON CONFLICT (club_id) DO UPDATE SET opponent_persona = EXCLUDED.opponent_persona, opponent_player = EXCLUDED.opponent_player
            """, ("now", work.NowSeconds), ("opponent", opponentPersona), ("player", opponentPlayer));

    /// <summary>This club's match clock without taking it; null when there is none.</summary>
    public static async ValueTask<MatchClock?> MatchClockAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("""
            -- query: match-clock.peek
            SELECT created_at, kickoff_at, opponent_persona, opponent_player FROM ultimateteam.match_clock WHERE club_id = @club
            """, reader => new MatchClock(reader.GetInt64(0), NullableLong(reader, 1), NullableLong(reader, 2), reader.IsDBNull(3) ? null : reader.GetString(3)))).FirstOrDefault();

    /// <summary>Takes this club's match clock (a match end or an ended search consumes it); null when there is none.</summary>
    public static async ValueTask<MatchClock?> TakeMatchClockAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("""
            -- query: match-clock.take
            DELETE FROM ultimateteam.match_clock WHERE club_id = @club RETURNING created_at, kickoff_at, opponent_persona, opponent_player
            """, reader => new MatchClock(reader.GetInt64(0), NullableLong(reader, 1), NullableLong(reader, 2), reader.IsDBNull(3) ? null : reader.GetString(3)))).FirstOrDefault();

    /// <summary>
    /// Games against <paramref name="opponentPersona"/> in <paramref name="modes"/> that paid coins: since
    /// <paramref name="since"/>, or within one Weekend League.
    /// </summary>
    public static ValueTask<long> RewardedPairGamesAsync(this IUnitOfWork work, long opponentPersona, IReadOnlyCollection<string> modes, long? since,
        long? weekendLeague) =>
        work.ScalarAsync<long>("""
            -- query: match-rewards.pair-count
            SELECT count(*) FROM ultimateteam.match_rewards
            WHERE club_id = @club AND opponent_persona = @opponent AND mode = ANY(@modes) AND coins > 0
              AND (@since::bigint IS NULL OR settled_at > @since) AND (@league::bigint IS NULL OR weekend_league = @league)
            """, ("opponent", opponentPersona), ("modes", modes.ToArray()), ("since", since), ("league", weekendLeague));

    public static ValueTask<int> AddMatchRewardAsync(this IUnitOfWork work, MatchRewardRow row) =>
        work.ExecuteAsync("""
            -- query: match-rewards.add
            INSERT INTO ultimateteam.match_rewards(club_id, settled_at, mode, verdict, opponent_persona, weekend_league, coins, pack_tier, pack_highlight)
            VALUES (@club, @now, @mode, @verdict, @opponent, @league, @coins, @tier, @highlight)
            """, ("now", work.NowSeconds), ("mode", row.Mode), ("verdict", row.Verdict), ("opponent", row.OpponentPersona), ("league", row.WeekendLeague),
            ("coins", row.Coins), ("tier", row.PackTier), ("highlight", row.PackHighlight));

    /// <summary>The resource ids of the club's customisation items (kits among them), in any pile: the Match Pack roller's owned kits.</summary>
    public static ValueTask<List<long>> OwnedCustomisationResourcesAsync(this IUnitOfWork work) =>
        work.QueryAsync("""
            -- query: items.owned-customisations
            SELECT DISTINCT d.native_resource_id FROM ultimateteam.items i
            JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
            WHERE i.club_id = @club AND i.family_id = 4 AND d.native_resource_id IS NOT NULL
            """, reader => reader.GetInt64(0));

    /// <summary>An owned pack whose contents were rolled when it was granted (a Match Pack).</summary>
    public static async ValueTask<long> AddRolledPackAsync(this IUnitOfWork work, long packId, string source, JsonObject contents, bool untradeable)
    {
        await work.LockRelationalClubAsync();
        return await work.ScalarAsync<long>("""
            -- query: unopened-packs.add-rolled
            INSERT INTO ultimateteam.unopened_packs(club_id, pack_id, source, created, untradeable, contents)
            VALUES (@club, @pack, @source, @now, @untradeable, @contents) RETURNING id
            """, ("pack", packId), ("source", source), ("now", work.NowSeconds), ("untradeable", untradeable), ("contents", Json(contents)));
    }

    /// <summary>
    /// Takes one owned copy of a pack as <see cref="PackRepository.TakeOwnedPackAsync"/> does (tradeable copies first, then the
    /// oldest), with its source and its pre-rolled contents (null when it rolls on opening); null when the club owns none.
    /// </summary>
    public static async ValueTask<(bool Untradeable, string Source, JsonObject? Contents)?> TakeOwnedPackContentsAsync(this IUnitOfWork work, long packId)
    {
        await work.LockRelationalClubAsync();
        var taken = await work.QueryAsync("""
            -- query: unopened-packs.take-owned
            DELETE FROM ultimateteam.unopened_packs WHERE club_id=@club AND id=(
                SELECT id FROM ultimateteam.unopened_packs WHERE club_id=@club AND pack_id=@pack ORDER BY untradeable, id LIMIT 1)
            RETURNING untradeable, source, contents::text
            """, reader => (reader.GetBoolean(0), reader.GetString(1), reader.IsDBNull(2) ? null : FutJson.ParseObject(reader.GetString(2))), ("pack", packId));
        return taken.Count == 0 ? null : taken[0];
    }
}
