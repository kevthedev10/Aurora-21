using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Auth;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Server.Lsx;
using Aurora21.UltimateTeam.Server.Relay;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// Rivals matchmaking: notification payloads compared byte for byte with the Python server's
/// builders, the registry's lifecycle gates, the gameplay relay and per-player clubs.
/// </summary>
public sealed class MatchmakingTests
{
    private const long NowSeconds = 1_700_000_000;
    private const string HostId = "111111";
    private const string GuestId = "222222";

    private static byte[] Hex(JsonNode vectors, string key) => Convert.FromHexString(vectors[key]!.GetValue<string>());

    private static byte[] StartPayload(params (long Port, string? Version)[] entries)
    {
        var writer = new TdfWriter();
        foreach (var (port, version) in entries)
        {
            writer.Int("PORT", port);
            if (version is not null)
            {
                writer.Str("GVER", version);
            }
        }
        return writer.ToArray();
    }

    /// <summary>
    /// The payloads this change did not touch still match the Python vectors byte for byte. GameSetup has the structure
    /// of the frames the production Python server sent (fixtures fingerprinted from its logs of Rivals game 237452738),
    /// and the other changed notifications carry production's members in production's order.
    /// </summary>
    [Fact]
    public void NotificationPayloadsMatchProduction()
    {
        // The Python vectors were captured when every player was named LocalPlayer; record that name for both players
        // (shared-server players otherwise take their own recorded name, PlayerNamesTests).
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(NowSeconds), prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "player-names.json"), $$"""{ "{{HostId}}": "LocalPlayer", "{{GuestId}}": "LocalPlayer" }""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.203.21.5", "10.203.21.1") { MachineId = 424242 };
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.203.21.6", "10.203.21.1");
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", AdvertisedAddress = "10.203.21.1", PortStart = 47100, PortEnd = 47109 }, NullLogger.Instance);
        relay.Start("127.0.0.1");
        var registry = new MatchmakingRegistry(relay, NullLogger.Instance, new TestRuntime.FixedTime(NowSeconds));
        registry.Start(HostId, "FUT_Rivals", StartPayload((3659, "fifa21-test")), host);
        registry.Start(GuestId, "FUT_Rivals", StartPayload((0, null), (3660, null)), guest);
        Assert.Null(registry.TakeReady(HostId, host));
        var match = registry.TakeReady(GuestId, guest)!;
        Assert.Equal(new[] { HostId, GuestId }, match.Players.Select(p => p.PlayerId));
        Assert.Equal(47100, match.Relay!.Ports[HostId]);

        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "matchmaking-python-vectors.json")))!;
        // These captured production frames predate the server-set game-speed attribute. Reproduce
        // that captured configuration; GameSpeedIsTheServersAndTheSameForBothPlayers tests today's default.
        var payloads = new MatchmakingPayloads(BlazeSettings.Load(), () => NowSeconds, gameSpeed: new OnlineGameSpeed());
        var (hostEntry, guestEntry) = (match.Players[0], match.Players[1]);

        // Unchanged payloads: the Python vectors.
        Assert.Equal(Hex(vectors, "userUpdatedGuest"), MatchmakingPayloads.PeerUserUpdated(guest));
        Assert.Equal(Hex(vectors, "extendedDataUpdateHostSelf"), payloads.PeerExtendedDataUpdate(host, MatchmakingPayloads.ProjectedEndpoint(match, hostEntry, hostEntry)));
        Assert.Equal(Hex(vectors, "matchmakingCancelled"), MatchmakingPayloads.MatchmakingCancelledNotification(9, rt.Game.Config.PersonaId));
        Assert.Equal(Hex(vectors, "matchmakingFinishedHost"), MatchmakingPayloads.MatchmakingFinishedNotification(host, 0));
        Assert.Equal(Hex(vectors, "playerStateChange"), MatchmakingPayloads.GamePlayerStateChange(match.GameId, host.PersonaId));
        Assert.Equal(Hex(vectors, "stateChange"), MatchmakingPayloads.GameStateChange(match.GameId, GameState.PreGame));
        var sessionRequest = new TdfWriter().Blob("NPSI", "x"u8).Blob("XNNC", new byte[] { 1, 2 }).Blob("XSES", new byte[] { 3 }).ToArray();
        Assert.Equal(Hex(vectors, "sessionUpdated"), MatchmakingPayloads.GameSessionUpdated(match.GameId, sessionRequest));

        // GameSetup: exactly the structure and protocol values production sent the host and the joiner (relayed or direct).
        Assert.Equal(TdfShape.ReadFixture("production-rivals-gamesetup-host.shape"), TdfShape.Of(payloads.GameSetup(match, 0)));
        Assert.Equal(TdfShape.ReadFixture("production-rivals-gamesetup-joiner.shape"), TdfShape.Of(payloads.GameSetup(match, 1)));
        var directRegistry = new MatchmakingRegistry(null, NullLogger.Instance, new TestRuntime.FixedTime(NowSeconds));
        directRegistry.Start(HostId, "FUT_Rivals", StartPayload((3659, "fifa21-test")), host);
        directRegistry.Start(GuestId, "FUT_Rivals", StartPayload((0, null), (3660, null)), guest);
        Assert.Equal(TdfShape.ReadFixture("production-rivals-gamesetup-joiner.shape"), TdfShape.Of(payloads.GameSetup(directRegistry.TakeReady(GuestId, guest)!, 1)));

        // Every identity carries AIDS {PLAT 4}, right after AID.
        var userAdded = TdfShape.Of(payloads.PeerUserAdded(guest, MatchmakingPayloads.ProjectedEndpoint(match, hostEntry, guestEntry)));
        Assert.Equal(new[] { "/USER:3/AID:0", "/USER:3/AIDS:3", "/USER:3/AIDS:3/PLAT:0 = 4", "/USER:3/ALOC:0" }, userAdded.SkipWhile(line => line != "/USER:3/AID:0").Take(4));
        // LookupUsersByPersonaNames answers only the users that exist, each under its own identity: FIFA's EA friends are
        // nobody here, and the opponent's own name finds exactly the opponent.
        Assert.Equal(new[] { "/ULST:4", "/ULST:4 list<3> count=0" },
            TdfShape.Of(payloads.LookupUsers(match, guest, new[] { "EAName1", "EAName2", "EAName3" })));
        // Both players here carry the vectors' name (LocalPlayer), so it finds both, the opponent first, case-insensitively.
        var byName = payloads.LookupUsers(match, guest, new[] { "EAName1", host.DisplayName.ToUpperInvariant() });
        Assert.Equal(2, TdfShape.Of(byName).Count(line => line.EndsWith("/AIDS:3/PLAT:0 = 4", StringComparison.Ordinal)));
        Assert.Equal(host.PersonaId, TdfReader.FindInteger(byName, "ID"));
        Assert.Equal(host.DisplayName, TdfReader.FindString(byName, "NAME"));
        var everyone = TdfShape.Of(payloads.LookupUsers(match, guest, Array.Empty<string>()));
        Assert.Equal(2, everyone.Count(line => line.EndsWith("/AIDS:3/PLAT:0 = 4", StringComparison.Ordinal)));

        // ConnectionValidated in tag order with the scenario id and NTOP 130; PlayerJoinCompleted with its microsecond
        // timestamp; PlatformHostInitialized naming the host (PHID).
        Assert.Equal(new[]
        {
            "/CONV:3", "/CONV:3/ALST:4", "/CONV:3/ALST:4 list<0> count=0", "/CONV:3/FCNT:0", "/CONV:3/GIDL:4", "/CONV:3/GIDL:4 list<0> count=0",
            "/CONV:3/NTOP:0 = 130", "/CONV:3/TIER:0", "/DISP:0", "/GID:0", "/GRID:9", "/MSCD:0 = 0", "/MSID:0", "/QSVR:0", "/USID:0",
        }, TdfShape.Of(payloads.ConnectionValidated(match, guestEntry)));
        Assert.Equal(guestEntry.SessionId, TdfReader.FindInteger(payloads.ConnectionValidated(match, guestEntry), "MSID"));
        var joined = payloads.PlayerJoinCompleted(match.GameId, guest.PersonaId);
        Assert.Equal(new[] { "/GID:0", "/PID:0", "/TIME:0" }, TdfShape.Of(joined));
        Assert.Equal(NowSeconds * 1_000_000, TdfReader.FindInteger(joined, "TIME"));
        var platformHost = MatchmakingPayloads.PlatformHostInitialized(match.GameId, host.PersonaId, 0);
        Assert.Equal(new[] { "/GID:0", "/PHID:0", "/PHST:0" }, TdfShape.Of(platformHost));
        Assert.Equal(host.PersonaId, TdfReader.FindInteger(platformHost, "PHID"));
    }

    /// <summary>
    /// The online game speed (GAME.ATTR fifaGameSpeed) is the server's alone: Fast by default (blaze.json online.gameSpeed);
    /// both players' GameSetup carry the same value, fixed for the game by its first GameSetup
    /// so a later change reaches only the next game; and a client's own fifaGameSpeed is neither stored nor relayed.
    /// </summary>
    [Fact]
    public void GameSpeedIsTheServersAndTheSameForBothPlayers()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        BlazeClient Connection(PlayerSession session, string address) =>
            new(session, new BlazeResponses(session.Fut), new Pipe().Writer, address, "10.0.0.1");
        var host = Connection(hostLease.Session, "10.0.0.5");
        var guest = Connection(guestLease.Session, "10.0.0.6");
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        RivalsMatch Pair()
        {
            registry.Start(HostId, "FUT_Rivals", StartPayload((3659, "fifa21-test")), host);
            registry.Start(GuestId, "FUT_Rivals", StartPayload((3660, null)), guest);
            return registry.TakeReady(GuestId, guest)!;
        }
        static string? Speed(byte[] gameSetup) => TdfReader.FindStringMap(gameSetup, "ATTR").GetValueOrDefault("fifaGameSpeed");

        var unset = Pair();
        var defaults = new MatchmakingPayloads(BlazeSettings.Load(), () => NowSeconds);
        Assert.Equal(OnlineGameSpeed.Fast, defaults.GameSpeed.Value);
        Assert.Equal("2", Speed(defaults.GameSetup(unset, 0)));
        Assert.Equal("2", Speed(defaults.GameSetup(unset, 1)));
        Assert.True(registry.EndGame(HostId, unset.GameId));

        var speed = new OnlineGameSpeed(OnlineGameSpeed.Fast);
        var payloads = new MatchmakingPayloads(BlazeSettings.Load(), () => NowSeconds, gameSpeed: speed);
        Assert.Same(speed, payloads.GameSpeed);
        var fast = Pair();
        Assert.Equal("2", Speed(payloads.GameSetup(fast, 0)));
        speed.Value = OnlineGameSpeed.Slow;
        Assert.Equal("2", Speed(payloads.GameSetup(fast, 1)));
        Assert.True(registry.EndGame(HostId, fast.GameId));

        var slow = Pair();
        var stored = registry.SetGameAttributes(HostId, slow.GameId, new Dictionary<string, string> { ["FIFAGAMESPEED"] = "2", ["ARB_3"] = "1" }, 128);
        Assert.Equal("ok", stored.Status);
        Assert.Equal(new[] { "ARB_3" }, stored.Changed.Keys);
        var (settings, gameAttributes, playerAttributes) = registry.OsdkState(slow.GameId);
        foreach (var recipient in new[] { 0, 1 })
        {
            var attributes = TdfReader.FindStringMap(payloads.GameSetup(slow, recipient, settings, gameAttributes, playerAttributes), "ATTR");
            Assert.Equal("0", attributes["fifaGameSpeed"]);
            Assert.Equal("1", attributes["ARB_3"]);
            Assert.Single(attributes.Keys, key => key.Equals("fifaGameSpeed", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => speed.Value = 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => speed.Value = -1);
        speed.Value = null;
        Assert.Null(speed.Value);
    }

    /// <summary>
    /// A resumed Blaze connection takes over the old one's search and game, so the old connection's close (its idle timeout,
    /// or the resume aborting it) no longer ends the game; the new connection's close still does.
    /// </summary>
    [Fact]
    public void ARebindMovesTheSearchAndGameToTheResumedConnection()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        BlazeClient Connection(PlayerSession session, string address) =>
            new(session, new BlazeResponses(session.Fut), new Pipe().Writer, address, "10.0.0.1");
        var host = Connection(hostLease.Session, "10.0.0.5");
        var resumedHost = Connection(hostLease.Session, "10.0.0.5");
        var guest = Connection(guestLease.Session, "10.0.0.6");
        var resumedGuest = Connection(guestLease.Session, "10.0.0.6");
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);

        Assert.Throws<ArgumentException>(() => registry.Rebind(host, guest));

        // A queued search follows the resume: the old connection closing leaves it, and it pairs with the new connection.
        registry.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), host);
        Assert.Equal((1, 0), registry.Rebind(host, resumedHost));
        registry.Disconnect(host);
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        var gameId = registry.GameIdFor(HostId)!.Value;
        Assert.Same(resumedHost, registry.MatchFor(gameId)!.Entry(HostId)!.Client);

        // The game follows the resume of the player who completed the pairing.
        Assert.Equal((0, 1), registry.Rebind(guest, resumedGuest));
        Assert.Same(resumedGuest, registry.MatchFor(gameId)!.Entry(GuestId)!.Client);
        Assert.Null(registry.TakeReady(GuestId, guest));
        Assert.NotNull(registry.TakeReady(GuestId, resumedGuest));
        registry.Disconnect(guest);
        Assert.Equal(gameId, registry.GameIdFor(GuestId));
        Assert.Equal(gameId, registry.GameIdFor(HostId));

        registry.Disconnect(resumedGuest);
        Assert.Null(registry.MatchFor(gameId));
    }

    /// <summary>
    /// FIFA's match end names its game by the report id (gid), not the Blaze game id the relay keys on. On 2026-10-04 all 21
    /// lost-connection ends looked the relay up by gid and found nothing, so the hard-drop rule never applied.
    /// </summary>
    [Fact]
    public void TheRelayAnswersWhoWentQuietForTheMatchEndsReportId()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        BlazeClient Connection(PlayerSession session) => new(session, new BlazeResponses(session.Fut), new Pipe().Writer, "127.0.0.1", "127.0.0.1");
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = 47340, PortEnd = 47341 }, NullLogger.Instance,
            new GameplayRelayLimits { SweepInterval = TimeSpan.FromHours(1) });
        relay.Start("127.0.0.1");
        var registry = new MatchmakingRegistry(relay, NullLogger.Instance, new TestRuntime.FixedTime(NowSeconds));
        registry.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), Connection(hostLease.Session));
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), Connection(guestLease.Session));
        var match = registry.MatchFor(registry.GameIdFor(GuestId)!.Value)!;
        Assert.Equal(match.GameId, MatchmakingRegistry.GameIdOfReport(match.ReportId));
        var from = new IPEndPoint(IPAddress.Loopback, 3659);
        relay.HandleDatagram(match.Relay!, HostId, new byte[] { 1 }, from, receivedTicks: 1_000);
        relay.HandleDatagram(match.Relay!, GuestId, new byte[] { 1 }, from, receivedTicks: 16_000);

        var roster = new MatchmakingRoster(registry);
        Assert.Equal(TimeSpan.FromSeconds(15), roster.OpponentQuietFirstBy(GuestId, match.ReportId));
        Assert.Equal(TimeSpan.FromSeconds(15), roster.OpponentQuietFirstBy(GuestId, match.GameId));
        Assert.Null(roster.OpponentQuietFirstBy(GuestId, match.ReportId + (1L << 16)));
    }

    /// <summary>A real ConnectionTelemetry report (live 2026-10-04 20:00:14): the game, FIFA's latency and its packet loss.</summary>
    [Fact]
    public void ConnectionTelemetryGivesTheGameLatencyAndLoss()
    {
        var payload = Convert.FromHexString("9e990000aa919c8802b23a6400a682c89709bb4bf0008202cb0d330403018f486400008f48a400008f49270000b21d23003ac2bb260a3e9e359fc2bd2c0001ca3a640080ec81d908ca3da40082f502cb0a2203930cb40a422c637c937d340a3cdb8d14a64c330000b30aec0a00000000be4c330000cb0aec0a0000000000ce5bb4008cf60200");
        var telemetry = RivalsMatchmaking.ReadTelemetry(payload);
        Assert.NotNull(telemetry);
        Assert.Equal(277054570, telemetry!.Value.GameId);
        Assert.Equal(58, telemetry.Value.LatencyMs);
        Assert.Equal(0.309, telemetry.Value.LossPercent!.Value, 3);
        Assert.Null(RivalsMatchmaking.ReadTelemetry(new byte[] { 1, 2, 3 }));
    }

    /// <summary>
    /// A resume from a new address (a router that reconnected mid-match) re-points the relay: the player's gameplay
    /// datagrams are accepted from the verified new address instead of being dropped as foreign.
    /// </summary>
    [Fact]
    public void ARebindFromANewAddressRepointsTheRelay()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        BlazeClient Connection(PlayerSession session, string address) =>
            new(session, new BlazeResponses(session.Fut), new Pipe().Writer, address, "127.0.0.1");
        var host = Connection(hostLease.Session, "127.0.0.1");
        var guest = Connection(guestLease.Session, "127.0.0.1");
        var resumedGuest = Connection(guestLease.Session, "203.0.113.9");
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = 47330, PortEnd = 47331 }, NullLogger.Instance);
        relay.Start("127.0.0.1");
        var registry = new MatchmakingRegistry(relay, NullLogger.Instance, new TestRuntime.FixedTime(NowSeconds));
        registry.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), host);
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        var allocation = registry.MatchFor(registry.GameIdFor(GuestId)!.Value)!.Relay!;
        var moved = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 52000);
        relay.HandleDatagram(allocation, GuestId, new byte[] { 1 }, moved);
        Assert.Equal(1, allocation.DroppedForeignSource);

        Assert.Equal((0, 1), registry.Rebind(guest, resumedGuest));
        relay.HandleDatagram(allocation, GuestId, new byte[] { 1 }, moved);
        Assert.Equal(1, allocation.DroppedForeignSource);
        relay.HandleDatagram(allocation, HostId, new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, 52001));
        Assert.Equal(1, allocation.DroppedForeignSource);
    }

    [Fact]
    public async Task RegistryReleasesPregameOnlyAfterTheFullLifecycle()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.6", "10.0.0.1");
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);

        // Other modes never pair, and a player cannot pair with themselves.
        registry.Start(HostId, "FUT_Champions", Array.Empty<byte>(), host);
        registry.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), host);
        Assert.Null(registry.GameIdFor(HostId));
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        var gameId = registry.GameIdFor(HostId)!.Value;
        Assert.Equal(gameId, registry.GameIdFor(GuestId));
        Assert.Equal(new OnlineOpponent(true, GuestId, guest.PersonaId, gameId), new MatchmakingRoster(registry).OpponentOf(HostId));
        Assert.False(new MatchmakingRoster(registry).OpponentOf(GuestId)!.CallerIsHome);

        // PRE_GAME waits: only the host may ask, and not before the mesh is complete.
        Assert.False(registry.AdvanceGameState(GuestId, gameId, GameState.PreGame).Changed);
        Assert.True(registry.AdvanceGameState(HostId, gameId, GameState.PreGame).Changed);
        Assert.False(registry.ClaimPregameIfReady(gameId).Claimed);

        Assert.True(registry.AcknowledgeEndpointInitialization(HostId, gameId).First);
        Assert.True(registry.AcknowledgeMeshTarget(GuestId, gameId, guest.PersonaId, 0).First);
        Assert.False(registry.AcknowledgeMeshTarget(HostId, gameId, 999, 4).First);
        Assert.True(registry.ClaimConnectionValidation(gameId, HostId).Claimed);
        Assert.False(registry.ClaimConnectionValidation(gameId, HostId).Claimed);
        Assert.True(registry.ClaimConnectionValidation(gameId, GuestId).Claimed);
        Assert.False(registry.ClaimSessionUpdateNotification(gameId).Claimed);
        registry.StoreHostSessionUpdatePayload(GuestId, gameId, new byte[] { 9 });
        registry.StoreHostSessionUpdatePayload(HostId, gameId, new byte[] { 1 });
        registry.StoreHostSessionUpdatePayload(HostId, gameId, new byte[] { 2 });
        Assert.Equal(new byte[] { 1 }, registry.ClaimSessionUpdateNotification(gameId).Payload);
        registry.AcknowledgeGameStep(HostId, gameId, connected: false);

        Assert.False(registry.AcknowledgeMeshTarget(HostId, gameId, guest.PersonaId, 2).CompletedNow);
        Assert.False(await registry.WaitForMeshCompletionAsync(gameId, TimeSpan.FromMilliseconds(30)));
        Assert.False(registry.AcknowledgeMeshTarget(HostId, gameId, guest.PersonaId, 4).CompletedNow);
        Assert.True(registry.AcknowledgeMeshTarget(GuestId, gameId, host.PersonaId, 4).CompletedNow);
        Assert.False(registry.AcknowledgeMeshTarget(GuestId, gameId, host.PersonaId, 4).CompletedNow);
        Assert.True(await registry.WaitForMeshCompletionAsync(gameId, TimeSpan.Zero));

        Assert.False(registry.ClaimPregameIfReady(gameId).Claimed);
        Assert.True(registry.ClaimJoinNotifications(gameId).Claimed);
        Assert.False(registry.ClaimJoinNotifications(gameId).Claimed);
        foreach (var id in new[] { HostId, GuestId })
        {
            registry.MarkPlayerStateNotificationsDelivered(gameId, id);
            registry.MarkJoinNotificationsDelivered(gameId, id);
        }
        Assert.True(registry.ClaimPregameIfReady(gameId).Claimed);
        Assert.False(registry.ClaimPregameIfReady(gameId).Claimed);
        Assert.Equal(GameState.PreGame, registry.MatchFor(gameId)!.State);

        // Invalid transitions are refused; valid ones advance.
        Assert.False(registry.AdvanceGameState(HostId, gameId, GameState.PostGame).Changed);
        Assert.True(registry.AdvanceGameState(HostId, gameId, GameState.InGame).Changed);

        registry.Disconnect(guest);
        Assert.Null(registry.GameIdFor(HostId));
        Assert.Null(new MatchmakingRoster(registry).OpponentOf(HostId));
    }

    /// <summary>
    /// Departures: every way a player leaves a game that the other player is still in is announced once, with FIFA's
    /// PlayerRemovedReason. Before IN_GAME the game ends with the leaver; from IN_GAME on it stays for the remaining player.
    /// </summary>
    [Fact]
    public void EveryDepartureIsAnnouncedOnceWithItsReason()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.6", "10.0.0.1");
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        var departures = new List<GameDeparture>();
        registry.PlayerDeparted = departures.Add;
        long Pair()
        {
            registry.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), host);
            registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
            return registry.GameIdFor(HostId)!.Value;
        }
        void Expect(string leaver, long reason, bool removed)
        {
            var departure = Assert.Single(departures);
            Assert.Equal(leaver, departure.Leaver.PlayerId);
            Assert.Equal(reason, departure.Reason);
            Assert.Equal(removed, departure.GameRemoved);
            Assert.Equal(new[] { leaver == HostId ? GuestId : HostId }, departure.Recipients.Select(player => player.PlayerId));
            departures.Clear();
        }

        // Pregame leave: the game ends.
        var gameId = Pair();
        Assert.Equal("ended", registry.Leave(HostId, gameId, PlayerRemovedReason.GroupLeft));
        Expect(HostId, PlayerRemovedReason.GroupLeft, removed: true);
        Assert.Null(registry.GameIdFor(GuestId));
        Assert.Equal("unknown-game", registry.Leave(GuestId, gameId, PlayerRemovedReason.PlayerLeftSwitchedGameSession));
        Assert.Empty(departures);

        // A new search, a cancelled search and a lost connection each end the game and say why.
        Pair();
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        Expect(GuestId, PlayerRemovedReason.PlayerLeftSwitchedGameSession, removed: true);
        Assert.Null(registry.GameIdFor(HostId));
        Assert.True(registry.Cancel(GuestId, 0));
        Assert.Empty(departures);
        gameId = Pair();
        Assert.True(registry.Cancel(HostId, 0));
        Expect(HostId, PlayerRemovedReason.PlayerLeftCancelledMatchmaking, removed: true);
        Pair();
        registry.Disconnect(guest);
        Expect(GuestId, PlayerRemovedReason.BlazeServerConnectionLost, removed: true);

        // In game: the remaining player keeps the game, and the leaver's next search leaves it alone.
        gameId = Pair();
        var reportId = registry.MatchFor(gameId)!.ReportId;
        registry.MatchFor(gameId)!.State = GameState.InGame;
        Assert.False(registry.OpponentLeftGameFirst(gameId, GuestId));
        Assert.Equal("left", registry.Leave(HostId, gameId, PlayerRemovedReason.GroupLeft));
        Expect(HostId, PlayerRemovedReason.GroupLeft, removed: false);
        Assert.Equal("already-left", registry.Leave(HostId, gameId, PlayerRemovedReason.GroupLeft));
        // The host left the live game first (an alt-tab disconnect): the guest's match end may name it by either id.
        Assert.True(registry.OpponentLeftGameFirst(gameId, GuestId));
        Assert.True(registry.OpponentLeftGameFirst(reportId, GuestId));
        Assert.False(registry.OpponentLeftGameFirst(gameId, HostId));
        Assert.False(registry.OpponentLeftGameFirst(gameId, "someone-else"));
        Assert.Null(registry.GameIdFor(HostId));
        registry.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), host);
        Assert.Equal(gameId, registry.GameIdFor(GuestId));
        Assert.Empty(departures);
        Assert.Equal("all-left", registry.Leave(GuestId, gameId, PlayerRemovedReason.GroupLeft));
        Assert.Empty(departures);
        Assert.Null(registry.GameIdFor(GuestId));
        // Kept after the game is gone, for the match ends that follow; the second to leave never left first.
        Assert.True(registry.OpponentLeftGameFirst(reportId, GuestId));
        Assert.False(registry.OpponentLeftGameFirst(reportId, HostId));
    }

    /// <summary>Only a seat in an IN_GAME game the player has not left counts as a live match (the resume grace's test).</summary>
    [Fact]
    public void OnlyAnInGameSeatCountsAsALiveMatch()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.6", "10.0.0.1");
        var resumed = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.7", "10.0.0.1");
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        registry.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), host);
        Assert.False(registry.InLiveGame(host));
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        var gameId = registry.GameIdFor(HostId)!.Value;
        Assert.False(registry.InLiveGame(guest));

        registry.MatchFor(gameId)!.State = GameState.InGame;
        Assert.True(registry.InLiveGame(host) && registry.InLiveGame(guest));
        // A resumed connection holds the seat from then on; the old one no longer does.
        registry.Rebind(guest, resumed);
        Assert.False(registry.InLiveGame(guest));
        Assert.True(registry.InLiveGame(resumed));
        registry.Disconnect(guest);
        Assert.Equal(gameId, registry.GameIdFor(GuestId));
        // A player who left keeps no live seat.
        Assert.Equal("left", registry.Leave(HostId, gameId, PlayerRemovedReason.GroupLeft));
        Assert.False(registry.InLiveGame(host));
        Assert.True(registry.InLiveGame(resumed));
    }

    [Fact]
    public async Task RelayForwardsBothWaysAndReportsPromotedLinks()
    {
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = 47200, PortEnd = 47209 }, NullLogger.Instance);
        relay.Start("127.0.0.1");
        var promoted = new List<(long, string, string)>();
        relay.PeerLinkPromoted = (game, source, target) => { lock (promoted) { promoted.Add((game, source, target)); } };
        var allocation = relay.Allocate(5, new[] { new RelayPlayer("a", "127.0.0.1", "127.0.0.1"), new RelayPlayer("b", "127.0.0.1", "127.0.0.1") })!;
        Assert.Same(allocation, relay.Allocate(5, new[] { new RelayPlayer("a", "127.0.0.1", "127.0.0.1"), new RelayPlayer("b", "127.0.0.1", "127.0.0.1") }));
        Assert.Equal(("127.0.0.1", 47200), allocation.EndpointFor("a"));

        using var a = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var promotion = new byte[26];
        promotion[2] = 0x80;
        // A speaks first: B's endpoint is unknown, so the datagram waits until B sends.
        await a.SendAsync(promotion, new IPEndPoint(IPAddress.Loopback, allocation.Ports["a"]));
        await Task.Delay(100);
        await b.SendAsync(new byte[] { 7, 7 }, new IPEndPoint(IPAddress.Loopback, allocation.Ports["b"]));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var atB = await b.ReceiveAsync(timeout.Token);
        Assert.Equal(promotion, atB.Buffer);
        Assert.Equal(allocation.Ports["b"], atB.RemoteEndPoint.Port);
        var atA = await a.ReceiveAsync(timeout.Token);
        Assert.Equal(new byte[] { 7, 7 }, atA.Buffer);
        Assert.Equal(allocation.Ports["a"], atA.RemoteEndPoint.Port);
        // The promotion callback runs on the thread pool, off the receiver that forwarded the datagram.
        while (!timeout.IsCancellationRequested && promoted.Count == 0)
        {
            await Task.Delay(10);
        }
        lock (promoted)
        {
            Assert.Equal(new[] { (5L, "a", "b") }, promoted);
        }

        Assert.True(relay.Release(5, "test"));
        Assert.False(relay.Release(5, "test"));
        Assert.NotNull(relay.Allocate(6, new[] { new RelayPlayer("c", "127.0.0.1", "127.0.0.1"), new RelayPlayer("d", "127.0.0.1", "127.0.0.1") }));
    }

    /// <summary>
    /// A released pair rests before another game gets it: the ended game's clients keep sending to their old relay port for a
    /// while (live 2026-10-04, games 272737579 and 272737580 took the pair the second it was released and dropped 144 and 146
    /// of the previous game's datagrams). Only when every free pair is resting is one reused.
    /// </summary>
    [Fact]
    public void RelayRestsAReleasedPairBeforeReusingIt()
    {
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = 47300, PortEnd = 47305 }, NullLogger.Instance);
        relay.Start("127.0.0.1");
        int FirstPort(long game)
        {
            var allocation = relay.Allocate(game, new[] { new RelayPlayer($"a{game}", "127.0.0.1", "127.0.0.1"), new RelayPlayer($"b{game}", "127.0.0.1", "127.0.0.1") })!;
            Assert.True(relay.Release(game, "test"));
            return allocation.Ports[$"a{game}"];
        }
        Assert.Equal(new[] { 47300, 47302, 47304, 47300 }, new[] { FirstPort(1), FirstPort(2), FirstPort(3), FirstPort(4) });
    }

    /// <summary>A datagram from an address other than the port owner's is dropped and counted as foreign.</summary>
    [Fact]
    public void RelayCountsDatagramsFromAnotherAddressAsForeign()
    {
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = 47310, PortEnd = 47311 }, NullLogger.Instance);
        relay.Start("127.0.0.1");
        var allocation = relay.Allocate(9, new[] { new RelayPlayer("a", "127.0.0.1", "127.0.0.1"), new RelayPlayer("b", "127.0.0.1", "127.0.0.1") })!;
        relay.HandleDatagram(allocation, "a", new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 3659));
        Assert.Equal(1, allocation.PacketsReceived);
        Assert.Equal(1, allocation.DroppedPackets);
        Assert.Equal(1, allocation.DroppedForeignSource);
        Assert.Equal(0, allocation.PacketsForwarded);
        Assert.True(relay.Release(9, "test"));
    }

    /// <summary>
    /// The relay remembers each player's last datagram, so the gap says who went quiet first: on 2026-10-04 the player who
    /// stayed kept sending for 15 s after the leaver's last datagram. The answer outlives the game's release, since the
    /// stayer's match end can arrive after it.
    /// </summary>
    [Fact]
    public void RelayTellsWhichPlayerWentQuietFirst()
    {
        using var relay = new GameplayRelay(new GameplayRelayOptions { Enabled = true, BindAddress = "127.0.0.1", PortStart = 47320, PortEnd = 47321 }, NullLogger.Instance);
        relay.Start("127.0.0.1");
        var allocation = relay.Allocate(11, new[] { new RelayPlayer("stayer", "127.0.0.1", "127.0.0.1"), new RelayPlayer("leaver", "127.0.0.1", "127.0.0.1") })!;
        Assert.Null(relay.QuietFirstBy(11, "stayer"));
        var from = new IPEndPoint(IPAddress.Loopback, 3659);
        relay.HandleDatagram(allocation, "leaver", new byte[] { 1 }, from, receivedTicks: 1_000);
        Assert.Null(relay.QuietFirstBy(11, "stayer"));
        relay.HandleDatagram(allocation, "stayer", new byte[] { 1 }, from, receivedTicks: 16_000);
        Assert.Equal(TimeSpan.FromSeconds(15), relay.QuietFirstBy(11, "stayer"));
        Assert.Equal(TimeSpan.FromSeconds(-15), relay.QuietFirstBy(11, "leaver"));
        Assert.Null(relay.QuietFirstBy(11, "someone-else"));
        Assert.True(relay.Release(11, "test"));
        Assert.Equal(TimeSpan.FromSeconds(15), relay.QuietFirstBy(11, "stayer"));
        Assert.Null(relay.QuietFirstBy(12, "stayer"));
    }

    [Fact]
    public void EveryPlayerHasTheirOwnClub()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 3 }, shared: true);
        var one = PlayerIdentity.FromPeerAddress("10.203.21.5");
        Assert.Equal(one, PlayerIdentity.FromPeerAddress("10.203.21.5"));
        Assert.NotEqual(one.PersonaId, PlayerIdentity.FromPeerAddress("10.203.21.6").PersonaId);
        Assert.Throws<ArgumentException>(() => PlayerIdentity.Of("12ab5"));

        using (var first = players.Acquire(one))
        using (var second = players.Acquire(PlayerIdentity.Of(GuestId)))
        {
            Assert.NotEqual(first.Session.Game.Store().ClubId, second.Session.Game.Store().ClubId);
            Assert.Equal(one.PersonaId, first.Session.Game.Config.PersonaId);
            using (var again = players.Acquire(one))
            {
                Assert.Same(first.Session, again.Session);
            }
            first.Session.Game.Store().AddCredits(-1000);
            Assert.NotEqual(first.Session.Game.Store().Credits(), second.Session.Game.Store().Credits());
            Assert.Equal(250, first.Session.Game.InUnit(uow => first.Session.Fut.Club.NativeUserAsync(uow))["clubPlayers"].L());
            // Both clubs are on the server leaderboards.
            Assert.Equal(3, rt.Fut.Leaderboards.Rankings(rt.Game).Count);
        }
        using (var local = players.Acquire(PlayerIdentity.SinglePlayer(rt.Game.Config)))
        {
            Assert.Same(players.Local, local.Session);
        }

        // Full with every club idle: the least recently used one is closed to make room.
        using (players.Acquire(PlayerIdentity.Of("333333")))
        {
            Assert.Equal(3, players.OpenCount);
        }
        Assert.False(players.IsOpen(GuestId));
        Assert.True(players.IsOpen(one.Id));
        Assert.True(players.IsOpen("333333"));

        // At capacity with every non-local club active, a refused open releases its database
        // connection. Once the active lease ends, the same player can be opened normally.
        using var bounded = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 2 }, shared: true);
        var held = bounded.Acquire(PlayerIdentity.Of("444444"));
        Assert.Throws<InvalidOperationException>(() => bounded.Acquire(PlayerIdentity.Of("555555")));
        Assert.Equal(2, bounded.OpenCount);
        held.Dispose();
        using var recovered = bounded.Acquire(PlayerIdentity.Of("555555"));
        Assert.Equal(2, bounded.OpenCount);
    }

    [Fact]
    public void SharedIdentityFlowsThroughFutAuthAndLsxAndRejectsIpv6()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 3 }, shared: true);
        var identity = PlayerIdentity.FromPeerAddress("10.203.21.5");
        using var lease = players.Acquire(identity);

        var tokenInfo = lease.Session.Router.Handle(new FutRequest("GET",
            $"/connect/tokeninfo?access_token={LocalAuthService.AccessToken}&client_id={LocalAuthService.SdkClientId}",
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        var token = FutJson.ParseObject(Encoding.UTF8.GetString(tokenInfo.Body))!;
        Assert.Equal(identity.NucleusId.ToString(System.Globalization.CultureInfo.InvariantCulture), token["user_id"].S());
        Assert.Equal(identity.PersonaId.ToString(System.Globalization.CultureInfo.InvariantCulture), token["persona_id"].S());

        var lsx = new LsxResponder(lease.Session.Game);
        var profile = lsx.Respond("<LSX><Request recipient=\"EbisuSDK\" id=\"7\"><GetProfile /></Request></LSX>");
        Assert.Contains($"UserId=\"{identity.NucleusId}\"", profile, StringComparison.Ordinal);
        Assert.Contains($"PersonaId=\"{identity.PersonaId}\"", profile, StringComparison.Ordinal);

        Assert.Equal(identity, FutEndpoint.PlayerFor(IPAddress.Parse("::ffff:10.203.21.5"), players));
        Assert.Throws<InvalidOperationException>(() => FutEndpoint.PlayerFor(IPAddress.IPv6Loopback, players));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 0 }, shared: true));

        var grant = lease.Session.Router.Handle(new FutRequest("POST", "/ultimateteam/customisation/grant-all",
            Array.Empty<KeyValuePair<string, string>>(), "{}"u8.ToArray()));
        // The daily FIFA Points drop is gone in every mode (PLAN 4.1), operator requests included.
        var unlimited = lease.Session.Router.Handle(new FutRequest("POST", "/ultimateteam/daily-drop?unlimited=1",
            Array.Empty<KeyValuePair<string, string>>(), "{}"u8.ToArray()));
        Assert.Equal(403, grant.Status);
        Assert.Contains(unlimited.Headers, header => header.Key == "X-UltimateTeam-Unknown");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(100, true)]
    [InlineData(1000, true)]
    [InlineData(1500, false)]
    public void RelayDatagramFailuresAreLoggedAtTheFirstAndEachTenfoldRise(long failures, bool logged) =>
        Assert.Equal(logged, Aurora21.UltimateTeam.Server.Relay.GameplayRelay.IsLogStep(failures));
}
