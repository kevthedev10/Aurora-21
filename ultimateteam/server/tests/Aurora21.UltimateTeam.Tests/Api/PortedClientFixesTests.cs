using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Client fixes ported from the Python server (port-python.md, section B).</summary>
public sealed class PortedClientFixesTests
{
    private const string G = "/ut/game/fifa21";

    [Theory]
    [InlineData("en_us")]
    [InlineData("fr_fr")]
    [InlineData("zh_cn")]
    public void StorePackNamesAreServedForEveryClientLocale(string locale)
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var response = rt.Router.Handle(new FutRequest("GET", $"/fut/packs/loc/storepackdescriptions.{locale}.xml",
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        Assert.Equal(200, response.Status);
        Assert.Contains("FUT_STORE_PACK_", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void FavouriteSbcSetsAreStoredAndReported()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var sets = rt.Game.Sbc.Sets.Take(2).Select(set => (long)set.Id).ToArray();

        var tagged = rt.CallObject("PUT", $"{G}/sbs/sets/tag", new JsonObject
        {
            ["sets"] = new JsonArray(
                new JsonObject { ["setId"] = sets[0], ["tagged"] = true },
                new JsonObject { ["setId"] = sets[1], ["tagged"] = true },
                new JsonObject { ["setId"] = 999_999_999, ["tagged"] = true }),
        });
        Assert.Equal(sets.Order(), tagged["sets"]!.AsArray().OfType<JsonObject>().Select(entry => entry.Int("setId")));

        rt.Call("PUT", $"{G}/sbs/sets/tag", new JsonObject { ["sets"] = new JsonArray(new JsonObject { ["setId"] = sets[1], ["tagged"] = false }) });
        var listed = rt.CallObject("GET", $"{G}/sbs/sets")["categories"]!.AsArray().OfType<JsonObject>()
            .SelectMany(category => category["sets"]!.AsArray().OfType<JsonObject>())
            .ToDictionary(set => set.Int("id"));
        Assert.True(listed[sets[0]].Bool("taggedByUser"));
        // An empty rewardPreviewImageId switched FIFA's set tile to the player-pick preview layout with a missing image.
        Assert.All(listed.Values, set => Assert.False(set.ContainsKey("rewardPreviewImageId")));
        Assert.False(listed[sets[1]].Bool("taggedByUser"));
        Assert.Equal(new[] { sets[0] }, rt.CallObject("GET", $"{G}/sbs/sets/tag")["sets"]!.AsArray().OfType<JsonObject>().Select(entry => entry.Int("setId")));
    }

    [Fact]
    public void AQuitEarnsNoCoinRows()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1 });
        var ended = rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "QUIT",
            ["matchId"] = 11,
            ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
        });
        Assert.Empty(ended["matchCoinPartials"]!.AsArray());
        Assert.Equal(ended["participationAward"]!.GetValue<long>(), ended["matchCoins"]!.GetValue<long>());
    }

    [Fact]
    public void TheRivalsHubListsEveryDivision()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var rivals = rt.Game.Rules.Seasons.Rivals;
        var divisions = rt.CallObject("GET", $"{G}/rivals/user/hub")["divisionsList"]!.AsArray().OfType<JsonObject>()
            .Select(entry => entry.Obj("division")!).ToList();

        // Bottom first (Python server 56145ed): FIFA reads division d at divisionsList[count - d].
        Assert.Equal(rivals.DivisionCount, divisions.Count);
        var entry = divisions[0];
        Assert.Equal((10L, "FUT_10"), (entry.Int("divisionId"), entry.Text("localizedName")));
        Assert.Equal((0L, 115L, 0L), (entry.Int("eloThresholdDown"), entry.Int("eloThresholdUp"), entry.Int("relegationBuffer")));
        // Division 5: 1,000 to 1,250, relegated below 900.
        var middle = divisions[5];
        Assert.Equal((5L, 1000L, 1250L, 100L), (middle.Int("divisionId"), middle.Int("eloThresholdDown"), middle.Int("eloThresholdUp"), middle.Int("relegationBuffer")));
        // Division 1 has no upper line: the Skill Rating from which matchmaking treats everyone alike.
        var top = divisions[^1];
        Assert.Equal((1L, "FUT_1", 1900L, rivals.TopSkillThreshold), (top.Int("divisionId"), top.Text("localizedName"), top.Int("eloThresholdDown"), top.Int("eloThresholdUp")));
    }
}
