namespace Aurora21.UltimateTeam.Core.Sessions;

/// <summary>
/// The other player of a live online match, as the caller sees it. <paramref name="Friendly"/>: an online Friendlies game
/// (FUT_HouseRules), which is never a Division Rivals match whatever its CreateMatch says.
/// </summary>
public sealed record OnlineOpponent(bool CallerIsHome, string OpponentId, long OpponentPersonaId, long GameId, bool Friendly = false);

/// <summary>Live online matches (provided by the Blaze matchmaking layer on a server that has one).</summary>
public interface IMatchRoster
{
    /// <summary>The caller's opponent while they are in a paired game, or null.</summary>
    OnlineOpponent? OpponentOf(string playerId);

    /// <summary>
    /// Whether the player has a queued search that is not paired yet: the pre-match squad fetch can arrive before Blaze
    /// pairs the two searches (Python server 10fff54), so a caller in this state may wait briefly for its opponent.
    /// </summary>
    bool IsSearching(string playerId);

    /// <summary>
    /// How long before the caller's last gameplay datagram its opponent's last one reached the relay in this game (positive:
    /// the opponent went quiet first), or null when unknown (no relay, an unknown game, a player who never sent).
    /// </summary>
    TimeSpan? OpponentQuietFirstBy(string playerId, long gameId) => null;

    /// <summary>
    /// Whether the caller's opponent left this game (Blaze game id or FIFA's report id) first while it was being played,
    /// with the caller still in it; false when unknown.
    /// </summary>
    bool OpponentLeftGameFirst(string playerId, long gameId) => false;
}
