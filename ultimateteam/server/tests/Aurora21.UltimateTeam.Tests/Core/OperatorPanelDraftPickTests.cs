using System.Text.Json.Nodes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task DraftPickSettingsAreReadStagedPublishedAndRemovedFromTheRuleBrowser()
    {
        using var setup = await CreateAsync();
        var initial = (await Send(setup.Panel, "GET", "/panel/api/content/modes/draft")).Json!;
        var picks = initial["pickSettings"]!.AsObject();
        Assert.Equal(setup.Runtime.Game.Rules.Draft.Picks.MinRating, picks["minRating"]!.GetValue<long>());
        Assert.Equal(setup.Runtime.Game.Rules.Draft.Picks.CategoryWeights["icon"], picks["categoryWeights"]!["icon"]!.GetValue<double>());
        Assert.DoesNotContain(picks, pair => pair.Key is "ratingBiasBase" or "exponent" or "captainExponent" or "weakRoundExponent" or "scheduledPool");
        Assert.Contains(initial["pickOptions"]!["categories"]!.AsArray(), value => value!.GetValue<string>() == "icon");
        Assert.Contains(initial["pickOptions"]!["goldRareGroups"]!.AsArray(), value => value!.GetValue<string>() == "rating_75_79");

        var browser = (await Send(setup.Panel, "GET", "/panel/api/content/rules?view=rules&comments=1")).Json!;
        Assert.DoesNotContain(browser["files"]!.AsArray(), file => file!["file"]!.GetValue<string>() == "draft");
        var rows = (await Send(setup.Panel, "GET", "/panel/api/content/rules/draft?view=rules&comments=1")).Json!;
        Assert.Equal(0, rows["total"]!.GetValue<int>());

        var next = setup.Runtime.Game.Rules.Draft.Picks.MinRating + 1;
        var change = new JsonObject
        {
            ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "draft", ["path"] = new JsonArray("picks", "minRating") },
            ["operation"] = "update", ["after"] = new JsonObject { ["value"] = next },
        };
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", change.ToJsonString())).Status);
        Assert.Equal(next - 1, setup.Runtime.Game.Rules.Draft.Picks.MinRating);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var published = (await Send(setup.Panel, "GET", "/panel/api/content/modes/draft")).Json!;
        Assert.Equal(next, published["pickSettings"]!["minRating"]!.GetValue<long>());
        Assert.Equal(next, setup.Runtime.Game.Rules.Draft.Picks.MinRating);
        var exact = (await Send(setup.Panel, "GET", "/panel/api/content/rules/draft?path=" + Uri.EscapeDataString("[\"picks\",\"minRating\"]"))).Json!;
        Assert.Equal(next, exact["definition"]!["value"]!.GetValue<long>());
    }
}
