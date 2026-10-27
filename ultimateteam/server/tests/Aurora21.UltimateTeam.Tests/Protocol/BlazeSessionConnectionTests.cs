using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Server.Diagnostics;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>Real Blaze connection handler and FUT endpoint, synthetic clients, one source IP, throwaway clubs.</summary>
public sealed class BlazeSessionConnectionTests
{
    private static readonly IPAddress SharedAddress = IPAddress.Parse("203.0.113.7");
    private static readonly PlayerIdentity First = PlayerIdentity.Of("111111");
    private static readonly PlayerIdentity Second = PlayerIdentity.Of("222222");

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private sealed class SyntheticClient : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _fromServer = new();
        private readonly DefaultConnectionContext _context;
        private readonly Task _connection;
        private bool _stopped;

        public SyntheticClient(BlazeConnectionHandler handler, int port)
        {
            _context = new DefaultConnectionContext($"synthetic-{port}")
            {
                Transport = new Duplex(_toServer.Reader, _fromServer.Writer),
                RemoteEndPoint = new IPEndPoint(SharedAddress, port),
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 44321),
            };
            _connection = handler.OnConnectedAsync(_context);
        }

        /// <summary>Completes when the server is done with this connection.</summary>
        public Task Closed => _connection;

        /// <summary>The server side aborting the connection, as a panel block or a resume does.</summary>
        public void Abort() => _context.Abort();

        public async Task SendAsync(int component, int command, int number, byte[]? payload = null, byte[]? metadata = null)
        {
            await _toServer.Writer.WriteAsync(Fire2Frame.Build(component, command, number, Fire2MessageType.Message,
                payload ?? Array.Empty<byte>(), metadata ?? Array.Empty<byte>()));
        }

        public async Task<Fire2Frame> ReadUntilAsync(int component, int command, Fire2MessageType type)
        {
            while (true)
            {
                var frame = await ReadAsync();
                if (frame.Component == component && frame.Command == command && frame.Type == type)
                {
                    return frame;
                }
            }
        }

        public async Task<Fire2Frame> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var result = await _fromServer.Reader.ReadAsync(timeout.Token);
                var buffer = result.Buffer;
                if (Fire2Frame.TryRead(ref buffer, out var frame))
                {
                    _fromServer.Reader.AdvanceTo(buffer.Start, buffer.Start);
                    return frame!;
                }
                _fromServer.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    throw new EndOfStreamException("Blaze closed before a complete reply.");
                }
            }
        }

        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            await _toServer.Writer.CompleteAsync();
            await _connection.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public bool HasReply
        {
            get
            {
                if (!_fromServer.Reader.TryRead(out var result)) return false;
                var hasBytes = !result.Buffer.IsEmpty;
                _fromServer.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                return hasBytes;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _fromServer.Reader.CompleteAsync();
        }
    }

    private static BlazeConnectionHandler Handler(TestRuntime runtime, PlayerRegistry players,
        ConnectionLatencyAdmission? latencyAdmission = null)
    {
        var capture = new PacketCapture(runtime.Game, enabled: false);
        var logger = NullLogger.Instance;
        var registry = new MatchmakingRegistry(null, logger);
        var matchmaking = new RivalsMatchmaking(registry,
            new MatchmakingPayloads(BlazeSettings.Load(), () => runtime.Game.NowSeconds),
            null, runtime.Game.Config.Switches, capture, logger);
        return new BlazeConnectionHandler(players, matchmaking, capture, latencyAdmission: latencyAdmission);
    }

    private static byte[] Login(string? code, long claimedPersona = 0)
    {
        var writer = new TdfWriter().Int("PID", claimedPersona);
        if (code is not null) writer.Str("AUTH", code);
        return writer.ToArray();
    }

    /// <summary>The Fire2 metadata FIFA attaches to its first request on a resumed connection: all defaults except SKEY.</summary>
    private static byte[] ResumeMetadata(string key) => new TdfWriter()
        .Int("CNTX", 0)
        .Int("ERRC", 0)
        .Int("FLAG", 0)
        .Struct("LCTX", w => w.Int("BLID", 0).Struct("CLIP", c => c.Int("IP", 0).Int("MACI", 0).Int("PORT", 0)).Str("DVID", string.Empty))
        .Struct("MADR", w => w.Int("IP", 0).Int("MACI", 0).Int("PORT", 0))
        .Int("MIID", 0)
        .Str("NAME", string.Empty)
        .Int("SIDN", 0)
        .Str("SKEY", key)
        .ToArray();

    private static async Task<string> LoginKeyAsync(SyntheticClient client, string code)
    {
        await client.SendAsync(BlazeComponent.Authentication, AuthenticationCommand.Login, 4, Login(code));
        var authenticated = await client.ReadUntilAsync(BlazeComponent.UserSessions, UserSessionsNotification.UserAuthenticated,
            Fire2MessageType.Notification);
        return TdfReader.FindString(authenticated.Payload, "KEY")!;
    }

    private static async Task AssertResumeRefusedAsync(BlazeConnectionHandler handler, int port, string key)
    {
        await using var client = new SyntheticClient(handler, port);
        await client.SendAsync(BlazeComponent.Util, UtilCommand.Ping, 50, metadata: ResumeMetadata(key));
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(client.HasReply);
    }

    private static async Task<(int Status, string? Sid, long? Persona)> FutAsync(FutEndpoint endpoint,
        string method, string path, string? code = null, string? sid = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = SharedAddress;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = path;
        context.Request.ContentType = "application/json";
        if (sid is not null) context.Request.Headers["X-UT-SID"] = sid;
        var body = code is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(new JsonObject
        {
            ["method"] = "authcode",
            ["identification"] = new JsonObject { ["authCode"] = code },
            ["nucleusPersonaId"] = Second.PersonaId,
        }.ToJsonString());
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        await endpoint.HandleAsync(context);
        context.Response.Body.Position = 0;
        var json = await JsonNode.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, context.Response.Headers["X-UT-SID"].FirstOrDefault(),
            json?["personaId"]?.GetValue<long>());
    }

    [Fact]
    public async Task SameIpClientsBindToTheirCodesAcrossBlazeAndFut()
    {
        using var runtime = TestRuntime.Create();
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(new NativeAuthSettings(900, 7200)),
        };
        var sessions = players.NativeSessions!;
        var firstCode = sessions.IssueCode(First);
        var secondCode = sessions.IssueCode(Second);
        var handler = Handler(runtime, players);
        var openedBefore = players.OpenCount;

        await using var first = new SyntheticClient(handler, 41001);
        await first.SendAsync(BlazeComponent.Util, UtilCommand.Ping, 1);
        Assert.Equal(Fire2MessageType.Reply, (await first.ReadAsync()).Type);
        await first.SendAsync(BlazeComponent.Util, UtilCommand.PreAuth, 2);
        Assert.Equal(Fire2MessageType.Reply, (await first.ReadAsync()).Type);
        await first.SendAsync(BlazeComponent.Util, UtilCommand.FetchClientConfig, 3,
            new TdfWriter().Str("CFID", "OSDK_CORE").ToArray());
        Assert.Equal(Fire2MessageType.Reply, (await first.ReadAsync()).Type);
        Assert.Equal(openedBefore, players.OpenCount);

        // A claimed persona never overrides the issued code.
        await first.SendAsync(BlazeComponent.Authentication, AuthenticationCommand.Login, 4,
            Login(firstCode, Second.PersonaId));
        var firstReply = await first.ReadAsync();
        Assert.Equal(Fire2MessageType.Reply, firstReply.Type);
        Assert.Equal(First.PersonaId, TdfReader.FindInteger(firstReply.Payload, "PID"));

        await using var second = new SyntheticClient(handler, 41002);
        await second.SendAsync(BlazeComponent.Authentication, AuthenticationCommand.Login, 1, Login(secondCode));
        var secondReply = await second.ReadAsync();
        Assert.Equal(Fire2MessageType.Reply, secondReply.Type);
        Assert.Equal(Second.PersonaId, TdfReader.FindInteger(secondReply.Payload, "PID"));
        Assert.Equal(openedBefore + 2, players.OpenCount);

        var endpoint = new FutEndpoint(players);
        var firstAuth = await FutAsync(endpoint, "POST", "/ut/auth", firstCode);
        var secondAuth = await FutAsync(endpoint, "POST", "/ut/auth", secondCode);
        Assert.Equal((200, 200), (firstAuth.Status, secondAuth.Status));
        Assert.NotEqual(firstAuth.Sid, secondAuth.Sid);
        var firstUser = await FutAsync(endpoint, "GET", "/ut/game/fifa21/user", sid: firstAuth.Sid);
        var secondUser = await FutAsync(endpoint, "GET", "/ut/game/fifa21/user", sid: secondAuth.Sid);
        Assert.Equal((First.PersonaId, Second.PersonaId), (firstUser.Persona, secondUser.Persona));

        await first.StopAsync();
        await using var reconnect = new SyntheticClient(handler, 41003);
        await reconnect.SendAsync(BlazeComponent.Authentication, AuthenticationCommand.Login, 1, Login(firstCode));
        Assert.Equal(First.PersonaId, TdfReader.FindInteger((await reconnect.ReadAsync()).Payload, "PID"));
        Assert.Equal(openedBefore + 2, players.OpenCount);
    }

    [Fact]
    public async Task InvalidBlazeCredentialsAndPreloginClubCommandsNeverOpenAClub()
    {
        var clock = new TestRuntime.MovableTime(1_790_000_000);
        using var runtime = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(new NativeAuthSettings(900, 7200), clock),
        };
        var sessions = players.NativeSessions!;
        var handler = Handler(runtime, players);
        var openedBefore = players.OpenCount;
        var expired = sessions.IssueCode(First);
        clock.Now += 900;
        var revoked = sessions.IssueCode(Second);
        sessions.Revoke(Second.Id);

        async Task Refused(int port, int component, int command, byte[] payload)
        {
            await using var client = new SyntheticClient(handler, port);
            await client.SendAsync(component, command, 1, payload);
            await client.StopAsync();
            Assert.False(client.HasReply);
            Assert.Equal(openedBefore, players.OpenCount);
        }

        await Refused(42001, BlazeComponent.Authentication, AuthenticationCommand.Login, Login(null));
        await Refused(42002, BlazeComponent.Authentication, AuthenticationCommand.Login, Login(new string('0', 40)));
        await Refused(42003, BlazeComponent.Authentication, AuthenticationCommand.Login, Login(expired));
        await Refused(42004, BlazeComponent.Authentication, AuthenticationCommand.Login, Login(revoked));
        await Refused(42005, BlazeComponent.Authentication, AuthenticationCommand.Login,
            new TdfWriter().Str("AUTH", expired).Str("AUTH", expired).ToArray());
        await Refused(42006, BlazeComponent.Fifa, FifaCommand.LoginSession, Array.Empty<byte>());
    }

    /// <summary>
    /// FIFA resumes a dropped Blaze link on a new connection with the KEY of our 30722/8 notification as Fire2 metadata SKEY
    /// (10-04: two Weekend League games died when we refused it). Only the live key of the player's own login resumes, and
    /// the resumed connection replaces the old one.
    /// </summary>
    [Fact]
    public async Task AResumeNeedsTheLiveKeyOfThePlayersOwnLogin()
    {
        using var runtime = TestRuntime.Create();
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(new NativeAuthSettings(900, 7200)),
        };
        var sessions = players.NativeSessions!;
        var handler = Handler(runtime, players);

        await using var first = new SyntheticClient(handler, 43001);
        var firstKey = await LoginKeyAsync(first, sessions.IssueCode(First));
        await using var second = new SyntheticClient(handler, 43002);
        var secondKey = await LoginKeyAsync(second, sessions.IssueCode(Second));

        // Every login gets its own random key, never the configured constant every player used to share.
        Assert.Matches("^[0-9a-f]{32}$", firstKey);
        Assert.NotEqual(firstKey, secondKey);
        Assert.NotEqual(BlazeSettings.Load().Session.TitleSessionKey, firstKey);

        // An unknown key gets no reply and opens nothing.
        var openedBefore = players.OpenCount;
        await AssertResumeRefusedAsync(handler, 43003, new string('0', 32));
        await AssertResumeRefusedAsync(handler, 43004, firstKey.ToUpperInvariant());
        Assert.Equal(openedBefore, players.OpenCount);

        // The live key resumes the first player's session without a login: the resume ping and then a request the prelogin
        // gate refuses are answered for that player, and the old connection is closed.
        await using var resumed = new SyntheticClient(handler, 43005);
        await resumed.SendAsync(BlazeComponent.Util, UtilCommand.Ping, 60, metadata: ResumeMetadata(firstKey));
        var pong = await resumed.ReadAsync();
        Assert.Equal((Fire2MessageType.Reply, 60), (pong.Type, pong.MessageNumber));
        await resumed.SendAsync(BlazeComponent.Util, UtilCommand.PostAuth, 61);
        var postAuth = await resumed.ReadAsync();
        Assert.Equal((Fire2MessageType.Reply, 61), (postAuth.Type, postAuth.MessageNumber));
        Assert.Equal(First.PersonaId, TdfReader.FindInteger(postAuth.Payload, "UID"));
        await first.Closed.WaitAsync(TimeSpan.FromSeconds(20));

        // The key stays with the session through the resume, and dies with the connection that holds it.
        await resumed.StopAsync();
        await AssertResumeRefusedAsync(handler, 43006, firstKey);

        // A connection the server aborted (a panel block, a maintenance kick) cannot be resumed either.
        second.Abort();
        await second.Closed.WaitAsync(TimeSpan.FromSeconds(20));
        await AssertResumeRefusedAsync(handler, 43007, secondKey);
    }

    [Fact]
    public async Task ThePingLimitRefusesANewLoginButNeverAResume()
    {
        using var runtime = TestRuntime.Create();
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(new NativeAuthSettings(900, 7200)),
        };
        var sessions = players.NativeSessions!;
        double ping = 40;
        var handler = Handler(runtime, players, new ConnectionLatencyAdmission(200, _ => ping));

        await using var first = new SyntheticClient(handler, 43101);
        var firstKey = await LoginKeyAsync(first, sessions.IssueCode(First));

        // The line gets congested: a fresh login over the limit is refused before its club opens.
        ping = 900;
        var openedBefore = players.OpenCount;
        await using (var refused = new SyntheticClient(handler, 43102))
        {
            await refused.SendAsync(BlazeComponent.Authentication, AuthenticationCommand.Login, 4, Login(sessions.IssueCode(Second)));
            await refused.Closed.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(refused.HasReply);
        }
        Assert.Equal(openedBefore, players.OpenCount);

        // The player already in a match reconnects on that same congested line: the resume is answered.
        await using var resumed = new SyntheticClient(handler, 43103);
        await resumed.SendAsync(BlazeComponent.Util, UtilCommand.Ping, 60, metadata: ResumeMetadata(firstKey));
        var pong = await resumed.ReadAsync();
        Assert.Equal((Fire2MessageType.Reply, 60), (pong.Type, pong.MessageNumber));
        await first.Closed.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ASessionKeyBelongsToOneConnectionOfOnePlayer()
    {
        using var runtime = TestRuntime.Create();
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var firstLease = players.Acquire(First);
        using var secondLease = players.Acquire(Second);
        BlazeClient Connection(PlayerSession session) => new(session, new BlazeResponses(session.Fut), new Pipe().Writer, "203.0.113.7", "127.0.0.1");
        BlazeSessionKeys.Owner Owner(BlazeClient client) => new(client, () => { }, CancellationToken.None);
        var keys = new BlazeSessionKeys();
        var (old, resumed, stranger) = (Connection(firstLease.Session), Connection(firstLease.Session), Connection(secondLease.Session));

        var oldKey = BlazeSessionKeys.NewKey();
        keys.Register(oldKey, Owner(old));
        var key = BlazeSessionKeys.NewKey();
        keys.Register(key, Owner(old));
        Assert.Null(keys.Find(oldKey));
        Assert.Null(keys.Find(null));
        Assert.Null(keys.Find(string.Empty));

        // Another player's connection can never take the key over, and a connection cannot take over its own key.
        Assert.Null(keys.TakeOver(key, Owner(stranger)));
        Assert.Null(keys.TakeOver(key, Owner(old)));
        Assert.Same(old, keys.TakeOver(key, Owner(resumed))!.Client);
        Assert.Null(keys.TakeOver(key, Owner(resumed)));

        // The old connection closing leaves the moved key alone; the new one closing takes it.
        keys.Release(old);
        Assert.Same(resumed, keys.Find(key)!.Client);
        keys.Release(resumed);
        Assert.Null(keys.Find(key));

        // A key whose connection is already closing is dead.
        using var closed = new CancellationTokenSource();
        closed.Cancel();
        keys.Register(key, new BlazeSessionKeys.Owner(old, () => { }, closed.Token));
        Assert.Null(keys.Find(key));
        Assert.Null(keys.TakeOver(key, Owner(resumed)));
        Assert.NotEqual(BlazeSessionKeys.Fingerprint(key), key[..12]);
    }

    /// <summary>
    /// A connection closed mid-match keeps its key resumable for the grace period (FeatureSwitches.ResumeGraceSeconds): a
    /// resume on a new connection takes it over; after the grace, or once released, it is dead like any closed key.
    /// </summary>
    [Fact]
    public async Task AKeyClosedMidMatchStaysResumableForTheGraceOnly()
    {
        using var runtime = TestRuntime.Create();
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var lease = players.Acquire(First);
        BlazeClient Connection() => new(lease.Session, new BlazeResponses(lease.Session.Fut), new Pipe().Writer, "203.0.113.7", "127.0.0.1");
        var keys = new BlazeSessionKeys();
        var (old, resumed, late) = (Connection(), Connection(), Connection());
        using var oldClosed = new CancellationTokenSource();
        var key = BlazeSessionKeys.NewKey();
        keys.Register(key, new BlazeSessionKeys.Owner(old, () => { }, oldClosed.Token));
        oldClosed.Cancel();
        Assert.Null(keys.Find(key));

        // Within the grace the closed connection's key resumes, and the resume takes it over for good.
        Assert.True(keys.Linger(old, TimeSpan.FromSeconds(5)));
        Assert.Same(old, keys.Find(key)!.Client);
        Assert.Same(old, keys.TakeOver(key, new BlazeSessionKeys.Owner(resumed, () => { }, CancellationToken.None))!.Client);
        keys.Release(old);
        Assert.Same(resumed, keys.Find(key)!.Client);

        // A key nobody resumes dies when the grace runs out; a zero grace, or a connection without a key, never lingers.
        using var resumedClosed = new CancellationTokenSource();
        keys.Register(key, new BlazeSessionKeys.Owner(resumed, () => { }, resumedClosed.Token));
        resumedClosed.Cancel();
        Assert.False(keys.Linger(resumed, TimeSpan.Zero));
        Assert.False(keys.Linger(late, TimeSpan.FromSeconds(5)));
        Assert.True(keys.Linger(resumed, TimeSpan.FromMilliseconds(200)));
        Assert.NotNull(keys.Find(key));
        await Task.Delay(400);
        Assert.Null(keys.Find(key));
        Assert.Null(keys.TakeOver(key, new BlazeSessionKeys.Owner(late, () => { }, CancellationToken.None)));

        // Released early, it is dead at once.
        Assert.True(keys.Linger(resumed, TimeSpan.FromSeconds(5)));
        keys.Release(resumed);
        Assert.Null(keys.Find(key));
    }

    [Fact]
    public void TheResumeGraceIsFiveSecondsAndAtMostTen()
    {
        Assert.Equal(5, FeatureSwitches.FromEnvironment(_ => null).ResumeGraceSeconds);
        Assert.Equal(0, FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.ResumeGraceVariable ? "0" : null).ResumeGraceSeconds);
        Assert.Equal(10, FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.ResumeGraceVariable ? "60" : null).ResumeGraceSeconds);
        Assert.Equal(0, FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.ResumeGraceVariable ? "-3" : null).ResumeGraceSeconds);
    }
}
