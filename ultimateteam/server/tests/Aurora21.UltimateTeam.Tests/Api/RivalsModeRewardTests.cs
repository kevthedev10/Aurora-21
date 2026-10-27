using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The Division Rivals reward tables (migration 131, todo.md section 2): filled once at start-up from the stored rules or the
/// bundled seed, each line with the tradeability it was paid with.
/// </summary>
public sealed class RivalsModeRewardTests
{
    private static string Lines(RewardOption option) =>
        $"{option.Coins}/{option.FcPoints}:" + string.Join("+", option.Packs.Select(pack =>
            $"{(pack.Count > 1 ? $"{pack.Count}x" : "")}{pack.PackId}{(pack.Untradeable ? "u" : "")}{(pack.Source is { } source ? $"@{source}" : "")}"));

    private static long Count(TestRuntime rt, string sql) => rt.Game.InUnit(work => work.ScalarAsync<long>(sql));

    [Fact]
    public void AFreshDatabaseHasTheBundledRivalsRewards()
    {
        using var rt = TestRuntime.Create();
        var seed = ModeRewardRepository.RivalsSeed(TestRuntime.DataRoot);
        var rewards = rt.Game.Rules.RivalsRewards;
        Assert.Equal(Enumerable.Range(1, 10), rewards.Divisions.Keys.Order());
        foreach (var (division, ranks) in seed.Divisions)
        {
            Assert.Equal(ranks.Select(options => options.Select(option => Lines(RewardOption.From(option)))),
                rewards.Divisions[division].Select(options => options.Select(Lines)));
        }
        Assert.Equal(seed.Promotion.OrderBy(pair => pair.Key).Select(pair => Lines(RewardOption.From(pair.Value))),
            rewards.Promotion.OrderBy(pair => pair.Key).Select(pair => Lines(pair.Value)));
        Assert.Equal(10 * 6, Count(rt, "SELECT count(DISTINCT (division, rank)) FROM ultimateteam.rivals_reward_options"));
        Assert.Equal(0, Count(rt, "SELECT count(*) FROM ultimateteam.content_rules WHERE file='rivals_rewards' AND path->>0 IN ('divisions','promotion')"));
        // Option 3 of a choice is granted from rivals_option_c and untradeable; promotion to Division 1 pays the untradeable 86+ pick.
        Assert.EndsWith("u@rivals_option_c", Lines(rewards.Divisions[1][0][2]).Split('+')[0]);
        Assert.Contains("515u", Lines(rewards.Promotion[1]));
    }

    [Fact]
    public void RivalsRewardTablesNameTheirPacks()
    {
        using var rt = TestRuntime.Create();
        var uses = rt.Game.InUnit(work => ModeRewardRepository.PackUsesAsync(work));
        Assert.Contains("Rivals promotion: Division 1", uses[515]);
        Assert.Contains(uses.Values.SelectMany(labels => labels), label => label.StartsWith("Rivals: Division 1 Rank I", StringComparison.Ordinal));
    }
}
