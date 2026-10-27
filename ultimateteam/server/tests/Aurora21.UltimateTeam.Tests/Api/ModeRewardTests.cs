using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The mode reward tables (migration 126, todo.md section 2): the shared reward format, filled once at start-up from the stored
/// rules or the bundled seed, with each line's own tradeability.
/// </summary>
public sealed class ModeRewardTests
{
    private static string Lines(Reward reward) =>
        string.Join("+", reward.Lines.Select(line => $"{(line.Count > 1 ? $"{line.Count}x" : "")}{line.PackId}{(line.Untradeable ? "u" : "")}"));

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var command = source.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task AFreshDatabaseHasTheBundledChampionsRanks()
    {
        using var rt = TestRuntime.Create();
        var seed = ModeRewardRepository.ChampionsSeed(TestRuntime.DataRoot).OrderBy(rank => rank.TierLevel).ToList();
        var ranks = rt.Game.InUnit(work => ModeRewardRepository.ChampionsRanksAsync(work));
        Assert.Equal(18, ranks.Count);
        Assert.Equal(seed.Select(rank => (rank.TierLevel, rank.Name, rank.Reward.Coins, rank.Reward.FcPoints, Lines(rank.Reward))),
            ranks.Select(rank => (rank.TierLevel, rank.Name, rank.Reward.Coins, rank.Reward.FcPoints, Lines(rank.Reward))));
        Assert.Equal(seed.Select(rank => (rank.Wins, rank.Positions)).Select(pair => $"{string.Join("-", pair.Wins ?? [])}/{string.Join("-", pair.Positions ?? [])}"),
            ranks.Select(rank => $"{string.Join("-", rank.Wins ?? [])}/{string.Join("-", rank.Positions ?? [])}"));
        // The rules hold only the settings; the loaded table is the seed.
        Assert.Equal(0, await CountAsync(rt.ConnectionString,
            "SELECT count(*) FROM ultimateteam.content_rules WHERE file='champions_rewards' AND path->>0='ranks'"));
        Assert.Equal("541+405+402+5x550u", Lines(rt.Game.Rules.ChampionsRewards.Ranks[0].Reward));
    }

    [Fact]
    public void RestartPreservesCustomRewardsClubStateAndSettledResults()
    {
        var clock = new TestRuntime.MovableTime(1_790_928_000);
        using var rt = TestRuntime.Create(time: clock);
        rt.Game.InUnit(work => work.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 20, 19, 0, 1, 5)));
        clock.Now = 1_791_216_000;
        rt.CallObject("GET", "/ut/game/fifa21/champion/user/hub");
        var settled = rt.Game.InUnit(work => work.QueryAsync("SELECT row_to_json(r)::text FROM ultimateteam.champion_results r", row => row.GetString(0)));
        Assert.Single(settled);
        var credits = rt.Game.Store().Credits();

        // Operator reward edits must survive without changing already settled rewards.
        rt.Game.InUnit(work => work.ExecuteAsync("""
            UPDATE ultimateteam.rewards SET coins = 123456
            WHERE reward_id = (SELECT reward_id FROM ultimateteam.champions_reward_ranks WHERE tier_level = 2);
            """));
        var rewards = rt.Game.InUnit(work => ModeRewardRepository.ChampionsRanksAsync(work))
            .Select(rank => (rank.TierLevel, rank.Reward.ToJson().ToJsonString())).ToList();

        using var upgraded = TestRuntime.Create(connectionString: rt.Detach(), time: clock);
        var rules = upgraded.Game.Rules;
        Assert.Equal(25, rules.Seasons.WeekendLeague.TopX);
        // Band sizing is gone (migration 130): no leaderboardBands rule is left, and the bands are the Top 25 places.
        Assert.Equal(0L, upgraded.Game.InUnit(work => work.ScalarAsync<long>(
            "SELECT count(*) FROM ultimateteam.content_rules WHERE file = 'champions_rewards' AND path->>0 = 'leaderboardBands'")));
        Assert.Equal(["1-1", "2-5", "6-10", "11-15", "16-20", "21-25"],
            rules.ChampionsRewards.Bands().Select(band => $"{band.Low}-{band.High}"));
        Assert.Equal(rewards, rules.ChampionsRewards.Ranks.Select(rank => (rank.TierLevel, rank.Reward.ToJson().ToJsonString())));
        Assert.Equal(settled, upgraded.Game.InUnit(work => work.QueryAsync("SELECT row_to_json(r)::text FROM ultimateteam.champion_results r", row => row.GetString(0))));
        Assert.Equal(credits, upgraded.Game.Store().Credits());
        Assert.Equal(new WeekendLeagueRecord(2961, 20, 19, 0, 1, 5), upgraded.Game.InUnit(work => work.WeekendLeagueRecordAsync(2961)));

        // Later panel edits are also preserved at the next start.
        upgraded.Game.InUnit(work => work.ExecuteAsync("""
            UPDATE ultimateteam.content_rules SET value = '30' WHERE file = 'seasons' AND path = '["weekendLeague","topX"]';
            """));
        using var reopened = TestRuntime.Create(connectionString: upgraded.Detach(), time: clock);
        Assert.Equal(30, reopened.Game.Rules.Seasons.WeekendLeague.TopX);
    }

    [Fact]
    public void RewardTablesNameTheirPacksAndKeepThemFromBeingDeleted()
    {
        using var rt = TestRuntime.Create();
        var uses = rt.Game.InUnit(work => ModeRewardRepository.PackUsesAsync(work));
        Assert.Equal(["Champions: Rank 1", "Champions: Rank 2-5", "Champions: Rank 6-10", "Champions: Rank 11-15"], uses[541]);
        Assert.Contains("Champions: Bronze 3", uses[304]);
        var error = Assert.ThrowsAny<PostgresException>(() =>
            rt.Game.InUnit(work => work.ExecuteAsync("DELETE FROM ultimateteam.store_packs WHERE pack_id=541")));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public void ARewardRefusesNegativeAmountsBadLinesAndCardsWhereNotAllowed()
    {
        static Reward With(params RewardLine[] lines) => new() { Coins = 1, Lines = lines.ToList() };
        Assert.Null(With(new RewardLine { PackId = 300, Untradeable = false }).Problem(cards: false));
        Assert.NotNull(new Reward { Coins = -1 }.Problem(cards: false));
        Assert.NotNull(With(new RewardLine { Untradeable = false }).Problem(cards: false));
        Assert.NotNull(With(new RewardLine { PackId = 300, CardResourceId = 1, Untradeable = false }).Problem(cards: true));
        Assert.NotNull(With(new RewardLine { PackId = 300, Count = 0, Untradeable = false }).Problem(cards: false));
        Assert.NotNull(With(new RewardLine { PackId = 300, Count = RewardLine.MaxCount + 1, Untradeable = false }).Problem(cards: false));
        Assert.NotNull(With(new RewardLine { PackId = 300, Source = "nowhere", Untradeable = false }).Problem(cards: false));
        var card = With(new RewardLine { CardResourceId = 158023, Untradeable = true });
        Assert.Contains("not available", card.Problem(cards: false));
        Assert.Null(card.Problem(cards: true));
    }
}
