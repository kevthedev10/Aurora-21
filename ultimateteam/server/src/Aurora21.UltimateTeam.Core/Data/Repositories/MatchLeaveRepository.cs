namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>One online match an opponent left, as the stayer's match end saw it (migration 179 match_leaves).</summary>
public sealed record MatchLeaveRow(string LeaverPlayer, long? LeaverPersona, string StayerPlayer, string Mode, string MatchKey, long Minute,
    long StayerGoals, long LeaverGoals, bool Early, bool Serial, string StayerOutcome);

/// <summary>The opponent-left log the serial-leaver rule counts and the operator panel lists.</summary>
public static class MatchLeaveRepository
{
    /// <summary>Early leaves logged for <paramref name="leaverPlayer"/> since <paramref name="since"/> (unix seconds).</summary>
    public static ValueTask<long> EarlyLeavesSinceAsync(this IUnitOfWork work, string leaverPlayer, long since) =>
        work.ScalarAsync<long>("""
            -- query: match-leaves.early-count
            SELECT count(*) FROM ultimateteam.match_leaves WHERE leaver_player = @leaver AND early AND left_at > @since
            """, ("leaver", leaverPlayer), ("since", since));

    /// <summary>Logs a leave once per leaver and match (a replayed end adds nothing).</summary>
    public static ValueTask<int> AddMatchLeaveAsync(this IUnitOfWork work, MatchLeaveRow row) =>
        work.ExecuteAsync("""
            -- query: match-leaves.add
            INSERT INTO ultimateteam.match_leaves(leaver_player, leaver_persona, stayer_player, stayer_club_id, mode, match_key, minute,
                stayer_goals, leaver_goals, early, serial, stayer_outcome, left_at)
            VALUES (@leaver, @persona, @stayer, @club, @mode, @match, @minute, @mine, @theirs, @early, @serial, @outcome, @now)
            ON CONFLICT (leaver_player, match_key) DO NOTHING
            """, ("leaver", row.LeaverPlayer), ("persona", row.LeaverPersona), ("stayer", row.StayerPlayer), ("mode", row.Mode),
            ("match", row.MatchKey), ("minute", (int)row.Minute), ("mine", (int)row.StayerGoals), ("theirs", (int)row.LeaverGoals),
            ("early", row.Early), ("serial", row.Serial), ("outcome", row.StayerOutcome), ("now", work.NowSeconds));

    /// <summary>
    /// When <paramref name="player"/>'s last penalty started or was lifted (unix seconds), 0 for none: leaves up to then never
    /// count toward another offence.
    /// </summary>
    public static ValueTask<long> LastPenaltyAsync(this IUnitOfWork work, string player) =>
        work.ScalarAsync<long>("""
            -- query: leaver-penalties.last
            SELECT coalesce(max(greatest(started_at, coalesce(lifted_at, 0))), 0) FROM ultimateteam.leaver_penalties WHERE player = @player
            """, ("player", player));

    /// <summary>Penalties <paramref name="player"/> received since <paramref name="since"/> that were not lifted.</summary>
    public static ValueTask<long> OffencesSinceAsync(this IUnitOfWork work, string player, long since) =>
        work.ScalarAsync<long>("""
            -- query: leaver-penalties.offences
            SELECT count(*) FROM ultimateteam.leaver_penalties WHERE player = @player AND started_at > @since AND lifted_at IS NULL
            """, ("player", player), ("since", since));

    /// <summary>Records a penalty once per leave that started it; 0 when a replayed match end already did.</summary>
    public static ValueTask<int> AddPenaltyAsync(this IUnitOfWork work, string player, int offence, string matchKey, long? until) =>
        work.ExecuteAsync("""
            -- query: leaver-penalties.add
            INSERT INTO ultimateteam.leaver_penalties(player, offence, match_key, started_at, until)
            VALUES (@player, @offence, @match, @now, @until)
            ON CONFLICT (player, match_key) DO NOTHING
            """, ("player", player), ("offence", offence), ("match", matchKey), ("now", work.NowSeconds), ("until", until));

    /// <summary>Every penalty running at <paramref name="now"/>: who, which offence, until when (null: a ban).</summary>
    public static ValueTask<List<(string Player, int Offence, long? Until)>> ActivePenaltiesAsync(this IUnitOfWork work, long now) =>
        work.QueryAsync("""
            -- query: leaver-penalties.active
            SELECT player, offence, until FROM ultimateteam.leaver_penalties
            WHERE lifted_at IS NULL AND (until IS NULL OR until > @now) ORDER BY started_at
            """, row => (row.GetString(0), row.GetInt32(1), row.IsDBNull(2) ? (long?)null : row.GetInt64(2)), ("now", now));

    /// <summary>Lifts every penalty <paramref name="player"/> is serving; returns how many.</summary>
    public static ValueTask<int> LiftPenaltiesAsync(this IUnitOfWork work, string player) =>
        work.ExecuteAsync("""
            -- query: leaver-penalties.lift
            UPDATE ultimateteam.leaver_penalties SET lifted_at = @now
            WHERE player = @player AND lifted_at IS NULL AND (until IS NULL OR until > @now)
            """, ("player", player), ("now", work.NowSeconds));
}
