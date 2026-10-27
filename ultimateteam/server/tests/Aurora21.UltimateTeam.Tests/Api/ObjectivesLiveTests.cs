using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Live objectives (owner, 2026-10-04): rules/objectives.json's groups are the catalogue and its live list says which the game
/// shows, in order. A group counts from when it first went live (migration 143), and Season Pass levels pay a full reward.
/// </summary>
public sealed class ObjectivesLiveTests
{
    private const string G = "/ut/game/fifa21";

    private static JsonObject Key(params JsonNode[] path) => new() { ["file"] = "objectives", ["path"] = new JsonArray(path) };

    private static JsonArray Listing(params long[] groups) =>
        new(groups.Select(group => (JsonNode?)new JsonObject { ["group"] = group, ["enabled"] = true }).ToArray());

    private static List<long> ServedGroups(TestRuntime rt) => rt.CallObject("GET", $"{G}/scmp/campaign/active")["categories"]![0]!["groups"]!
        .AsArray().OfType<JsonObject>().Select(group => group.Int("groupId")).ToList();

    [Fact]
    public async Task OnlyListedGroupsAreServedInListOrderAndClaimable()
    {
        using var rt = TestRuntime.Create();
        Assert.Contains(4L, ServedGroups(rt));
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("live"), ContentOperation.Update, new JsonObject { ["value"] = Listing(8, 1) }, null);
        await changes.PublishAsync("two live groups", "test");
        Assert.Equal([8L, 1L], ServedGroups(rt));

