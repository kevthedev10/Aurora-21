using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// What the SBC editor stages: a challenge's formation, its locked slots (bricks: open slots, locked cards, placeholders)
/// with the squad size they imply, and player and manager league rewards.
/// </summary>
public sealed class SbcEditorTests
{
    private static JsonObject Bronze(TestRuntime rt) => (JsonObject)rt.Game.Sbc.BundledSet("UPG_BRONZE")!.DeepClone();

    private static JsonObject Challenge(JsonObject set) => set["challenges"]![0]!.AsObject();

    private static string? Problem(TestRuntime rt, JsonObject set) => new SbcSetArea(rt.Game).Problem("UPG_BRONZE", set);

    [Fact]
    public async Task MultiChallengeSetsRequireANonemptyValidGroupReward()
    {
        using var rt = TestRuntime.Create();
        var set = Bronze(rt);
        set.Remove("rewards");
        Assert.Null(Problem(rt, set));
        var second = (JsonObject)Challenge(set).DeepClone();
        second["id"] = "EDITOR_GROUP_2";
        second.Remove("number");
        set["challenges"]!.AsArray().Add(second);
        var changes = new ContentChangesets(rt.Game, [new SbcSetArea(rt.Game)]);
        foreach (var reward in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["entries"] = new JsonArray() } })
        {
            set["rewards"] = reward;
            Assert.Contains("A group reward is required", Problem(rt, set), StringComparison.Ordinal);
            var error = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(
                SbcSetArea.AreaName, new JsonObject { ["setKey"] = "UPG_BRONZE" }, ContentOperation.Update,
                new JsonObject { ["definition"] = set.DeepClone() }, null));
            Assert.Contains("A group reward is required", error.Message, StringComparison.Ordinal);
        }
        set["rewards"] = new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["type"] = "COINS", ["value"] = 0 }) };
        Assert.Contains("reward is 1 to", Problem(rt, set), StringComparison.Ordinal);
        set["rewards"]!["entries"]![0]!["value"] = 1000;
        Assert.Null(Problem(rt, set));
        await changes.SetChangeAsync(SbcSetArea.AreaName, new JsonObject { ["setKey"] = "UPG_BRONZE" }, ContentOperation.Update,
            new JsonObject { ["definition"] = set }, null);
    }

    [Fact]
    public void LockedSlotsMustLeaveTheSquadSizeOpenAndNameRealCards()
    {
        using var rt = TestRuntime.Create();
        var set = Bronze(rt);
        var challenge = Challenge(set);
        challenge["formation"] = "f4231";
        challenge["bricks"] = new JsonObject { ["freeIndices"] = new JsonArray(1, 2, 3, 4, 5, 6, 7, 8, 9, 10), ["players"] = new JsonObject { ["0"] = 158023 } };
        Assert.Contains("Players in squad requirement is 10", Problem(rt, set), StringComparison.Ordinal);
        var players = challenge["requirements"]!["rules"]!.AsArray().Single(rule => rule!["type"]!.GetValue<string>() == "PLAYERS")!;
        players["attributes"] = new JsonObject { ["min"] = 10, ["max"] = 10 };
        Assert.Null(Problem(rt, set));

        challenge["formation"] = "f999";
        Assert.Contains("formation", Problem(rt, set), StringComparison.Ordinal);
        challenge["formation"] = "f4231";
        challenge["bricks"]!["players"] = new JsonObject { ["1"] = 158023 };
        Assert.Contains("locked slot", Problem(rt, set), StringComparison.Ordinal);
        challenge["bricks"]!["players"] = new JsonObject { ["0"] = 999999999 };
        Assert.Contains("is not a card", Problem(rt, set), StringComparison.Ordinal);
        challenge["bricks"]!["players"] = null;
        challenge["bricks"]!.AsObject().Remove("players");
        challenge["bricks"]!["dummies"] = new JsonObject { ["0"] = new JsonObject { ["league"] = 13, ["nation"] = 0 } };
        Assert.Contains("league and a nation", Problem(rt, set), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APickedPlayerIsPaidExactlyAndAManagerLeagueNeedsItsCard()
    {
        using var rt = TestRuntime.Create();
        var set = Bronze(rt);
        var entries = Challenge(set)["rewards"]!["entries"]!.AsArray();
        entries.Add(new JsonObject { ["type"] = "MANAGER_LEAGUE", ["attributes"] = new JsonObject { ["league"] = 999 } });
        Assert.Contains("manager league card", Problem(rt, set), StringComparison.Ordinal);
        entries[1]!["attributes"]!["league"] = 13;
        entries.Add(new JsonObject { ["type"] = "PLAYER", ["attributes"] = new JsonObject { ["resourceId"] = 1 } });
        Assert.Contains("names a card", Problem(rt, set), StringComparison.Ordinal);
        var card = rt.Game.Players.Entries.First(entry => entry.Value.Rating == 75).Key;
        entries[2]!["attributes"]!["resourceId"] = card;
        Assert.Null(Problem(rt, set));

        var changes = new ContentChangesets(rt.Game, [new SbcSetArea(rt.Game)]);
        await changes.SetChangeAsync(SbcSetArea.AreaName, new JsonObject { ["setKey"] = "UPG_BRONZE" }, ContentOperation.Update, new JsonObject { ["definition"] = set }, null);
        await changes.PublishAsync("editor rewards", "test");
        var reward = rt.Game.Sbc.FindSet("UPG_BRONZE")!.Challenges[0].Source["rewards"]!["entries"]![2]!.AsObject();
        // No authored name: the card picked in the panel, not a name match.
        Assert.Equal(card, rt.Fut.Sbc.PlayerResourceId(reward));
    }

    [Fact]
    public async Task AChallengeCannotBeRenamedOnceItHasANumber()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, [new SbcSetArea(rt.Game)]);
        var set = Bronze(rt);
        set["description"] = "Numbered by its first panel edit.";
        await changes.SetChangeAsync(SbcSetArea.AreaName, new JsonObject { ["setKey"] = "UPG_BRONZE" }, ContentOperation.Update, new JsonObject { ["definition"] = set }, null);
        await changes.PublishAsync("number the challenges", "test");
        var numbered = (JsonObject)rt.Game.Sbc.FindSet("UPG_BRONZE")!.Source.DeepClone();
        Challenge(numbered)["id"] = "RENAMED_1";
        var error = await Assert.ThrowsAsync<ContentChangeException>(() => new ContentChangesets(rt.Game, [new SbcSetArea(rt.Game)]).SetChangeAsync(
            SbcSetArea.AreaName, new JsonObject { ["setKey"] = "UPG_BRONZE" }, ContentOperation.Update, new JsonObject { ["definition"] = numbered }, null));
        Assert.Contains("cannot be renamed", error.Message, StringComparison.Ordinal);
    }
}
