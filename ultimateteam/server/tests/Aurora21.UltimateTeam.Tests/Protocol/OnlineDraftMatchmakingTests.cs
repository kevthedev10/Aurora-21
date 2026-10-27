using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>Online Draft matchmaking (Python server 51a1670): FUT_Draft searches pair, and their game carries the host's criteria.</summary>
public sealed class OnlineDraftMatchmakingTests
{
    private const string HostId = "111111";
    private const string GuestId = "222222";

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

    [Fact]
    public void TheScenarioCriteriaDecodeAndAnyOtherShapeIsEmpty()
    {
        var payload = ScenarioPayload(("GAME_MODE", 80L), ("FUT_TOURNY_RND_THR", "OSDK_matchAny"), ("FUT_TOURNY_RND", 2L));
        Assert.Equal(new Dictionary<string, string> { ["GAME_MODE"] = "80", ["FUT_TOURNY_RND_THR"] = "OSDK_matchAny", ["FUT_TOURNY_RND"] = "2" },
            TdfReader.FindGenericMap(payload, "SCNA"));
        Assert.Empty(TdfReader.FindGenericMap(payload, "ATTR"));
        var broken = payload.ToArray();
        broken[^1] = 7; // the last value's terminator
        Assert.Empty(TdfReader.FindGenericMap(broken, "SCNA"));
    }

    [Fact]
    public void DraftSearchesPairOnlyWithDraftSearchesAndPublishTheHostsCriteria()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.6", "10.0.0.1");
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        var criteria = ScenarioPayload(("GAME_MODE", 80L), ("FUT_TOURNY_RND", 1L), ("NET_TOP", 9L));

        // A Draft search does not pair with a Rivals one.
        registry.Start(HostId, "FUT_Draft", criteria, host);
        registry.Start(GuestId, "FUT_Rivals", Array.Empty<byte>(), guest);
        Assert.Null(registry.GameIdFor(HostId));

        registry.Start(GuestId, "FUT_Draft", Array.Empty<byte>(), guest);
        var match = registry.TakeReady(GuestId, guest)!;
        Assert.Equal(new[] { HostId, GuestId }, match.Players.Select(player => player.PlayerId));
        Assert.Equal(new OnlineOpponent(false, HostId, host.PersonaId, match.GameId), new MatchmakingRoster(registry).OpponentOf(GuestId));

