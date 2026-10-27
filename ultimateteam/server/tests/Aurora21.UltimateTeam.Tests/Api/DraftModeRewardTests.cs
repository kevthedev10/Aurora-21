using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The Draft reward levels (migration 135, todo.md section 2): filled once at start-up from the bundled rewards/draft.json; the
/// old draft.json runRewards rules are removed, not converted (the owner replaced them with FIFA 21's bundles).
/// </summary>
public sealed class DraftModeRewardTests
{
    private static long Count(TestRuntime rt, string sql) => rt.Game.InUnit(work => work.ScalarAsync<long>(sql));

    private static string Text(IEnumerable<DraftRewardLevel> levels) => string.Join("|", levels.Select(level =>
        $"{level.Mode}{level.Wins}:{level.Reward.ToJson().ToJsonString()}:{string.Join(",", level.Choices.Select(choice => $"{choice.Weight}{choice.Reward.ToJson().ToJsonString()}"))}"));

    [Fact]
    public void AFreshDatabaseHasTheBundledDraftLevels()
    {
        using var rt = TestRuntime.Create();
        Assert.Equal(Text(ModeRewardRepository.DraftSeed(TestRuntime.DataRoot)), Text(rt.Game.Rules.Draft.RewardLevels));
        Assert.Equal(10, Count(rt, "SELECT count(*) FROM ultimateteam.draft_reward_levels"));
        Assert.Equal(82, Count(rt, "SELECT count(*) FROM ultimateteam.draft_reward_choices"));
        Assert.Equal(0, Count(rt, "SELECT count(*) FROM ultimateteam.content_rules WHERE file='draft' AND path->>0='runRewards'"));
    }

    [Fact]
    public void RestartKeepsAnEditedDraftReward()
    {
        using var rt = TestRuntime.Create();
        // Filled once: an edited level survives the next start.
        rt.Game.InUnit(work => work.ExecuteAsync("""
            UPDATE ultimateteam.rewards SET coins = 4321
            WHERE reward_id = (SELECT reward_id FROM ultimateteam.draft_reward_levels WHERE mode='online' AND wins=0)
            """));
        using var restarted = TestRuntime.Create(connectionString: rt.Detach());
        Assert.Equal(4321, restarted.Game.Rules.Draft.RewardLevel(DraftRewardLevel.Online, 0)!.Reward.Coins);
    }
}
