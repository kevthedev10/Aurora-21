namespace Aurora21.UltimateTeam.Core.Sessions;

/// <summary>
/// The running leaver penalties (rewardEligibility.opponentLeft.queueCooldown; owner, 2026-10-09): players who may not start
/// an online search for a while, or at all, because they keep leaving matches before the win minute. The penalties live in
/// leaver_penalties (migration 180); this is their in-memory copy for matchmaking, loaded at start-up and kept current by
/// the match end that starts one and the panel that lifts one. Shared by every club this process serves.
/// </summary>
public sealed class LeaverCooldown
{
    /// <summary>A running penalty: which offence it is and when it ends (null: a ban, until lifted).</summary>
    public sealed record Penalty(int Offence, long? Until);

    private readonly object _gate = new();
    private readonly Dictionary<string, Penalty> _penalties = new(StringComparer.Ordinal);

    /// <summary>Records a penalty <paramref name="player"/> now serves; a longer or later one already running is kept.</summary>
    public void Set(string player, Penalty penalty)
    {
        lock (_gate)
        {
            if (!_penalties.TryGetValue(player, out var current) || Ends(current) <= Ends(penalty))
            {
                _penalties[player] = penalty;
            }
        }
    }

    /// <summary>When <paramref name="player"/> may search again (<see cref="long.MaxValue"/> for a ban), or null when they may now.</summary>
    public long? Until(string player, long now)
    {
        lock (_gate)
        {
            if (!_penalties.TryGetValue(player, out var penalty))
            {
                return null;
            }
            if (now < Ends(penalty))
            {
                return Ends(penalty);
            }
            _penalties.Remove(player);
            return null;
        }
    }

    /// <summary>Lifts <paramref name="player"/>'s penalty (the operator panel, after it is lifted in leaver_penalties).</summary>
    public bool Clear(string player)
    {
        lock (_gate)
        {
            return _penalties.Remove(player);
        }
    }

    /// <summary>Every penalty running at <paramref name="now"/>, bans first, then by end.</summary>
    public IReadOnlyList<(string Player, Penalty Penalty)> Active(long now)
    {
        lock (_gate)
        {
            return _penalties.Where(pair => Ends(pair.Value) > now).OrderByDescending(pair => Ends(pair.Value))
                .Select(pair => (pair.Key, pair.Value)).ToList();
        }
    }

    private static long Ends(Penalty penalty) => penalty.Until ?? long.MaxValue;
}
