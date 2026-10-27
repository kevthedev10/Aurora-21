using System.Diagnostics.Metrics;

namespace Aurora21.UltimateTeam.Server.Relay;

/// <summary>
/// The gameplay relay's metrics (exported with <see cref="Hosting.MetricsExport"/>). No instrument carries a player id,
/// a game id or an address: drops are tagged by reason only.
/// </summary>
public static class RelayMetrics
{
    public const string MeterName = "Aurora21.UltimateTeam.Relay";

    private static readonly Meter Meter = new(MeterName);
    private static long s_forwardedDatagrams;
    private static long s_forwardedBytes;
    private static long s_games;

    internal static readonly Counter<long> Dropped = Meter.CreateCounter<long>("ultimateteam.relay.dropped", "{datagram}",
        "Gameplay datagrams the relay did not forward, by reason");

    internal static readonly Counter<long> Stalls = Meter.CreateCounter<long>("ultimateteam.relay.stalls", "{stall}",
        "Gaps in one player's datagrams during play");

    internal static readonly Counter<long> SharedStalls = Meter.CreateCounter<long>("ultimateteam.relay.shared_stalls", "{stall}",
        "Both players' datagrams resumed together after a gap: held at the server's end");

    internal static readonly Histogram<double> StallDuration = Meter.CreateHistogram<double>("ultimateteam.relay.stall.duration", "ms",
        "How long each stall lasted",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [250, 350, 500, 750, 1000, 2000, 3000, 5000, 10000, 20000] });

    internal static readonly Counter<long> SplitSources = Meter.CreateCounter<long>("ultimateteam.relay.split_sources", "{player}",
        "Players whose gameplay came from another address of their network than their Blaze connection (accepted)");

    internal static readonly Histogram<double> ClientLatency = Meter.CreateHistogram<double>("ultimateteam.relay.client_latency", "ms",
        "FIFA's own latency to the opponent (ConnectionTelemetry LATC), one sample per report",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [10, 20, 30, 40, 50, 60, 80, 100, 150, 200, 300] });

    internal static readonly Histogram<double> ClientLoss = Meter.CreateHistogram<double>("ultimateteam.relay.client_loss", "%",
        "FIFA's own packet loss to the opponent over a match (ConnectionTelemetry PKLF), one sample per player per game",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10] });

    internal static readonly Counter<long> SilentPlayers = Meter.CreateCounter<long>("ultimateteam.relay.silent_players", "{player}",
        "Players who stopped sending mid-game while their opponent still sent");

    static RelayMetrics()
    {
        Meter.CreateObservableCounter("ultimateteam.relay.forwarded", () => Volatile.Read(ref s_forwardedDatagrams), "{datagram}",
            "Gameplay datagrams forwarded");
        Meter.CreateObservableCounter("ultimateteam.relay.forwarded.bytes", () => Volatile.Read(ref s_forwardedBytes), "By",
            "Gameplay bytes forwarded");
        Meter.CreateObservableUpDownCounter("ultimateteam.relay.games", () => Volatile.Read(ref s_games), "{game}",
            "Games with a relay allocation");
    }

    /// <summary>Creates the instruments (the first use of this type does), so the dashboard has series from start-up, not from the first game.</summary>
    internal static void Register()
    {
    }

    internal static void Forwarded(int bytes)
    {
        Interlocked.Increment(ref s_forwardedDatagrams);
        Interlocked.Add(ref s_forwardedBytes, bytes);
    }

    internal static void GameAllocated() => Interlocked.Increment(ref s_games);

    internal static void GameReleased() => Interlocked.Decrement(ref s_games);

    internal static void Drop(string reason) => Dropped.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
