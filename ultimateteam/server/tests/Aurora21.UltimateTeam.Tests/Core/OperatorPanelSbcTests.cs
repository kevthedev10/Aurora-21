using System.Text.Json.Nodes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>The panel's SBC page reply: the catalogue, the live list, and the SBCs → Settings values in the panel's camelCase.</summary>
public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task TheSbcReplyCarriesTheLiveListAndTheSettings()
    {
        using var setup = await CreateAsync();
        var reply = (await Send(setup.Panel, "GET", "/panel/api/sbc")).Json!.AsObject();
        Assert.Equal(57, reply["sets"]!.AsArray().Count);
        Assert.Equal(11, reply["live"]!.AsArray().Count);
        Assert.False(reply["live"]!.AsArray().Single(entry => entry!["setKey"]!.GetValue<string>() == "POTM_1")!["enabled"]!.GetValue<bool>());
        Assert.Equal("sbc", reply["settingsFile"]!.GetValue<string>());
        var settings = reply["settings"]!.AsObject();
        Assert.Equal("f442", settings["formation"]!.GetValue<string>());
        Assert.Equal("  |  ", settings["text"]!["requirementSeparator"]!.GetValue<string>());
        Assert.Equal(8, settings["images"]!["fallbackTabId"]!.GetValue<long>());
        var rare = reply["requirementRules"]!.AsArray().Single(rule => rule!["type"]!.GetValue<string>() == "RARE")!;
        Assert.Equal(("Rarity", 1L, "AtLeast"), (rare["eligibilityKey"]!.GetValue<string>(), rare["eligibilityValue"]!.GetValue<long>(), rare["scope"]!.GetValue<string>()));
        Assert.Null(reply["requirementRules"]!.AsArray().Single(rule => rule!["type"]!.GetValue<string>() == "AGE")!["eligibilityKey"]);
        var f442 = reply["formations"]!.AsArray().Single(formation => formation!["id"]!.GetValue<string>() == "f442")!;
        Assert.Equal(11, f442["slots"]!.AsArray().Count);
        Assert.Equal("GK", reply["positions"]![f442["slots"]![0]!.GetValue<int>()]!.GetValue<string>());
        Assert.Contains(reply["managerLeagues"]!.AsArray(), league => league!["league"]!.GetValue<long>() == 13);
        Assert.Null(reply["rewardPackKeys"]);
    }

    [Fact]
    public async Task TheSbcPreviewShowsTheGameTextAndWhyStagingWouldBeRefused()
    {
        using var setup = await CreateAsync();
        var definition = (JsonObject)setup.Runtime.Game.Sbc.BundledSet("FND_LETS_GET_STARTED")!.DeepClone();
        var ok = (await Send(setup.Panel, "POST", "/panel/api/sbc/preview",
            new JsonObject { ["setKey"] = "FND_LETS_GET_STARTED", ["definition"] = definition.DeepClone() }.ToJsonString())).Json!;
        Assert.Null(ok["problem"]);
        var challenge = setup.Runtime.Game.Sbc.FindSet("FND_LETS_GET_STARTED")!.Challenges[0];
        Assert.Equal(setup.Runtime.Fut.Sbc.Describe(challenge), ok["challenges"]![0]!["description"]!.GetValue<string>());
        Assert.Equal(1, ok["challenges"]![0]!["players"]!.GetValue<long>());

        // One more open slot than the PLAYERS rule allows: the preview says what staging would refuse.
        definition["challenges"]![0]!["bricks"]!["freeIndices"] = new JsonArray(9, 10);
        var bad = (await Send(setup.Panel, "POST", "/panel/api/sbc/preview",
            new JsonObject { ["setKey"] = "FND_LETS_GET_STARTED", ["definition"] = definition }.ToJsonString())).Json!;
        Assert.Contains("Players in squad requirement is 2", bad["problem"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
