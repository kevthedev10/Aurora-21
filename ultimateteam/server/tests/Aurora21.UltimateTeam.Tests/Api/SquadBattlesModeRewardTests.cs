using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The Squad Battles reward tiers (migration 133, todo.md section 2): six Top slots placed by the Top players number and the twelve
/// points tiers, filled once at start-up from the stored rules or the bundled seed.
/// </summary>
public sealed class SquadBattlesModeRewardTests
{
    private static string Prize(SquadBattlesTier tier) =>
        string.Join("+", tier.Awards.Select(award => award.Type == "coin" ? $"{award.Amount}" : $"{award.PackId}{(award.Untradeable ? "u" : "")}"));

    private static long Count(TestRuntime rt, string sql) => rt.Game.InUnit(work => work.ScalarAsync<long>(sql));

    [Fact]
    public void AFreshDatabaseHasTheBundledSquadBattlesTiers()
    {
        using var rt = TestRuntime.Create();
        var seed = ModeRewardRepository.SquadBattlesSeed(TestRuntime.DataRoot);
        var rewards = rt.Game.Rules.SquadBattlesRewards;
        Assert.Equal(seed.Select(tier => tier.Id), rewards.Tiers.Select(tier => tier.Id));
        Assert.Equal(seed.Select(tier => Prize(tier.ToTier())), rewards.Tiers.Select(Prize));
        Assert.Equal(18, Count(rt, "SELECT count(*) FROM ultimateteam.squad_battles_reward_tiers"));
        Assert.Equal(0, Count(rt, "SELECT count(*) FROM ultimateteam.content_rules WHERE file='sqbt_rewards' AND path->>0 IN ('tiers','leaderboardBands')"));
    }


}
