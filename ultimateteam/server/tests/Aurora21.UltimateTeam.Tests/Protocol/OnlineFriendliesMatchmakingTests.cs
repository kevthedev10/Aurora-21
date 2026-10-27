using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// Online Friendlies (FUT_HouseRules): two searches pair only on a house-rule game type both chose, the game publishes that
/// type as FUTFriendlyType, mutual launcher friends meet first, and the match never settles as Division Rivals.
/// </summary>
public sealed class OnlineFriendliesMatchmakingTests
{
    private const string G = "/ut/game/fifa21";
    private const string HostId = "111111";
    private const string GuestId = "222222";
    private const string ThirdId = "333333";

    /// <summary>A StartMatchmakingScenario payload with an SCNA map&lt;string, generic&gt; of integer and string criteria.</summary>
    private static byte[] ScenarioPayload(params (string Key, object Value)[] criteria)
    {
        var output = new ArrayBufferWriter<byte>();
        void Bytes(params byte[] bytes) => output.Write(bytes);
        void Text(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value + "\0");
            TdfWriter.WriteVarInt(output, encoded.Length);
            output.Write(encoded);
        }
        output.Write(TdfTag.Encode("SCNA", TdfType.Map));
        Bytes((byte)TdfType.String, 0xC);
        TdfWriter.WriteVarInt(output, criteria.Length);
        foreach (var (key, value) in criteria)
        {
            Text(key);
            Bytes(1);
            if (value is long number)
            {
                TdfWriter.WriteVarInt(output, 22);
                TdfWriter.WriteVarInt(output, number);
            }
            else
            {
                TdfWriter.WriteVarInt(output, 6);
                Text((string)value);
            }
            Bytes(0);
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>A Friendlies search for these game types in order; unset preferences stay "abstain" as FIFA leaves them.</summary>
    private static byte[] Friendly(params long[] types)
    {
        var criteria = new List<(string, object)> { ("GAME_MODE", 77L) };
        for (var index = 0; index < 3; index++)
        {
            criteria.Add(($"FUT_HOUSE_RULE_PREF{index + 1}", index < types.Length ? types[index] : "abstain"));
        }
        criteria.Add(("FUT_HOUSE_RULE_SEARCH", 1000L));
        return ScenarioPayload(criteria.ToArray());
    }

    [Fact]
    public void PreferencesReadTheChosenGameTypesAndSkipAbstainedOrInvalidOnes()
    {
        Assert.Equal(new[] { 3, 0 }, HouseRules.Preferences(Friendly(3, 0)));
        Assert.Empty(HouseRules.Preferences(Friendly()));
        Assert.Equal(new[] { 5 }, HouseRules.Preferences(ScenarioPayload(("FUT_HOUSE_RULE_PREF1", 5L), ("FUT_HOUSE_RULE_PREF2", 12L),
            ("FUT_HOUSE_RULE_PREF3", 5L))));
        Assert.Equal(new[] { 4 }, HouseRules.Preferences(ScenarioPayload(("FUT_HOUSE_RULE_PREF1", "4"))));

        // The waiting player's first choice the newcomer shares.
        Assert.Equal((true, (int?)3), HouseRules.Agree(new[] { 3, 1 }, new[] { 1, 3 }));
        Assert.Equal((true, (int?)1), HouseRules.Agree(new[] { 2, 1 }, new[] { 1, 3 }));
        Assert.Equal((false, (int?)null), HouseRules.Agree(new[] { 3 }, new[] { 1 }));
        Assert.Equal((true, (int?)6), HouseRules.Agree(Array.Empty<int>(), new[] { 6 }));
        Assert.Equal((true, (int?)null), HouseRules.Agree(Array.Empty<int>(), Array.Empty<int>()));
        Assert.True(HouseRules.IsHouseRulesMode("FUT_HouseRules") && HouseRules.IsHouseRulesMode("FUT_HouseRules_LTM"));
        Assert.False(HouseRules.IsHouseRulesMode("FUT_Rivals"));
    }

    private sealed class Clients : IDisposable
    {
        private readonly TestRuntime _rt = TestRuntime.Create();
        private readonly PlayerRegistry _players;
        private readonly List<PlayerLease> _leases = new();

        public Clients() => _players = new PlayerRegistry(_rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);

        public BlazeClient Connect(string playerId, string address)
        {
            var lease = _players.Acquire(PlayerIdentity.Of(playerId));
            _leases.Add(lease);
            return new BlazeClient(lease.Session, new BlazeResponses(lease.Session.Fut), new Pipe().Writer, address, "10.0.0.1");
        }

        public void Dispose()
        {
            foreach (var lease in _leases)
            {
                lease.Dispose();
            }
            _players.Dispose();
            _rt.Dispose();
        }
    }

    [Fact]
    public void FriendliesPairOnASharedHouseRuleAndBothPlayersGetIt()
    {
        using var clients = new Clients();
        var host = clients.Connect(HostId, "10.0.0.5");
        var guest = clients.Connect(GuestId, "10.0.0.6");
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);

        // No game type in common: both keep searching. A Rivals search never takes a Friendlies one.
        registry.Start(HostId, "FUT_HouseRules", Friendly(3), host);
        registry.Start(GuestId, "FUT_HouseRules", Friendly(5), guest);
        Assert.Null(registry.GameIdFor(HostId));
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        Assert.Null(registry.GameIdFor(HostId));

        // The waiting host's first choice the guest also made.
        registry.Start(GuestId, "FUT_HouseRules", Friendly(1, 5, 3), guest);
        var match = registry.TakeReady(GuestId, guest)!;
        Assert.Equal(new[] { HostId, GuestId }, match.Players.Select(player => player.PlayerId));
        Assert.Equal(3, match.HouseRuleType);
        var roster = new MatchmakingRoster(registry);
        Assert.Equal(new OnlineOpponent(false, HostId, host.PersonaId, match.GameId, Friendly: true), roster.OpponentOf(GuestId));

        var payloads = new MatchmakingPayloads(BlazeSettings.Load(), () => 1_700_000_000);
        foreach (var recipient in new[] { 0, 1 })
        {
            var attributes = TdfReader.FindStringMap(payloads.GameSetup(match, recipient), "ATTR");
            // FIFA's join reads FUTFriendlyType (0..9, or FailedToJoin); the host's criteria are published, never Rivals' GAME_MODE 76.
            Assert.Equal("3", attributes["FUTFriendlyType"]);
            Assert.Equal("3", attributes["FUT_FRIENDY_HOUSE_RULES_TYPE"]);
            Assert.Equal("77", attributes["GAME_MODE"]);
            Assert.False(attributes.ContainsKey("FUT_RIVALS_SEARCH_VALUE"));
            Assert.Single(attributes.Keys, key => string.Equals(key, "FUTFriendlyType", StringComparison.OrdinalIgnoreCase));
        }
        // A client storing its own type cannot split the two players.
        registry.SetGameAttributes(HostId, match.GameId, new Dictionary<string, string> { ["futfriendlytype"] = "5" }, 16);
        var stored = registry.OsdkState(match.GameId).GameAttributes;
        var afterStore = TdfReader.FindStringMap(payloads.GameSetup(match, 1, gameAttributes: stored), "ATTR");
        Assert.Equal("3", afterStore["FUTFriendlyType"]);
        Assert.Single(afterStore.Keys, key => string.Equals(key, "FUTFriendlyType", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MutualLauncherFriendsMeetFirstAndAreHeldBrieflyForEachOther()
    {
        using var clients = new Clients();
        var host = clients.Connect(HostId, "10.0.0.5");
        var guest = clients.Connect(GuestId, "10.0.0.6");
        var third = clients.Connect(ThirdId, "10.0.0.7");
        var friends = new LauncherFriends();
        friends.Record(GuestId, new[] { ThirdId });
        friends.Record(ThirdId, new[] { GuestId });
        friends.Record(HostId, new[] { GuestId }); // one-sided: not a friend
        var online = new HashSet<string>(StringComparer.Ordinal);
        MatchmakingRegistry Registry(TimeSpan hold) => new(null, NullLogger.Instance, friends: friends.Mutual, friendsOf: friends.Of,
            online: online.Contains, friendHold: hold);

        // The guest is online: its friend's search is held from strangers, so the stranger who waits longest does not take it,
        // and the guest, arriving later, meets its friend.
        online.Add(GuestId);
        var registry = Registry(TimeSpan.FromMinutes(1));
        registry.Start(HostId, "FUT_HouseRules", Friendly(3), host);
        registry.Start(ThirdId, "FUT_HouseRules", Friendly(3), third);
        Assert.Null(registry.GameIdFor(HostId));
        registry.Start(GuestId, "FUT_HouseRules", Friendly(3), guest);
        Assert.Equal(new[] { ThirdId, GuestId }, registry.TakeReady(GuestId, guest)!.Players.Select(player => player.PlayerId));
        Assert.True(registry.IsSearching(HostId));

        // A newcomer whose friend is online is held too, even with a stranger waiting.
        online.Clear();
        online.Add(ThirdId);
        var held = Registry(TimeSpan.FromMinutes(1));
        held.Start(HostId, "FUT_HouseRules", Friendly(3), host);
        held.Start(GuestId, "FUT_HouseRules", Friendly(3), guest);
        Assert.Null(held.GameIdFor(GuestId));
        held.Start(ThirdId, "FUT_HouseRules", Friendly(3), third);
        Assert.Equal(new[] { GuestId, ThirdId }, held.TakeReady(ThirdId, third)!.Players.Select(player => player.PlayerId));
        Assert.True(held.IsSearching(HostId));

        // Past the hold (here none) a stranger pairs as usual; friends never change Rivals pairing.
        var open = Registry(TimeSpan.Zero);
        open.Start(HostId, "FUT_HouseRules", Friendly(3), host);
        open.Start(GuestId, "FUT_HouseRules", Friendly(3), guest);
        Assert.Equal(new[] { HostId, GuestId }, open.TakeReady(GuestId, guest)!.Players.Select(player => player.PlayerId));
        var rivals = Registry(TimeSpan.FromMinutes(1));
        rivals.Start(HostId, "FUT_Rivals", Array.Empty<byte>(), host);
        rivals.Start(ThirdId, "FUT_Rivals", Array.Empty<byte>(), third);
        rivals.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        Assert.Equal(new[] { HostId, ThirdId }, rivals.TakeReady(ThirdId, third)!.Players.Select(player => player.PlayerId));
    }

    /// <summary>A roster that pairs the test's club with one opponent in an online Friendly or a Rivals game.</summary>
    private sealed class FixedRoster(OnlineOpponent opponent) : IMatchRoster
    {
        public OnlineOpponent? OpponentOf(string playerId) => playerId == PlayerIdentity.SinglePlayerId ? opponent : null;
        public bool IsSearching(string playerId) => false;
    }

    private static JsonObject Call(FutRouter router, string method, string path, JsonNode? body = null)
    {
        var response = router.Handle(new FutRequest(method, path, Array.Empty<KeyValuePair<string, string>>(),
            body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString())));
        Assert.True(response.Status == 200, $"{method} {path} -> {response.Status}: {Encoding.UTF8.GetString(response.Body)}");
        return FutJson.ParseObject(Encoding.UTF8.GetString(response.Body))!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOnlineFriendlyHandsOverBothClubsButIsNeverARivalsMatch(bool friendly)
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var opponentLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var peer = opponentLease.Session.Game.Config.PersonaId;
        players.MatchRoster = new FixedRoster(new OnlineOpponent(true, GuestId, peer, 91, friendly));

        var create = Call(rt.Router, "POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["matchSubType"] = "HOUSE_RULE" });
        // Both sides' clubs, as in Rivals; the subtype stays the one FIFA sent.
        Assert.Equal(peer, create["opponentSquad"]!.AsObject().Int("personaId"));
        Assert.True(create.Bool("isHomeTeam"));
        Assert.Equal(friendly ? "HOUSE_RULE" : "RIVALS_MATCH", create.Text("matchSubType"));
        // Only a Rivals game leaves the pairing the match end settles as Division Rivals (skill rating and rewards).
        var pairing = rt.Game.InUnit(uow => rt.Fut.Rivals.TakePairingAsync(uow));
        Assert.Equal(!friendly, pairing is not null);
    }
}
