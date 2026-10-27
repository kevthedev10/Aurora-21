using System.Text.Json.Nodes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task CompletionRewardSettingsPublishLiveAndRejectMissingPacks()
    {
        using var setup = await CreateAsync();
        var key = new JsonObject { ["file"] = "seasons", ["path"] = new JsonArray("matchCompletionRewards", "rivals") };
        async Task<int> Stage(long coins, long? pack, bool untradeable) => (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes",
            new JsonObject { ["area"] = "rules", ["key"] = key.DeepClone(), ["operation"] = "update", ["after"] = new JsonObject {
                ["value"] = new JsonObject { ["coins"] = coins, ["packId"] = pack, ["untradeable"] = untradeable } } }.ToJsonString())).Status;
        var catalogue = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        Assert.DoesNotContain(catalogue["packs"]!.AsArray(), pack => pack!["id"]!.GetValue<long>() == 544);
        Assert.Equal(200, await Stage(777, 74, false));
        Assert.Equal(2500, setup.Runtime.Game.Rules.Seasons.MatchCompletionRewards["rivals"].Coins);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var reward = setup.Runtime.Game.Rules.Seasons.MatchCompletionRewards["rivals"];
        Assert.Equal((777L, (long?)74, false), (reward.Coins, reward.PackId, reward.Untradeable));
        Assert.Equal(400, await Stage(-1, 74, false));
        Assert.Equal(200, await Stage(888, 987654321, true));
        Assert.NotEqual(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(777, setup.Runtime.Game.Rules.Seasons.MatchCompletionRewards["rivals"].Coins);
    }
}
