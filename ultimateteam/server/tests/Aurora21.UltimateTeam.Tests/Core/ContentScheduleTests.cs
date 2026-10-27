using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class ContentScheduleTests
{
    private const long Instant = 1_792_000_000;
    private static ContentSchedule Schedule(TestRuntime rt) => new(rt.Game,
        ContentAreas.Default.Append(new ScheduledAvailabilityArea(rt.Game, new UnusedStoreArea())));
    private static JsonObject PromoKey(string code="gold_rare") => new() { ["code"]=code };
    private static Task<JsonObject?> ReadPromo(TestRuntime rt,string code="gold_rare") =>
        rt.Game.UnitOfWork.RunReadAsync(0,w=>new PromoArea().ReadAsync(w,PromoKey(code)));
    private static Task<ContentCampaign> Campaign(ContentSchedule schedule) => schedule.SaveCampaignAsync(null,"Synthetic campaign",null,0);
    private static JsonObject SyntheticPromo()=>new(){["name"]="Synthetic scheduled group",["odds_category"]="totw",["draw_weight"]=1m,["status"]="active",
        ["buckets"]=new JsonArray(new JsonObject{["code"]="main",["name"]="Main",["draw_weight"]=1m,["enabled"]=true,["cards"]=new JsonArray()}),
        ["card_reductions"]=new JsonObject()};
    private static Task<ContentDrop> Drop(ContentSchedule schedule,ContentCampaign campaign,string name,int seconds=60,DateTimeOffset? end=null) =>
        schedule.SaveDropAsync(null,campaign.Id,name,DateTimeOffset.FromUnixTimeSeconds(Instant+seconds),end,false,0);
    private static async Task<ContentDrop> EditPromo(TestRuntime rt,ContentSchedule schedule,ContentDrop drop,string field,JsonNode value,string code="gold_rare")
    {
        var row=(await ReadPromo(rt,code))!; row[field]=value;
        await schedule.Draft(drop.ChangesetId).SetChangeAsync("promo",PromoKey(code),ContentOperation.Update,row,"synthetic");
        return await schedule.GetDropAsync(drop.Id);
    }
    private static async Task<ContentDrop> ObjectiveWindow(ContentSchedule schedule,ContentCampaign campaign,string name,int start,int end)
    {
        var drop=await Drop(schedule,campaign,name,start,DateTimeOffset.FromUnixTimeSeconds(Instant+end));
        await schedule.Draft(drop.ChangesetId).SetChangeAsync("content_window",new JsonObject{["kind"]="objective",["target"]="2"},
            ContentOperation.Update,new JsonObject{["enabled"]=true},null);
        drop=await schedule.GetDropAsync(drop.Id);
        return await schedule.ScheduleAsync(drop.Id,drop.Version);
    }

    [Fact]
    public async Task IndependentDraftsAndChronologicalPreviewPreserveDisjointChangesAndDoNotPublish()
    {
        using var rt=TestRuntime.Create(time:new TestRuntime.MovableTime(Instant),marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var original=(await ReadPromo(rt))!;
        var first=await EditPromo(rt,schedule,await Drop(schedule,campaign,"Team one"),"name",JsonValue.Create("Synthetic first")!);
        var second=await EditPromo(rt,schedule,await Drop(schedule,campaign,"Team two",120),"draw_weight",JsonValue.Create(2m)!);
        Assert.NotEqual(first.ChangesetId,second.ChangesetId);
        Assert.Single((await schedule.Draft(first.ChangesetId).OpenAsync())!.Changes);
        Assert.Single((await schedule.Draft(second.ChangesetId).OpenAsync())!.Changes);
        var validation=await schedule.ValidateAsync(first.Id,first.Version);
        Assert.True(validation.Ok,string.Join(";",validation.Errors));
        Assert.Equal("draft",(await schedule.GetDropAsync(first.Id)).Status);
        first=await schedule.ScheduleAsync(first.Id,first.Version);
        second=await schedule.ScheduleAsync(second.Id,second.Version);
        var preview=await schedule.ProjectAsync(second.StartsAt);
        var projected=preview.Catalog.Promos.Single(p=>p.Code=="gold_rare");
        Assert.Equal("Synthetic first",projected.Name);
        Assert.Equal(2m,projected.DrawWeight);
        Assert.True(JsonNode.DeepEquals(original,await ReadPromo(rt)));
        Assert.Equal(1,rt.Game.Live.Current.Revision);
        Assert.Empty(await schedule.Draft(first.ChangesetId).HistoryAsync(10));
        Assert.Equal("scheduled",(await schedule.GetDropAsync(second.Id)).Status);
    }

    [Fact]
    public async Task DueReleasePublishesOnceAndRetainsHistory()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var drop=await EditPromo(rt,schedule,await Drop(schedule,campaign,"Release"),"name",JsonValue.Create("Synthetic live")!);
        drop=await schedule.ScheduleAsync(drop.Id,drop.Version);
        await schedule.RunOnceAsync();
        Assert.Equal("scheduled",(await schedule.GetDropAsync(drop.Id)).Status);
        clock.Now=Instant+60;
        await schedule.RunOnceAsync();
        var live=await schedule.GetDropAsync(drop.Id);
        Assert.Equal("live",live.Status); Assert.NotNull(live.PublishedAt);
        Assert.Equal("Synthetic live",(await ReadPromo(rt))!["name"]!.GetValue<string>());
        var revision=rt.Game.Live.Current.Revision;
        await schedule.RunOnceAsync();
        Assert.Equal(revision,rt.Game.Live.Current.Revision);
        var history=Assert.Single(await schedule.Draft(drop.ChangesetId).HistoryAsync(10));
        Assert.Equal(drop.ChangesetId,history.Id); Assert.Equal("published",history.Status);
    }

    [Fact]
    public async Task ConflictingDueReleaseFailsWithoutPublishingItsOtherEdit()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var originalCommon=(await ReadPromo(rt,"gold_common"))!;
        var drop=await EditPromo(rt,schedule,await Drop(schedule,campaign,"Atomic release"),"name",JsonValue.Create("Scheduled name")!);
        drop=await EditPromo(rt,schedule,drop,"name",JsonValue.Create("Must roll back")!,"gold_common");
        drop=await schedule.ScheduleAsync(drop.Id,drop.Version);
        // Simulate a concurrent publication in this disposable database after validation.
        await rt.Game.UnitOfWork.RunAsync(0,"synthetic-concurrent-content",async w=>
        {
            await w.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','on',true)");
            await w.ExecuteAsync("UPDATE ultimateteam.promos SET name='Concurrent name' WHERE code='gold_rare'");
            return await w.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','off',true)");
        });
        clock.Now=Instant+60; await schedule.RunOnceAsync();
        var failed=await schedule.GetDropAsync(drop.Id);
        Assert.Equal("failed",failed.Status); Assert.NotNull(failed.Error);
        Assert.True(JsonNode.DeepEquals(originalCommon,await ReadPromo(rt,"gold_common")));
        Assert.Equal("Concurrent name",(await ReadPromo(rt))!["name"]!.GetValue<string>());
        Assert.Empty(await schedule.Draft(drop.ChangesetId).HistoryAsync(10));
    }

    [Fact]
    public async Task CancellationAndCampaignEditsRejectStaleVersions()
    {
        using var rt=TestRuntime.Create(time:new TestRuntime.MovableTime(Instant),marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var drop=await EditPromo(rt,schedule,await Drop(schedule,campaign,"Cancelled"),"name",JsonValue.Create("Never live")!);
        var scheduled=await schedule.ScheduleAsync(drop.Id,drop.Version);
        Assert.Equal(409,(await Assert.ThrowsAsync<ContentChangeException>(()=>schedule.CancelAsync(drop.Id,drop.Version))).Status);
        var cancelled=await schedule.CancelAsync(drop.Id,scheduled.Version);
        Assert.Equal("cancelled",cancelled.Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<ContentChangeException>(()=>schedule.ScheduleAsync(drop.Id,cancelled.Version))).Status);
        await schedule.SaveCampaignAsync(campaign.Id,"Updated",null,campaign.Version);
        Assert.Equal(409,(await Assert.ThrowsAsync<ContentChangeException>(()=>schedule.SaveCampaignAsync(campaign.Id,"Stale",null,campaign.Version))).Status);
    }

    [Fact]
    public async Task EditingScheduledDropReturnsItToDraftAndInvalidatesItsReadiness()
    {
        using var rt=TestRuntime.Create(time:new TestRuntime.MovableTime(Instant),marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var drop=await EditPromo(rt,schedule,await Drop(schedule,campaign,"Edit queued"),"name",JsonValue.Create("First draft")!);
        drop=await schedule.ScheduleAsync(drop.Id,drop.Version);
        var edited=await EditPromo(rt,schedule,drop,"name",JsonValue.Create("Second draft")!);
        Assert.Equal("draft",edited.Status); Assert.True(edited.Version>drop.Version);
        Assert.Equal(409,(await Assert.ThrowsAsync<ContentChangeException>(()=>schedule.ValidateAsync(drop.Id,drop.Version))).Status);
    }

    [Fact]
    public async Task OverlappingObjectiveWindowsStayLiveUntilTheLastEndBoundary()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var first=await ObjectiveWindow(schedule,campaign,"First window",60,120);
        var second=await ObjectiveWindow(schedule,campaign,"Second window",90,180);
        clock.Now=Instant+60; await schedule.RunOnceAsync();
        Assert.True(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        clock.Now=Instant+90; await schedule.RunOnceAsync();
        clock.Now=Instant+120; await schedule.RunOnceAsync();
        Assert.Equal("ended",(await schedule.GetDropAsync(first.Id)).Status);
        Assert.Equal("live",(await schedule.GetDropAsync(second.Id)).Status);
        Assert.True(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        clock.Now=Instant+179; await schedule.RunOnceAsync();
        Assert.True(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        clock.Now=Instant+180; await schedule.RunOnceAsync();
        Assert.False(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        Assert.Equal("ended",(await schedule.GetDropAsync(second.Id)).Status);
    }

    [Fact]
    public async Task ExplicitAvailabilityReenablesAfterAGapEvenWhenStagedWhileAlreadyLive()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        Assert.True(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        var first=await ObjectiveWindow(schedule,campaign,"First appearance",60,120);
        var second=await ObjectiveWindow(schedule,campaign,"Returning appearance",180,240);
        var preview=await schedule.ProjectAsync(second.StartsAt);
        Assert.True(preview.Rules.Objectives.IsLive(2));
        clock.Now=Instant+60; await schedule.RunOnceAsync();
        clock.Now=Instant+120; await schedule.RunOnceAsync();
        Assert.False(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        Assert.Equal("ended",(await schedule.GetDropAsync(first.Id)).Status);
        clock.Now=Instant+180; await schedule.RunOnceAsync();
        Assert.True(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        Assert.Equal("live",(await schedule.GetDropAsync(second.Id)).Status);
    }

    [Fact]
    public async Task OverdueExpiredWindowDoesNotDisableAnotherActiveWindow()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var active=await ObjectiveWindow(schedule,campaign,"Long availability",60,300);
        var overdue=await ObjectiveWindow(schedule,campaign,"Missed short availability",90,120);
        clock.Now=Instant+60; await schedule.RunOnceAsync();
        Assert.Equal("live",(await schedule.GetDropAsync(active.Id)).Status);
        clock.Now=Instant+180; await schedule.RunOnceAsync();
        Assert.Equal("ended",(await schedule.GetDropAsync(overdue.Id)).Status);
        Assert.True(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        Assert.Equal("live",(await schedule.GetDropAsync(active.Id)).Status);
        clock.Now=Instant+300; await schedule.RunOnceAsync();
        Assert.False(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
    }

    [Fact]
    public async Task EndNowPreservesOtherLiveWindowsAndRejectsStaleVersions()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var first=await ObjectiveWindow(schedule,campaign,"First active",60,300);
        var second=await ObjectiveWindow(schedule,campaign,"Second active",90,400);
        clock.Now=Instant+90; await schedule.RunOnceAsync();
        var firstLive=await schedule.GetDropAsync(first.Id); var secondLive=await schedule.GetDropAsync(second.Id);
        Assert.Equal(409,(await Assert.ThrowsAsync<ContentChangeException>(()=>schedule.EndAsync(first.Id,first.Version))).Status);
        var ended=await schedule.EndAsync(first.Id,firstLive.Version);
        Assert.Equal("ended",ended.Status); Assert.True(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        Assert.Equal("live",(await schedule.GetDropAsync(second.Id)).Status);
        await schedule.EndAsync(second.Id,secondLive.Version);
        Assert.False(rt.Game.Live.Current.Rules.Objectives.IsLive(2));
        Assert.Equal(409,(await Assert.ThrowsAsync<ContentChangeException>(()=>schedule.EndAsync(second.Id,secondLive.Version))).Status);
    }

    [Fact]
    public async Task NativePromoEventItemEndOverridesPreviewAndPublishedWindowThenExpiresNaturally()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var drop=await Drop(schedule,campaign,"Timed promo",60,DateTimeOffset.FromUnixTimeSeconds(Instant+300));
        var draft=schedule.Draft(drop.ChangesetId);
        await draft.SetChangeAsync("promo",new JsonObject{["code"]="synthetic_native_window"},ContentOperation.Insert,SyntheticPromo(),null);
        await draft.SetChangeAsync("promo_event",new JsonObject{["code"]="synthetic_item_end"},ContentOperation.Insert,
            new JsonObject{["name"]="Synthetic timed promo",["enabled"]=true,["starts_at"]=drop.StartsAt.ToString("O"),
                ["ends_at"]=DateTimeOffset.FromUnixTimeSeconds(Instant+240).ToString("O"),["promos"]=new JsonArray("synthetic_native_window")},null);
        var change=(await draft.OpenAsync())!.Changes.Single(c=>c.Area=="promo_event");
        drop=await schedule.GetDropAsync(drop.Id);
        var itemEnd=DateTimeOffset.FromUnixTimeSeconds(Instant+120);
        drop=await schedule.SetItemEndAsync(drop.Id,change.Id,drop.Version,itemEnd);
        drop=await schedule.ScheduleAsync(drop.Id,drop.Version);
        var preview=await schedule.ProjectAsync(drop.StartsAt);
        Assert.Equal(itemEnd.UtcDateTime,preview.Catalog.PromoEvents.Single(e=>e.Code=="synthetic_item_end").EndsAt);
        var promoId=preview.Catalog.Promos.Single(p=>p.Code=="synthetic_native_window").PromoId;
        Assert.Contains(promoId,preview.PromoAvailability.ActivePromoIds(drop.StartsAt));
        clock.Now=Instant+60; await schedule.RunOnceAsync();
        Assert.Equal(itemEnd.UtcDateTime,rt.Game.Live.Current.Catalog.PromoEvents.Single(e=>e.Code=="synthetic_item_end").EndsAt);
        clock.Now=Instant+120; await schedule.RunOnceAsync();
        Assert.Equal("ended",(await schedule.GetDropAsync(drop.Id)).Status);
        Assert.DoesNotContain(promoId,rt.Game.Live.Current.PromoAvailability.ActivePromoIds(itemEnd));
    }

    [Fact]
    public async Task GeneratedPromoWindowMovesWithItsDropAndUsesTheUpdatedCampaignEnd()
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt);
        var campaign=await schedule.SaveCampaignAsync(null,"Synthetic movable campaign",DateTimeOffset.FromUnixTimeSeconds(Instant+300),0);
        var originalStart=DateTimeOffset.FromUnixTimeSeconds(Instant+60);
        var drop=await schedule.SaveDropAsync(null,campaign.Id,"Move this promo",originalStart,null,true,0);
        var draft=schedule.Draft(drop.ChangesetId);
        await draft.SetChangeAsync("promo",new JsonObject{["code"]="synthetic_movable_promo"},ContentOperation.Insert,SyntheticPromo(),null);
        var eventCode=$"drop_{drop.Id}_synthetic_movable_promo";
        await draft.SetChangeAsync("promo_event",new JsonObject{["code"]=eventCode},ContentOperation.Insert,
            new JsonObject{["name"]="Synthetic attached promo",["enabled"]=true,["starts_at"]=originalStart.ToString("O"),
                ["ends_at"]=campaign.EndsAt!.Value.ToString("O"),["promos"]=new JsonArray("synthetic_movable_promo")},null);
        drop=await schedule.GetDropAsync(drop.Id); drop=await schedule.ScheduleAsync(drop.Id,drop.Version);
        var newEnd=DateTimeOffset.FromUnixTimeSeconds(Instant+400);
        campaign=await schedule.SaveCampaignAsync(campaign.Id,campaign.Name,newEnd,campaign.Version);
        drop=await schedule.GetDropAsync(drop.Id);
        Assert.Equal("draft",drop.Status);
        var newStart=DateTimeOffset.FromUnixTimeSeconds(Instant+120);
        drop=await schedule.SaveDropAsync(drop.Id,campaign.Id,drop.Name,newStart,null,true,drop.Version);
        var validation=await schedule.ValidateAsync(drop.Id,drop.Version);
        Assert.True(validation.Ok,string.Join(";",validation.Errors));
        drop=await schedule.ScheduleAsync(drop.Id,drop.Version);
        var before=await schedule.ProjectAsync(originalStart);
        Assert.DoesNotContain(before.Catalog.PromoEvents,e=>e.Code==eventCode);
        var preview=await schedule.ProjectAsync(newStart);
        var projected=preview.Catalog.PromoEvents.Single(e=>e.Code==eventCode);
        Assert.Equal(newStart.UtcDateTime,projected.StartsAt); Assert.Equal(newEnd.UtcDateTime,projected.EndsAt);
        clock.Now=Instant+120; await schedule.RunOnceAsync();
        var published=rt.Game.Live.Current.Catalog.PromoEvents.Single(e=>e.Code==eventCode);
        Assert.Equal(newStart.UtcDateTime,published.StartsAt); Assert.Equal(newEnd.UtcDateTime,published.EndsAt);
        var promoId=rt.Game.Live.Current.Catalog.Promos.Single(p=>p.Code=="synthetic_movable_promo").PromoId;
        Assert.DoesNotContain(promoId,rt.Game.Live.Current.PromoAvailability.ActivePromoIds(originalStart));
        Assert.Contains(promoId,rt.Game.Live.Current.PromoAvailability.ActivePromoIds(newStart));
    }

    [Fact]
    public async Task CancellingNewPromoInvalidatesLaterEventThatReferencesIt()
    {
        using var rt=TestRuntime.Create(time:new TestRuntime.MovableTime(Instant),marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        var source=await Drop(schedule,campaign,"New promo",60);
        await schedule.Draft(source.ChangesetId).SetChangeAsync("promo",new JsonObject{["code"]="synthetic_schedule_promo"},ContentOperation.Insert,
            SyntheticPromo(),null);
        source=await schedule.GetDropAsync(source.Id); source=await schedule.ScheduleAsync(source.Id,source.Version);
        var dependent=await Drop(schedule,campaign,"New promo event",120);
        await schedule.Draft(dependent.ChangesetId).SetChangeAsync("promo_event",new JsonObject{["code"]="synthetic_reference_event"},ContentOperation.Insert,
            new JsonObject{["name"]="Synthetic referencing event",["enabled"]=true,["starts_at"]=dependent.StartsAt.ToString("O"),
                ["ends_at"]=DateTimeOffset.FromUnixTimeSeconds(Instant+240).ToString("O"),["promos"]=new JsonArray("synthetic_schedule_promo")},null);
        dependent=await schedule.GetDropAsync(dependent.Id); dependent=await schedule.ScheduleAsync(dependent.Id,dependent.Version);
        await schedule.CancelAsync(source.Id,source.Version);
        var invalidated=await schedule.GetDropAsync(dependent.Id);
        Assert.Equal("draft",invalidated.Status); Assert.True(invalidated.Version>dependent.Version); Assert.NotNull(invalidated.Error);
        var validation=await schedule.ValidateAsync(invalidated.Id,invalidated.Version);
        Assert.False(validation.Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartCatchupOfExpiredObjectiveDoesNotCreateItsStartOrBaseline(bool implicitLive)
    {
        var clock=new TestRuntime.MovableTime(Instant);
        using var rt=TestRuntime.Create(time:clock,marketTarget:0);
        var schedule=Schedule(rt); var campaign=await Campaign(schedule);
        if(implicitLive)
        {
            var immediate=new ContentChangesets(rt.Game,ContentAreas.Default);
            var fixtureKey=new JsonObject{["file"]="objectives",["path"]=new JsonArray("grindGroups")};
            var fixtureGroups=(await rt.Game.UnitOfWork.RunReadAsync(0,w=>new RulesArea().ReadAsync(w,fixtureKey)))!;
            fixtureGroups["value"]=new JsonArray(fixtureGroups["value"]!.AsArray().OfType<JsonObject>()
                .Where(g=>g["objectives"]!.AsArray().OfType<JsonObject>().All(o=>o["requirements"] is null)).Select(g=>(JsonNode?)g.DeepClone()).ToArray());
            await immediate.SetChangeAsync("rules",fixtureKey,ContentOperation.Update,fixtureGroups,null);
            await immediate.SetChangeAsync("rules",new JsonObject{["file"]="objectives",["path"]=new JsonArray("live")},ContentOperation.Update,
                new JsonObject{["value"]=null},null);
            await immediate.PublishAsync("Synthetic implicit live list",null);
            Assert.Null(rt.Game.Live.Current.Rules.Objectives.Live);
        }
        var previouslyLive=rt.Game.Live.Current.Rules.Objectives.LiveGroupIds.ToArray();
        var rules=new RulesArea(); var groupsKey=new JsonObject{["file"]="objectives",["path"]=new JsonArray("grindGroups")};
        var groups=(await rt.Game.UnitOfWork.RunReadAsync(0,w=>rules.ReadAsync(w,groupsKey)))!;
        var synthetic=groups["value"]![0]!.DeepClone().AsObject();
        synthetic["id"]=99001; synthetic["title"]="Synthetic expired objective";
        foreach(var objective in synthetic["objectives"]!.AsArray().OfType<JsonObject>()) objective["id"]=objective["id"]!.GetValue<long>()+990000;
        groups["value"]!.AsArray().Add(synthetic);
        var drop=await Drop(schedule,campaign,"Expired objective",60,DateTimeOffset.FromUnixTimeSeconds(Instant+120));
        var draft=schedule.Draft(drop.ChangesetId);
        await draft.SetChangeAsync("rules",groupsKey,ContentOperation.Update,groups,null);
        await draft.SetChangeAsync("content_window",new JsonObject{["kind"]="objective",["target"]="99001"},ContentOperation.Update,new JsonObject{["enabled"]=true},null);
        drop=await schedule.GetDropAsync(drop.Id); drop=await schedule.ScheduleAsync(drop.Id,drop.Version);
        clock.Now=Instant+180;
        var connection=rt.Detach();
        using var restarted=TestRuntime.Create(time:clock,marketTarget:0,connectionString:connection);
        var restartedSchedule=Schedule(restarted);
        await restartedSchedule.RunOnceAsync();
        Assert.Equal("ended",(await restartedSchedule.GetDropAsync(drop.Id)).Status);
        Assert.False(restarted.Game.Live.Current.Rules.Objectives.IsLive(99001));
        Assert.All(previouslyLive,group=>Assert.True(restarted.Game.Live.Current.Rules.Objectives.IsLive(group),$"Existing group {group} lost availability."));
        var starts=await restarted.Game.UnitOfWork.RunReadAsync(0,w=>w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.objective_group_starts WHERE group_id=99001"));
        var baselines=await restarted.Game.UnitOfWork.RunReadAsync(0,w=>w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.objective_baselines WHERE group_id=99001"));
        Assert.Equal(0,starts); Assert.Equal(0,baselines);
    }

    private sealed class UnusedStoreArea:IContentArea
    {
        public string Name=>"unused_store";
        public int ApplyOrder=>100;
        public JsonObject NormalizeKey(JsonObject key)=>key;
        public ValueTask<JsonObject?> ReadAsync(IUnitOfWork work,JsonObject key)=>throw new InvalidOperationException("This fixture does not exercise store availability.");
        public ValueTask ValidateAsync(IUnitOfWork work,ContentOperation operation,JsonObject key,JsonObject? after)=>throw new InvalidOperationException();
        public ValueTask ApplyAsync(IUnitOfWork work,ContentOperation operation,JsonObject key,JsonObject? after)=>throw new InvalidOperationException();
    }
}
