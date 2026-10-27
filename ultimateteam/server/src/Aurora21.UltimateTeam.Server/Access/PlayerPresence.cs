namespace Aurora21.UltimateTeam.Server.Access;

/// <summary>
/// Which verified players hold a Blaze session right now (Aurora 16's <c>blaze.presence</c>): the launcher's player
/// count, the friends list and the panel dashboard read it, and a panel block ends that player's connection at once.
/// A player is online from a verified Blaze login until that connection closes; a newer connection of the same player
/// counts once.
/// </summary>
public sealed class PlayerPresence
{
    private sealed record Connection(long Id, Action Abort, DateTimeOffset Since, string? Remote);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Connection>> _players = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly PlayerSessionLog? _sessions;
    private long _nextId;

    /// <param name="sessions">Where sessions are recorded for the operator panel; null records nothing.</param>
    public PlayerPresence(TimeProvider? time = null, PlayerSessionLog? sessions = null)
    {
        _time = time ?? TimeProvider.System;
        _sessions = sessions;
    }

    /// <summary>
    /// Records a verified Blaze session; disposing the result removes it. <paramref name="abort"/> closes it.
    /// <paramref name="remote"/> is shown to the operator only; identity never comes from it.
    /// </summary>
    public IDisposable Enter(string playerId, Action abort, string? remote = null)
    {
        Connection connection;
        lock (_gate)
        {
            connection = new Connection(++_nextId, abort, _time.GetUtcNow(), remote);
            if (!_players.TryGetValue(playerId, out var list)) _players[playerId] = list = [];
            list.Add(connection);
        }
        var logged = _sessions is not null && PlayerAccess.IsDiscordId(playerId) ? _sessions.OpenAsync(playerId, remote) : null;
        return new Exit(this, playerId, connection.Id, logged);
    }

    public int OnlineCount
    {
        get { lock (_gate) return _players.Count; }
    }

    public bool IsOnline(string playerId)
    {
        lock (_gate) return _players.ContainsKey(playerId);
    }

    /// <summary>Every online player id with the start of its oldest open session and its newest session's address.</summary>
    public IReadOnlyList<(string PlayerId, DateTimeOffset Since, string? Remote)> Online()
    {
        lock (_gate)
        {
            return _players.Select(pair => (pair.Key, pair.Value.Min(connection => connection.Since),
                pair.Value.MaxBy(connection => connection.Id)!.Remote)).ToArray();
        }
    }

    /// <summary>Closes every Blaze connection of this player (a block); returns how many were open.</summary>
    public int Disconnect(string playerId)
    {
        Connection[] open;
        lock (_gate) open = _players.TryGetValue(playerId, out var list) ? list.ToArray() : [];
        foreach (var connection in open)
        {
            try { connection.Abort(); }
            catch (ObjectDisposedException) { }
        }
        return open.Length;
    }

    private void Leave(string playerId, long id)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(playerId, out var list)) return;
            list.RemoveAll(connection => connection.Id == id);
            if (list.Count == 0) _players.Remove(playerId);
        }
    }

    private sealed class Exit(PlayerPresence presence, string playerId, long id, Task<long?>? logged) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            presence.Leave(playerId, id);
            if (logged is not null) presence._sessions?.Close(logged);
        }
    }
}