        var setup = new MatchmakingPayloads(BlazeSettings.Load(), () => 1_700_000_000).GameSetup(match, 1);
        var attributes = TdfReader.FindStringMap(setup, "ATTR");
        // The host's criteria, with this server's NET_TOP, the recipient's team indices and the OSDK roster keys every
        // production GameSetup carries; none of the fixed Rivals list.
        Assert.Equal(new Dictionary<string, string>
        {
            ["FUT_TOURNY_RND"] = "1", ["GAME_MODE"] = "80", ["NET_TOP"] = "1", ["FUT_USER_TEAM_ID"] = "1", ["FUT_OPPONENT_TEAM_ID"] = "0",
            ["OSDK_rosterURL"] = string.Empty, ["OSDK_rosterVersion"] = "1",
            ["fifaGameSpeed"] = "2", // The current server default applies to Draft as well as Rivals.
        }, attributes);
        Assert.Equal($"Aurora21 FUT_Draft {match.GameId}", TdfReader.FindString(setup, "GNAM"));
    }

    private static FutResponse Send(PlayerLease lease, string method, string path, JsonNode? body = null) =>
        lease.Session.Router.Handle(new FutRequest(method, path, Array.Empty<KeyValuePair<string, string>>(),
            body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString())));

    private static JsonObject Call(PlayerLease lease, string method, string path, JsonNode? body = null)
    {
        var response = Send(lease, method, path, body);
        Assert.True(response.Status == 200, $"{method} {path} -> {response.Status}: {Encoding.UTF8.GetString(response.Body)}");
        return FutJson.ParseObject(Encoding.UTF8.GetString(response.Body))!;
    }

    private static IEnumerable<long> PickIds(JsonObject squad) =>
        squad["players"]!.AsArray().OfType<JsonObject>().Select(slot => slot["itemData"]?["id"]).OfType<JsonNode>().Select(id => id.GetValue<long>());

    [Fact]
    public void AnOnlineDraftCreateMatchCarriesBothDraftSquads()
    {
        const string G = "/ut/game/fifa21";
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        foreach (var lease in new[] { hostLease, guestLease })
        {
            lease.Session.Game.Store().AddCredits(1_000_000);
            Call(lease, "POST", $"{G}/purchase/mode/0/draft", new JsonObject { ["currency"] = "COINS" });
            Call(lease, "POST", $"{G}/squad/mode/0/draft/autocomplete");
        }
        var draftSquad = rt.Game.Rules.Draft.SquadId;
        var hostPersona = hostLease.Session.Game.Config.PersonaId;
        var guestPersona = guestLease.Session.Game.Config.PersonaId;

        // Before Blaze pairs the searches, the handoff carries the caller's own Draft XI only.
        var unpaired = Call(hostLease, "POST", $"{G}/match", new JsonObject { ["squadId"] = draftSquad, ["type"] = "ONLINE" });
        Assert.False(unpaired.ContainsKey("opponentSquad"));

        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        players.MatchRoster = new MatchmakingRoster(registry);
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.6", "10.0.0.1");
        registry.Start(HostId, "FUT_Draft", Array.Empty<byte>(), host);
        registry.Start(GuestId, "FUT_Draft", Array.Empty<byte>(), guest);

        var paired = Call(hostLease, "POST", $"{G}/match", new JsonObject { ["squadId"] = draftSquad, ["type"] = "ONLINE" });
        Assert.True(paired.Bool("isHomeTeam"));
        Assert.Equal(guestPersona, paired.Int("opponentPersonaId"));
        Assert.True(paired.Int("matchId") > 0);
        var guestSquad = guestLease.Session.Game.InUnit(uow => guestLease.Session.Fut.Draft.OnlineMatchSquadAsync(uow))!;
        Assert.Equal(PickIds(guestSquad), PickIds(paired["opponentSquad"]!.AsObject()));
        Assert.False(paired["opponentSquad"]!.AsObject().Bool("isHomeTeam"));
        Assert.Equal(PickIds(guestSquad), PickIds(paired["awaySquad"]!.AsObject()));

        // The sides are the same for the guest: its awaySquad is its own Draft XI, its homeSquad the host's.
        var guestCreate = Call(guestLease, "POST", $"{G}/match", new JsonObject { ["squadId"] = draftSquad, ["type"] = "ONLINE" });
        Assert.False(guestCreate.Bool("isHomeTeam"));
        Assert.Equal(PickIds(guestSquad), PickIds(guestCreate["awaySquad"]!.AsObject()));
        Assert.Equal(PickIds(paired["squad"]!.AsObject()), PickIds(guestCreate["homeSquad"]!.AsObject()));

        // The pre-match screen asks for each side's Draft squad by persona.
        var byPersona = Call(hostLease, "GET", $"{G}/squad/{draftSquad}/user/{guestPersona}?includeMyStadium=1");
        Assert.Equal(PickIds(guestSquad), PickIds(byPersona));
        Assert.NotNull(byPersona["myStadiumInfo"]);
        Assert.Equal(hostPersona, Call(hostLease, "GET", $"{G}/squad/{draftSquad}/user/{hostPersona}").Int("personaId"));
        Assert.Equal(404, Send(hostLease, "GET", $"{G}/squad/{draftSquad}/user/42").Status);

        // Each club's picks carry ids from its own band, so the two Draft squads never share one.
        var hostSquad = hostLease.Session.Game.InUnit(uow => hostLease.Session.Fut.Draft.OnlineMatchSquadAsync(uow))!;
        Assert.NotEmpty(PickIds(hostSquad));
        Assert.Empty(PickIds(hostSquad).Intersect(PickIds(guestSquad)));
    }
}
