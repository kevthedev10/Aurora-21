using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Server.Http;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class ChampionsUiEvidenceTests
{
    [Fact]
    public void CapturesBothLeagueListsAndHomeRangesWithoutPlayerDataOrChangingResponse()
    {
        var league = JsonNode.Parse("""
            {"id":42,"prizeTiers":[
              {"tierType":"LB_RANKING_TYPE","tierLevel":2,"tierStart":2,"tierEnd":5,"awards":{"secret":"synthetic-secret"}},
              {"tierType":"NUMBER_OF_WINS_TYPE","tierLevel":1,"tierStart":27,"tierEnd":30},
              {"tierType":"synthetic-secret","tierLevel":"synthetic-secret","tierStart":null,"tierEnd":25}],
             "personaId":123456,"name":"synthetic-player"}
            """)!;
        var response = FutResponse.Json(new JsonObject
        {
            ["activeChampionLeagues"] = new JsonArray(league),
            ["previousChampionEvents"] = new JsonArray(new JsonObject { ["id"] = 41, ["prizeTiers"] = new JsonArray() }),
            ["token"] = "synthetic-secret",
        });
        var before = Encoding.UTF8.GetString(response.Body);
        var evidence = ChampionsUiEvidence.Summarize("//ut/game/fifa21/champion/user/hub?token=synthetic-secret", response)!;
        Assert.Contains("\"tierStart\":2,\"tierEnd\":5", evidence);
        Assert.Contains("NUMBER_OF_WINS_TYPE", evidence);
        Assert.Contains("\"eventId\":41", evidence);
        Assert.Contains("UNKNOWN", evidence);
        Assert.DoesNotContain("synthetic", evidence);
        Assert.DoesNotContain("123456", evidence);
        Assert.Equal(before, Encoding.UTF8.GetString(response.Body));

        var current = league.DeepClone().AsObject();
        current["championEventId"] = 42;
        var home = FutResponse.Json(new JsonObject { ["championEvent"] = new JsonObject { ["currentChampionEvent"] = current } });
        var homeEvidence = ChampionsUiEvidence.Summarize("/ut/game/fifa21/hub", home)!;
        Assert.Contains("\"eventId\":42", homeEvidence);
        Assert.Contains("\"tierStart\":2,\"tierEnd\":5", homeEvidence);
        Assert.DoesNotContain("synthetic", homeEvidence);
    }

    [Fact]
    public void LeaderboardOnlyRecordsCountAndIgnoresUnrelatedEndpoints()
    {
        var response = FutResponse.Json(new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject { ["personaId"] = 123456, ["name"] = "synthetic-player" }),
        });
        var evidence = ChampionsUiEvidence.Summarize("/ut/game/fifa21/champion/leaderboard?period=curr&token=synthetic-secret", response)!;
        Assert.Contains("\"entryCount\":1", evidence);
        Assert.DoesNotContain("synthetic", evidence);
        Assert.DoesNotContain("123456", evidence);
        Assert.Null(ChampionsUiEvidence.Summarize("/ut/auth", response));
        Assert.Null(ChampionsUiEvidence.Summarize("/ut/game/fifa21/usermassinfo", response));
        Assert.Null(ChampionsUiEvidence.Summarize("/ut/game/fifa21/champion/user/prize", response));
    }

    [Fact]
    public void MalformedResponseDoesNotThrowOrDumpBody()
    {
        var response = new FutResponse(500, Encoding.UTF8.GetBytes("synthetic-secret"));
        var evidence = ChampionsUiEvidence.Summarize("/ut/game/fifa21/champion/user/hub", response)!;
        Assert.Contains("\"parseError\":true", evidence);
        Assert.DoesNotContain("synthetic-secret", evidence);
    }
}
