using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class OtwCampaignMigrationTests
{
    [Fact]
    public async Task ExistingContentGetsLondonWindowsAndFutureFoundationsPublishAndExpireWithoutDuplicateCampaigns()
    {
        var clock = new TestRuntime.MovableTime(DateTimeOffset.Parse("2026-10-07T16:00:00Z").ToUnixTimeSeconds());
        using var rt = TestRuntime.Create(time: clock, marketTarget: 0);
        using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/171-otw-campaign.sql")!;
        using var reader = new StreamReader(stream);
        // Freeze PostgreSQL's migration clock as well as the scheduler clock.
        var sql = (await reader.ReadToEndAsync()).Replace("now()", "TIMESTAMPTZ '2026-10-07 16:00:00+00'");
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.promo_events(code,name,enabled,starts_at,ends_at)
                VALUES('event_ones_to_watch','Ones to Watch',true,'2026-10-05 17:00Z','2026-10-12 16:59Z');
                INSERT INTO ultimateteam.promo_event_promos(event_id,promo_id,enabled)
                SELECT e.event_id,p.promo_id,true FROM ultimateteam.promo_events e,ultimateteam.promos p
                WHERE e.code='event_ones_to_watch' AND p.code='ones_to_watch_team_1';
                DELETE FROM ultimateteam.schema_migrations WHERE version=171;
                """);
            await work.ExecuteAsync(sql);
            return 0;
        });
        var campaigns = rt.Game.InUnit(async work => await ContentSchedule.CampaignsAsync(work));
        var campaign = Assert.Single(campaigns);
        Assert.Equal(DateTimeOffset.Parse("2026-10-05T18:00:00Z"), campaign.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T18:00:00Z"), campaign.EndsAt);
        var drops = rt.Game.InUnit(async work => await ContentSchedule.DropsAsync(work));
        var foundations = Assert.Single(drops, d => d.Name == "Squad Foundations 2");
        Assert.Equal("scheduled", foundations.Status);
        Assert.Equal(4, foundations.Expires.Count);
        Assert.All(drops, d => Assert.Equal(campaign.EndsAt, d.EndsAt));
        var originalIds = drops.Select(d => d.Id).ToArray();
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=171");
            await work.ExecuteAsync(sql);
            return 0;
        });
        Assert.Single(rt.Game.InUnit(async work => await ContentSchedule.CampaignsAsync(work)));
        Assert.Equal(originalIds, rt.Game.InUnit(async work => await ContentSchedule.DropsAsync(work)).Select(d => d.Id));

        var schedule = new ContentSchedule(rt.Game, ContentAreas.Default.Append(
            new ScheduledAvailabilityArea(rt.Game, new RulesArea())));
        clock.Now = DateTimeOffset.Parse("2026-10-08T17:59:59Z").ToUnixTimeSeconds();
        await schedule.RunOnceAsync();
        Assert.Equal("scheduled", (await schedule.GetDropAsync(foundations.Id)).Status);
        clock.Now++;
        await schedule.RunOnceAsync();
        Assert.Equal("live", (await schedule.GetDropAsync(foundations.Id)).Status);
        Assert.All(foundations.Expires.OfType<JsonObject>(), e =>
            Assert.Contains(long.Parse(e["target"]!.ToString()), rt.Game.Rules.Objectives.LiveGroupIds));
        clock.Now = DateTimeOffset.Parse("2026-10-12T18:00:00Z").ToUnixTimeSeconds();
        await schedule.RunOnceAsync();
        Assert.All(rt.Game.InUnit(async work => await ContentSchedule.DropsAsync(work)), d => Assert.Equal("ended", d.Status));
        Assert.All(foundations.Expires.OfType<JsonObject>(), e =>
            Assert.DoesNotContain(long.Parse(e["target"]!.ToString()), rt.Game.Rules.Objectives.LiveGroupIds));
    }
}
