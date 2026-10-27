using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Server.Diagnostics;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// The online-match behaviour the production Python server ran with and the C# port first left out (flag bank
/// prod-flags-20260927): the Messaging relay with per-message echo, AIDS on every identity, the OSDK pregame relays, the
/// GameSetup-then-InitiateConnections order, the ONLINE end-of-match report, the Rivals pairing snapshot and the client
/// configuration's kill switches. Real Blaze connection handler, two synthetic clients, throwaway clubs.
/// </summary>
public sealed class OnlineMatchParityTests
{
    private static readonly PlayerIdentity Host = PlayerIdentity.Of("111111");
    private static readonly PlayerIdentity Guest = PlayerIdentity.Of("222222");

    /// <summary>The co-op Ready vote's FourCCs: ClientMessage TYPE 'gctl', TAG 'abrq'.</summary>
    private const long GameControl = 0x6763746C;
    private const long ArbitrationRequest = 0x61627271;

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _fromServer = new();
        private readonly Task _connection;
        private readonly List<Fire2Frame> _pending = new();
        private int _number;

        public Client(BlazeConnectionHandler handler, int port)
        {
            var context = new DefaultConnectionContext($"online-{port}")
            {
                Transport = new Duplex(_toServer.Reader, _fromServer.Writer),
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), port),
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 44321),
            };
            _connection = handler.OnConnectedAsync(context);
        }

        public async Task<Fire2Frame> RequestAsync(int component, int command, byte[]? payload = null)
        {
            var number = ++_number;
            await _toServer.Writer.WriteAsync(Fire2Frame.Build(component, command, number, Fire2MessageType.Message, payload ?? Array.Empty<byte>()));
            return await NextAsync(frame => frame.Type == Fire2MessageType.Reply && frame.MessageNumber == number);
        }

        /// <summary>The next received frame matching <paramref name="match"/>; frames skipped on the way stay available.</summary>
        public async Task<Fire2Frame> NextAsync(Func<Fire2Frame, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var index = _pending.FindIndex(frame => match(frame));
                if (index >= 0)
                {
                    var found = _pending[index];
                    _pending.RemoveAt(index);
                    return found;
                }
                var result = await _fromServer.Reader.ReadAsync(timeout.Token);
                var buffer = result.Buffer;
                while (Fire2Frame.TryRead(ref buffer, out var frame))
                {
                    _pending.Add(frame!);
                }
                _fromServer.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    throw new EndOfStreamException("Blaze closed.");
                }
            }
        }

        public Task<Fire2Frame> NotificationAsync(int component, int command) =>
            NextAsync(frame => frame.Type == Fire2MessageType.Notification && frame.Component == component && frame.Command == command);

        /// <summary>Every notification received so far (after a short settle) with this component and command, taken.</summary>
        public async Task<int> DrainAsync(int component, int command)
        {
            await Task.Delay(300);
            while (_fromServer.Reader.TryRead(out var result))
            {
                var buffer = result.Buffer;
                while (Fire2Frame.TryRead(ref buffer, out var frame))
                {
                    _pending.Add(frame!);
                }
                _fromServer.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.Buffer.IsEmpty)
                {
                    break;
                }
            }
            return _pending.RemoveAll(frame => frame.Type == Fire2MessageType.Notification && frame.Component == component && frame.Command == command);
        }

        public async ValueTask DisposeAsync()
        {
            await _toServer.Writer.CompleteAsync();
            await _connection.WaitAsync(TimeSpan.FromSeconds(20));
            await _fromServer.Reader.CompleteAsync();
        }
    }

    private static byte[] ClientMessage(long gameId, long flags, long targetComponent = BlazeComponent.GameManager)
    {
        var w = new ArrayBufferWriter<byte>();
        TdfTag.Write(w, "ATTR", TdfType.Map);
        w.Write(new[] { (byte)TdfType.Integer, (byte)TdfType.String });
        TdfWriter.WriteVarInt(w, 1);
        TdfWriter.WriteVarInt(w, 0x61627279);
        var value = Encoding.UTF8.GetBytes("1");
        TdfWriter.WriteVarInt(w, value.Length + 1);
        w.Write(value);
        w.Write(new byte[] { 0 });
        TdfTag.Write(w, "FLAG", TdfType.Integer);
        TdfWriter.WriteVarInt(w, flags);
        TdfTag.Write(w, "TAG", TdfType.Integer);
        TdfWriter.WriteVarInt(w, ArbitrationRequest);
        TdfTag.Write(w, "TIDS", TdfType.List);
        w.Write(new[] { (byte)TdfType.Integer });
        TdfWriter.WriteVarInt(w, 1);
        TdfWriter.WriteVarInt(w, gameId);
        TdfTag.Write(w, "TTYP", TdfType.ObjectType);
        TdfWriter.WriteVarInt(w, targetComponent);
        TdfWriter.WriteVarInt(w, 1);
        TdfTag.Write(w, "TYPE", TdfType.Integer);
        TdfWriter.WriteVarInt(w, GameControl);
        return w.WrittenSpan.ToArray();
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public async Task AnOnlineMatchRunsProductionsReadyVoteRelaysAndReport()
    {
        using var runtime = TestRuntime.Create();
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(new NativeAuthSettings(900, 7200)),
        };
        var capture = new PacketCapture(runtime.Game, enabled: false);
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        players.MatchRoster = new MatchmakingRoster(registry);
        var handler = new BlazeConnectionHandler(players, new RivalsMatchmaking(registry,
            new MatchmakingPayloads(BlazeSettings.Load(), () => runtime.Game.NowSeconds), null, FeatureSwitches.FromEnvironment(_ => null), capture,
            NullLogger.Instance), capture);

        await using var host = new Client(handler, 42001);
        await using var guest = new Client(handler, 42002);
        foreach (var (client, identity) in new[] { (host, Host), (guest, Guest) })
        {
            var code = players.NativeSessions!.IssueCode(identity);
            await client.RequestAsync(BlazeComponent.Authentication, AuthenticationCommand.Login, new TdfWriter().Str("AUTH", code).ToArray());
            // The login identities are filed under the pc platform (AIDS {PLAT 4}), UserAdded right after AID, LoginSession first.
            var userAdded = TdfShape.Of((await client.NotificationAsync(BlazeComponent.UserSessions, UserSessionsNotification.UserAdded)).Payload);
            Assert.Equal(new[] { "/USER:3/AID:0", "/USER:3/AIDS:3", "/USER:3/AIDS:3/PLAT:0 = 4" }, userAdded.SkipWhile(line => line != "/USER:3/AID:0").Take(3));
            var loginSession = TdfShape.Of((await client.NotificationAsync(BlazeComponent.UserSessions, UserSessionsNotification.UserAuthenticated)).Payload);
            Assert.Equal(new[] { "/AIDS:3", "/AIDS:3/PLAT:0 = 4", "/BUID:0" }, loginSession.Take(3));
        }

        var search = new TdfWriter().Str("SCNM", "FUT_Rivals").ToArray();
        await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.StartMatchmakingScenario, search);
        await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.StartMatchmakingScenario, search);
        var gameId = registry.GameIdFor(Host.Id)!.Value;

        // Each player gets GameSetup, then InitiateConnections with the same payload.
        foreach (var client in new[] { host, guest })
        {
            var setup = await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameSetup);
            var initiate = await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.JoiningPlayerInitiateConnections);
            Assert.Equal(setup.Payload, initiate.Payload);
            Assert.Empty(TdfShape.OrderViolations(setup.Payload));
        }

        // The Rivals pairing is snapshotted for both clubs when Blaze pairs them (CreateMatch usually arrives first).
        foreach (var (identity, opponent) in new[] { (Host, Guest), (Guest, Host) })
        {
            using var lease = players.Acquire(identity);
            var pending = await WaitForAsync(() => lease.Session.Game.InUnit(uow => uow.QueryAsync(
                "SELECT opponent_persona FROM ultimateteam.rivals_pending WHERE club_id=@club", reader => reader.GetInt64(0))));
            Assert.Equal(new[] { opponent.PersonaId }, pending);
        }

        // Messaging: the reply and NotifyMessage share one id; with Echo the sender gets its own message back too.
        var echoed = ClientMessage(gameId, flags: 0x2);
        var reply = await host.RequestAsync(BlazeComponent.Messaging, MessagingCommand.SendMessage, echoed);
        var messageId = TdfReader.FindInteger(reply.Payload, "MGID")!.Value;
        foreach (var client in new[] { host, guest })
        {
            var notify = await client.NotificationAsync(BlazeComponent.Messaging, MessagingNotification.NotifyMessage);
            Assert.True(Contains(notify.Payload, echoed));
            Assert.Equal(messageId, TdfReader.FindInteger(notify.Payload, "MGID"));
            Assert.Equal(Host.PersonaId, TdfReader.FindObjectId(notify.Payload, "SRCE")!.Value.EntityId);
        }
        // Without Echo only the other player gets it; a message for another game or component is not relayed.
        await guest.RequestAsync(BlazeComponent.Messaging, MessagingCommand.SendMessage, ClientMessage(gameId, flags: 0));
        Assert.True(Contains((await host.NotificationAsync(BlazeComponent.Messaging, MessagingNotification.NotifyMessage)).Payload, ClientMessage(gameId, flags: 0)));
        await guest.RequestAsync(BlazeComponent.Messaging, MessagingCommand.SendMessage, ClientMessage(gameId + 1, flags: 0x2));
        await guest.RequestAsync(BlazeComponent.Messaging, MessagingCommand.SendMessage, ClientMessage(gameId, flags: 0x2, targetComponent: BlazeComponent.UserSessions));
        Assert.Equal(0, await guest.DrainAsync(BlazeComponent.Messaging, MessagingNotification.NotifyMessage));
        Assert.Equal(0, await host.DrainAsync(BlazeComponent.Messaging, MessagingNotification.NotifyMessage));

        // OSDK: a player attribute (REQ=1) and a game attribute reach both players once; a repeat is not relayed again.
        var request = new TdfWriter().MapStringString("ATTR", new[] { KeyValuePair.Create("REQ", "1") }).Int("GID", gameId).Int("PID", Guest.PersonaId).ToArray();
        await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, request);
        foreach (var client in new[] { host, guest })
        {
            var change = await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange);
            Assert.Equal("1", TdfReader.FindStringMap(change.Payload, "ATTR")["REQ"]);
            Assert.Equal(Guest.PersonaId, TdfReader.FindInteger(change.Payload, "PID"));
        }
        await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, request);
        Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
        await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetGameAttributes,
            new TdfWriter().MapStringString("ATTR", new[] { KeyValuePair.Create("ARB", "3") }).Int("GID", gameId).ToArray());
        foreach (var client in new[] { host, guest })
        {
            Assert.Equal("3", TdfReader.FindStringMap((await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameAttribChange)).Payload, "ATTR")["ARB"]);
        }

        // LockGame: the open settings (0x2) are cleared under the mask, and GameSettingsChange always follows.
        var lockGame = new TdfWriter().Int("GID", gameId).Int("GSET", 0).Int("GSTM", 0x80F).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetGameSettings, lockGame);
            foreach (var client in new[] { host, guest })
            {
                Assert.Equal(0, TdfReader.FindInteger((await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameSettingsChange)).Payload, "ATTR"));
            }
        }
        var (settings, gameAttributes, playerAttributes) = registry.OsdkState(gameId);
        Assert.Equal(0, settings);
        Assert.Equal("3", gameAttributes["ARB"]);
        Assert.Equal("1", playerAttributes[Guest.Id]["REQ"]);

        // The ONLINE report is answered with ResultNotification; the game ends only once both players have reported.
        await host.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport, new TdfWriter().Int("GRID", 77).ToArray());
        Assert.Equal(77, TdfReader.FindInteger((await host.NotificationAsync(BlazeComponent.GameReporting, GameReportingCommand.ResultNotification)).Payload, "GRID"));
        Assert.Equal(gameId, registry.GameIdFor(Guest.Id));
        await guest.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport, new TdfWriter().Int("GRID", 78).ToArray());
        Assert.Equal(78, TdfReader.FindInteger((await guest.NotificationAsync(BlazeComponent.GameReporting, GameReportingCommand.ResultNotification)).Payload, "GRID"));
        Assert.Null(registry.GameIdFor(Host.Id));
        Assert.Null(registry.GameIdFor(Guest.Id));
    }

    /// <summary>
    /// By default a pregame leave ends the game without telling the other player: a client told in the kit screen that its
    /// opponent left bails out of its own next setup 1-5 s in (live 2026-10-05). It learns through its own peer timeout.
    /// </summary>
    [Fact]
    public async Task LeavingThePregameIsNotAnnouncedByDefault()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42045, 42046);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.LeaveGameByGroup,
                new TdfWriter().Int("GID", gameId).Int("PID", Host.PersonaId).Int("REAS", PlayerRemovedReason.GroupLeft).ToArray());
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved));
            Assert.Null(registry.GameIdFor(Host.Id));
            Assert.Null(registry.GameIdFor(Guest.Id));
        }
    }

    /// <summary>
    /// In the self mode each client is also told of its own removal, the only NotifyPlayerRemoved FIFA destroys its local game
    /// for: the remaining player GAME_DESTROYED after the leaver's removal, the leaver its own reason.
    /// </summary>
    [Fact]
    public async Task LeavingThePregameRemovesEachClientInSelfMode()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42047, 42048, pregameMode: PregameDeparture.SelfRemoved);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.LeaveGameByGroup,
                new TdfWriter().Int("GID", gameId).Int("PID", Host.PersonaId).Int("REAS", PlayerRemovedReason.GroupLeft).ToArray());
            var opponent = (await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved)).Payload;
            Assert.Equal(Host.PersonaId, TdfReader.FindInteger(opponent, "PID"));
            var own = (await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved)).Payload;
            Assert.Equal(gameId, TdfReader.FindInteger(own, "GID"));
            Assert.Equal(Guest.PersonaId, TdfReader.FindInteger(own, "PID"));
            Assert.Equal(PlayerRemovedReason.GameDestroyed, TdfReader.FindInteger(own, "REAS"));
            await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved);
            var leaver = (await host.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved)).Payload;
            Assert.Equal(Host.PersonaId, TdfReader.FindInteger(leaver, "PID"));
            Assert.Equal(PlayerRemovedReason.GroupLeft, TdfReader.FindInteger(leaver, "REAS"));
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved));
            Assert.Null(registry.GameIdFor(Guest.Id));
        }
    }

    /// <summary>
    /// In the announce mode a player who leaves in the pregame (LeaveGameByGroup, REAS 8 GROUP_LEFT)
    /// is announced to the other player at once through PlayerRemoved, then GameRemoved.
    /// </summary>
    [Fact]
    public async Task LeavingThePregameTellsTheOtherPlayerInAnnounceMode()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42041, 42042, pregameMode: PregameDeparture.Announce);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            // Leaving a game the server already ended (FIFA does this with REAS 26 when it joins its next game) changes nothing.
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.RemovePlayer,
                new TdfWriter().Int("GID", 1).Int("PID", Host.PersonaId).Int("REAS", PlayerRemovedReason.PlayerLeftSwitchedGameSession).ToArray());
            Assert.Equal(gameId, registry.GameIdFor(Guest.Id));

            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.LeaveGameByGroup,
                new TdfWriter().Int("GID", gameId).Int("PID", Host.PersonaId).Int("REAS", PlayerRemovedReason.GroupLeft).ToArray());
            var removed = (await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved)).Payload;
            Assert.Equal(new[] { "/GID:0", "/PID:0", "/REAS:0" }, TdfShape.Of(removed));
            Assert.Equal(gameId, TdfReader.FindInteger(removed, "GID"));
            Assert.Equal(Host.PersonaId, TdfReader.FindInteger(removed, "PID"));
            Assert.Equal(PlayerRemovedReason.GroupLeft, TdfReader.FindInteger(removed, "REAS"));
            // Then the game itself, so the remaining client does not carry the dead game into its next setup (live 2026-10-05).
            var gameRemoved = (await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved)).Payload;
            Assert.Equal(new[] { "/GID:0", "/REAS:0" }, TdfShape.Of(gameRemoved));
            Assert.Equal(gameId, TdfReader.FindInteger(gameRemoved, "GID"));
            Assert.Equal(GameDestructionReason.SysGameEnding, TdfReader.FindInteger(gameRemoved, "REAS"));
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved));
            Assert.Null(registry.GameIdFor(Host.Id));
            Assert.Null(registry.GameIdFor(Guest.Id));
        }
    }

    /// <summary>With ULTIMATETEAM21_GAME_REMOVED=0 a pregame leave is announced through PlayerRemoved only.</summary>
    [Fact]
    public async Task GameRemovedCanBeSwitchedOff()
    {
        var switches = FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.GameRemovedNotificationVariable ? "0" : null);
        var (runtime, players, _, host, guest, gameId) = await PairedClientsAsync(42043, 42044, switches, PregameDeparture.Announce);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.LeaveGameByGroup,
                new TdfWriter().Int("GID", gameId).Int("PID", Host.PersonaId).Int("REAS", PlayerRemovedReason.GroupLeft).ToArray());
            await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved);
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved));
        }
    }

    private static readonly FeatureSwitches QuickStall = FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.SetupStallVariable ? "1" : null);

    private static byte[] EndpointInitialized(long gameId) => new TdfWriter().Int("GID", gameId).ToArray();

    private static byte[] PeerLinked(long gameId, long targetPersona) =>
        new TdfWriter().Int("FLGS", MatchmakingRegistry.MeshConnectedStatus).Int("GID", gameId).ObjectId("TCG", 30722, 1, targetPersona).ToArray();

    /// <summary>
    /// Game 285813784 (2026-10-06): the host initialized and waited on "Initializing..." while the guest never reported after
    /// GameSetup. Once the stall window passes the game ends and each client is told it was removed itself, never that its
    /// opponent left.
    /// </summary>
    [Fact]
    public async Task AStalledSetupRemovesEachPlayerFromTheGame()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42071, 42072, QuickStall);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.MeshEndpointsConnected, EndpointInitialized(gameId));
            foreach (var (client, identity) in new[] { (host, Host), (guest, Guest) })
            {
                var removed = (await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved)).Payload;
                Assert.Equal(gameId, TdfReader.FindInteger(removed, "GID"));
                Assert.Equal(identity.PersonaId, TdfReader.FindInteger(removed, "PID"));
                Assert.Equal(PlayerRemovedReason.GameDestroyed, TdfReader.FindInteger(removed, "REAS"));
                Assert.Equal(0, await client.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
                Assert.Equal(0, await client.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved));
            }
            Assert.Null(registry.GameIdFor(Host.Id));
            Assert.Null(registry.GameIdFor(Guest.Id));
            // Their own leave requests for the ended game are still answered.
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.LeaveGameByGroup,
                new TdfWriter().Int("GID", gameId).Int("PID", Host.PersonaId).Int("REAS", PlayerRemovedReason.GroupLeft).ToArray());
        }
    }

    /// <summary>A game whose mesh completed is never touched by the stall check.</summary>
    [Fact]
    public async Task ACompletedMeshIsNotAStall()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42073, 42074, QuickStall);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.MeshEndpointsConnected, EndpointInitialized(gameId));
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.MeshEndpointsConnected, EndpointInitialized(gameId));
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.MeshEndpointsConnected, PeerLinked(gameId, Guest.PersonaId));
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.MeshEndpointsConnected, PeerLinked(gameId, Host.PersonaId));
            Assert.True(await registry.WaitForMeshCompletionAsync(gameId, TimeSpan.FromSeconds(5)));
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            Assert.Equal(gameId, registry.GameIdFor(Host.Id));
            Assert.Equal(gameId, registry.GameIdFor(Guest.Id));
        }
    }

    /// <summary>With no client waiting (neither reported after GameSetup) or the switch at 0, the game is left alone.</summary>
    [Theory]
    [InlineData(42075, 42076, "1", false)]
    [InlineData(42077, 42078, "0", true)]
    public async Task TheStallCheckNeedsAWaitingPlayerAndItsSwitch(int hostPort, int guestPort, string seconds, bool hostInitializes)
    {
        var switches = FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.SetupStallVariable ? seconds : null);
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(hostPort, guestPort, switches);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            if (hostInitializes)
            {
                await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.MeshEndpointsConnected, EndpointInitialized(gameId));
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            Assert.Equal(gameId, registry.GameIdFor(Guest.Id));
        }
    }

    [Fact]
    public void TheSetupStallCheckDefaultsToTwentySeconds()
    {
        Assert.Equal(20, FeatureSwitches.FromEnvironment(_ => null).SetupStallSeconds);
        Assert.Equal(0, FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.SetupStallVariable ? "0" : null).SetupStallSeconds);
        Assert.Equal(20, FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.SetupStallVariable ? "soon" : null).SetupStallSeconds);
    }

    private static FeatureSwitches RequestHold(string seconds) =>
        FeatureSwitches.FromEnvironment(name => name == FeatureSwitches.RequestHoldVariable ? seconds : null);

    private static byte[] RequestAttribute(long gameId, string value) =>
        new TdfWriter().MapStringString("ATTR", new[] { KeyValuePair.Create("REQ", value) }).Int("GID", gameId).Int("PID", Guest.PersonaId).ToArray();

    private static async Task<string> NextRequestAsync(Client client) =>
        TdfReader.FindStringMap((await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange)).Payload, "ATTR")["REQ"];

    /// <summary>
    /// 2026-10-07, 465 setups: a guest REQ=1 reaching the host 0.6 s after GameSetup left 36% stuck on "Initializing...", 2% from
    /// 0.9 s on. The guest hears its own change at once; the host only once the hold after its GameSetup has passed. Game
    /// attributes are never held.
    /// </summary>
    [Fact]
    public async Task TheGuestsRequestReachesTheHostOnlyAfterTheHold()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42081, 42082, RequestHold("1"));
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            var setup = registry.MatchFor(gameId)!.SetupDeliveredTicks!.Value;
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, RequestAttribute(gameId, "1"));
            Assert.Equal("1", await NextRequestAsync(guest));
            Assert.True(Environment.TickCount64 - setup < 600);
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetGameAttributes,
                new TdfWriter().MapStringString("ATTR", new[] { KeyValuePair.Create("ARB", "3") }).Int("GID", gameId).ToArray());
            await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameAttribChange);
            Assert.True(Environment.TickCount64 - setup < 970);
            Assert.Equal("1", await NextRequestAsync(host));
            Assert.True(Environment.TickCount64 - setup >= 970);
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
        }
    }

    /// <summary>A REQ=2 sent while REQ=1 is still held never overtakes it.</summary>
    [Fact]
    public async Task HeldRequestsReachTheHostInTheirOrder()
    {
        var (runtime, players, _, host, guest, gameId) = await PairedClientsAsync(42083, 42084, RequestHold("1"));
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, RequestAttribute(gameId, "1"));
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, RequestAttribute(gameId, "2"));
            Assert.Equal("1", await NextRequestAsync(guest));
            Assert.Equal("2", await NextRequestAsync(guest));
            Assert.Equal("1", await NextRequestAsync(host));
            Assert.Equal("2", await NextRequestAsync(host));
        }
    }

    /// <summary>Once the hold has passed, a request reaches the host at once.</summary>
    [Fact]
    public async Task ARequestAfterTheHoldIsRelayedAtOnce()
    {
        var (runtime, players, _, host, guest, gameId) = await PairedClientsAsync(42085, 42086, RequestHold("0.3"));
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, RequestAttribute(gameId, "1"));
            Assert.Equal(1, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
            Assert.Equal(1, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
        }
    }

    /// <summary>With the switch at 0 the request reaches both players at once, as before the hold.</summary>
    [Fact]
    public async Task AHoldOfZeroRelaysAtOnce()
    {
        var (runtime, players, _, host, guest, gameId) = await PairedClientsAsync(42087, 42088, RequestHold("0"));
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, RequestAttribute(gameId, "1"));
            Assert.Equal(1, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
            Assert.Equal(1, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
        }
    }

    /// <summary>A guest who leaves during the hold takes its held request with it.</summary>
    [Fact]
    public async Task AHeldRequestIsDroppedWhenTheGameEnds()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42089, 42090, RequestHold("1"));
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.SetPlayerAttributes, RequestAttribute(gameId, "1"));
            await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.LeaveGameByGroup,
                new TdfWriter().Int("GID", gameId).Int("PID", Guest.PersonaId).Int("REAS", PlayerRemovedReason.GroupLeft).ToArray());
            await Task.Delay(TimeSpan.FromSeconds(1.2));
            Assert.Null(registry.GameIdFor(Host.Id));
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerAttribChange));
        }
    }

    [Fact]
    public void TheRequestHoldDefaultsToOneSecond()
    {
        Assert.Equal(1, FeatureSwitches.FromEnvironment(_ => null).RequestHoldSeconds);
        Assert.Equal(0, RequestHold("0").RequestHoldSeconds);
        Assert.Equal(0.8, RequestHold("0.8").RequestHoldSeconds);
        Assert.Equal(3, RequestHold("9").RequestHoldSeconds);
        Assert.Equal(0, RequestHold("-1").RequestHoldSeconds);
        Assert.Equal(1, RequestHold("soon").RequestHoldSeconds);
    }

    /// <summary>In the announce mode, a player whose Blaze connection closes is announced (REAS 3 BLAZESERVER_CONN_LOST).</summary>
    [Fact]
    public async Task ALostConnectionTellsTheOtherPlayer()
    {
        var (runtime, players, registry, host, guest, _) = await PairedClientsAsync(42051, 42052, pregameMode: PregameDeparture.Announce);
        using (runtime)
        using (players)
        await using (guest)
        {
            await host.DisposeAsync();
            var removed = (await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved)).Payload;
            Assert.Equal(Host.PersonaId, TdfReader.FindInteger(removed, "PID"));
            Assert.Equal(PlayerRemovedReason.BlazeServerConnectionLost, TdfReader.FindInteger(removed, "REAS"));
            await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved);
            Assert.Null(registry.GameIdFor(Guest.Id));
        }
    }

    /// <summary>A quit report and the leave request FIFA sends right after it are one departure: the survivor hears it once.</summary>
    [Fact]
    public async Task AQuitIsAnnouncedOnceEvenWithItsLeaveRequest()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42061, 42062);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport,
                new TdfWriter().Int("GRID", 97).Int("FNSH", 0).ToArray());
            await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.LeaveGameByGroup,
                new TdfWriter().Int("GID", gameId).Int("PID", Host.PersonaId).Int("REAS", PlayerRemovedReason.GroupLeft).ToArray());
            Assert.Equal(1, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            // The game stays for the remaining player, so it is not removed from that client.
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.GameRemoved));
            Assert.Equal(gameId, registry.GameIdFor(Guest.Id));
        }
    }

    private static async Task<IReadOnlyList<long>> WaitForAsync(Func<IReadOnlyList<long>> read)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var value = read();
            if (value.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return value;
            }
            await Task.Delay(50);
        }
    }

    /// <summary>Two logged-in clients with a paired game, sharing the production switches; the caller owns every disposable.</summary>
    private static async Task<(TestRuntime Runtime, PlayerRegistry Players, MatchmakingRegistry Registry, Client Host, Client Guest, long GameId)> PairedClientsAsync(
        int hostPort, int guestPort, FeatureSwitches? switches = null, string? pregameMode = null)
    {
        var runtime = TestRuntime.Create();
        var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(new NativeAuthSettings(900, 7200)),
        };
        var capture = new PacketCapture(runtime.Game, enabled: false);
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        players.MatchRoster = new MatchmakingRoster(registry);
        var handler = new BlazeConnectionHandler(players, new RivalsMatchmaking(registry,
            new MatchmakingPayloads(BlazeSettings.Load(), () => runtime.Game.NowSeconds, pregameDeparture: new PregameDeparture(pregameMode)), null, switches ?? FeatureSwitches.FromEnvironment(_ => null), capture,
            NullLogger.Instance), capture);
        var host = new Client(handler, hostPort);
        var guest = new Client(handler, guestPort);
        foreach (var (client, identity) in new[] { (host, Host), (guest, Guest) })
        {
            var code = players.NativeSessions!.IssueCode(identity);
            await client.RequestAsync(BlazeComponent.Authentication, AuthenticationCommand.Login, new TdfWriter().Str("AUTH", code).ToArray());
            await client.NotificationAsync(BlazeComponent.UserSessions, UserSessionsNotification.UserAdded);
            await client.NotificationAsync(BlazeComponent.UserSessions, UserSessionsNotification.UserAuthenticated);
        }
        var search = new TdfWriter().Str("SCNM", "FUT_Rivals").ToArray();
        await host.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.StartMatchmakingScenario, search);
        await guest.RequestAsync(BlazeComponent.GameManager, GameManagerCommand.StartMatchmakingScenario, search);
        var gameId = registry.GameIdFor(Host.Id)!.Value;
        foreach (var client in new[] { host, guest })
        {
            await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.GameSetup);
            await client.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.JoiningPlayerInitiateConnections);
        }
        return (runtime, players, registry, host, guest, gameId);
    }

    [Fact]
    public async Task AFirstDnfReportTellsTheSurvivorTheQuitterLeft()
    {
        // Game 272737575 (2026-10-03): the quitter's FNSH=0 report left the survivor in loading for 37 s, until his own
        // mesh-loss timeout, because nothing named the leaver for him.
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42011, 42012);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport,
                new TdfWriter().Int("GRID", 91).Int("FNSH", 0).ToArray());
            var removed = await guest.NotificationAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved);
            Assert.Equal(0x28, removed.Command);
            Assert.Equal(gameId, TdfReader.FindInteger(removed.Payload, "GID"));
            Assert.Equal(Host.PersonaId, TdfReader.FindInteger(removed.Payload, "PID"));
            Assert.Equal(7, TdfReader.FindInteger(removed.Payload, "REAS")); // PLAYER_LEFT
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            // The game survives the leaver's report: only the second one may end it (the survivor still needs his transport).
            Assert.Equal(gameId, registry.GameIdFor(Guest.Id));
            await guest.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport,
                new TdfWriter().Int("GRID", 92).Int("FNSH", 0).ToArray());
            await guest.NotificationAsync(BlazeComponent.GameReporting, GameReportingCommand.ResultNotification);
            Assert.Null(registry.GameIdFor(Host.Id));
            Assert.Null(registry.GameIdFor(Guest.Id));
        }
    }

    [Fact]
    public async Task AFirstFinishedReportRemovesNobody()
    {
        // A full-time report (FNSH=2) is a result, not a departure: the peer is still in his own end-of-match flow.
        var (runtime, players, registry, host, guest, _) = await PairedClientsAsync(42021, 42022);
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport,
                new TdfWriter().Int("GRID", 93).Int("FNSH", 2).ToArray());
            await host.NotificationAsync(BlazeComponent.GameReporting, GameReportingCommand.ResultNotification);
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            Assert.Equal(0, await host.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            await guest.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport,
                new TdfWriter().Int("GRID", 94).Int("FNSH", 2).ToArray());
            await guest.NotificationAsync(BlazeComponent.GameReporting, GameReportingCommand.ResultNotification);
            Assert.Null(registry.GameIdFor(Guest.Id));
        }
    }

    [Fact]
    public async Task ThePlayerRemovedNotificationIsBehindItsSwitch()
    {
        var (runtime, players, registry, host, guest, gameId) = await PairedClientsAsync(42031, 42032,
            new FeatureSwitches { PlayerRemovedNotification = false });
        using (runtime)
        using (players)
        await using (host)
        await using (guest)
        {
            await host.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport,
                new TdfWriter().Int("GRID", 95).Int("FNSH", 0).ToArray());
            await host.NotificationAsync(BlazeComponent.GameReporting, GameReportingCommand.ResultNotification);
            Assert.Equal(0, await guest.DrainAsync(BlazeComponent.GameManager, GameManagerNotification.PlayerRemoved));
            Assert.Equal(gameId, registry.GameIdFor(Guest.Id));
            await guest.RequestAsync(BlazeComponent.GameReporting, GameReportingCommand.SubmitGameReport,
                new TdfWriter().Int("GRID", 96).Int("FNSH", 0).ToArray());
            await guest.NotificationAsync(BlazeComponent.GameReporting, GameReportingCommand.ResultNotification);
            Assert.Null(registry.GameIdFor(Guest.Id));
        }
    }

    [Fact]
    public void AMessageForNoGameOrWithoutTargetsIsNeverRelayed()
    {
        var limits = BlazeSettings.Load().Online.Messaging;
        Assert.Null(Server.Blaze.Matchmaking.ClientMessage.Parse(ClientMessage(5, 2))!.Problem(limits));
        Assert.True(Server.Blaze.Matchmaking.ClientMessage.Parse(ClientMessage(5, 2))!.Echo);
        Assert.Equal("target-type-not-gamemanager", Server.Blaze.Matchmaking.ClientMessage.Parse(ClientMessage(5, 2, BlazeComponent.UserSessions))!.Problem(limits));
        Assert.Equal("no-target-type", Server.Blaze.Matchmaking.ClientMessage.Parse(new TdfWriter().Int("FLAG", 2).ToArray())!.Problem(limits));
        var noTargets = ClientMessage(5, 2);
        var withoutTids = new TdfWriter().Int("FLAG", 2).ToArray().Concat(noTargets.AsSpan(noTargets.AsSpan().IndexOf(TdfTag.Encode("TTYP", TdfType.ObjectType))).ToArray()).ToArray();
        Assert.Equal("no-target-ids", Server.Blaze.Matchmaking.ClientMessage.Parse(withoutTids)!.Problem(limits));
        Assert.Equal("gctl", Server.Blaze.Matchmaking.ClientMessage.FourCc(GameControl));
    }

    [Fact]
    public void TheSwitchesDefaultToProductionsSettings()
    {
        var defaults = FeatureSwitches.FromEnvironment(_ => null);
        Assert.False(defaults.JoinerCommand22);
        Assert.True(defaults.InitiateConnectionsAfterSetup);
        Assert.True(defaults.PlatformHostNotification);
        var off = FeatureSwitches.FromEnvironment(name => name is FeatureSwitches.InitiateConnectionsAfterSetupVariable or FeatureSwitches.PlatformHostNotificationVariable ? "0" : null);
        Assert.False(off.InitiateConnectionsAfterSetup);
        Assert.False(off.PlatformHostNotification);
    }

    [Fact]
    public void TheClientConfigTurnsOffTheKickoffSquadChecks()
    {
        using var runtime = TestRuntime.Create();
        var reply = new BlazeResponses(runtime.Fut).Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.Util, UtilCommand.FetchClientConfig, 1,
            Fire2MessageType.Message, new TdfWriter().Str("CFID", "OSDK_CORE").ToArray())));
        var config = TdfReader.FindStringMap(reply.Payload, "CONF");
        // The kickoff lineup check otherwise ends every Rivals match, and the online-squads CRC compares two local installs.
        Assert.Equal("1", config["DISABLE_ONLINE_LINEUP_CHECK"]);
        Assert.Equal("1", config["DISABLE_ONLINE_SQUADS_CRC_CHECK"]);
    }

    [Fact]
    public void TheClientConfigKeepsAltTabDisconnects()
    {
        using var runtime = TestRuntime.Create();
        var reply = new BlazeResponses(runtime.Fut).Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.Util, UtilCommand.FetchClientConfig, 1,
            Fire2MessageType.Message, new TdfWriter().Str("CFID", "OSDK_CORE").ToArray())));
        var config = TdfReader.FindStringMap(reply.Payload, "CONF");
        // FIFA reads the key with default 1 when its window loses focus in an online game, and at 1 ends that game
        // (EndOnlineGame 11, GameEndedReason 12 ALTTAB). Any other value keeps the player in, but a paused FIFA then
        // freezes its lockstep opponent, so the alt-tabber is disconnected instead.
        Assert.Equal("1", config["FIFA_SETTING_ENABLE_ALT_TAB_DISCONNECTS"]);
    }
}
