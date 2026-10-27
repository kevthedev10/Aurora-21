using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Sessions;

/// <summary>
/// Keeps a client that desyncs with everyone away from healthy players (seasons.json _desyncQuarantineComment). One PC
/// whose FIFA simulates differently (live 2026-10-06: an FX-8300 without AVX2, before the maths groups knew AVX2) desynced 8
/// online matches in an hour and stalled 15 kit screens, each with a different opponent. A player whose matches desync
/// early against enough distinct opponents within the window is quarantined: from then on they pair only with other
/// quarantined players, until the quarantine runs out. Desyncs against a quarantined player do not count toward anyone
/// else's. Kept in memory only: a restart clears every quarantine. Shared by every club this process serves.
/// </summary>
public sealed class DesyncQuarantine
{
    private sealed record Desync(long At, string Opponent, string Match);

    private readonly object _gate = new();
    private readonly Func<DesyncQuarantineRules> _rules;
    private readonly Func<long> _now;
    private readonly ILogger _log;
    private readonly Dictionary<string, List<Desync>> _desyncs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _until = new(StringComparer.Ordinal);

    /// <param name="rules">The current seasons.json quarantine rules (the live content).</param>
    /// <param name="now">Unix seconds.</param>
    public DesyncQuarantine(Func<DesyncQuarantineRules> rules, Func<long> now, ILogger log)
    {
        _rules = rules;
        _now = now;
        _log = log;
    }

    /// <summary>
    /// Notes an online match that ended in a desync between <paramref name="player"/> and <paramref name="opponent"/>, from
    /// either player's end-of-match report (the second report of the same match counts once). Only a desync within the first
    /// EarlySeconds of real play counts: a broken client desyncs at once, while a long match that desyncs late is the rare
    /// one-off every pair can hit.
    /// </summary>
    public void Note(string player, string opponent, string match, long? realSeconds)
    {
        var rules = _rules();
        if (!rules.Enabled || string.IsNullOrEmpty(player) || string.IsNullOrEmpty(opponent) || player == opponent
            || realSeconds is not { } seconds || seconds > rules.EarlySeconds)
        {
            return;
        }
        var now = _now();
        lock (_gate)
        {
            // A match against a player already kept apart says nothing about the other one.
            if (QuarantinedLocked(player, now) || QuarantinedLocked(opponent, now))
            {
                return;
            }
            Add(player, opponent);
            Add(opponent, player);
        }

        void Add(string who, string against)
        {
            if (!_desyncs.TryGetValue(who, out var list))
            {
                _desyncs[who] = list = new List<Desync>();
            }
            list.RemoveAll(desync => now - desync.At > rules.WindowSeconds);
            if (list.Any(desync => desync.Match == match))
            {
                return;
            }
            list.Add(new Desync(now, against, match));
            var opponents = list.Select(desync => desync.Opponent).Distinct(StringComparer.Ordinal).Count();
            if (opponents >= rules.DistinctOpponents)
            {
                _until[who] = now + rules.QuarantineSeconds;
                _desyncs.Remove(who);
                _log.LogWarning("DESYNC QUARANTINE player={Player} desyncs={Desyncs} opponents={Opponents} windowS={Window} until={Until}: pairs only with other quarantined players",
                    who, list.Count, string.Join(",", list.Select(desync => desync.Opponent).Distinct(StringComparer.Ordinal)), rules.WindowSeconds, now + rules.QuarantineSeconds);
            }
        }
    }

    /// <summary>Whether <paramref name="player"/> is quarantined now.</summary>
    public bool Quarantined(string player)
    {
        lock (_gate)
        {
            return QuarantinedLocked(player, _now());
        }
    }

    /// <summary>Whether two searchers may pair: both quarantined or neither (always, with the rule off).</summary>
    public bool Compatible(string first, string second)
    {
        if (!_rules().Enabled)
        {
            return true;
        }
        lock (_gate)
        {
            var now = _now();
            return QuarantinedLocked(first, now) == QuarantinedLocked(second, now);
        }
    }

    private bool QuarantinedLocked(string player, long now)
    {
        if (!_until.TryGetValue(player, out var until))
        {
            return false;
        }
        if (now < until)
        {
            return true;
        }
        _until.Remove(player);
        return false;
    }
}
