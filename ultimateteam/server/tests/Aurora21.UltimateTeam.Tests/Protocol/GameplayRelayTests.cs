using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Server.Relay;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// How the relay reads its players' datagrams (stalls, shared stalls, slow seconds, silent players), its per-player rate
/// limit, re-pointing after a Blaze resume, and closing its receivers. Arrival times are injected; the shapes are the live
/// ones of 2026-10-04: about 30 datagrams a second in play, host network holds of ~0.4 s that release both players'
/// datagrams in the same millisecond, and one player's 2.9 s line stall.
/// </summary>
public sealed class GameplayRelayTests
{
    private static readonly IPEndPoint FromA = new(IPAddress.Loopback, 50001);
    private static readonly IPEndPoint FromB = new(IPAddress.Loopback, 50002);

    private static (GameplayRelay Relay, RelayAllocation Allocation, RecordingLogger Log) Start(int port, GameplayRelayLimits? limits = null)
    {
        var log = new RecordingLogger();
        // Arrival times are injected, so the sweeper (which reads the real clock) must not expire the game mid-test.
        limits = (limits ?? new GameplayRelayLimits()) with { SweepInterval = TimeSpan.FromHours(1) };
        var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = port, PortEnd = port + 1 }, log, limits);
        relay.Start("127.0.0.1");
        var allocation = relay.Allocate(1, new[] { new RelayPlayer("a", "127.0.0.1", "127.0.0.1"), new RelayPlayer("b", "127.0.0.1", "127.0.0.1") })!;
        return (relay, allocation, log);
    }

    /// <summary>Feeds <paramref name="count"/> datagrams from one player, <paramref name="intervalMs"/> apart; returns the last arrival.</summary>
    private static long Send(GameplayRelay relay, RelayAllocation allocation, string player, long start, int count, long intervalMs = 33)
    {
        var at = start;
        for (var i = 0; i < count; i++, at += intervalMs)
        {
            relay.HandleDatagram(allocation, player, new byte[] { 1, 2, 3 }, player == "a" ? FromA : FromB, receivedTicks: at);
        }
        return at - intervalMs;
    }

    /// <summary>Both players at play rate for <paramref name="count"/> datagrams each; returns the last arrival.</summary>
    private static long Play(GameplayRelay relay, RelayAllocation allocation, long start, int count)
    {
        var at = start;
        for (var i = 0; i < count; i++, at += 33)
        {
            relay.HandleDatagram(allocation, "a", new byte[] { 1 }, FromA, receivedTicks: at);
            relay.HandleDatagram(allocation, "b", new byte[] { 1 }, FromB, receivedTicks: at + 16);
        }
        return at - 33 + 16;
    }

    [Fact]
    public void AStallCountsOnlyOnceThePlayerIsFlowing()
    {
        var (relay, allocation, _) = Start(47400);
        using var running = relay;
        // Set-up traffic before kick-off: a few datagrams a second with half-second gaps.
        var last = Send(relay, allocation, "a", 1_000, 10, intervalMs: 500);
        Assert.Equal(0, allocation.Links["a"].Stalls);

        last = Send(relay, allocation, "a", last + 33, 150);
        Assert.True(allocation.Links["a"].Flowed);
        Assert.Equal(0, allocation.Links["a"].Stalls);
        Send(relay, allocation, "a", last + 400, 1);
        Assert.Equal(1, allocation.Links["a"].Stalls);
        Assert.Equal(400, allocation.Links["a"].LongestStallMilliseconds);

        // A gap under the threshold is ordinary jitter.
        last = Send(relay, allocation, "a", last + 433, 60);
        Send(relay, allocation, "a", last + 180, 1);
        Assert.Equal(1, allocation.Links["a"].Stalls);
    }

    /// <summary>
    /// Both players' datagrams resuming in the same millisecond is the host-hold signature (counted once, as shared); one
    /// player's long gap while the other keeps sending is that player's line, logged with how quiet the other was.
    /// </summary>
    [Fact]
    public void AHoldAtTheServerIsToldApartFromOnePlayersLine()
    {
        var (relay, allocation, log) = Start(47402);
        using var running = relay;
        var last = Play(relay, allocation, 1_000, 150);

        // Host network hold: nothing arrives for ~0.4 s, then both players' datagrams in the same millisecond.
        var resumed = last + 380;
        relay.HandleDatagram(allocation, "a", new byte[] { 1 }, FromA, receivedTicks: resumed);
        relay.HandleDatagram(allocation, "b", new byte[] { 1 }, FromB, receivedTicks: resumed + 1);
        Assert.Equal(1, allocation.SharedStalls);
        Assert.Equal(1, allocation.Links["a"].Stalls);
        Assert.Equal(1, allocation.Links["b"].Stalls);
        Assert.Single(log.Messages, line => line.StartsWith("GAMEPLAY RELAY SHARED STALL gameId=1", StringComparison.Ordinal));

        // B's line stalls for 2.9 s while A keeps sending.
        last = Play(relay, allocation, resumed + 33, 60);
        var aLast = Send(relay, allocation, "a", last + 17, 87);
        relay.HandleDatagram(allocation, "b", new byte[] { 1 }, FromB, receivedTicks: last + 2_900);
        Assert.Equal(1, allocation.SharedStalls);
        Assert.Equal(2, allocation.Links["b"].Stalls);
        Assert.Equal(1, allocation.Links["a"].Stalls);
        Assert.Equal(2_900, allocation.Links["b"].LongestStallMilliseconds);
        var stall = Assert.Single(log.Messages, line => line.StartsWith("GAMEPLAY RELAY STALL gameId=1 player=b", StringComparison.Ordinal));
        Assert.Contains($"otherQuietMs={last + 2_900 - aLast} stalls=2", stall);

        Assert.True(relay.Release(1, "test"));
        var released = Assert.Single(log.Messages, line => line.StartsWith("GAMEPLAY RELAY RELEASED gameId=1", StringComparison.Ordinal));
        Assert.Contains("quality=a:stalls=1,", released);
        Assert.Contains(" b:stalls=2,stallMs=3281,longestMs=2900,", released);
        Assert.EndsWith(" shared=1", released);
    }

    /// <summary>
    /// One player's line going silent for ~220 ms every few seconds (52 times in one match on 2026-10-04, a short freeze for
    /// both players each time) counts every hiccup, and is logged at the 1st and 10th, not 52 times.
    /// </summary>
    [Fact]
    public void ALineThatKeepsHiccupingIsCountedAndLoggedSparingly()
    {
        var (relay, allocation, log) = Start(47414);
        using var running = relay;
        var last = Play(relay, allocation, 1_000, 150);
        for (var hiccup = 0; hiccup < 12; hiccup++)
        {
            // B goes silent for 224 ms while A keeps sending, then both play on.
            var aLast = Send(relay, allocation, "a", last + 17, 6);
            Send(relay, allocation, "b", last + 224, 1);
            last = Play(relay, allocation, aLast + 33, 150);
        }
        Assert.Equal(12, allocation.Links["b"].Stalls);
        Assert.Equal(0, allocation.Links["a"].Stalls);
        Assert.Equal(0, allocation.SharedStalls);
        var lines = log.Messages.Where(line => line.StartsWith("GAMEPLAY RELAY STALL gameId=1 player=b", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("stalls=1", lines[0]);
        Assert.EndsWith("stalls=10", lines[1]);
    }

    /// <summary>FIFA drops to about ten keep-alives a second while its simulation waits; each such second is a slow second.</summary>
    [Fact]
    public void SecondsOfKeepAlivesCountAsSlowSeconds()
    {
        var (relay, allocation, _) = Start(47404);
        using var running = relay;
        var last = Send(relay, allocation, "a", 1_000, 150);
        Assert.Equal(0, allocation.Links["a"].SlowSeconds);
        last = Send(relay, allocation, "a", last + 100, 50, intervalMs: 100);
        Send(relay, allocation, "a", last + 33, 60);
        Assert.InRange(allocation.Links["a"].SlowSeconds, 4, 5);
        Assert.Equal(0, allocation.Links["a"].Stalls);
    }

    /// <summary>A flowing player who stops while the other keeps sending is logged once, at the moment of the drop.</summary>
    [Fact]
    public void APlayerWhoStopsSendingIsReportedOnce()
    {
        var (relay, allocation, log) = Start(47406);
        using var running = relay;
        var last = Play(relay, allocation, 1_000, 150);
        // A is gone; B's FIFA keeps sending about ten a second until its own peer timeout.
        var aLast = last - 16;
        var bLast = Send(relay, allocation, "b", last + 100, 45, intervalMs: 100);

        relay.ReportSilentPlayers(allocation, aLast + 4_000);
        Assert.DoesNotContain(log.Messages, line => line.Contains("PLAYER SILENT", StringComparison.Ordinal));
        relay.ReportSilentPlayers(allocation, aLast + 5_000);
        relay.ReportSilentPlayers(allocation, aLast + 5_500);
        var silent = Assert.Single(log.Messages, line => line.Contains("PLAYER SILENT", StringComparison.Ordinal));
        Assert.Equal($"GAMEPLAY RELAY PLAYER SILENT gameId=1 player=a silentMs=5000 otherQuietMs={aLast + 5_000 - bLast}", silent);

        // Both quiet (the game ended, or the server's own link is down): nobody is singled out.
        relay.ReportSilentPlayers(allocation, bLast + 9_000);
        Assert.Single(log.Messages, line => line.Contains("PLAYER SILENT", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMilliseconds(bLast - aLast), relay.QuietFirstBy(1, "b"));
    }

    /// <summary>The rate limit is per sending player: one side's flood (or spoofed datagrams) cannot starve the other side.</summary>
    [Fact]
    public void TheRateLimitIsPerPlayer()
    {
        var (relay, allocation, _) = Start(47408, new GameplayRelayLimits { MaximumPacketsPerSecond = 5 });
        using var running = relay;
        Send(relay, allocation, "a", 1_000, 10, intervalMs: 1);
        Send(relay, allocation, "b", 1_000, 5, intervalMs: 1);
        Assert.Equal(5, allocation.DroppedRateLimited);
        Assert.Equal(15, allocation.PacketsReceived);
    }

    /// <summary>
    /// After a verified Blaze resume from a new address (a router that reconnects mid-match), the player's datagrams are
    /// accepted from there, and no longer from the old address.
    /// </summary>
    [Fact]
    public void ARepointedPlayerIsAcceptedFromTheNewAddress()
    {
        var (relay, allocation, log) = Start(47410);
        using var running = relay;
        var moved = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 61000);
        relay.HandleDatagram(allocation, "a", new byte[] { 1 }, moved);
        Assert.Equal(1, allocation.DroppedForeignSource);

        Assert.True(relay.Repoint(1, "a", "203.0.113.7"));
        Assert.False(relay.Repoint(1, "a", "203.0.113.7"));
        Assert.False(relay.Repoint(1, "nobody", "203.0.113.7"));
        Assert.False(relay.Repoint(2, "a", "203.0.113.7"));
        Assert.Contains("GAMEPLAY RELAY REPOINTED gameId=1 player=a from=127.0.0.1 to=203.0.113.7", log.Messages);

        relay.HandleDatagram(allocation, "a", new byte[] { 1 }, moved);
        Assert.Equal(1, allocation.DroppedForeignSource);
        relay.HandleDatagram(allocation, "a", new byte[] { 1 }, FromA);
        Assert.Equal(2, allocation.DroppedForeignSource);
    }

    /// <summary>Releasing a game closes its sockets at once (its receivers end with them), so the ports are free again.</summary>
    [Fact]
    public void ReleaseClosesTheGamesSockets()
    {
        var (relay, allocation, _) = Start(47412);
        using var running = relay;
        Assert.True(relay.Release(1, "test"));
        foreach (var port in allocation.Ports.Values)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
    }

    [Fact]
    public void HandshakeSamplingStopsAtThePerPlayerPacketLimitWithoutDroppingPackets()
    {
        var (relay, allocation, log) = Start(47430, new GameplayRelayLimits { MaximumHandshakeLogPackets = 2 });
        using var running = relay;
        Play(relay, allocation, 1_000, 10);
        Assert.Equal(4, log.Messages.Count(line => line.StartsWith("GAMEPLAY RELAY HANDSHAKE", StringComparison.Ordinal)));
        Assert.Equal(20, allocation.PacketsReceived);
        Assert.Equal(20, allocation.PacketsForwarded);
        Assert.Equal(0, allocation.DroppedPackets);
    }

    [Fact]
    public void HandshakeSamplingExpiresAndStopsAtInGame()
    {
        var (relay, allocation, log) = Start(47432, new GameplayRelayLimits { HandshakeLogDuration = TimeSpan.FromMilliseconds(100) });
        using var running = relay;
        Play(relay, allocation, 1_000, 10);
        Assert.Equal(8, log.Messages.Count(line => line.StartsWith("GAMEPLAY RELAY HANDSHAKE", StringComparison.Ordinal)));
        Assert.True(relay.StopHandshakeCapture(1));
        Play(relay, allocation, 2_000, 10);
        Assert.Equal(8, log.Messages.Count(line => line.StartsWith("GAMEPLAY RELAY HANDSHAKE", StringComparison.Ordinal)));
        Assert.Equal(40, allocation.PacketsForwarded);
    }

    [Fact]
    public void HandshakeFormattingAndLoggingHappenOutsideTheMatchLockAndAfterForwarding()
    {
        var (relay, allocation, log) = Start(47434);
        using var running = relay;
        log.OnMessage = message =>
        {
            if (!message.StartsWith("GAMEPLAY RELAY HANDSHAKE", StringComparison.Ordinal)) return;
            Assert.False(Monitor.IsEntered(allocation.Gate));
            if (message.Contains("direction=b->a", StringComparison.Ordinal))
                Assert.Equal(2, allocation.PacketsForwarded);
        };
        Play(relay, allocation, 1_000, 1);
    }

    [Fact]
    public void DisabledInformationLoggingDoesNotSpendTheHandshakeBudget()
    {
        var (relay, allocation, log) = Start(47436);
        using var running = relay;
        log.Enabled = false;
        Play(relay, allocation, 1_000, 10);
        Assert.Equal(0, allocation.Links["a"].HandshakePacketsLogged);
        Assert.Equal(0, allocation.Links["b"].HandshakePacketsLogged);
        Assert.Equal(20, allocation.PacketsForwarded);
    }

    /// <summary>
    /// A player whose gameplay leaves from another address of their network than their Blaze connection (Cloudflare WARP,
    /// a carrier NAT pool: two players on 2026-10-04 could not start a match) is accepted, but only while their Blaze address
    /// itself sends nothing; other networks stay foreign.
    /// </summary>
    [Fact]
    public void ASameNetworkSourceIsAcceptedOnlyWhileTheBlazeAddressIsQuiet()
    {
        var log = new RecordingLogger();
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = 47440, PortEnd = 47441 }, log,
            new GameplayRelayLimits { SweepInterval = TimeSpan.FromHours(1) });
        relay.Start("127.0.0.1");
        var allocation = relay.Allocate(1, new[] { new RelayPlayer("warp", "104.28.196.187", "127.0.0.1"), new RelayPlayer("b", "127.0.0.1", "127.0.0.1") })!;
        var udpEgress = new IPEndPoint(IPAddress.Parse("104.28.163.227"), 63594);
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, udpEgress, receivedTicks: 1_000);
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, udpEgress, receivedTicks: 1_033);
        Assert.Equal(0, allocation.DroppedForeignSource);
        Assert.Single(log.Messages, line => line.StartsWith("GAMEPLAY RELAY SPLIT SOURCE gameId=1 player=warp expected=104.28.196.187 source=104.28.163.227:63594", StringComparison.Ordinal));

        // Another network is never accepted.
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, new IPEndPoint(IPAddress.Parse("104.29.1.1"), 3659), receivedTicks: 1_066);
        Assert.Equal(1, allocation.DroppedForeignSource);

        // A second address of the same network cannot take over while the first one sends.
        var other = new IPEndPoint(IPAddress.Parse("104.28.9.9"), 3659);
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, other, receivedTicks: 1_100);
        Assert.Equal(2, allocation.DroppedForeignSource);
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, udpEgress, receivedTicks: 1_133);
        Assert.Equal(2, allocation.DroppedForeignSource);

        // Once the Blaze address itself sends, the rest of its network is kept out for a while (a stray datagram cannot move the endpoint).
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, new IPEndPoint(IPAddress.Parse("104.28.196.187"), 3659), receivedTicks: 2_000);
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, udpEgress, receivedTicks: 2_033);
        Assert.Equal(3, allocation.DroppedForeignSource);
        relay.HandleDatagram(allocation, "warp", new byte[] { 1 }, udpEgress, receivedTicks: 12_001);
        Assert.Equal(3, allocation.DroppedForeignSource);
    }

    /// <summary>With the exact rule (32 bits) only the Blaze address is accepted.</summary>
    [Fact]
    public void AThirtyTwoBitPrefixAcceptsOnlyTheBlazeAddress()
    {
        var (relay, allocation, _) = Start(47442, new GameplayRelayLimits { SourcePrefixBits = 32 });
        using var running = relay;
        relay.HandleDatagram(allocation, "a", new byte[] { 1 }, new IPEndPoint(IPAddress.Parse("127.0.0.2"), 3659), receivedTicks: 1_000);
        Assert.Equal(1, allocation.DroppedForeignSource);
    }

    /// <summary>FIFA's own latency and loss reports (ConnectionTelemetry) end up in the match's quality summary.</summary>
    [Fact]
    public void TelemetryGoesIntoTheReleaseSummary()
    {
        var (relay, allocation, log) = Start(47444);
        using var running = relay;
        Assert.True(relay.RecordTelemetry(1, "a", 40, 0.0));
        Assert.True(relay.RecordTelemetry(1, "a", 90, null));
        Assert.True(relay.RecordTelemetry(1, "a", 44, 0.309));
        Assert.False(relay.RecordTelemetry(1, "a", 0, 0.5));
        Assert.False(relay.RecordTelemetry(2, "a", 40, 0.5));
        Assert.False(relay.RecordTelemetry(1, "nobody", 40, 0.5));
        Assert.True(relay.Release(1, "test"));
        Assert.False(relay.RecordTelemetry(1, "a", 40, 0.5));
        var released = Assert.Single(log.Messages, line => line.StartsWith("GAMEPLAY RELAY RELEASED gameId=1", StringComparison.Ordinal));
        Assert.Contains(",latencyMs=44/90,lossPct=0.31 b:", released);
    }

    /// <summary>The keep-alive run that is still going at the release (the screens after full time) is not counted as play.</summary>
    [Fact]
    public void TheSlowRunAfterFullTimeIsLeftOut()
    {
        var (relay, allocation, log) = Start(47446);
        using var running = relay;
        var last = Send(relay, allocation, "a", 1_000, 150);
        Send(relay, allocation, "a", last + 100, 60, intervalMs: 100);
        Assert.InRange(allocation.Links["a"].SlowSeconds, 5, 6);
        Assert.Equal(0, allocation.Links["a"].PlaySlowSeconds);
        Assert.True(relay.Release(1, "test"));
        Assert.Contains("a:stalls=0,stallMs=0,longestMs=0,slowSeconds=0,", Assert.Single(log.Messages, line => line.StartsWith("GAMEPLAY RELAY RELEASED", StringComparison.Ordinal)));
    }

    internal sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public IReadOnlyCollection<string> Messages => _messages.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool Enabled { get; set; } = true;
        public Action<string>? OnMessage { get; set; }
        public bool IsEnabled(LogLevel logLevel) => Enabled;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!Enabled) return;
            var message = formatter(state, exception);
            OnMessage?.Invoke(message);
            _messages.Enqueue(message);
        }
    }
}
