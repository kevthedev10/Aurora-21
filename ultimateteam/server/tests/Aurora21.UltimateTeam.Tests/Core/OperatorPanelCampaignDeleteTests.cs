using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Server.Panel;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;
public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task CampaignDeleteOnlyRemovesNonLiveCampaignAndItsUnpublishedDrops()
    {
        using var setup = await CreateAsync();
        var schedule = OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var now = setup.Runtime.Game.Time.GetUtcNow();
        var active = await schedule.SaveCampaignAsync(null, "Synthetic active", now.AddDays(1), 0, startsAt: now.AddDays(-1));
        var future = await schedule.SaveCampaignAsync(null, "Synthetic upcoming", now.AddDays(7), 0, startsAt: now.AddDays(2));
        async Task<int> Delete(long id, long version) => (await Send(setup.Panel, "DELETE", $"/panel/api/content/campaigns/{id}", Body(new { version }))).Status;
        Assert.Equal(409, await Delete(active.Id, active.Version));
        var draft = await schedule.SaveDropAsync(null, future.Id, "Synthetic draft", now.AddDays(3), now.AddDays(4), false, 0);
        var queued = await schedule.SaveDropAsync(null, future.Id, "Synthetic queued", now.AddDays(4), now.AddDays(5), false, 0);
        var ended = await schedule.SaveDropAsync(null, future.Id, "Synthetic history", now.AddDays(5), now.AddDays(6), false, 0);
        await setup.Runtime.Game.UnitOfWork.RunAsync(0, "synthetic-delete-fixture", async work => {
            await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='scheduled' WHERE drop_id=@id", ("id", queued.Id));
            await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='live',published_at=now() WHERE drop_id=@id", ("id", ended.Id));
            return true;
        });
        Assert.Equal(409, await Delete(future.Id, future.Version));
        await setup.Runtime.Game.UnitOfWork.RunAsync(0, "synthetic-ended-fixture", async work => {
            await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='ended' WHERE drop_id=@id", ("id", ended.Id)); return true;
        });
        Assert.Equal(409, await Delete(future.Id, future.Version + 1));
        Assert.Equal(200, await Delete(future.Id, future.Version));
        var reply = (await Send(setup.Panel, "GET", "/panel/api/content/schedule")).Json!;
        Assert.Single(reply["campaigns"]!.AsArray());
        Assert.Equal(active.Id, reply["campaigns"]![0]!["id"]!.GetValue<long>());
        var history = Assert.Single(reply["drops"]!.AsArray());
        Assert.Equal(ended.Id, history!["id"]!.GetValue<long>());
        Assert.Null(history["campaignId"]);
        Assert.Equal("ended", history["status"]!.GetValue<string>());
        foreach (var id in new[] { draft.ChangesetId, queued.ChangesetId })
            Assert.Equal("discarded", await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<string>("SELECT status FROM ultimateteam.content_changesets WHERE changeset_id=@id", ("id", id))));
        Assert.Equal(404, await Delete(future.Id, future.Version));
    }
}
