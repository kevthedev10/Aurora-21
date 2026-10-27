using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Server.Hosting;

namespace Aurora21.UltimateTeam.Server.Relay;

public class GameplayRelayException : Exception
{
    public GameplayRelayException(string message) : base(message)
    {
    }
}

/// <summary>No free pair of relay ports is left.</summary>
public sealed class GameplayRelayCapacityException : GameplayRelayException
{
    public GameplayRelayCapacityException(string message) : base(message)
    {
    }
}

/// <summary>The fixed safety limits of one relayed game.</summary>
public sealed record GameplayRelayLimits
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan MaximumLifetime { get; init; } = TimeSpan.FromHours(3);
    public int MaximumPendingPackets { get; init; } = 64;
    public int MaximumPendingBytes { get; init; } = 256 * 1024;

    /// <summary>Per sending player, so one side's flood cannot use up the other side's allowance.</summary>
    public int MaximumPacketsPerSecond { get; init; } = 2000;

    /// <summary>Per sending player, like <see cref="MaximumPacketsPerSecond"/>.</summary>
    public int MaximumBytesPerSecond { get; init; } = 8 * 1024 * 1024;
    public int MaximumDatagramSize { get; init; } = 65507;
    public bool EnforceSourceAddress { get; init; } = true;

    /// <summary>
    /// How many leading bits of a datagram's source must match the player's Blaze address (8-32). 32 accepts only that
    /// address. 16 also accepts the rest of its /16, but only while nothing comes from the address itself: some players' UDP
    /// leaves from another address of the same network than their TCP. On 2026-10-04 a Cloudflare WARP player (TCP
    /// 104.28.196.187, UDP 104.28.163.227) and a carrier NAT pool (185.97.94.84 and .195) had every gameplay datagram
    /// dropped as foreign, so their matches never started. Other networks stay out, and so does the rest of a /16 once
    /// the player's own address is sending (a stray datagram cannot take the player's endpoint).
    /// </summary>
    public int SourcePrefixBits { get; init; } = 16;

    /// <summary>For how long datagrams from the player's own address keep other addresses of its network out.</summary>
    public TimeSpan ExactSourceHold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>FIFA binds gameplay UDP to this port; a peer's endpoint starts here until it sends.</summary>
    public int FifaGameplayPort { get; init; } = 3659;

    /// <summary>How many bytes of each handshake datagram are logged.</summary>
    public int HandshakeLogBytes { get; init; } = 512;

    /// <summary>Diagnostic sampling only; never limits forwarding.</summary>
    public int MaximumHandshakeLogPackets { get; init; } = 20;
    public TimeSpan HandshakeLogDuration { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a released port pair rests before another game gets it. The clients of the game that ended keep sending to
    /// their relay port until their own peer link times out; a game given that pair at once receives those datagrams (and,
    /// when a player is in both games, forwards that player's leftovers to the new opponent).
    /// </summary>
    public TimeSpan PortReuseDelay { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a released game's last datagram times are kept for <see cref="GameplayRelay.QuietFirstBy"/>.</summary>
    public TimeSpan EndedLinkMemory { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a receiver waits for a datagram before it checks whether its game was released. Releasing a game closes its
    /// sockets, which ends the wait at once where the platform supports that; this bounds it everywhere else.
    /// </summary>
    public TimeSpan ReceivePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A gap in one player's datagrams at least this long, while they were arriving at play rate, is a stall. In play FIFA
    /// sends about 30 a second and the gap stays under 200 ms (99.9th percentile 194-199 ms in four clean games on
    /// 2026-10-04); that day's host network holds were 210-470 ms, and one player's line went silent for 212-256 ms every
    /// 5-15 s (52 times in one match, a short freeze for both players each time; 250 ms caught 2 of them).
    /// </summary>
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A single player's stall at least this long is always logged, like the 2.9 s line stall of one player on 2026-10-04;
    /// shorter ones are logged at that player's 1st, 10th and 100th stall, so a line that keeps hiccuping shows during the match.
    /// </summary>
    public TimeSpan LoggedStallThreshold { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Both players' stalls ending this close together is a shared stall: their datagrams were held at the server's end
    /// and released at once. On 2026-10-04 every VPS host hold delivered both players' datagrams in the same millisecond,
    /// while two clients answering each other before kick-off resumed 7-21 ms apart (one player's round trip).
    /// </summary>
    public TimeSpan SharedStallWindow { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// A player is in play once <see cref="FlowingSeconds"/> whole seconds in a row each brought this many of their
    /// datagrams, and is flowing at a gap when this many arrived in the second before it (at most 64). Stalls, slow seconds
    /// and silences count only then: before kick-off FIFA sends 2-33 a second in bursts, in play a steady 30.
    /// </summary>
    public int FlowingPacketsPerSecond { get; init; } = 25;

    /// <summary>How many whole seconds in a row at <see cref="FlowingPacketsPerSecond"/> mark a player as in play.</summary>
    public int FlowingSeconds { get; init; } = 3;

    /// <summary>
    /// Once a player has flowed, a second with fewer datagrams than this is a slow second: FIFA falls back to about ten
    /// keep-alives a second while its simulation waits for a client that hitched, or for a peer that is gone.
    /// </summary>
    public int SlowSecondPackets { get; init; } = 15;

    /// <summary>
    /// A flowing player silent this long while the other player still sends is logged once: the moment of a hard drop,
    /// well before FIFA's own peer timeout (about 15 s) and the match end that follows.
    /// </summary>
    public TimeSpan SilentPlayerThreshold { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>One relay participant: who they are, where they must send from and which relay address they use.</summary>
public sealed record RelayPlayer(string PlayerId, string PeerAddress, string ServerAddress);

/// <summary>
/// One player's side of a relayed game: their relay port, the address they must send from, where they receive, and how
/// their datagrams have been arriving. Guarded by the allocation's gate.
/// </summary>
internal sealed class RelayLink
{
    private const int ArrivalRing = 64;
    private readonly long[] _arrivals = new long[ArrivalRing];
    private long _arrivalCount;
    private long _secondStartedTicks;
    private int _secondPackets;
    private int _fastSeconds;
    private long _slowStreak;
    private uint _expectedV4;
    private readonly List<int> _latencies = new();

    internal RelayLink(string playerId, Socket socket, int port, string peerAddress)
    {
        PlayerId = playerId;
        Socket = socket;
        Port = port;
        Expect(peerAddress);
    }

    public string PlayerId { get; }
    public RelayLink Other { get; set; } = null!;
    public Socket Socket { get; }
    public int Port { get; }

    /// <summary>The address this player's datagrams must come from; null when unparseable or not IPv4, so none match.</summary>
    public IPAddress? Expected { get; private set; }

    /// <summary>Where this player receives: their latest datagram's source. Replaced, never changed in place, so it can be sent to outside the gate.</summary>
    public SocketAddress? Endpoint { get; set; }

    /// <summary>The other player's datagrams waiting for this player's endpoint.</summary>
    public List<(byte[] Payload, long ReceivedTicks, long Number)> Pending { get; } = new();

    public long Captured { get; set; }
    public long? HandshakeStartedTicks { get; set; }
    public int HandshakePacketsLogged { get; set; }
    public bool HasReceived { get; private set; }
    public long LastReceivedTicks { get; private set; }
    public long RateWindowStartedTicks { get; set; }
    public long RateWindowPackets { get; set; }
    public long RateWindowBytes { get; set; }

    /// <summary>Latched at kick-off: <see cref="GameplayRelayLimits.FlowingSeconds"/> whole seconds in a row at play rate.</summary>
    public bool Flowed { get; private set; }
    public long Stalls { get; private set; }
    public long StallMilliseconds { get; private set; }
    public long LongestStallMilliseconds { get; private set; }
    public long SlowSeconds { get; private set; }
    public long LastStallEndTicks { get; set; } = long.MinValue;
    public long LastStallMilliseconds { get; set; }
    public bool LastStallShared { get; set; }
    public bool SilenceReported { get; set; }

    /// <summary>When the last datagram from the player's exact Blaze address arrived (not just its network).</summary>
    public long? LastExactTicks { get; set; }

    /// <summary>Latched once a datagram from another address of the player's network was accepted (logged once).</summary>
    public bool SplitSourceSeen { get; set; }

    /// <summary>The other address of the player's network their gameplay is accepted from, and when it last sent.</summary>
    public (uint Address, long LastTicks)? SplitSource { get; set; }

    /// <summary>FIFA's own packet loss to the peer, in percent (ConnectionTelemetry PKLF, cumulative), from its latest report.</summary>
    public double? LossPercent { get; private set; }

    /// <summary>The slow seconds of play: the run still going at the release (the screens after full time) is left out.</summary>
    public long PlaySlowSeconds => SlowSeconds - _slowStreak;

    /// <summary>Records FIFA's own telemetry for this player's link (LATC latency in ms, PKLF loss in percent).</summary>
    public void Report(int latencyMs, double? lossPercent)
    {
        if (_latencies.Count < 1024)
        {
            _latencies.Add(latencyMs);
        }
        LossPercent = lossPercent ?? LossPercent;
    }

    /// <summary>The median and highest latency FIFA reported for this link, or null before any report.</summary>
    public (int Median, int Max)? Latency
    {
        get
        {
            if (_latencies.Count == 0)
            {
                return null;
            }
            var sorted = _latencies.Order().ToArray();
            return (sorted[sorted.Length / 2], sorted[^1]);
        }
    }

    public void Expect(string peerAddress)
    {
        Expected = null;
        if (IPAddress.TryParse(peerAddress, out var address))
        {
            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                Expected = address;
                Span<byte> bytes = stackalloc byte[4];
                address.TryWriteBytes(bytes, out _);
                _expectedV4 = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            }
        }
    }

    /// <summary>
    /// Whether a datagram's source is this player's address (any port: NATs rebind ports), or another address in the same
    /// network (<paramref name="prefixBits"/> leading bits); <paramref name="exact"/> tells which. Allocation-free.
    /// </summary>
    public bool IsFrom(SocketAddress source, int prefixBits, out bool exact, out uint address)
    {
        exact = false;
        address = 0;
        if (Expected is null || source.Family != AddressFamily.InterNetwork || source.Size < 8)
        {
            return false;
        }
        address = BinaryPrimitives.ReadUInt32BigEndian(source.Buffer.Span.Slice(4, 4));
        exact = address == _expectedV4;
        var mask = uint.MaxValue << (32 - Math.Clamp(prefixBits, 8, 32));
        return exact || (address & mask) == (_expectedV4 & mask);
    }

    /// <summary>Records an arrival from this player; returns the gap it ended when that was a stall, else 0.</summary>
    public long Arrive(long now, GameplayRelayLimits limits)
    {
        var flowing = Math.Clamp(limits.FlowingPacketsPerSecond, 1, ArrivalRing);
        long stall = 0;
        if (HasReceived)
        {
            // Close the seconds this arrival ends first, so the second that completes kick-off counts.
            var elapsed = now - _secondStartedTicks;
            if (elapsed >= 1000)
            {
                var seconds = elapsed / 1000;
                if (_secondPackets >= flowing)
                {
                    _fastSeconds++;
                    Flowed |= _fastSeconds >= limits.FlowingSeconds;
                }
                else
                {
                    _fastSeconds = 0;
                }
                var slow = Flowed && _secondPackets < limits.SlowSecondPackets ? 1 : 0;
                SlowSeconds += slow;
                _slowStreak = slow == 1 ? _slowStreak + 1 : 0;
                // Whole seconds without a datagram.
                if (seconds > 1)
                {
                    _fastSeconds = 0;
                    SlowSeconds += Flowed ? seconds - 1 : 0;
                    _slowStreak += Flowed ? seconds - 1 : 0;
                }
                _secondStartedTicks += seconds * 1000;
                _secondPackets = 0;
            }
            var gap = now - LastReceivedTicks;
            if (Flowed && gap >= limits.StallThreshold.TotalMilliseconds && _arrivalCount >= flowing
                && LastReceivedTicks - _arrivals[(_arrivalCount - flowing) % ArrivalRing] <= 1000)
            {
                stall = gap;
                Stalls++;
                StallMilliseconds += gap;
                LongestStallMilliseconds = Math.Max(LongestStallMilliseconds, gap);
            }
        }
        else
        {
            _secondStartedTicks = now;
        }
        _secondPackets++;
        _arrivals[_arrivalCount % ArrivalRing] = now;
        _arrivalCount++;
        HasReceived = true;
        LastReceivedTicks = now;
        SilenceReported = false;
        return stall;
    }

    public string Quality(long now) =>
        $"{PlayerId}:stalls={Stalls},stallMs={StallMilliseconds},longestMs={LongestStallMilliseconds},slowSeconds={PlaySlowSeconds}," +
        $"quietMs={(HasReceived ? now - LastReceivedTicks : -1)}" +
        (Latency is { } latency ? $",latencyMs={latency.Median}/{latency.Max}" : "") +
        (LossPercent is { } loss ? $",lossPct={loss.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" : "");
}

/// <summary>The two relay ports of one matched game.</summary>
public sealed class RelayAllocation
{
    internal readonly object Gate = new();
    internal readonly HashSet<(string Source, string Target)> ConnectedEdges = new();
    internal readonly HashSet<(string Source, string Target)> PromotedEdges = new();
    private volatile bool _closed;
    private long _packetsForwarded;
    private long _bytesForwarded;
    private long _droppedSendFailed;
    private long _handlerFailures;

    internal RelayAllocation(long gameId, IReadOnlyList<RelayPlayer> players, IReadOnlyDictionary<string, string> advertised,
        IReadOnlyDictionary<string, Socket> sockets, IReadOnlyDictionary<string, int> ports)
    {
        GameId = gameId;
        Players = players;
        Advertised = advertised;
        Ports = ports;
        CreatedTicks = LastActivityTicks = GameplayRelay.Now();
        Links = players.ToDictionary(player => player.PlayerId, player => new RelayLink(player.PlayerId, sockets[player.PlayerId], ports[player.PlayerId], player.PeerAddress),
            StringComparer.Ordinal);
        Links[players[0].PlayerId].Other = Links[players[1].PlayerId];
        Links[players[1].PlayerId].Other = Links[players[0].PlayerId];
    }

    public long GameId { get; }
    public IReadOnlyList<RelayPlayer> Players { get; }
    public IReadOnlyDictionary<string, string> Advertised { get; }
    public IReadOnlyDictionary<string, int> Ports { get; }
    internal IReadOnlyDictionary<string, RelayLink> Links { get; }
    internal long CreatedTicks { get; }
    internal long LastActivityTicks { get; set; }
    internal long PendingBytes { get; set; }
    internal bool CaptureActive { get; set; } = true;

    /// <summary>Set before the sockets close; read by the receivers without the gate.</summary>
    internal bool Closed { get => _closed; set => _closed = value; }
    public long PacketsReceived { get; internal set; }
    public long PacketsForwarded => Interlocked.Read(ref _packetsForwarded);
    public long BytesForwarded => Interlocked.Read(ref _bytesForwarded);
    public long DroppedPackets { get; internal set; }

    /// <summary>Dropped because they came from an address other than the port owner's (usually a previous game's client).</summary>
    public long DroppedForeignSource { get; internal set; }
    public long DroppedOversize { get; internal set; }
    public long DroppedRateLimited { get; internal set; }

    /// <summary>Dropped while waiting for the other player's first datagram because the wait queue was full.</summary>
    public long DroppedQueueFull { get; internal set; }
    public long DroppedSendFailed => Interlocked.Read(ref _droppedSendFailed);

    /// <summary>Datagrams whose handling threw; each was dropped and its receiver kept running.</summary>
    public long HandlerFailures => Interlocked.Read(ref _handlerFailures);

    /// <summary>Stalls of both players that ended together: their datagrams were held at the server's end.</summary>
    public long SharedStalls { get; internal set; }

    /// <summary>The relay address and port a recipient sends to; it stands for the other player in the recipient's roster.</summary>
    public (string Address, int Port) EndpointFor(string recipientId) => (Advertised[recipientId], Ports[recipientId]);

    internal void CountForwarded(int bytes)
    {
        Interlocked.Increment(ref _packetsForwarded);
        Interlocked.Add(ref _bytesForwarded, bytes);
    }

    internal void CountSendFailed()
    {
        Interlocked.Increment(ref _droppedSendFailed);
        lock (Gate)
        {
            DroppedPackets++;
        }
    }

    internal long CountHandlerFailure() => Interlocked.Increment(ref _handlerFailures);
}

/// <summary>
/// A bounded, payload-transparent two-player UDP relay. Each player gets their own server-side
/// port: what player A sends to A's port is forwarded to B from B's port, matching the endpoint
/// projection of every captured FIFA 21 handshake that reached phase 04 in both directions.
/// </summary>
/// <remarks>
/// Each port has its own receiver thread, so forwarding never waits for the shared thread pool: a starved pool held the
/// former async receive loops' datagrams for seconds (bench 2026-10-04: p50 1.3 s while every pool worker was blocked).
/// The receive and forward path allocates nothing per datagram. The relay also watches how each player's datagrams arrive,
/// which tells lag at the server's end (both players stall together) from one player's line or game.
/// </remarks>
public sealed class GameplayRelay : IDisposable
{
    /// <summary>FIFA 21's 39-byte established-link datagram, seen right before native setup advances.</summary>
    private static readonly byte[] ConnectedPrefix = { 0x81, 0x01, 0x80, 0x04 };
    private const int ConnectedLength = 39;

    /// <summary>The shorter control datagram a side sends once it is promoted.</summary>
    private static readonly byte[] PromotedPrefix = { 0x00, 0x00, 0x80 };
    private const int PromotedLength = 26;

    /// <summary>Windows reports an ICMP port-unreachable for an earlier send as an error on the next receive unless this is off.</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private static readonly long TicksPerMillisecond = Stopwatch.Frequency / 1000;

    /// <summary>
    /// The relay's clock in milliseconds. Environment.TickCount64 is too coarse for pairing stalls (15.6 ms steps on
    /// Windows, the kernel tick on Linux); this one moves in true milliseconds everywhere.
    /// </summary>
    internal static long Now() => Stopwatch.GetTimestamp() / TicksPerMillisecond;

    private readonly GameplayRelayOptions _options;
    private readonly GameplayRelayLimits _limits;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<long, RelayAllocation> _allocations = new();
    private readonly HashSet<int> _usedPorts = new();

    /// <summary>When each port was last released (<see cref="Now"/>).</summary>
    private readonly Dictionary<int, long> _releasedAt = new();

    /// <summary>Released games' last datagram times per player, kept <see cref="GameplayRelayLimits.EndedLinkMemory"/>.</summary>
    private readonly Dictionary<long, (long ReleasedTicks, IReadOnlyList<RelayPlayer> Players, Dictionary<string, long> LastReceived)> _endedLinks = new();

    /// <summary>The pair index the next allocation starts searching from, so the range is used round-robin.</summary>
    private int _nextPair;
    private int _disposed;
    private string _bindAddress = string.Empty;
    private Timer? _sweeper;

    public GameplayRelay(GameplayRelayOptions options, ILogger log, GameplayRelayLimits? limits = null)
    {
        _options = options;
        _limits = limits ?? new GameplayRelayLimits();
        _log = log;
    }

    /// <summary>
    /// Called once per direction when a side's promotion datagram is first forwarded: (game id, source, target). It runs on
    /// the thread pool, never on a receiver.
    /// </summary>
    public Action<long, string, string>? PeerLinkPromoted { get; set; }

    public bool Active { get; private set; }

    public string BindAddress => _bindAddress;

    public GameplayRelayOptions Options => _options;

    /// <summary>Validates the configuration and starts the idle sweeper when the relay is enabled.</summary>
    public void Start(string serverHost)
    {
        if (_options.PortStart is < 1024 or > 65535)
        {
            throw new GameplayRelayException("gameplay relay port start must be 1024-65535");
        }
        if (_options.PortEnd <= _options.PortStart || _options.PortEnd > 65535)
        {
            throw new GameplayRelayException("gameplay relay port end must be greater than start and at most 65535");
        }
        if (!_options.Enabled || Active)
        {
            return;
        }
        _bindAddress = _options.BindAddress ?? serverHost;
        RequireIpv4(_bindAddress, "relay bind address", allowAny: true);
        if (_options.AdvertisedAddress is { } advertised)
        {
            RequireIpv4(advertised, "relay advertised address", allowAny: false);
        }
        Active = true;
        RelayMetrics.Register();
        _sweeper = new Timer(_ => Sweep(), null, _limits.SweepInterval, _limits.SweepInterval);
        _log.LogInformation("GAMEPLAY RELAY READY bind={Bind} ports={Start}-{End} advertised={Advertised} capacity={Capacity}",
            _bindAddress, _options.PortStart, _options.PortEnd, _options.AdvertisedAddress ?? "auto", (_options.PortEnd - _options.PortStart + 1) / 2);
    }

    /// <summary>Reserves a port pair for a two-player game (the same allocation when called again), or null when the relay is off.</summary>
    public RelayAllocation? Allocate(long gameId, IReadOnlyList<RelayPlayer> players)
    {
        if (!Active)
        {
            return null;
        }
        if (players.Count != 2 || players[0].PlayerId == players[1].PlayerId)
        {
            throw new GameplayRelayException("gameplay relay requires exactly two distinct players");
        }
        var advertised = players.ToDictionary(player => player.PlayerId, AdvertisedFor, StringComparer.Ordinal);
        RelayAllocation allocation;
        lock (_gate)
        {
            if (_allocations.TryGetValue(gameId, out var existing))
            {
                return existing;
            }
            var (ports, sockets) = BindPortPair() ?? throw new GameplayRelayCapacityException($"no free UDP relay pair in {_options.PortStart}-{_options.PortEnd}");
            allocation = new RelayAllocation(gameId, players, advertised,
                new Dictionary<string, Socket> { [players[0].PlayerId] = sockets.First, [players[1].PlayerId] = sockets.Second },
                new Dictionary<string, int> { [players[0].PlayerId] = ports.First, [players[1].PlayerId] = ports.Second });
            foreach (var link in allocation.Links.Values)
            {
                // Seed each authenticated endpoint so the first peer packet is forwarded at once; a slow
                // client could otherwise exceed FIFA's short mesh timeout. A received datagram replaces it.
                if (link.Expected is { } peer && !IPAddress.IsLoopback(peer))
                {
                    link.Endpoint = new IPEndPoint(peer, _limits.FifaGameplayPort).Serialize();
                }
            }
            _allocations[gameId] = allocation;
        }
        RelayMetrics.GameAllocated();
        foreach (var link in allocation.Links.Values)
        {
            new Thread(() => ReceiveLoop(allocation, link), 256 * 1024)
            {
                IsBackground = true,
                Name = $"relay {link.Port}",
            }.Start();
        }
        _log.LogInformation("GAMEPLAY RELAY ALLOCATED gameId={GameId} players={Players} ports={Ports}",
            gameId, string.Join(",", players.Select(player => player.PlayerId)), string.Join(",", allocation.Ports.Values));
        return allocation;
    }

    public RelayAllocation? AllocationFor(long gameId)
    {
        lock (_gate)
        {
            return _allocations.GetValueOrDefault(gameId);
        }
    }

    /// <summary>Stops verbose packet tracing once the game reaches IN_GAME.</summary>
    public bool StopHandshakeCapture(long gameId)
    {
        if (AllocationFor(gameId) is not { } allocation)
        {
            return false;
        }
        lock (allocation.Gate)
        {
            allocation.CaptureActive = false;
        }
        return true;
    }

    /// <summary>
    /// FIFA's own view of a player's link to the peer (Blaze ConnectionTelemetry, about every 20 s): LATC latency in ms and
    /// PKLF packet loss in percent. It goes into the release's quality summary and the latency metric. False for an unknown
    /// game or player.
    /// </summary>
    public bool RecordTelemetry(long gameId, string playerId, int latencyMs, double? lossPercent)
    {
        if (latencyMs <= 0 || AllocationFor(gameId) is not { } allocation || !allocation.Links.TryGetValue(playerId, out var link))
        {
            return false;
        }
        lock (allocation.Gate)
        {
            if (allocation.Closed)
            {
                return false;
            }
            link.Report(latencyMs, lossPercent);
        }
        RelayMetrics.ClientLatency.Record(latencyMs);
        return true;
    }

    /// <summary>
    /// Accepts a player's gameplay datagrams from a new address: their Blaze session resumed on a connection from there,
    /// and that resume was verified by the session's key, so the address is theirs (a router that reconnects mid-match can
    /// change it). Their endpoint follows from their next datagram. False when the game is not relayed, the player is not in
    /// it, or the address is unchanged.
    /// </summary>
    public bool Repoint(long gameId, string playerId, string peerAddress)
    {
        if (AllocationFor(gameId) is not { } allocation || !allocation.Links.TryGetValue(playerId, out var link))
        {
            return false;
        }
        IPAddress? previous;
        lock (allocation.Gate)
        {
            previous = link.Expected;
            link.Expect(peerAddress);
            if (Equals(previous, link.Expected))
            {
                return false;
            }
            link.LastExactTicks = null;
            link.SplitSource = null;
            link.SplitSourceSeen = false;
        }
        _log.LogInformation("GAMEPLAY RELAY REPOINTED gameId={GameId} player={Player} from={From} to={To}",
            gameId, playerId, previous?.ToString() ?? "none", link.Expected?.ToString() ?? "none");
        return true;
    }

    /// <summary>
    /// How long before this player's last gameplay datagram the other player's last one arrived: positive when the other
    /// player went quiet first, negative when this one did. FIFA keeps sending to a lost peer (about 10 a second) until its
    /// own peer timeout (about 15 s) while a player whose game or connection died sends nothing, so a hard drop shows as
    /// many seconds; during play the gap between datagrams stays under half a second (owner capture, 2026-10-04). Also
    /// answered for a game released within <see cref="GameplayRelayLimits.EndedLinkMemory"/>; null for an unknown game or
    /// player, or a player who never sent.
    /// </summary>
    public TimeSpan? QuietFirstBy(long gameId, string playerId)
    {
        RelayAllocation? allocation;
        lock (_gate)
        {
            allocation = _allocations.GetValueOrDefault(gameId);
            if (allocation is null)
            {
                return _endedLinks.TryGetValue(gameId, out var ended) ? Gap(ended.Players, ended.LastReceived, playerId) : null;
            }
        }
        lock (allocation.Gate)
        {
            return Gap(allocation.Players, LastReceived(allocation), playerId);
        }
    }

    private static Dictionary<string, long> LastReceived(RelayAllocation allocation) =>
        allocation.Links.Values.Where(link => link.HasReceived).ToDictionary(link => link.PlayerId, link => link.LastReceivedTicks, StringComparer.Ordinal);

    private static TimeSpan? Gap(IReadOnlyList<RelayPlayer> players, IReadOnlyDictionary<string, long> last, string playerId) =>
        players.Any(player => player.PlayerId == playerId) && players.FirstOrDefault(player => player.PlayerId != playerId) is { } other
        && last.TryGetValue(playerId, out var mine) && last.TryGetValue(other.PlayerId, out var theirs)
            ? TimeSpan.FromMilliseconds(mine - theirs)
            : null;

    public bool Release(long gameId, string reason)
    {
        RelayAllocation? allocation;
        lock (_gate)
        {
            if (!_allocations.Remove(gameId, out allocation))
            {
                return false;
            }
            _usedPorts.ExceptWith(allocation.Ports.Values);
            var releasedAt = Now();
            foreach (var port in allocation.Ports.Values)
            {
                _releasedAt[port] = releasedAt;
            }
        }
        Dictionary<string, long> lastReceived;
        string quality;
        lock (allocation.Gate)
        {
            allocation.Closed = true;
            foreach (var link in allocation.Links.Values)
            {
                link.Pending.Clear();
            }
            allocation.PendingBytes = 0;
            lastReceived = LastReceived(allocation);
            foreach (var link in allocation.Links.Values)
            {
                if (link.LossPercent is { } loss)
                {
                    RelayMetrics.ClientLoss.Record(loss);
                }
            }
            var now = Now();
            quality = string.Join(" ", allocation.Players.Select(player => allocation.Links[player.PlayerId].Quality(now))) + $" shared={allocation.SharedStalls}";
        }
        lock (_gate)
        {
            _endedLinks[gameId] = (Now(), allocation.Players, lastReceived);
        }
        // Closing a socket also ends its receiver's wait.
        foreach (var link in allocation.Links.Values)
        {
            link.Socket.Dispose();
        }
        RelayMetrics.GameReleased();
        _log.LogInformation("GAMEPLAY RELAY RELEASED gameId={GameId} reason={Reason} received={Received} forwarded={Forwarded} bytes={Bytes} dropped={Dropped} foreignSource={Foreign} oversize={Oversize} rateLimited={RateLimited} queueFull={QueueFull} sendFailed={SendFailed} handlerFailures={HandlerFailures} quality={Quality}",
            gameId, reason, allocation.PacketsReceived, allocation.PacketsForwarded, allocation.BytesForwarded, allocation.DroppedPackets,
            allocation.DroppedForeignSource, allocation.DroppedOversize, allocation.DroppedRateLimited, allocation.DroppedQueueFull, allocation.DroppedSendFailed,
            allocation.HandlerFailures, quality);
        return true;
    }

    /// <summary>Releases every allocation. Safe to call again: shutdown releases the relay before the host's own disposal.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _sweeper?.Dispose();
        List<long> games;
        lock (_gate)
        {
            games = _allocations.Keys.ToList();
        }
        foreach (var gameId in games)
        {
            Release(gameId, "server-stop");
        }
        Active = false;
    }

    private string AdvertisedFor(RelayPlayer player)
    {
        if (_options.AdvertisedAddress is { } configured)
        {
            return configured;
        }
        foreach (var candidate in new[] { player.ServerAddress, _bindAddress })
        {
            if (IPAddress.TryParse(candidate, out var address) && address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
            {
                return candidate;
            }
        }
        throw new GameplayRelayException("automatic relay advertisement could not determine a concrete server IPv4 address");
    }

    /// <summary>
    /// The next free pair, searched round-robin from after the last one handed out and skipping pairs released less than
    /// <see cref="GameplayRelayLimits.PortReuseDelay"/> ago; only when every free pair is still resting is one of them reused.
    /// </summary>
    private ((int First, int Second), (Socket First, Socket Second))? BindPortPair()
    {
        var bind = IPAddress.Parse(_bindAddress);
        var pairs = (_options.PortEnd - _options.PortStart + 1) / 2;
        var now = Now();
        bool Resting(int port) => _releasedAt.TryGetValue(port, out var released) && now - released < _limits.PortReuseDelay.TotalMilliseconds;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var step = 0; step < pairs; step++)
            {
                var index = (_nextPair + step) % pairs;
                var first = _options.PortStart + 2 * index;
                var second = first + 1;
                if (_usedPorts.Contains(first) || _usedPorts.Contains(second) || (pass == 0 && (Resting(first) || Resting(second))))
                {
                    continue;
                }
                if (TryBindPair(bind, first, second) is { } sockets)
                {
                    _nextPair = (index + 1) % pairs;
                    return ((first, second), sockets);
                }
            }
        }
        return null;
    }

    private (Socket First, Socket Second)? TryBindPair(IPAddress bind, int first, int second)
    {
        var sockets = new List<Socket>();
        try
        {
            foreach (var port in new[] { first, second })
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sockets.Add(socket);
                if (OperatingSystem.IsWindows())
                {
                    socket.IOControl(SioUdpConnReset, new byte[4], null);
                }
                socket.ReceiveTimeout = (int)_limits.ReceivePollInterval.TotalMilliseconds;
                socket.Bind(new IPEndPoint(bind, port));
            }
        }
        catch (SocketException)
        {
            sockets.ForEach(socket => socket.Dispose());
            return null;
        }
        _usedPorts.Add(first);
        _usedPorts.Add(second);
        return (sockets[0], sockets[1]);
    }

    /// <summary>
    /// One port's receiver, on its own thread: a blocking receive wakes this thread directly, so a datagram is forwarded
    /// as soon as it arrives whatever the rest of the server is doing. It ends when its game is released.
    /// </summary>
    private void ReceiveLoop(RelayAllocation allocation, RelayLink link)
    {
        var socket = link.Socket;
        var buffer = GC.AllocateUninitializedArray<byte>(_limits.MaximumDatagramSize + 1);
        var source = new SocketAddress(AddressFamily.InterNetwork);
        long errors = 0;
        while (!allocation.Closed)
        {
            int received;
            try
            {
                received = socket.ReceiveFrom(buffer, SocketFlags.None, source);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock or SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                if (allocation.Closed)
                {
                    return;
                }
                // Never spin on a socket that keeps failing; log the first failure and each tenfold rise.
                if (IsLogStep(++errors))
                {
                    _log.LogWarning(error, "GAMEPLAY RELAY RECEIVE FAILED gameId={GameId} port={Port} failures={Failures}", allocation.GameId, link.Port, errors);
                }
                Thread.Sleep(10);
                continue;
            }
            try
            {
                HandleDatagram(allocation, link, buffer.AsSpan(0, received), source, Now());
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // One bad datagram must not end this player's receiver: forwarding would stop in one
                // direction for the rest of the match. Drop it, count it, and log the first and each tenfold rise.
                var failures = allocation.CountHandlerFailure();
                RelayMetrics.Drop("handler-failed");
                if (IsLogStep(failures))
                {
                    _log.LogError(error, "GAMEPLAY RELAY DATAGRAM FAILED gameId={GameId} source={Source} failures={Failures}",
                        allocation.GameId, link.PlayerId, failures);
                }
            }
        }
    }

    internal static bool IsLogStep(long count)
    {
        while (count >= 10 && count % 10 == 0) count /= 10;
        return count == 1;
    }

    /// <summary>Test entry: a datagram from <paramref name="endpoint"/> to <paramref name="sourceId"/>'s relay port.</summary>
    internal void HandleDatagram(RelayAllocation allocation, string sourceId, ReadOnlySpan<byte> data, IPEndPoint endpoint, long? receivedTicks = null) =>
        HandleDatagram(allocation, allocation.Links[sourceId], data, endpoint.Serialize(), receivedTicks ?? Now());

    /// <remarks>
    /// Allocation-free on the common path: <paramref name="data"/> is the receive buffer, sent on at once
    /// (a send copies it to the kernel), and <paramref name="source"/> is copied only when the player's endpoint changes.
    /// A datagram is copied only when it has to wait for the other player's endpoint.
    /// </remarks>
    private void HandleDatagram(RelayAllocation allocation, RelayLink link, ReadOnlySpan<byte> data, SocketAddress source, long now)
    {
        var target = link.Other;
        List<(byte[] Payload, long Number)>? queued = null;
        Socket? forwardSocket = null;
        SocketAddress? forwardTarget = null;
        SocketAddress? flushTarget = null;
        long foreign = 0;
        long stall = 0;
        long stallCount = 0;
        long otherStall = 0;
        long otherQuiet = -1;
        var shared = false;
        var splitSource = false;
        var logHandshake = false;
        long handshakeNumber = 0;
        lock (allocation.Gate)
        {
            if (allocation.Closed)
            {
                return;
            }
            allocation.PacketsReceived++;
            if (data.Length > _limits.MaximumDatagramSize)
            {
                allocation.DroppedPackets++;
                allocation.DroppedOversize++;
                RelayMetrics.Drop("oversize");
                return;
            }
            var exact = true;
            uint address = 0;
            var hold = _limits.ExactSourceHold.TotalMilliseconds;
            if (_limits.EnforceSourceAddress && (!link.IsFrom(source, _limits.SourcePrefixBits, out exact, out address)
                // Another address of the network only while the player's own address is quiet, and only one at a time:
                // the first one accepted keeps the link until it has been quiet as long.
                || (!exact && ((link.LastExactTicks is { } lastExact && now - lastExact < hold)
                               || (link.SplitSource is { } split && split.Address != address && now - split.LastTicks < hold)))))
            {
                allocation.DroppedPackets++;
                foreign = ++allocation.DroppedForeignSource;
            }
            else
            {
                if (exact)
                {
                    link.LastExactTicks = now;
                }
                else if (_limits.EnforceSourceAddress)
                {
                    link.SplitSource = (address, now);
                    if (!link.SplitSourceSeen)
                    {
                        link.SplitSourceSeen = true;
                        splitSource = true;
                    }
                }
                // The player is still sending, whether or not the rate limit lets this datagram through.
                stall = link.Arrive(now, _limits);
                if (stall > 0)
                {
                    stallCount = link.Stalls;
                    otherQuiet = target.HasReceived ? now - target.LastReceivedTicks : -1;
                    shared = !target.LastStallShared && target.LastStallEndTicks != long.MinValue
                             && Math.Abs(now - target.LastStallEndTicks) <= _limits.SharedStallWindow.TotalMilliseconds;
                    if (shared)
                    {
                        allocation.SharedStalls++;
                        target.LastStallShared = true;
                        otherStall = target.LastStallMilliseconds;
                    }
                    link.LastStallShared = shared;
                    link.LastStallEndTicks = now;
                    link.LastStallMilliseconds = stall;
                }
                if (now - link.RateWindowStartedTicks >= 1000)
                {
                    link.RateWindowStartedTicks = now;
                    link.RateWindowPackets = 0;
                    link.RateWindowBytes = 0;
                }
                link.RateWindowPackets++;
                link.RateWindowBytes += data.Length;
                if (link.RateWindowPackets > _limits.MaximumPacketsPerSecond || link.RateWindowBytes > _limits.MaximumBytesPerSecond)
                {
                    allocation.DroppedPackets++;
                    allocation.DroppedRateLimited++;
                    RelayMetrics.Drop("rate-limited");
                }
                else
                {
                    allocation.LastActivityTicks = now;
                    if (link.Endpoint is null || !link.Endpoint.Equals(source))
                    {
                        link.Endpoint = Copy(source);
                    }
                    var number = ++link.Captured;
                    if (allocation.CaptureActive && _log.IsEnabled(LogLevel.Information))
                    {
                        link.HandshakeStartedTicks ??= now;
                        if (link.HandshakePacketsLogged < _limits.MaximumHandshakeLogPackets
                            && now - link.HandshakeStartedTicks.Value < _limits.HandshakeLogDuration.TotalMilliseconds)
                        {
                            link.HandshakePacketsLogged++;
                            logHandshake = true;
                            handshakeNumber = number;
                        }
                    }

                    // Datagrams that waited for this player's endpoint go out first.
                    if (link.Pending.Count > 0)
                    {
                        queued = new List<(byte[], long)>(link.Pending.Count);
                        foreach (var (payload, _, queuedNumber) in link.Pending)
                        {
                            queued.Add((payload, queuedNumber));
                            allocation.PendingBytes -= payload.Length;
                        }
                        link.Pending.Clear();
                        flushTarget = link.Endpoint;
                    }

                    if (target.Endpoint is { } endpoint)
                    {
                        forwardSocket = target.Socket;
                        forwardTarget = endpoint;
                    }
                    else if (target.Pending.Count >= _limits.MaximumPendingPackets || allocation.PendingBytes + data.Length > _limits.MaximumPendingBytes)
                    {
                        allocation.DroppedPackets++;
                        allocation.DroppedQueueFull++;
                        RelayMetrics.Drop("queue-full");
                    }
                    else
                    {
                        target.Pending.Add((data.ToArray(), now, number));
                        allocation.PendingBytes += data.Length;
                    }
                }
            }
        }

        if (foreign > 0)
        {
            RelayMetrics.Drop("foreign-source");
            if (IsLogStep(foreign))
            {
                _log.LogInformation("GAMEPLAY RELAY FOREIGN DATAGRAM gameId={GameId} port={Port} owner={Owner} expected={Expected} source={Source} bytes={Bytes} count={Count}",
                    allocation.GameId, link.Port, link.PlayerId, link.Expected, Describe(source), data.Length, foreign);
            }
            return;
        }
        if (splitSource)
        {
            RelayMetrics.SplitSources.Add(1);
            _log.LogInformation("GAMEPLAY RELAY SPLIT SOURCE gameId={GameId} player={Player} expected={Expected} source={Source}: gameplay comes from another address of the player's network, accepted while the Blaze address sends nothing",
                allocation.GameId, link.PlayerId, link.Expected, Describe(source));
        }
        if (stall > 0)
        {
            ReportStall(allocation, link, stall, stallCount, shared, otherStall, otherQuiet);
        }
        if (queued is not null)
        {
            // Queued datagrams came from the other player and go to this one, from this player's own port.
            foreach (var (payload, _) in queued)
            {
                Forward(allocation, link.Socket, flushTarget!, payload, target.PlayerId, link.PlayerId);
            }
        }
        if (forwardSocket is not null)
        {
            Forward(allocation, forwardSocket, forwardTarget!, data, link.PlayerId, target.PlayerId);
        }
        // The receive buffer remains ours until this method returns. Forward first, and never
        // build diagnostic hex or call the logger while holding the match's gate.
        if (logHandshake)
        {
            var sample = data[..Math.Min(data.Length, Math.Max(0, _limits.HandshakeLogBytes))];
            _log.LogInformation("GAMEPLAY RELAY HANDSHAKE gameId={GameId} direction={Source}->{Target} packet={Packet} source={Endpoint} bytes={Bytes} hex={Hex}",
                allocation.GameId, link.PlayerId, target.PlayerId, handshakeNumber, Describe(source), data.Length, Convert.ToHexString(sample).ToLowerInvariant());
        }
    }

    private void ReportStall(RelayAllocation allocation, RelayLink link, long stall, long count, bool shared, long otherStall, long otherQuiet)
    {
        RelayMetrics.Stalls.Add(1);
        RelayMetrics.StallDuration.Record(stall);
        if (shared)
        {
            RelayMetrics.SharedStalls.Add(1);
            _log.LogInformation("GAMEPLAY RELAY SHARED STALL gameId={GameId} gapMs={Gap}/{OtherGap} players={Player},{Other}: both players' datagrams resumed together, held at the server's end",
                allocation.GameId, stall, otherStall, link.PlayerId, link.Other.PlayerId);
        }
        else if (stall >= _limits.LoggedStallThreshold.TotalMilliseconds || IsLogStep(count))
        {
            _log.LogInformation("GAMEPLAY RELAY STALL gameId={GameId} player={Player} gapMs={Gap} otherQuietMs={OtherQuiet} stalls={Count}",
                allocation.GameId, link.PlayerId, stall, otherQuiet, count);
        }
    }

    private void Forward(RelayAllocation allocation, Socket socket, SocketAddress target, ReadOnlySpan<byte> payload, string sourceId, string targetId)
    {
        int sent;
        try
        {
            sent = socket.SendTo(payload, SocketFlags.None, target);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            allocation.CountSendFailed();
            RelayMetrics.Drop("send-failed");
            return;
        }
        allocation.CountForwarded(sent);
        RelayMetrics.Forwarded(sent);
        ObserveLinkPhase(allocation, sourceId, targetId, payload);
    }

    /// <summary>
    /// Counts the directed link phases once each, after forwarding succeeds: the established-link
    /// datagram (logged), and the promotion packet, which is reported as a completed peer link.
    /// </summary>
    private void ObserveLinkPhase(RelayAllocation allocation, string sourceId, string targetId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == ConnectedLength && payload.StartsWith(ConnectedPrefix))
        {
            bool first;
            lock (allocation.Gate)
            {
                first = allocation.ConnectedEdges.Add((sourceId, targetId));
            }
            if (first)
            {
                _log.LogInformation("GAMEPLAY RELAY PEER LINK gameId={GameId} source={Source} target={Target}", allocation.GameId, sourceId, targetId);
            }
        }
        if (payload.Length == PromotedLength && payload.StartsWith(PromotedPrefix))
        {
            bool first;
            lock (allocation.Gate)
            {
                first = allocation.PromotedEdges.Add((sourceId, targetId));
            }
            if (!first)
            {
                return;
            }
            _log.LogInformation("GAMEPLAY RELAY PROMOTED gameId={GameId} source={Source} target={Target} packetType={Type}",
                allocation.GameId, sourceId, targetId, Convert.ToHexString(payload[..4]).ToLowerInvariant());
            if (PeerLinkPromoted is { } promoted)
            {
                // Matchmaking takes its own locks and sends Blaze notifications; none of that may delay forwarding.
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        promoted(allocation.GameId, sourceId, targetId);
                    }
                    catch (Exception error)
                    {
                        _log.LogError(error, "GAMEPLAY RELAY promotion callback failed gameId={GameId}", allocation.GameId);
                    }
                }, null);
            }
        }
    }

    private static SocketAddress Copy(SocketAddress address)
    {
        var copy = new SocketAddress(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }

    private static string Describe(SocketAddress address)
    {
        try
        {
            return new IPEndPoint(IPAddress.Any, 0).Create(address).ToString() ?? "unknown";
        }
        catch (Exception error) when (error is ArgumentException or SocketException)
        {
            return "unknown";
        }
    }

    private void Sweep()
    {
        var now = Now();
        List<(long GameId, string Reason)> expired;
        List<RelayAllocation> live;
        lock (_gate)
        {
            foreach (var gameId in _endedLinks.Where(ended => now - ended.Value.ReleasedTicks >= _limits.EndedLinkMemory.TotalMilliseconds).Select(ended => ended.Key).ToList())
            {
                _endedLinks.Remove(gameId);
            }
            expired = _allocations.Values
                .Where(allocation => now - allocation.LastActivityTicks >= _limits.IdleTimeout.TotalMilliseconds
                                     || now - allocation.CreatedTicks >= _limits.MaximumLifetime.TotalMilliseconds)
                .Select(allocation => (allocation.GameId, now - allocation.LastActivityTicks >= _limits.IdleTimeout.TotalMilliseconds ? "idle-timeout" : "maximum-lifetime"))
                .ToList();
            live = _allocations.Values.ToList();
        }
        foreach (var allocation in live)
        {
            ReportSilentPlayers(allocation, now);
        }
        foreach (var (gameId, reason) in expired)
        {
            Release(gameId, reason);
        }
    }

    /// <summary>
    /// Logs, once per silence, a player who flowed and has now sent nothing for <see cref="GameplayRelayLimits.SilentPlayerThreshold"/>
    /// while the other player still sends: who dropped, and when, before either client says anything.
    /// </summary>
    internal void ReportSilentPlayers(RelayAllocation allocation, long now)
    {
        List<(string Player, long Silent, long OtherQuiet)>? silent = null;
        lock (allocation.Gate)
        {
            if (allocation.Closed)
            {
                return;
            }
            foreach (var link in allocation.Links.Values)
            {
                var other = link.Other;
                if (link.Flowed && link.HasReceived && !link.SilenceReported && now - link.LastReceivedTicks >= _limits.SilentPlayerThreshold.TotalMilliseconds
                    && other.HasReceived && now - other.LastReceivedTicks < _limits.SilentPlayerThreshold.TotalMilliseconds / 2)
                {
                    link.SilenceReported = true;
                    (silent ??= new()).Add((link.PlayerId, now - link.LastReceivedTicks, now - other.LastReceivedTicks));
                }
            }
        }
        foreach (var (player, silentFor, otherQuiet) in silent ?? [])
        {
            RelayMetrics.SilentPlayers.Add(1);
            _log.LogInformation("GAMEPLAY RELAY PLAYER SILENT gameId={GameId} player={Player} silentMs={Silent} otherQuietMs={OtherQuiet}",
                allocation.GameId, player, silentFor, otherQuiet);
        }
    }

    private static void RequireIpv4(string value, string label, bool allowAny)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork || (!allowAny && address.Equals(IPAddress.Any)))
        {
            throw new GameplayRelayException($"{label} must be a usable IPv4 literal");
        }
    }
}
