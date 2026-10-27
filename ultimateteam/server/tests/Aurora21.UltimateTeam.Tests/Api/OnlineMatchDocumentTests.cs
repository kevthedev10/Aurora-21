using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The squad documents around an online match, as production serves them (the Python server's RIVALS_OWN_SQUAD,
/// SQUAD_ACTIVE_BY_PERSONA, RIVALS_OPPONENT_HANDOFF ACTIVE_SWAP, PARITY_PEER_DOC and SQUAD_IDENTITY_STABLE_CLUB): the
/// CreateMatch handoff, the pre-match fetch by persona and the pre-match GET squad/active.
/// </summary>
public sealed class OnlineMatchDocumentTests
{
    private const string G = "/ut/game/fifa21";
    private const string OpponentId = "222222";
    private const long GameId = 77;

    /// <summary>A matchmaking roster the test drives: who is paired with whom, and who is still searching.</summary>
    private sealed class TestRoster : IMatchRoster
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, OnlineOpponent> _pairs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _searching = new(StringComparer.Ordinal);

        public void Pair(string home, long homePersona, string away, long awayPersona)
        {
            lock (_gate)
            {
                _pairs[home] = new OnlineOpponent(true, away, awayPersona, GameId);
                _pairs[away] = new OnlineOpponent(false, home, homePersona, GameId);
                _searching.Remove(home);
                _searching.Remove(away);
            }
        }

        public void Search(string player)
        {
            lock (_gate)
            {
                _searching.Add(player);
            }
        }

        public OnlineOpponent? OpponentOf(string playerId)
        {
            lock (_gate)
            {
                return _pairs.GetValueOrDefault(playerId);
            }
        }

        public bool IsSearching(string playerId)
        {
            lock (_gate)
            {
                return _searching.Contains(playerId);
            }
        }
    }

    private static FutResponse Send(FutRouter router, string method, string path, JsonNode? body = null, params (string Name, string Value)[] headers) =>
        router.Handle(new FutRequest(method, path, headers.Select(header => KeyValuePair.Create(header.Name, header.Value)),
            body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString())));

    private static JsonObject Call(FutRouter router, string method, string path, JsonNode? body = null, params (string Name, string Value)[] headers)
    {
        var response = Send(router, method, path, body, headers);
        Assert.True(response.Status == 200, $"{method} {path} -> {response.Status}: {Encoding.UTF8.GetString(response.Body)}");
        return FutJson.ParseObject(Encoding.UTF8.GetString(response.Body))!;
    }

    private static bool NoStore(FutResponse response) =>
        response.Headers.Any(header => header.Key == "Cache-Control" && header.Value == "no-store");

    /// <summary>The club's active items exactly as /userMassInfo serves them.</summary>
    private static JsonArray MassInfoActives(FutRouter router) => Call(router, "GET", $"{G}/usermassinfo")["userInfo"]!["actives"]!.AsArray();

    private static JsonObject Stadium(JsonNode? actives) =>
        actives!.AsArray().OfType<JsonObject>().Single(item => item.Text("itemState") == "activeStadium");

    [Fact]
    public void TheRivalsHandoffPlaysBothClubsAsTheyAreAndNamesTheSidesByCaller()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var opponentLease = players.Acquire(PlayerIdentity.Of(OpponentId));
        var roster = new TestRoster();
        players.MatchRoster = roster;
        var own = rt.Game.Config.PersonaId;
        var peer = opponentLease.Session.Game.Config.PersonaId;
        var venue = rt.Game.Rules.Club.Venues.DefaultVenueId;
        var matchVenue = rt.Game.Rules.SquadBattles.Venue.StadiumId;
        var rivals = new JsonObject { ["squadId"] = 1, ["matchSubType"] = "RIVALS_MATCH" };

        // Before the pairing a Rivals create already plays the whole club: the five active items /userMassInfo served, the
        // real stadium item unchanged, and the club's own venue; no opponent keys.
        var unpaired = Call(rt.Router, "POST", $"{G}/match", rivals);
        var squad = unpaired["squad"]!.AsObject();
        var ownActives = MassInfoActives(rt.Router);
        Assert.Equal(5, ownActives.Count);
        Assert.True(squad["actives"].JsonEquals(ownActives) && squad["club"].JsonEquals(ownActives));
        Assert.Equal((venue, venue), (squad.Int("stadiumAssetId"), squad.Int("stadiumId")));
        Assert.Equal(venue, Stadium(squad["actives"]).Int("assetId"));
        Assert.False(unpaired.ContainsKey("opponentSquad"));
        // The response's own venue fields are unchanged.
        Assert.Equal(matchVenue, unpaired.Int("stadiumAssetId"));

        // The opponent hosts: the caller is away.
        roster.Pair(OpponentId, peer, PlayerIdentity.SinglePlayerId, own);
        var away = Call(rt.Router, "POST", $"{G}/match", rivals);
        Assert.Equal(("RIVALS_MATCH", false, peer), (away.Text("matchSubType"), away.Bool("isHomeTeam"), away.Int("opponentPersonaId")));
        Assert.Equal(own, away["squad"]!.AsObject().Int("personaId"));
        Assert.Equal(peer, away["opponentSquad"]!.AsObject().Int("personaId"));
        Assert.True(away["opponentSquad"].JsonEquals(away["opponent"]));
        // homeSquad and awaySquad name the sides: the away caller's awaySquad is its own squad.
        Assert.Equal(peer, away["homeSquad"]!.AsObject().Int("personaId"));
        Assert.Equal(own, away["awaySquad"]!.AsObject().Int("personaId"));
        Assert.True(away["awaySquad"].JsonEquals(away["squad"]));
        // The opponent's document carries its own club, exactly as its /userMassInfo serves it, and its own venue.
        var opponent = away["opponentSquad"]!.AsObject();
        var peerActives = MassInfoActives(opponentLease.Session.Router);
        Assert.True(opponent["actives"].JsonEquals(peerActives) && opponent["club"].JsonEquals(peerActives));
        Assert.NotEqual(Stadium(ownActives).Int("id"), Stadium(peerActives).Int("id"));
        Assert.Equal(venue, opponent.Int("stadiumAssetId"));
        Assert.True(opponent.Bool("isHomeTeam"));

        // The host's awaySquad is its opponent.
        var home = Call(opponentLease.Session.Router, "POST", $"{G}/match", rivals);
        Assert.True(home.Bool("isHomeTeam"));
        Assert.Equal((peer, peer, own), (home["squad"]!.AsObject().Int("personaId"), home["homeSquad"]!.AsObject().Int("personaId"),
            home["awaySquad"]!.AsObject().Int("personaId")));
    }

    [Fact]
    public void AWeekendLeagueCreatePlaysTheClubAndAHomeSquadBattleKeepsTheClubStadium()
    {
        using var rt = TestRuntime.Create();
        var venue = rt.Game.Rules.SquadBattles.Venue;
        var league = Call(rt.Router, "POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 })["squad"]!.AsObject();
        Assert.True(league["actives"].JsonEquals(MassInfoActives(rt.Router)));

        // A Squad Battles fixture: the club hosts in its own stadium; a stand-in CPU (no real clubs here) brings the
        // Bernabeu for the fixtures it hosts.
        var created = Call(rt.Router, "POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtOpponentSquadId"] = 0 });
        var battle = created["squad"]!.AsObject();
        var home = rt.Game.Rules.Club.Venues.DefaultVenueId;
        Assert.Equal(3, battle["actives"]!.AsArray().Count);
        Assert.Equal(home, Stadium(battle["actives"]).Int("assetId"));
        Assert.Equal(home, battle.Int("stadiumAssetId"));
        var opponent = created["opponent"]!.AsObject();
        Assert.Equal((venue.ResourceId, venue.StadiumId), (Stadium(opponent["actives"]).Int("resourceId"), Stadium(opponent["actives"]).Int("assetId")));
    }

    [Fact]
    public void AWeekendLeagueCreateNamesTheFutChampionsStadiumPairedOrNot()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var opponentLease = players.Acquire(PlayerIdentity.Of(OpponentId));
        var roster = new TestRoster();
        players.MatchRoster = roster;
        var champions = rt.Game.Rules.Seasons.WeekendLeague.VenueId;
        Assert.Equal(445, champions);
        long Venue(FutRouter router, JsonObject body) => Call(router, "POST", $"{G}/match", body).Int("championsWeekendLeagueStadiumId");

        // FIFA plays a Weekend League match at championsWeekendLeagueStadiumId, read from the create before the pairing
        // (live 10-04: the POST came a second before Blaze paired, and its 0 put the match in Emirates as *StadiumName_0).
        // A Weekend League create (it names championId) names the FUT Champions Stadium, paired or not.
        var league = new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 };
        Assert.Equal(champions, Venue(rt.Router, league));
        roster.Pair(OpponentId, opponentLease.Session.Game.Config.PersonaId, PlayerIdentity.SinglePlayerId, rt.Game.Config.PersonaId);
        Assert.Equal(champions, Venue(rt.Router, league));
        Assert.Equal(champions, Venue(opponentLease.Session.Router, league));
        // Its kickoff PUT, which names no championId, keeps it.
        Assert.Equal(champions, Call(rt.Router, "PUT", $"{G}/match", new JsonObject { ["squadId"] = 1 }).Int("championsWeekendLeagueStadiumId"));

        // A Rivals match is at the home club's venue for both sides (the away caller reads it from the opponent's squad).
        var rivals = new JsonObject { ["squadId"] = 1, ["matchSubType"] = "RIVALS_MATCH" };
        var away = Call(rt.Router, "POST", $"{G}/match", rivals);
        var hostVenue = away["homeSquad"]!.AsObject().Int("stadiumAssetId");
        Assert.Equal(rt.Game.Rules.Club.Venues.DefaultVenueId, hostVenue);
        Assert.Equal(hostVenue, away.Int("championsWeekendLeagueStadiumId"));
        Assert.Equal(hostVenue, Venue(opponentLease.Session.Router, rivals));
    }

    [Fact]
    public void ThePrematchFetchByPersonaServesTheOpponentTheCallerAndNobodyElse()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var opponentLease = players.Acquire(PlayerIdentity.Of(OpponentId));
        var roster = new TestRoster();
        players.MatchRoster = roster;
        var own = rt.Game.Config.PersonaId;
        var peer = opponentLease.Session.Game.Config.PersonaId;

        // Unpaired, the opponent's persona is unknown: the unknown answer, as before.
        var unknown = Send(rt.Router, "GET", $"{G}/squad/active/user/{peer}?includeMyStadium=1");
        Assert.Equal(200, unknown.Status);
        Assert.Contains(unknown.Headers, header => header.Key == "X-UltimateTeam-Unknown");
        Assert.Equal("{}", Encoding.UTF8.GetString(unknown.Body));

        roster.Pair(PlayerIdentity.SinglePlayerId, own, OpponentId, peer);
        var handoff = Call(rt.Router, "POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["matchSubType"] = "RIVALS_MATCH" });

        // The opponent's persona gets the document the handoff carries, plus its My Stadium built from the same items.
        var response = Send(rt.Router, "GET", $"{G}/squad/active/user/{peer}?includeMyStadium=1");
        Assert.True(response.Status == 200 && NoStore(response));
        var opponent = FutJson.ParseObject(Encoding.UTF8.GetString(response.Body))!;
        var stadium = opponent.Pop("myStadiumInfo")!.AsObject();
        Assert.True(opponent.JsonEquals(handoff["opponentSquad"]));
        Assert.Equal(opponent.Int("stadiumAssetId"), stadium.Int("stadiumId"));
        Assert.Equal(Stadium(opponent["actives"]).Int("id"), stadium["tiers"]![0]!["slots"]!.AsArray().OfType<JsonObject>()
            .Single(slot => slot.Int("slotId") == 5)["itemData"]!.AsObject().Int("id"));
        // Without includeMyStadium the opponent still carries its My Stadium: a squad of the opponent's persona without one
        // clears the stadium FIFA holds for it (CardsDLL 0x3263ff), and with it the opponent's goal song and tifos.
        Assert.True(Call(rt.Router, "GET", $"{G}/squad/active/user/{peer}").ContainsKey("myStadiumInfo"));

        // The caller's own persona gets its own active squad; with includeMyStadium, its venue as well.
        var self = Call(rt.Router, "GET", $"{G}/squad/active/user/{own}");
        Assert.True(self.JsonEquals(Call(rt.Router, "GET", $"{G}/squad/active")));
        var selfWithStadium = Call(rt.Router, "GET", $"{G}/squad/active/user/{own}?includeMyStadium=1");
        Assert.Equal(own, selfWithStadium.Int("personaId"));
        Assert.Equal(selfWithStadium.Int("stadiumAssetId"), selfWithStadium["myStadiumInfo"]!.AsObject().Int("stadiumId"));

        // Any other persona keeps the unknown answer.
        Assert.Contains(Send(rt.Router, "GET", $"{G}/squad/active/user/42").Headers, header => header.Key == "X-UltimateTeam-Unknown");

        // The Weekend League form carries the opponent's My Stadium, asked for or not.
        Assert.True(Call(rt.Router, "GET", $"{G}/squad/1/user/{peer}?includeMyStadium=1").ContainsKey("myStadiumInfo"));
        Assert.True(Call(rt.Router, "GET", $"{G}/squad/1/user/{peer}").ContainsKey("myStadiumInfo"));
    }

    [Fact]
    public void ThePrematchSquadActiveServesTheOpponentAndWaitsOnlyForAQueuedCaller()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var opponentLease = players.Acquire(PlayerIdentity.Of(OpponentId));
        var roster = new TestRoster();
        players.MatchRoster = roster;
        var own = rt.Game.Config.PersonaId;
        var peer = opponentLease.Session.Game.Config.PersonaId;
        var prematch = ("X-PERFORMANCE-GROUP", "FFA21PCC_FUT_ONLINE_COOPPREMATCH");
        long Persona(params (string, string)[] headers) => Call(rt.Router, "GET", $"{G}/squad/active", null, headers).Int("personaId");

        // Neither queued nor paired: the caller's own squad at once.
        var watch = Stopwatch.StartNew();
        Assert.Equal(own, Persona(prematch));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"answered after {watch.Elapsed}");

        // Queued, and paired while the fetch waits: the opponent, as soon as the pairing appears.
        roster.Search(PlayerIdentity.SinglePlayerId);
        watch.Restart();
        using (var pairing = new Timer(_ => roster.Pair(PlayerIdentity.SinglePlayerId, own, OpponentId, peer), null, 400, Timeout.Infinite))
        {
            var response = Send(rt.Router, "GET", $"{G}/squad/active", null, prematch);
            Assert.True(NoStore(response));
            var opponent = FutJson.ParseObject(Encoding.UTF8.GetString(response.Body))!;
            Assert.Equal(peer, opponent.Int("personaId"));
            // The zero-persona fetch never asks for includeMyStadium; the opponent's My Stadium comes anyway.
            Assert.NotNull(opponent["myStadiumInfo"]);
        }
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(350), $"answered after {watch.Elapsed}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(rt.Game.Rules.Seasons.OnlineMatch.OpponentWaitSeconds), $"answered after {watch.Elapsed}");

        // Paired: only a request stamped by the pre-match screen is swapped (the hub's own fetch is FUT_MODEENTRY).
        Assert.Equal(peer, Persona(("x-performance-group", "FFA21PCC_FUT_ONLINE_PREMATCH")));
        Assert.Equal(own, Persona());
        Assert.Equal(own, Persona(("X-PERFORMANCE-GROUP", "FFA21PCC_FUT_MODEENTRY")));
        Assert.Equal(own, Persona(("X-PERFORMANCE-GROUP", "FFA21PCC_FUT_SQUADS")));
        Assert.Equal(own, Call(rt.Router, "PUT", $"{G}/squad/active", new JsonObject(), prematch).Int("personaId"));
    }

    [Fact]
    public void AQueuedCallerThatNeverPairsGetsItsOwnSquadAfterTheBudget()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        var roster = new TestRoster();
        players.MatchRoster = roster;
        var rules = rt.Game.Rules.Seasons.OnlineMatch;
        Assert.Equal(6.0, rules.OpponentWaitSeconds);
        // This runtime's own rules: a short budget keeps the test quick.
        rules.OpponentWaitSeconds = 0.5;
        roster.Search(PlayerIdentity.SinglePlayerId);
        var watch = Stopwatch.StartNew();
        var squad = Call(rt.Router, "GET", $"{G}/squad/active?active=true", null, ("X-PERFORMANCE-GROUP", "FFA21PCC_FUT_ONLINE_COOPPREMATCH"));
        Assert.Equal(rt.Game.Config.PersonaId, squad.Int("personaId"));
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(450), TimeSpan.FromSeconds(5));
    }
}
