using Aurora21.UltimateTeam.Core.Data;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class AuroraChampionsObjectiveMigrationTests
{
    [Fact]
    public async Task ExistingObjectiveCopyIsRenamedWithoutDiscardingOperatorEdits()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream(
            "DatabaseSetup/migrations/181-aurora-champions-objective-text.sql")!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();

        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                UPDATE ultimateteam.content_rules
                SET value=to_jsonb(replace(value #>> '{}', 'Aurora Champions', 'FUT Champions'))
                WHERE file='objectives' AND jsonb_typeof(value)='string'
                  AND strpos(value #>> '{}', 'Aurora Champions') > 0;
                UPDATE ultimateteam.content_rules
                SET value=to_jsonb((value #>> '{}') || ' Operator edit')
                WHERE file='objectives' AND path->>-1='description'
                  AND strpos(value #>> '{}', 'FUT Champions') > 0
                  AND path=(SELECT path FROM ultimateteam.content_rules
                    WHERE file='objectives' AND path->>-1='description'
                      AND strpos(value #>> '{}', 'FUT Champions') > 0
                    ORDER BY ordinal LIMIT 1);
                """);
            return 0;
        });
        var before = rt.Game.InUnit(work => work.ScalarAsync<long>("""
            SELECT count(*) FROM ultimateteam.content_rules
            WHERE file='objectives' AND strpos(value #>> '{}', 'FUT Champions') > 0
            """));
        Assert.True(before > 1);

        rt.Game.InUnit(async work => { await work.ExecuteAsync(sql); return 0; });
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("""
            SELECT count(*)=0 FROM ultimateteam.content_rules
            WHERE file='objectives' AND strpos(value #>> '{}', 'FUT Champions') > 0
            """)));
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("""
            SELECT count(*)=1 FROM ultimateteam.content_rules
            WHERE file='objectives' AND path->>-1='description'
              AND (value #>> '{}') LIKE '%Aurora Champions% Operator edit'
            """)));
        rt.Game.InUnit(async work => { await work.ExecuteAsync(sql); return 0; });
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("""
            SELECT count(*)=1 FROM ultimateteam.content_rules
            WHERE file='objectives' AND path->>-1='description'
              AND (value #>> '{}') LIKE '%Aurora Champions% Operator edit'
            """)));
    }
}
