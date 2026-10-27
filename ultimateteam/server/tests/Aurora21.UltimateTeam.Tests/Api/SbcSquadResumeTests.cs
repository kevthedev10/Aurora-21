using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Club;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// FIFA saves the SBC squad when the player backs out of a challenge; in retail the players are still in their slots when
/// the challenge is opened again. Entering a challenge (POST /sbs/challenge/{id}, GET .../squad) used to answer with a blank
/// squad every time, so the saved squad was never shown.
/// </summary>
public sealed class SbcSquadResumeTests
{
    private const string G = "/ut/game/fifa21";

    private static SbcChallenge Open(TestRuntime rt) =>
        rt.Game.Sbc.Challenges.First(challenge => SbcFixtures.FreeSlots(rt, challenge).Length == rt.Game.Rules.Sbc.SquadSize);

    private static long[] OwnedPlayers(TestRuntime rt, int count)
    {
        var store = rt.Game.Store();
        var seen = new HashSet<long>();
        return store.ListItems("club").Concat(store.ListItems("squad"))
            .Where(item => ClubInventoryService.IsPlayer(item) && !item.Bool("dream") && item.Int("loans") == 0
                && rt.Game.Players.Find(item.Int("resourceId")) is { } meta && seen.Add(meta.Int("assetId")))
            .Take(count).Select(item => item.Int("id")).ToArray();
    }

    private static JsonObject SaveBody(string formation, params (int Index, long Id)[] slots) => new()
    {
        ["formation"] = formation,
        ["players"] = new JsonArray(slots.Select(slot => (JsonNode)new JsonObject { ["index"] = slot.Index, ["itemData"] = new JsonObject { ["id"] = slot.Id } }).ToArray()),
    };

    private static Dictionary<int, long> Placed(JsonObject squad) =>
        squad["players"]!.AsArray().OfType<JsonObject>()
            .Where(entry => JsonValues.Int(entry["itemData"]!["id"]) > 0)
            .ToDictionary(entry => (int)entry.Int("index"), entry => JsonValues.Int(entry["itemData"]!["id"]));

    [Fact]
    public void ASavedSquadComesBackWhenTheChallengeIsOpenedAgain()
    {
        using var rt = TestRuntime.Create();
        var challenge = Open(rt);
        var players = OwnedPlayers(rt, 2);
        var formation = rt.Fut.Sbc.Formation(challenge);
        rt.Call("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody(formation, (3, players[0]), (9, players[1])));

        foreach (var squad in new[]
        {
            rt.CallObject("POST", $"{G}/sbs/challenge/{challenge.Id}")["squad"]!.AsObject(),
            rt.CallObject("GET", $"{G}/sbs/challenge/{challenge.Id}/squad")["squad"]!.AsObject(),
        })
        {
            Assert.Equal(new Dictionary<int, long> { [3] = players[0], [9] = players[1] }, Placed(squad));
            Assert.Equal(23, squad["players"]!.AsArray().Count);
            var card = squad["players"]!.AsArray().OfType<JsonObject>().Single(entry => entry.Int("index") == 3)["itemData"]!.AsObject();
            Assert.Equal("player", card.Text("itemType"));
            Assert.True(card.Int("resourceId") != 0);
            Assert.Equal(formation, squad.Text("formation"));
        }
    }

    [Fact]
    public void APlayerQuickSoldSinceTheSaveIsLeftOut()
    {
        using var rt = TestRuntime.Create();
        var challenge = Open(rt);
        var players = OwnedPlayers(rt, 2);
        rt.Call("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody(rt.Fut.Sbc.Formation(challenge), (0, players[0]), (1, players[1])));
        rt.CallObject("DELETE", $"{G}/item/{players[1]}");

        var squad = rt.CallObject("POST", $"{G}/sbs/challenge/{challenge.Id}")["squad"]!.AsObject();
        Assert.Equal(new Dictionary<int, long> { [0] = players[0] }, Placed(squad));
    }

    [Fact]
    public void ABrickWallKeepsItsLockedSlotsAroundTheSavedPlayer()
    {
        using var rt = TestRuntime.Create();
        var challenge = rt.Game.Sbc.Challenges.Single(c => c.Key == "CH_1");
        var bronze = SbcFixtures.OwnedPlayerItemId(rt, "bronze");
        rt.Call("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody("442", (10, bronze)));

        var squad = rt.CallObject("POST", $"{G}/sbs/challenge/{challenge.Id}")["squad"]!.AsObject();
        Assert.Equal(new Dictionary<int, long> { [10] = bronze }, Placed(squad));
        Assert.Equal(rt.Fut.Sbc.Formation(challenge), squad.Text("formation"));
    }

    [Fact]
    public void ARefusedSaveOpensBlank()
    {
        using var rt = TestRuntime.Create();
        var challenge = rt.Game.Sbc.Challenges.Single(c => c.Key == "CH_1");
        rt.Call("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody("442", (10, SbcFixtures.OwnedPlayerItemId(rt, "bronze"))));
        // A player in a locked slot is refused, and the refusal forgets the squad.
        rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody("442", (0, SbcFixtures.AnyOwnedPlayerItemId(rt))));

        Assert.Empty(Placed(rt.CallObject("POST", $"{G}/sbs/challenge/{challenge.Id}")["squad"]!.AsObject()));
    }
}
