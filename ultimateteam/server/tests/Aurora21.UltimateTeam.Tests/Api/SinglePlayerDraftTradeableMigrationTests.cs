using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class SinglePlayerDraftTradeableMigrationTests
{
    [Fact]
    public async Task UpgradeChangesOnlySinglePlayerRunTradeabilityAndPreservesAlreadyGrantedPacks()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var oldPack = rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                UPDATE ultimateteam.reward_lines SET untradeable=true
                WHERE reward_id IN (SELECT reward_id FROM ultimateteam.draft_reward_levels
                    UNION SELECT reward_id FROM ultimateteam.draft_reward_choices);
                UPDATE ultimateteam.rewards SET coins=4321
                WHERE reward_id=(SELECT reward_id FROM ultimateteam.draft_reward_levels WHERE mode='single' AND wins=0);
                UPDATE ultimateteam.draft_reward_choices SET weight=3 WHERE mode='single' AND wins=0 AND choice=1;
                DELETE FROM ultimateteam.schema_migrations WHERE version=175;
                """);
            return await work.AddUnopenedPackAsync(74, "draft_single", untradeable: true);
        });
        string Snapshot() => rt.Game.InUnit(work => work.ScalarAsync<string>("""
            SELECT jsonb_build_object(
                'rewards',(SELECT jsonb_agg(to_jsonb(r) ORDER BY reward_id) FROM ultimateteam.rewards r),
                'lines',(SELECT jsonb_agg(to_jsonb(l)-'untradeable' ORDER BY reward_id,position) FROM ultimateteam.reward_lines l),
                'levels',(SELECT jsonb_agg(to_jsonb(d) ORDER BY mode,wins) FROM ultimateteam.draft_reward_levels d),
                'choices',(SELECT jsonb_agg(to_jsonb(d) ORDER BY mode,wins,choice) FROM ultimateteam.draft_reward_choices d),
                'otherTradeability',(SELECT jsonb_agg(to_jsonb(l) ORDER BY reward_id,position)
                    FROM ultimateteam.reward_lines l WHERE reward_id NOT IN (
                        SELECT reward_id FROM ultimateteam.draft_reward_levels WHERE mode='single'
                        UNION SELECT reward_id FROM ultimateteam.draft_reward_choices WHERE mode='single')),
                'rules',(SELECT jsonb_agg(to_jsonb(c) ORDER BY file,ordinal) FROM ultimateteam.content_rules c),
                'ownedPacks',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM ultimateteam.unopened_packs p)
            )::text
            """));
        var before = Snapshot();
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        Assert.Equal(before, Snapshot());
        var levels = rt.Game.InUnit(ModeRewardRepository.DraftLevelsAsync);
        Assert.NotEmpty(levels);
        foreach (var level in levels)
        foreach (var reward in level.Choices.Select(choice => choice.Reward).Prepend(level.Reward))
            Assert.All(reward.Lines, line => Assert.Equal(level.Mode != "single", line.Untradeable));
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>(
            "SELECT untradeable FROM ultimateteam.unopened_packs WHERE club_id=@club AND id=@id", ("id", oldPack))));
        Assert.True(rt.Game.Rules.Seasons.MatchCompletionRewards["draftSinglePlayer"].Untradeable);
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        Assert.Equal(before, Snapshot());
    }
}
