using System.Text.Json.Nodes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task RuleBrowserHidesDedicatedAndUnusedControlsBeforeSearchAndPaging()
    {
        using var setup = await CreateAsync();
        var files = (await Send(setup.Panel, "GET", "/panel/api/content/rules?view=rules&comments=0")).Json!["files"]!.AsArray();
        Assert.DoesNotContain(files, entry => entry!["file"]!.GetValue<string>() is "sbc" or "objectives" or "champions_rewards" or "draft");
        foreach (var file in files)
        {
            var name = file!["file"]!.GetValue<string>();
            var page = (await Send(setup.Panel, "GET", $"/panel/api/content/rules/{name}?view=rules&comments=0&limit=500")).Json!;
            Assert.Equal(file["rules"]!.GetValue<int>(), page["total"]!.GetValue<int>());
            Assert.DoesNotContain(page["rules"]!.AsArray(), row => row!["path"]![0]!.GetValue<string>() is "version" || row["path"]![0]!.GetValue<string>().StartsWith('_'));
        }

        // The mode editor still reads and edits the real values, including objects, through exact paths.
        foreach (var (file, path) in new (string, JsonArray)[]
        {
            ("tradeability", new("sources", "store")),
            ("draft", new("entry", "coins")), ("draft", new("picks", "exponent")),
            ("pack_odds", new("goldRareBands")), ("cards", new("legacyPositionModifierIds")),
            ("squad_battles", new("eventEndsInDays")), ("seasons", new("matchCompletionRewards", "rivals")),
            ("rivals_rewards", new("rankShares")), ("sqbt_rewards", new("rankedTiers")),
        })
        {
            var exact = await Send(setup.Panel, "GET", $"/panel/api/content/rules/{file}?view=rules&path={Uri.EscapeDataString(path.ToJsonString())}");
            Assert.Equal(200, exact.Status);
            Assert.NotNull(exact.Json!["definition"]);
            var first = path[0]!.GetValue<string>();
            var search = (await Send(setup.Panel, "GET", $"/panel/api/content/rules/{file}?view=rules&comments=0&q={first}&limit=500")).Json!;
            Assert.DoesNotContain(search["rules"]!.AsArray(), row => row!["path"]!.AsArray().Count >= path.Count
                && path.Select((part, index) => JsonNode.DeepEquals(part, row["path"]![index])).All(equal => equal));
        }

        // Rivals' only remaining tunable is the settlement delay. Filtering occurs before offset/limit.
        var firstPage = (await Send(setup.Panel, "GET", "/panel/api/content/rules/rivals_rewards?view=rules&comments=0&limit=1")).Json!;
        Assert.Equal(1, firstPage["total"]!.GetValue<int>());
        Assert.Equal("settleDelaySeconds", firstPage["rules"]![0]!["path"]![0]!.GetValue<string>());
        var nextPage = (await Send(setup.Panel, "GET", "/panel/api/content/rules/rivals_rewards?view=rules&comments=0&limit=1&offset=1")).Json!;
        Assert.Equal(1, nextPage["total"]!.GetValue<int>());
        Assert.Empty(nextPage["rules"]!.AsArray());
        var hiddenSearch = (await Send(setup.Panel, "GET", "/panel/api/content/rules/rivals_rewards?view=rules&comments=0&q=weeklyScore")).Json!;
        Assert.Equal(0, hiddenSearch["total"]!.GetValue<int>());

        // Required legacy values and source provenance remain stored; documentation cannot restore dead controls.
        var documented = (await Send(setup.Panel, "GET", "/panel/api/content/rules/pack_odds?view=rules&comments=1&limit=500")).Json!["rules"]!.AsArray();
        Assert.DoesNotContain(documented, row => row!["path"]![0]!.GetValue<string>() is "goldRareBands" or "_bandsComment");
        Assert.Contains(documented, row => row!["path"]![0]!.GetValue<string>() == "iconChance");
        var club = (await Send(setup.Panel, "GET", "/panel/api/content/rules/club?view=rules&comments=0&q=clubNames")).Json!;
        Assert.True(club["total"]!.GetValue<int>() > 0); // Used to name hosted players' clubs.
    }
}
