using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    private static Task<(int Status, string Body, JsonNode? Json, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> StageTop(
        Setup setup, string file, string section, string setting, int size) =>
        Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "rules", ["key"] = new JsonObject { ["file"] = file, ["path"] = new JsonArray(section, setting) },
            ["operation"] = "update", ["after"] = new JsonObject { ["value"] = size },
        }.ToJsonString());

    [Theory]
    [InlineData(6)]
    [InlineData(50)]
    [InlineData(200)]
    public async Task LeaderboardTopPublishesSixSlotsAtomicallyAndPreservesRewards(int size)
    {
        using var setup = await CreateAsync();
        var game = setup.Runtime.Game;
        var before = game.Rules.ChampionsRewards.Ranks.Where(rank => rank.Positions is not null).OrderBy(rank => rank.Positions![0]).ToArray();
        var ids = before.Select(rank => rank.TierLevel).ToArray();
        var rewards = before.Select(rank => rank.Reward.ToJson().ToJsonString()).ToArray();
        var view = (await Send(setup.Panel, "GET", "/panel/api/content/modes/leaderboards")).Json!;
        Assert.All(view["modes"]!.AsArray(), mode => Assert.Equal(25, mode!["size"]!.GetValue<int>()));
        var revision = view["clientManifest"]!["revision"]!.GetValue<string>();
        Assert.Equal(21, view["clientManifest"]!["languages"]!.AsArray().Count);
        // Warm the game's event-band cache before publishing the setting.
        Assert.Equal(6, setup.Runtime.CallObject("GET", "/ut/game/fifa21/champion/user/hub")["activeChampionLeagues"]![0]!["prizeTiers"]!.AsArray()
            .OfType<JsonObject>().Count(tier => tier["tierType"]!.GetValue<string>() == "LB_RANKING_TYPE"));
        Assert.Equal(200, (await StageTop(setup, "seasons", "weekendLeague", "topX", size)).Status);
        Assert.Equal(25, game.Rules.Seasons.WeekendLeague.TopX);
        Assert.True((await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}")).Json!["ok"]!.GetValue<bool>());
        Assert.Equal(25, game.Rules.Seasons.WeekendLeague.TopX);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var after = game.Rules.ChampionsRewards.Ranks.Where(rank => rank.Positions is not null).OrderBy(rank => rank.Positions![0]).ToArray();
        Assert.Equal(size, game.Rules.Seasons.WeekendLeague.TopX);
        var sentBands = setup.Runtime.CallObject("GET", "/ut/game/fifa21/champion/user/hub")["activeChampionLeagues"]![0]!["prizeTiers"]!.AsArray()
            .OfType<JsonObject>().Where(tier => tier["tierType"]!.GetValue<string>() == "LB_RANKING_TYPE").ToArray();
        Assert.Equal(6, sentBands.Length);
        Assert.Equal(size, sentBands[^1]["tierEnd"]!.GetValue<long>());
        Assert.Equal(ids, after.Select(rank => rank.TierLevel));
        Assert.Equal(rewards, after.Select(rank => rank.Reward.ToJson().ToJsonString()));
        Assert.Equal(LeaderboardSizes.ChampionsBands(size), after.Select(rank => (rank.Positions![0], rank.Positions[1])));
        var next = (await Send(setup.Panel, "GET", "/panel/api/content/modes/leaderboards")).Json!;
        Assert.NotEqual(revision, next["clientManifest"]!["revision"]!.GetValue<string>());
        Assert.Equal(size, next["clientManifest"]!["captions"]![0]!["top"]!.GetValue<int>());
        var history = (await Send(setup.Panel, "GET", "/panel/api/content/history")).Json!;
        var id = history["changesets"]![0]!["id"]!.GetValue<long>();
        Assert.Equal(200, (await Send(setup.Panel, "POST", $"/panel/api/content/history/{id}/revert", "{}")).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(25, game.Rules.Seasons.WeekendLeague.TopX);
        Assert.Equal(LeaderboardSizes.ChampionsBands(25), game.Rules.ChampionsRewards.Ranks.Where(rank => rank.Positions is not null)
            .OrderBy(rank => rank.Positions![0]).Select(rank => (rank.Positions![0], rank.Positions[1])));
    }

    [Fact]
    public async Task LeaderboardSlotsCannotBeRemovedAddedConvertedOrManuallyResized()
    {
        using var setup = await CreateAsync();
        foreach (var rank in setup.Runtime.Game.Rules.ChampionsRewards.Ranks.Where(rank => rank.Positions is not null))
            Assert.Equal(400, (await StageChampionsRank(setup, rank.TierLevel, "hide", null)).Status);
        Assert.Equal(400, (await StageChampionsRank(setup, 1, "update", ChampionsRank("Rank 1", "positions", 1, 2, 100, 0))).Status);
        Assert.Equal(400, (await StageChampionsRank(setup, 1, "update", ChampionsRank("Win rank", "wins", 0, 0, 100, 0))).Status);
        Assert.Equal(400, (await StageChampionsRank(setup, 19, "insert", ChampionsRank("Extra", "positions", 26, 30, 100, 0))).Status);
        foreach (var invalid in new[] { 0, 5, 201 })
            Assert.Equal(400, (await StageTop(setup, "seasons", "weekendLeague", "topX", invalid)).Status);
    }

    [Fact]
    public async Task SquadBattleLeaderboardSizePublishesSeparatelyFromChampionsAndRewards()
    {
        using var setup = await CreateAsync();
        static string Prizes(Aurora21.UltimateTeam.Core.Rules.SquadBattlesRewardRules rules) =>
            System.Text.Json.JsonSerializer.Serialize(rules.Tiers.Select(tier => new { tier.Id, tier.Awards }));
        var before = Prizes(setup.Runtime.Game.Rules.SquadBattlesRewards);
        Assert.Equal(200, (await StageTop(setup, "squad_battles", "competition", "boardSize", 40)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(40, setup.Runtime.Game.Rules.SquadBattles.Competition.BoardSize);
        Assert.Equal(25, setup.Runtime.Game.Rules.Seasons.WeekendLeague.TopX);
        // The six Top slots move to the new places (migration 133); every tier keeps its prizes.
        Assert.Equal(before, Prizes(setup.Runtime.Game.Rules.SquadBattlesRewards));
        Assert.Equal(new[] { "1-1", "2-8", "9-16", "17-24", "25-32", "33-40" },
            setup.Runtime.Game.Rules.SquadBattlesRewards.RankedBands().Select(band => $"{band.Start}-{band.End}"));
        var view = (await Send(setup.Panel, "GET", "/panel/api/content/modes/leaderboards")).Json!;
        Assert.Equal(40, view["clientManifest"]!["captions"]![1]!["top"]!.GetValue<int>());
        // Six Top slots share the places, so the board has at least six.
        foreach (var invalid in new[] { 0, 5, 201 })
            Assert.Equal(400, (await StageTop(setup, "squad_battles", "competition", "boardSize", invalid)).Status);
    }
}
