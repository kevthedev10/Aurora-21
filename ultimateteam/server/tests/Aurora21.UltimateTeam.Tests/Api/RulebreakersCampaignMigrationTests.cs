using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RulebreakersCampaignMigrationTests
{
    [Fact]
    public async Task BothTeamsReleaseTogetherAtCampaignStartAndExpireOneWeekLaterWithoutDuplicateCampaigns()
    {
        var clock = new TestRuntime.MovableTime(DateTimeOffset.Parse("2026-10-12T17:59:59Z").ToUnixTimeSeconds());
        using var rt = TestRuntime.Create(time: clock, marketTarget: 0);
        using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/173-rulebreakers-campaign.sql")!;
        using var reader = new StreamReader(stream);
        var sql = (await reader.ReadToEndAsync()).Replace("now()", "TIMESTAMPTZ '2026-10-07 16:00:00+00'");
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                UPDATE ultimateteam.store_packs SET category_chances='{"rulebreakers_team_1":0.02,"totw":0.01}'::jsonb WHERE pack_id=304;
                UPDATE ultimateteam.store_packs SET category_chances='{"rulebreakers_team_1":0.03,"rulebreakers_team_2":0.04}'::jsonb WHERE pack_id=307;
                INSERT INTO ultimateteam.promo_events(code,name,enabled,starts_at,ends_at)
                VALUES('event_rulebreakers_team_1','RULEBREAKERS TEAM 1',true,'2026-10-12 17:00Z','2026-10-19 16:59Z');
                INSERT INTO ultimateteam.promo_event_promos(event_id,promo_id,enabled)
                SELECT e.event_id,p.promo_id,true FROM ultimateteam.promo_events e,ultimateteam.promos p
                WHERE e.code='event_rulebreakers_team_1' AND p.code='rulebreakers_team_1';
                DELETE FROM ultimateteam.schema_migrations WHERE version=173;
                """);
            await work.ExecuteAsync(sql);
            return 0;
        });
        var campaign = Assert.Single(rt.Game.InUnit(async work => await ContentSchedule.CampaignsAsync(work)));
        Assert.Equal("Rulebreakers", campaign.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T18:00:00Z"), campaign.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T18:00:00Z"), campaign.EndsAt);
        var drop = Assert.Single(rt.Game.InUnit(async work => await ContentSchedule.DropsAsync(work)));
        Assert.Equal(campaign.Id, drop.CampaignId);
        Assert.Equal("scheduled", drop.Status);
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("""
            SELECT category_chances='{"rulebreakers_team_1":0.01,"rulebreakers_team_2":0.01,"totw":0.01}'::jsonb
            FROM ultimateteam.store_packs WHERE pack_id=304
            """)));
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("""
            SELECT category_chances='{"rulebreakers_team_1":0.03,"rulebreakers_team_2":0.04}'::jsonb
            FROM ultimateteam.store_packs WHERE pack_id=307
            """)));
        var schedule = new ContentSchedule(rt.Game, ContentAreas.Default.Append(new ScheduledAvailabilityArea(rt.Game, new RulesArea())));
        var change = Assert.Single((await schedule.Draft(drop.ChangesetId).OpenAsync())!.Changes);
        Assert.Equal(new[] { "rulebreakers_team_1", "rulebreakers_team_2" }, change.After!["promos"]!.AsArray().Select(n => n!.ToString()));
        var area = new PromoTimingArea(false);
        var key = new JsonObject { ["code"] = "event_rulebreakers_team_1" };
        async Task<JsonObject> Event() => (await rt.Game.UnitOfWork.RunReadAsync(0, async work => await area.ReadAsync(work, key)))!;
        Assert.Single((await Event())["promos"]!.AsArray());
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=173");
            await work.ExecuteAsync(sql);
            return 0;
        });
        Assert.Single(rt.Game.InUnit(async work => await ContentSchedule.CampaignsAsync(work)));
        Assert.Equal(drop.Id, Assert.Single(rt.Game.InUnit(async work => await ContentSchedule.DropsAsync(work))).Id);
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("SELECT (category_chances->>'rulebreakers_team_1')::numeric=0.01 FROM ultimateteam.store_packs WHERE pack_id=304")));
        await schedule.RunOnceAsync();
        Assert.Equal("scheduled", (await schedule.GetDropAsync(drop.Id)).Status);
        clock.Now++;
        await schedule.RunOnceAsync();
        var published = await schedule.GetDropAsync(drop.Id);
        Assert.Equal("live", published.Status);
        Assert.Equal(new[] { "rulebreakers_team_1", "rulebreakers_team_2" }, (await Event())["promos"]!.AsArray().Select(n => n!.ToString()));
        clock.Now = campaign.EndsAt!.Value.ToUnixTimeSeconds();
        await schedule.RunOnceAsync();
        Assert.Equal("ended", (await schedule.GetDropAsync(drop.Id)).Status);
    }

    [Fact]
    public async Task TotwTransitionIsExactlySevenPmLondonAndRemainsIndependent()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/174-totw-four-boundary.sql")!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                INSERT INTO ultimateteam.promo_events(code,name,enabled,starts_at,ends_at)
                VALUES('initial_totw_1_3','TOTW 1–3',true,'2026-09-30 17:00Z','2026-10-07 16:59Z'),
                    ('event_totw_4','TOTW 4',true,'2026-10-07 17:00Z','2026-10-14 16:59Z')
                ON CONFLICT(code) DO UPDATE SET starts_at=excluded.starts_at,ends_at=excluded.ends_at;
                DELETE FROM ultimateteam.schema_migrations WHERE version=174;
                """);
            await work.ExecuteAsync(sql);
            return 0;
        });
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("""
            SELECT a.ends_at=b.starts_at AND a.ends_at='2026-10-07 18:00Z'::timestamptz
                AND a.starts_at='2026-09-30 17:00Z'::timestamptz AND b.ends_at='2026-10-14 16:59Z'::timestamptz
            FROM ultimateteam.promo_events a,ultimateteam.promo_events b
            WHERE a.code='initial_totw_1_3' AND b.code='event_totw_4'
            """)));
        Assert.Empty(rt.Game.InUnit(async work => await ContentSchedule.CampaignsAsync(work)));
    }
}
