using System.Collections.Concurrent;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// What the weekly competitions (Division Rivals, the Weekend League and Squad Battles) share across every club this
/// process serves, so a hub load does not scan every club or ask the database what cannot have changed:
/// <list type="bullet">
/// <item>Which weeks and events have settled. A settlement is written once and never undone, so a week known to be settled
/// stays settled; before its rewards time a week cannot be settled at all.</item>
/// <item>A week's live standings rows (every club's score), reused for a rule-set lifetime. Readers put their own club's
/// current row in place of the cached one, so only other clubs' scores can lag.</item>
/// <item>A week's or event's leaderboard bands, frozen when it opens from the previous one's settled field.</item>
/// </list>
/// Expiry follows elapsed process time, not the game clock, so a fixed test clock does not freeze it.
/// </summary>
public sealed class CompetitionCache
{
    private readonly ConcurrentDictionary<(string Competition, long Id), bool> _settled = new();
    private readonly ConcurrentDictionary<(string Competition, long Week), (long ExpiresAtTicks, object Rows)> _rows = new();

    /// <summary>
    /// Makes sure each due week or event is settled: the ones not yet known to be settled are looked up in one query, the
    /// missing ones settled (the first caller wins; a concurrent one waits on the settlement's key), and all remembered.
    /// No query at all once every due id is known.
    /// </summary>
    public async ValueTask EnsureSettledAsync(string competition, IEnumerable<long> due, Func<IReadOnlyList<long>, ValueTask<HashSet<long>>> settledAmong,
        Func<long, ValueTask> settle)
    {
        var unknown = due.Where(id => !_settled.ContainsKey((competition, id))).ToList();
        if (unknown.Count == 0)
        {
            return;
        }
        var settled = await settledAmong(unknown);
        foreach (var id in unknown)
        {
            if (!settled.Contains(id))
            {
                await settle(id);
            }
        }
        // Only ids the database already reported settled (committed) are remembered. One settled just now is remembered by
        // the next lookup, so a settlement whose transaction rolls back is never taken for done.
        foreach (var id in settled)
        {
            _settled[(competition, id)] = true;
        }
    }

    public bool IsSettled(string competition, long id) => _settled.ContainsKey((competition, id));

    private readonly ConcurrentDictionary<(string Competition, long Id), (long Revision, object Value)> _frozen = new();

    /// <summary>
    /// A value fixed for a week or event and content revision: loaded until <paramref name="load"/> says it is final,
    /// then remembered. Callers using live reward rules pass their snapshot revision so publication refreshes the bands.
    /// </summary>
    public async ValueTask<T> FrozenAsync<T>(string competition, long id, Func<ValueTask<(T Value, bool Final)>> load, long revision = 0) where T : class
    {
        if (_frozen.TryGetValue((competition, id), out var known) && known.Revision == revision && known.Value is T value)
        {
            return value;
        }
        var (loaded, final) = await load();
        if (final)
        {
            _frozen[(competition, id)] = (revision, loaded);
        }
        return loaded;
    }

    /// <summary>After an operator changes a club's current competition progress, reload live standings on the next read.</summary>
    public void InvalidateRows() => _rows.Clear();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, (long At, bool Conceded)>> _reports = new();

    /// <summary>
    /// Remembers that a club sent a Weekend League result for a game (and whether it conceded it, a quit), for
    /// <paramref name="memorySeconds"/>: both players' match ends reach this process, so a lost-connection claim can see
    /// whether the opponent reported too.
    /// </summary>
    public void NoteReport(string gameId, long clubId, bool conceded, long now, long memorySeconds)
    {
        foreach (var (key, seen) in _reports)
        {
            if (seen.Values.All(report => now - report.At > memorySeconds))
            {
                _reports.TryRemove(key, out _);
            }
        }
        _reports.GetOrAdd(gameId, _ => new())[clubId] = (now, conceded);
    }

    /// <summary>The other player's result for a game, when one arrived.</summary>
    public (long At, bool Conceded)? OpponentReport(string gameId, long clubId) =>
        _reports.TryGetValue(gameId, out var seen) && seen.FirstOrDefault(report => report.Key != clubId) is { Key: not 0 } other ? other.Value : null;

    /// <summary>A week's standings rows, loaded again once <paramref name="lifetimeSeconds"/> have passed (0 always loads).</summary>
    public async ValueTask<List<T>> RowsAsync<T>(string competition, long week, long lifetimeSeconds, Func<ValueTask<List<T>>> load)
    {
        var now = Environment.TickCount64;
        if (lifetimeSeconds > 0 && _rows.TryGetValue((competition, week), out var entry) && now < entry.ExpiresAtTicks && entry.Rows is List<T> cached)
        {
            return cached;
        }
        var rows = await load();
        if (lifetimeSeconds > 0)
        {
            // Concurrent misses may each load once; the last writer's rows are kept, and all are current.
            _rows[(competition, week)] = (now + lifetimeSeconds * 1000, rows);
        }
        return rows;
    }
}