        // Collector (group 4) is still in the catalogue but not live: its objectives cannot be claimed.
        for (var i = 0; i < 10; i++) rt.Game.InUnit(rt.Fut.Objectives.RecordSbcCompletedAsync);
        Assert.Equal(409, rt.CallRaw("POST", $"{G}/scmp/campaign/group/4/objective/401/rewards").Status);
        Assert.DoesNotContain(401L, rt.Game.Store().Objectives().ClaimedObjectives);
    }

    [Fact]
    public async Task ANewGroupCountsFromWhenItGoesLiveAndOlderGroupsKeepTheirProgress()
    {
        using var rt = TestRuntime.Create();
        for (var i = 0; i < 5; i++) rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        var document = rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        var groups = document["grindGroups"]!.AsArray();
        groups.Add(new JsonObject
        {
            ["id"] = 900, ["title"] = "Fresh Start", ["subTitle"] = "New this week", ["award"] = new JsonObject { ["coins"] = 1000 },
            ["objectives"] = new JsonArray(new JsonObject
            {
                ["id"] = 90001, ["name"] = "Two More", ["stat"] = "matches", ["target"] = 2, ["award"] = new JsonObject { ["coins"] = 500 }, ["xp"] = 100,
            }),
        });
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("grindGroups"), ContentOperation.Update, new JsonObject { ["value"] = groups.DeepClone() }, null);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("live"), ContentOperation.Update, new JsonObject { ["value"] = Listing(2, 900) }, null);
        await changes.PublishAsync("a new group", "test");

        JsonObject Objective(long group, long id) => rt.CallObject("GET", $"{G}/scmp/active/category/1/details")["groupsList"]!.AsArray().OfType<JsonObject>()
            .Single(entry => entry.Int("groupId") == group)["objectives"]!.AsArray().OfType<JsonObject>().Single(entry => entry.Int("objectiveId") == id);
        // The five matches before it went live do not count for the new group; the older group keeps them.
        Assert.Equal(0, Objective(900, 90001).Int("currentProgress"));
        Assert.Equal(5, Objective(2, 201).Int("currentProgress"));
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        Assert.Equal("COMPLETED", Objective(900, 90001).Text("state"));
        var credits = rt.Game.Store().Credits();
        rt.Call("POST", $"{G}/scmp/campaign/group/900/objective/90001/rewards");
        Assert.Equal(credits + 500, rt.Game.Store().Credits());

        // Switched off and on again, it keeps counting from its first start.
        var off = new ContentChangesets(rt.Game, ContentAreas.Default);
        await off.SetChangeAsync(RulesArea.AreaName, Key("live"), ContentOperation.Update, new JsonObject { ["value"] = Listing(2) }, null);
        await off.PublishAsync("off", "test");
        var on = new ContentChangesets(rt.Game, ContentAreas.Default);
        await on.SetChangeAsync(RulesArea.AreaName, Key("live"), ContentOperation.Update, new JsonObject { ["value"] = Listing(2, 900) }, null);
        await on.PublishAsync("on", "test");
        Assert.Equal(2, Objective(900, 90001).Int("currentProgress"));
    }

    [Fact]
    public async Task ASeasonPassLevelPaysCoinsFifaPointsAndSeveralPacks()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("seasonProgress", "levelRewards", 0), ContentOperation.Update, new JsonObject
        {
            ["value"] = new JsonObject
            {
                ["coins"] = 100, ["fifaPoints"] = 50,
                ["packs"] = new JsonArray(new JsonObject { ["packId"] = 74, ["count"] = 2, ["untradeable"] = true }, new JsonObject { ["packId"] = 304 }),
            },
        }, null);
        await changes.PublishAsync("a full level reward", "test");
        var store = rt.Game.Store();
        var (credits, points) = (store.Credits(), rt.Game.InUnit(uow => uow.FifaPointsAsync()));
        store.AddObjectivesXp(rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel);
        var claimed = rt.CallObject("POST", $"{G}/scmp/campaign/level/1/reward/0");
        Assert.Equal(3, claimed["awards"]!.AsArray().Count);
        Assert.Equal((credits + 100, points + 50), (store.Credits(), rt.Game.InUnit(uow => uow.FifaPointsAsync())));
        Assert.Equal(2, store.UnopenedPackIds().Count(id => id == 74));
        Assert.Contains(304L, store.UnopenedPackIds());
        Assert.True(rt.Game.InUnit(uow => uow.ScalarAsync<bool>(
            "SELECT bool_and(untradeable) FROM ultimateteam.unopened_packs WHERE club_id=@club AND pack_id=74")));
    }

    [Fact]
    public async Task ALiveListNamingAMissingGroupOrARewardNamingAMissingPackIsRefused()
    {
        using var rt = TestRuntime.Create();
        var listing = new ContentChangesets(rt.Game, ContentAreas.Default);
        await listing.SetChangeAsync(RulesArea.AreaName, Key("live"), ContentOperation.Update, new JsonObject { ["value"] = Listing(2, 999) }, null);
        var validation = await listing.ValidateAsync();
        Assert.Contains(validation.Errors, error => error.Contains("group 999", StringComparison.Ordinal));
        await listing.DiscardAsync();
        var twice = await Assert.ThrowsAsync<ContentChangeException>(() => listing.SetChangeAsync(RulesArea.AreaName, Key("live"),
            ContentOperation.Update, new JsonObject { ["value"] = Listing(2, 2) }, null));
        Assert.Equal(400, twice.Status);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("seasonProgress", "levelRewards", 0), ContentOperation.Update,
            new JsonObject { ["value"] = new JsonObject { ["packs"] = new JsonArray(new JsonObject { ["packId"] = 32000 }) } }, null);
        Assert.False((await changes.ValidateAsync()).Ok);
    }

    [Fact]
    public async Task ALevelOffersAChoiceAndPaysOnlyThePickedOption()
    {
        using var rt = TestRuntime.Create();
        var choice = new JsonObject
        {
            ["options"] = new JsonArray(new JsonObject { ["coins"] = 700 }, new JsonObject { ["packs"] = new JsonArray(new JsonObject { ["packId"] = 74 }) },
                new JsonObject { ["fifaPoints"] = 30 }),
        };
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("seasonProgress", "levelRewards", 0), ContentOperation.Update, new JsonObject { ["value"] = choice }, null);
        await changes.PublishAsync("a choice", "test");
        JsonObject Level() => rt.CallObject("GET", $"{G}/scmp/campaign/active")["levels"]![0]!.AsObject();
        var options = Level()["rewardOptionList"]!.AsArray();
        Assert.Equal([0L, 1L, 2L], options.OfType<JsonObject>().Select(option => option.Int("optionId")));
        Assert.Equal(74, options[1]!["awards"]![0]!.AsObject().Int("packId"));

        var store = rt.Game.Store();
        store.AddObjectivesXp(rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel);
        var credits = store.Credits();
        Assert.True(rt.CallObject("POST", $"{G}/scmp/campaign/level/1/reward/3")["alreadyClaimed"] is not null);
        Assert.DoesNotContain(74L, store.UnopenedPackIds());
        rt.Call("POST", $"{G}/scmp/campaign/level/1/reward/1");
        Assert.Contains(74L, store.UnopenedPackIds());
        Assert.Equal(credits, store.Credits());
        // Claimed, the level shows the option the club picked.
        Assert.Equal((2L, 1L), (Level().Int("state"), Level().Int("chosenOption")));
        Assert.True(rt.CallObject("POST", $"{G}/scmp/campaign/level/1/reward/0")["alreadyClaimed"].B());
    }

    [Fact]
    public async Task OnlyALevelOffersAChoiceAndAChoicePaysNothingBesideItsOptions()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var extra = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(RulesArea.AreaName, Key("seasonProgress", "levelRewards", 0),
            ContentOperation.Update, new JsonObject { ["value"] = new JsonObject
            {
                ["coins"] = 5, ["options"] = new JsonArray(new JsonObject { ["coins"] = 1 }, new JsonObject { ["coins"] = 2 }),
            } }, null));
        Assert.Contains("only the chosen one", extra.Message, StringComparison.Ordinal);
        var objective = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(RulesArea.AreaName, Key("grindGroups", 0, "award"),
            ContentOperation.Update, new JsonObject { ["value"] = new JsonObject { ["options"] = new JsonArray(new JsonObject { ["coins"] = 1 }, new JsonObject { ["coins"] = 2 }) } }, null));
        Assert.Contains("only a Season Pass level", objective.Message, StringComparison.Ordinal);
    }
}
