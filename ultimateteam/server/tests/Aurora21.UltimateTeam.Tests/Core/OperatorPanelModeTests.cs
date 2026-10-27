using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Modes → Champions (todo.md section 2): the reward ranks are content rows of the champions_rank area, staged, published and
/// reverted through Changes; the settings stay rules.
/// </summary>
public sealed partial class OperatorPanelTests
{
    private static JsonObject ChampionsRank(string name, string range, long low, long high, long coins, long fcPoints, params JsonObject[] lines) => new()
    {
        ["name"] = name,
        [range] = new JsonArray(low, high),
        ["reward"] = new JsonObject
        {
            ["coins"] = coins, ["fcPoints"] = fcPoints, ["xp"] = 0,
            ["lines"] = new JsonArray(lines.Select(line => (JsonNode?)line).ToArray()),
        },
    };

    private static JsonObject PackLine(long packId, long count, bool untradeable) =>
        new() { ["packId"] = packId, ["count"] = count, ["untradeable"] = untradeable };

    private static Task<(int Status, string Body, JsonNode? Json, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> StageChampionsRank(
        Setup setup, int tierLevel, string operation, JsonObject? rank) =>
        Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "champions_rank", ["key"] = new JsonObject { ["tierLevel"] = tierLevel },
            ["operation"] = operation, ["after"] = rank,
        }.ToJsonString());

    [Fact]
    public async Task ChampionsRanksAreReadEditedPublishedAndReverted()
    {
        using var setup = await CreateAsync();
        var view = (await Send(setup.Panel, "GET", "/panel/api/content/modes/champions")).Json!.AsObject();
        Assert.Equal("champions_rank", view["area"]!.GetValue<string>());
        Assert.Equal(20, view["maxWins"]!.GetValue<long>());
        Assert.False(view["settings"]!.AsObject().ContainsKey("positionsNeedWins"));
        var ranks = view["ranks"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(18, ranks.Count);
        var bronze = ranks.Single(rank => rank["tierLevel"]!.GetValue<int>() == 16);
        Assert.Equal(("Bronze 3", 12, "Bronze 3"), (bronze["name"]!.GetValue<string>(), bronze["clientTier"]!.GetValue<int>(), bronze["fifaName"]!.GetValue<string>()));
        Assert.Equal(6, view["clientTopBands"]!.GetValue<int>());
        Assert.Equal(1500, bronze["reward"]!["coins"]!.GetValue<long>());

        // Bronze 3 pays more coins and an untradeable 319 as well.
        var edited = ChampionsRank("Bronze 3", "wins", 0, 1, 2000, 250, PackLine(304, 1, false), PackLine(319, 2, true));
        Assert.Equal(200, (await StageChampionsRank(setup, 16, "update", edited)).Status);
        // Until it is published the game pays the old reward.
        Assert.Equal(1500, setup.Runtime.Game.Rules.ChampionsRewards.Ranks.Single(rank => rank.TierLevel == 16).Coins);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{\"note\":\"bronze 3\"}")).Status);
        var published = setup.Runtime.Game.Rules.ChampionsRewards.Ranks.Single(rank => rank.TierLevel == 16);
        Assert.Equal(2000, published.Coins);
        Assert.Equal([(304L, 1L, false), (319L, 2L, true)], published.Packs.Select(pack => (pack.PackId, pack.Count, pack.Untradeable)));
        var shown = (await Send(setup.Panel, "GET", "/panel/api/content/modes/champions")).Json!["ranks"]!.AsArray()
            .OfType<JsonObject>().Single(rank => rank["tierLevel"]!.GetValue<int>() == 16);
        Assert.Equal(2, shown["reward"]!["lines"]!.AsArray().Count);

        var history = await Send(setup.Panel, "GET", "/panel/api/content/history");
        var id = history.Json!["changesets"]![0]!["id"]!.GetValue<long>();
        Assert.Equal(200, (await Send(setup.Panel, "POST", $"/panel/api/content/history/{id}/revert", "{}")).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var reverted = setup.Runtime.Game.Rules.ChampionsRewards.Ranks.Single(rank => rank.TierLevel == 16);
        Assert.Equal((1500L, 1), (reverted.Coins, reverted.Packs.Count));
    }

    [Fact]
    public async Task TheMatchesPerWeekendLeagueChangeWithTheRanksThatCoverThem()
    {
        using var setup = await CreateAsync();
        var matches = new JsonObject
        {
            ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "seasons", ["path"] = new JsonArray("weekendLeague", "maxMatches") },
            ["operation"] = "update", ["after"] = new JsonObject { ["value"] = 22 },
        }.ToJsonString();
        // Alone, 21 and 22 wins have no rank: nothing is published.
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", matches)).Status);
        var alone = await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}");
        Assert.Equal(422, alone.Status);
        Assert.Contains("maxMatches", alone.Json!["error"]!.GetValue<string>());
        Assert.Equal(20, setup.Runtime.Game.Rules.Seasons.WeekendLeague.MaxMatches);

        // With Elite 1 covering 19-22 they publish together.
        var elite = (await Send(setup.Panel, "GET", "/panel/api/content/modes/champions")).Json!["ranks"]!.AsArray()
            .OfType<JsonObject>().Single(rank => rank["tierLevel"]!.GetValue<int>() == 5);
        var rank = new JsonObject { ["name"] = elite["name"]!.GetValue<string>(), ["wins"] = new JsonArray(19, 22), ["reward"] = elite["reward"]!.DeepClone() };
        Assert.Equal(200, (await StageChampionsRank(setup, 5, "update", rank)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal((22L, 22L), (setup.Runtime.Game.Rules.Seasons.WeekendLeague.MaxMatches, setup.Runtime.Game.Rules.ChampionsRewards.MaxWins));
        Assert.Equal(22, (await Send(setup.Panel, "GET", "/panel/api/content/modes/champions")).Json!["maxWins"]!.GetValue<long>());
    }

    [Fact]
    public async Task ChampionsRankEditsAreCheckedAloneAndAsAWholeTable()
    {
        using var setup = await CreateAsync();
        var card = ChampionsRank("Bronze 3", "wins", 0, 1, 1500, 250, new JsonObject { ["cardResourceId"] = 158023, ["count"] = 1, ["untradeable"] = true });
        var refused = await StageChampionsRank(setup, 16, "update", card);
        Assert.Equal(400, refused.Status);
        Assert.Contains("not available", refused.Json!["error"]!.GetValue<string>());
        var xp = ChampionsRank("Bronze 3", "wins", 0, 1, 1500, 250);
        xp["reward"]!["xp"] = 100;
        Assert.Equal(400, (await StageChampionsRank(setup, 16, "update", xp)).Status);
        Assert.Equal(400, (await StageChampionsRank(setup, 16, "update", ChampionsRank("Bronze 3", "wins", 0, 1, 1500, 250, PackLine(304, 0, false)))).Status);
        Assert.Equal(400, (await StageChampionsRank(setup, 0, "update", ChampionsRank("Bronze 3", "wins", 0, 1, 1500, 250))).Status);

        // A pack that does not exist is refused when the changeset is checked; nothing is published.
        Assert.Equal(200, (await StageChampionsRank(setup, 16, "update", ChampionsRank("Bronze 3", "wins", 0, 1, 1500, 250, PackLine(9999, 1, false)))).Status);
        var validation = await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}");
        Assert.False(validation.Json!["ok"]!.GetValue<bool>());
        Assert.Contains("9999", validation.Json.ToJsonString());
        Assert.Equal(422, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/discard", "{}")).Status);

        // Removing a win rank leaves 0-1 wins uncovered: the whole table no longer loads, so nothing is published.
        Assert.Equal(200, (await StageChampionsRank(setup, 16, "hide", null)).Status);
        var uncovered = await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}");
        Assert.Equal(422, uncovered.Status);
        Assert.Contains("cover 0-20", uncovered.Json!["error"]!.GetValue<string>());
        Assert.Equal(18, setup.Runtime.Game.Rules.ChampionsRewards.Ranks.Count);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/discard", "{}")).Status);

        // The ranks are no longer rules: the Rules editor points to Modes → Champions.
        var rule = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "champions_rewards", ["path"] = new JsonArray("ranks") },
            ["operation"] = "insert", ["after"] = new JsonObject { ["value"] = new JsonArray() },
        }.ToJsonString());
        Assert.Equal(409, rule.Status);
        Assert.Contains("Modes", rule.Json!["error"]!.GetValue<string>());
    }

    private static Task<(int Status, string Body, JsonNode? Json, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> StageRivals(
        Setup setup, string area, JsonObject key, string operation, JsonObject? after) =>
        Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = area, ["key"] = key.DeepClone(), ["operation"] = operation, ["after"] = after?.DeepClone(),
        }.ToJsonString());

    [Fact]
    public async Task RivalsRewardsAreReadEditedAndPublished()
    {
        using var setup = await CreateAsync();
        var view = (await Send(setup.Panel, "GET", "/panel/api/content/modes/rivals")).Json!.AsObject();
        Assert.Equal(("rivals_rank", "rivals_promotion", 4), (view["area"]!.GetValue<string>(), view["promotionArea"]!.GetValue<string>(), view["maxOptions"]!.GetValue<int>()));
        Assert.Equal(10, view["divisions"]!.AsArray().Count);
        Assert.Equal(30, view["settings"]!["matchCap"]!.GetValue<long>());
        var rankOne = view["divisions"]![4]!["ranks"]![0]!.AsObject();
        Assert.Equal((1, "I"), (rankOne["rank"]!.GetValue<int>(), rankOne["name"]!.GetValue<string>()));
        var options = rankOne["row"]!["options"]!.AsArray();

        // Division 5 Rank I: option 1 pays more coins; the options stay in order.
        var edited = (JsonArray)options.DeepClone();
        edited[0]!["coins"] = 31_000;
        var key = new JsonObject { ["division"] = 5, ["rank"] = 1 };
        Assert.Equal(200, (await StageRivals(setup, "rivals_rank", key, "update", new JsonObject { ["options"] = edited })).Status);
        var promotion = (JsonObject)view["promotion"]![2]!["reward"]!.DeepClone();
        promotion["coins"] = 16_000;
        Assert.Equal(200, (await StageRivals(setup, "rivals_promotion", new JsonObject { ["division"] = 3 }, "update", new JsonObject { ["reward"] = promotion })).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var rules = setup.Runtime.Game.Rules.RivalsRewards;
        Assert.Equal((31_000L, 16_000L), (rules.Divisions[5][0][0].Coins, rules.Promotion[3].Coins));
        Assert.Equal(options.Count, rules.Divisions[5][0].Count);

        // Four options at most; every rank and division keeps its rewards; Rivals pays no XP or cards yet.
        var five = new JsonArray(Enumerable.Range(0, 5).Select(_ => options[0]!.DeepClone()).ToArray());
        Assert.Equal(400, (await StageRivals(setup, "rivals_rank", key, "update", new JsonObject { ["options"] = five })).Status);
        Assert.Equal(400, (await StageRivals(setup, "rivals_rank", key, "hide", null)).Status);
        Assert.Equal(400, (await StageRivals(setup, "rivals_promotion", new JsonObject { ["division"] = 3 }, "hide", null)).Status);
        var xp = (JsonArray)options.DeepClone();
        xp[0]!["xp"] = 10;
        Assert.Equal(400, (await StageRivals(setup, "rivals_rank", key, "update", new JsonObject { ["options"] = xp })).Status);
        var card = (JsonArray)options.DeepClone();
        card[0]!["lines"] = new JsonArray(new JsonObject { ["cardResourceId"] = 158023, ["count"] = 1, ["untradeable"] = true });
        Assert.Equal(400, (await StageRivals(setup, "rivals_rank", key, "update", new JsonObject { ["options"] = card })).Status);
        var rule = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "rivals_rewards", ["path"] = new JsonArray("promotion") },
            ["operation"] = "insert", ["after"] = new JsonObject { ["value"] = new JsonObject() },
        }.ToJsonString());
        Assert.Equal(409, rule.Status);
        Assert.Contains("Modes", rule.Json!["error"]!.GetValue<string>());

        // A fourth option is allowed and published.
        var four = (JsonArray)options.DeepClone();
        while (four.Count < 4) four.Add(options[0]!.DeepClone());
        Assert.Equal(200, (await StageRivals(setup, "rivals_rank", key, "update", new JsonObject { ["options"] = four })).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(4, setup.Runtime.Game.Rules.RivalsRewards.Divisions[5][0].Count);
    }


    [Fact]
    public async Task SquadBattlesTiersAreReadEditedAndPublished()
    {
        using var setup = await CreateAsync();
        var view = (await Send(setup.Panel, "GET", "/panel/api/content/modes/squad-battles")).Json!.AsObject();
        Assert.Equal(("squad_battles_tier", 25, 6), (view["area"]!.GetValue<string>(), view["boardSize"]!.GetValue<int>(), view["boardMinimum"]!.GetValue<int>()));
        var tiers = view["tiers"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(18, tiers.Count);
        Assert.Equal(new[] { "Rank 1", "Rank 2-5", "Rank 6-10", "Rank 11-15", "Rank 16-20", "Rank 21-25", "Elite 1" },
            tiers.Take(7).Select(tier => tier["name"]!.GetValue<string>()));
        JsonObject Row(string id)
        {
            var row = (JsonObject)tiers.Single(tier => tier["tierId"]!.GetValue<string>() == id).DeepClone();
            row.Remove("tierId");
            row.Remove("name");
            return row;
        }
        Task<(int Status, string Body, JsonNode? Json, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> Stage(string id, string operation, JsonObject? after) =>
            Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
            {
                ["area"] = "squad_battles_tier", ["key"] = new JsonObject { ["tierId"] = id }, ["operation"] = operation, ["after"] = after,
            }.ToJsonString());

        // Elite 1 needs fewer points and pays more coins; Rank 2-5's prize gains a pack, untradeable.
        var elite = Row("elite1");
        elite["minPoints"] = 14_000;
        elite["reward"]!["coins"] = 31_000;
        Assert.Equal(200, (await Stage("elite1", "update", elite)).Status);
        var slot = Row("top20");
        slot["reward"]!["lines"]!.AsArray().Add(new JsonObject { ["packId"] = 304, ["count"] = 1, ["untradeable"] = true });
        Assert.Equal(200, (await Stage("top20", "update", slot)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var rules = setup.Runtime.Game.Rules.SquadBattlesRewards;
        Assert.Equal(14_000, rules.Tiers.Single(tier => tier.Id == "elite1").MinPoints);
        Assert.Contains(rules.Tiers.Single(tier => tier.Id == "elite1").Awards, award => award is { Type: "coin", Amount: 31_000 });
        Assert.Contains(rules.Tiers.Single(tier => tier.Id == "top20").Awards, award => award is { Type: "pack", PackId: 304, Untradeable: true });

        // A Top slot's places follow the Top players number; tiers stay; Squad Battles pays coins and packs only.
        var moved = Row("top40");
        moved["rankStart"] = 7;
        Assert.Equal(400, (await Stage("top40", "update", moved)).Status);
        Assert.Equal(400, (await Stage("top40", "hide", null)).Status);
        var kind = Row("elite2");
        kind["kind"] = "ranked";
        Assert.Equal(400, (await Stage("elite2", "update", kind)).Status);
        var points = Row("elite2");
        points["reward"]!["fcPoints"] = 100;
        Assert.Equal(400, (await Stage("elite2", "update", points)).Status);
        var rule = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "sqbt_rewards", ["path"] = new JsonArray("tiers") },
            ["operation"] = "insert", ["after"] = new JsonObject { ["value"] = new JsonArray() },
        }.ToJsonString());
        Assert.Equal(409, rule.Status);
    }

    [Fact]
    public async Task SquadBattlesTotwIsChosenAndPublished()
    {
        using var setup = await CreateAsync();
        var totw = (await Send(setup.Panel, "GET", "/panel/api/content/modes/squad-battles")).Json!["totw"]!.AsObject();
        var weeks = totw["weeks"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal((35L, 35, "sqbt_totw"), (totw["current"]!.GetValue<long>(), weeks.Count,
            (await Send(setup.Panel, "GET", "/panel/api/content/modes/squad-battles")).Json!["totwFile"]!.GetValue<string>()));
        Assert.Equal(Enumerable.Range(1, 35).Select(week => (long)week), weeks.Select(week => week["week"]!.GetValue<long>()));
        Assert.All(weeks, week => Assert.False(string.IsNullOrEmpty(week["headline"]?.GetValue<string>())));

        Task<(int Status, string Body, JsonNode? Json, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> Stage(long value) =>
            Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
            {
                ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "sqbt_totw", ["path"] = new JsonArray("current") },
                ["operation"] = "update", ["after"] = new JsonObject { ["value"] = value },
            }.ToJsonString());
        // A week outside the archive is refused when staged; an archived week publishes and becomes the playable one.
        Assert.Equal(400, (await Stage(36)).Status);
        Assert.Equal(200, (await Stage(12)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(12, setup.Runtime.Game.Rules.Totw.Current);
        Assert.Equal(12L, (await Send(setup.Panel, "GET", "/panel/api/content/modes/squad-battles")).Json!["totw"]!["current"]!.GetValue<long>());
    }

    [Fact]
    public async Task DraftLevelsAreReadEditedAndPublished()
    {
        using var setup = await CreateAsync();
        var view = (await Send(setup.Panel, "GET", "/panel/api/content/modes/draft")).Json!.AsObject();
        Assert.Equal(("draft_level", 4L, 15000L), (view["area"]!.GetValue<string>(), view["winsToComplete"]!.GetValue<long>(),
            view["settings"]!["entryCoins"]!.GetValue<long>()));
        var levels = view["levels"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(10, levels.Count);
        Assert.Equal(("online", 0, "Online, 0 wins"), (levels[0]["mode"]!.GetValue<string>(), levels[0]["wins"]!.GetValue<int>(), levels[0]["name"]!.GetValue<string>()));
        Assert.Equal("Single-player, 1 win", levels[6]["name"]!.GetValue<string>());
        JsonObject Row(string mode, int wins)
        {
            var row = (JsonObject)levels.Single(level => level["mode"]!.GetValue<string>() == mode && level["wins"]!.GetValue<int>() == wins).DeepClone();
            foreach (var key in new[] { "mode", "wins", "name" }) row.Remove(key);
            return row;
        }
        Task<(int Status, string Body, JsonNode? Json, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> Stage(string mode, int wins, string operation, JsonObject? after) =>
            Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
            {
                ["area"] = "draft_level", ["key"] = new JsonObject { ["mode"] = mode, ["wins"] = wins }, ["operation"] = operation, ["after"] = after,
            }.ToJsonString());

        // Single-player 1 win pays more coins and gains a second, heavier choice.
        var single = Row("single", 1);
        single["reward"]!["coins"] = 3500;
        single["choices"]!.AsArray().Add(new JsonObject
        {
            ["weight"] = 3, ["reward"] = new JsonObject { ["coins"] = 0, ["fcPoints"] = 0, ["xp"] = 0, ["lines"] = new JsonArray(PackLine(305, 2, true)) },
        });
        Assert.Equal(200, (await Stage("single", 1, "update", single)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var level = setup.Runtime.Game.Rules.Draft.RewardLevel("single", 1)!;
        Assert.Equal((3500L, 2, 3), (level.Reward.Coins, level.Choices.Count, level.Choices[1].Weight));
        Assert.Equal([(305L, 2L, true)], level.Choices[1].Reward.Packs.Select(pack => (pack.PackId, pack.Count, pack.Untradeable)));
        Assert.Contains("Draft: Single-player, 1 win",
            setup.Runtime.Game.InUnit(work => ModeRewardRepository.PackUsesAsync(work))[305]);

        // Every level stays, has 1-50 choices and pays coins and packs only; the old rules and the run length are refused.
        Assert.Equal(400, (await Stage("online", 2, "hide", null)).Status);
        var empty = Row("online", 2);
        empty["choices"] = new JsonArray();
        Assert.Equal(400, (await Stage("online", 2, "update", empty)).Status);
        var points = Row("online", 2);
        points["reward"]!["fcPoints"] = 100;
        Assert.Equal(400, (await Stage("online", 2, "update", points)).Status);
        Assert.Equal(404, (await Stage("online", 7, "update", Row("online", 2))).Status);
        foreach (var path in new[] { "runRewards", "winsToComplete" })
        {
            var rule = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
            {
                ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "draft", ["path"] = new JsonArray(path) },
                ["operation"] = "insert", ["after"] = new JsonObject { ["value"] = 5 },
            }.ToJsonString());
            Assert.Equal(409, rule.Status);
        }
    }
}
