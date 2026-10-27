using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Server.Panel;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task ScheduleAdditionPreservesOrdinaryContentEndpointsWithoutDropQuery()
    {
        using var setup=await CreateAsync();
        foreach(var path in new[]{"/panel/api/content","/panel/api/content/status","/panel/api/content/changeset","/panel/api/content/rules/objectives?limit=3"})
        {
            var response=await Send(setup.Panel,"GET",path);
            Assert.True(response.Status==200,$"{path}: {response.Status} {response.Body}");
        }
        var row=await setup.Runtime.Game.UnitOfWork.RunReadAsync(0,w=>new PromoArea().ReadAsync(w,new JsonObject{["code"]="gold_common"}));
        row!["name"]="Synthetic immediate changes";
        var staged=await Send(setup.Panel,"PUT","/panel/api/content/changeset/changes",new JsonObject{
            ["area"]="promo",["key"]=new JsonObject{["code"]="gold_common"},["operation"]="update",["after"]=row}.ToJsonString());
        Assert.True(staged.Status==200,staged.Body); Assert.Equal(1,staged.Json!["pending"]!.GetValue<int>());
        var validate=await Send(setup.Panel,"POST","/panel/api/content/changeset/validate","{}");
        Assert.True(validate.Status==200,validate.Body); Assert.True(validate.Json!["ok"]!.GetValue<bool>());
        Assert.Equal(400,(await Send(setup.Panel,"GET","/panel/api/content/changeset?dropId=bad")).Status);
    }

    [Fact]
    public async Task ScheduleApiCampaignDropsScopedChangesAndPreviewUseCamelCaseAndRemainIsolated()
    {
        using var setup=await CreateAsync();
        async Task<JsonObject> Ok(string method,string path,JsonObject? payload=null)
        {
            var response=await Send(setup.Panel,method,path,payload?.ToJsonString());
            Assert.True(response.Status==200,$"{method} {path}: {response.Status} {response.Body}");
            return response.Json!.AsObject();
        }
        var campaign=await Ok("POST","/panel/api/content/campaigns",new JsonObject{["name"]="Synthetic promo"});
        Assert.NotNull(campaign["id"]); Assert.NotNull(campaign["version"]); Assert.Null(campaign["Id"]);
        var start=setup.Runtime.Game.Time.GetUtcNow().AddHours(1);
        var drop=await Ok("POST","/panel/api/content/drops",new JsonObject{
            ["campaignId"]=campaign["id"]!.DeepClone(),["name"]="Synthetic team",["startsAt"]=start.ToString("O"),["inheritEnd"]=false});
        Assert.Equal("draft",drop["status"]!.GetValue<string>());
        Assert.NotNull(drop["campaignId"]); Assert.NotNull(drop["changesetId"]); Assert.NotNull(drop["startsAt"]);
        Assert.Null(drop["CampaignId"]);
        var dropId=drop["id"]!.GetValue<long>();
        var key=new JsonObject{["code"]="gold_rare"};
        var row=(await setup.Runtime.Game.UnitOfWork.RunReadAsync(0,w=>new PromoArea().ReadAsync(w,key)))!;
        row["name"]="Scheduled through API";
        var staged=await Ok("PUT",$"/panel/api/content/changeset/changes?dropId={dropId}",new JsonObject{
            ["area"]="promo",["key"]=key,["operation"]="update",["after"]=row});
        Assert.Equal(drop["changesetId"]!.GetValue<long>(),staged["changeset"]!["id"]!.GetValue<long>());
        Assert.Equal(1,staged["pending"]!.GetValue<int>());
        var ordinary=await Ok("GET","/panel/api/content/changeset");
        Assert.Equal(0,ordinary["pending"]!.GetValue<int>()); Assert.False(ordinary["open"]!.GetValue<bool>());
        var overview=await Ok("GET","/panel/api/content/schedule");
        var selected=Assert.Single(overview["drops"]!.AsArray())!.AsObject();
        Assert.Equal(1,selected["changes"]!.GetValue<int>());
        Assert.Single(selected["items"]!.AsArray());
        var version=selected["version"]!.GetValue<long>();
        var validation=await Ok("POST",$"/panel/api/content/drops/{dropId}/validate",new JsonObject{["version"]=version});
        Assert.True(validation["ok"]!.GetValue<bool>());
        var scheduled=await Ok("POST",$"/panel/api/content/drops/{dropId}/schedule",new JsonObject{["version"]=version});
        Assert.Equal("scheduled",scheduled["status"]!.GetValue<string>());
        var preview=await Ok("GET","/panel/api/content/schedule/preview?at="+Uri.EscapeDataString(start.ToString("O")));
        var projected=preview["promos"]!.AsArray().Single(p=>p!["code"]!.GetValue<string>()=="gold_rare")!;
        Assert.Equal("Scheduled through API",projected["name"]!.GetValue<string>());
        var current=await setup.Runtime.Game.UnitOfWork.RunReadAsync(0,w=>new PromoArea().ReadAsync(w,new JsonObject{["code"]="gold_rare"}));
        Assert.NotEqual("Scheduled through API",current!["name"]!.GetValue<string>());
        var stale=await Send(setup.Panel,"POST",$"/panel/api/content/drops/{dropId}/cancel",new JsonObject{["version"]=version}.ToJsonString());
        Assert.Equal(409,stale.Status);
        var cancelled=await Ok("POST",$"/panel/api/content/drops/{dropId}/cancel",new JsonObject{["version"]=scheduled["version"]!.DeepClone()});
        Assert.Equal("cancelled",cancelled["status"]!.GetValue<string>());
        var closed=await Ok("GET",$"/panel/api/content/changeset?dropId={dropId}");
        Assert.False(closed["open"]!.GetValue<bool>()); Assert.Equal(0,closed["pending"]!.GetValue<int>());
    }

    [Fact]
    public async Task ScheduleScopedOddsAndDistributionUseSelectedDraftWithoutPublishing()
    {
        using var setup=await CreateAsync();
        var schedule=OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start=setup.Runtime.Game.Time.GetUtcNow().AddHours(1);
        var drop=await schedule.SaveDropAsync(null,null,"Synthetic scoped analytics",start,null,false,0);
        var promo=(await setup.Runtime.Game.UnitOfWork.RunReadAsync(0,w=>new PromoArea().ReadAsync(w,new JsonObject{["code"]="gold_rare"})))!;
        foreach(var bucket in promo["buckets"]!.AsArray().OfType<JsonObject>())
            bucket["draw_weight"]=bucket["code"]!.GetValue<string>()=="rating_90_99"?10000m:0.001m;
        var staged=await Send(setup.Panel,"PUT",$"/panel/api/content/changeset/changes?dropId={drop.Id}",new JsonObject{
            ["area"]="promo",["key"]=new JsonObject{["code"]="gold_rare"},["operation"]="update",["after"]=promo}.ToJsonString());
        Assert.True(staged.Status==200,staged.Body);
        var distribution=await Send(setup.Panel,"POST",$"/panel/api/promos/distribution?dropId={drop.Id}",new JsonObject{
            ["category"]="gold_rare",["pending"]=true,["limit"]=10,["at"]=start.ToString("O")}.ToJsonString());
        Assert.True(distribution.Status==200,distribution.Body);
        Assert.Equal(2,distribution.Json!["revision"]!.GetValue<long>());
        Assert.NotEmpty(distribution.Json["cards"]!.AsArray());
        Assert.All(distribution.Json["cards"]!.AsArray(),card=>Assert.True(setup.Runtime.Game.Players.Find(card!["resourceId"]!.GetValue<long>())!.Rating>=90));
        var ordinary=await Send(setup.Panel,"POST","/panel/api/promos/distribution","{\"category\":\"gold_rare\",\"pending\":true,\"limit\":10}");
        Assert.True(ordinary.Status==200,ordinary.Body); Assert.Equal(1,ordinary.Json!["revision"]!.GetValue<long>());
        var odds=await Send(setup.Panel,"POST",$"/panel/api/packs/odds?dropId={drop.Id}",new JsonObject{
            ["packId"]=400,["samples"]=10,["seed"]="synthetic-scheduled-odds",["at"]=start.ToString("O"),["pending"]=true}.ToJsonString());
        Assert.True(odds.Status==200,odds.Body); Assert.Equal(2,odds.Json!["revision"]!.GetValue<long>());
        Assert.Equal(10,odds.Json["metrics"]!.AsArray().Single(m=>m!["code"]!.GetValue<string>()=="rare_88_plus")!["hits"]!.GetValue<int>());
        Assert.False(odds.Json["changedClubState"]!.GetValue<bool>());
        Assert.Equal(1,setup.Runtime.Game.Live.Current.Revision);
        var open=await Send(setup.Panel,"GET","/panel/api/content/changeset");
        Assert.False(open.Json!["open"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ScheduleStandaloneDropsCreateReadAndStageWithoutCampaign()
    {
        using var setup=await CreateAsync();
        var response=await Send(setup.Panel,"POST","/panel/api/content/drops",new JsonObject{
            ["campaignId"]=null,["name"]="Synthetic standalone drop",["startsAt"]=setup.Runtime.Game.Time.GetUtcNow().AddHours(1).ToString("O"),
            ["inheritEnd"]=false}.ToJsonString());
        Assert.True(response.Status==200,response.Body);
        var created=response.Json!.AsObject();
        Assert.True(created.ContainsKey("campaignId")); Assert.Null(created["campaignId"]);
        Assert.Equal("draft",created["status"]!.GetValue<string>());
        var id=created["id"]!.GetValue<long>();
        var key=new JsonObject{["code"]="gold_rare"};
        var promo=(await setup.Runtime.Game.UnitOfWork.RunReadAsync(0,w=>new PromoArea().ReadAsync(w,key)))!;
        promo["name"]="Synthetic standalone content";
        var staged=await Send(setup.Panel,"PUT",$"/panel/api/content/changeset/changes?dropId={id}",new JsonObject{
            ["area"]="promo",["key"]=key,["operation"]="update",["after"]=promo}.ToJsonString());
        Assert.True(staged.Status==200,staged.Body);
        var overview=await Send(setup.Panel,"GET","/panel/api/content/schedule");
        Assert.True(overview.Status==200,overview.Body);
        Assert.Empty(overview.Json!["campaigns"]!.AsArray());
        var drop=Assert.Single(overview.Json["drops"]!.AsArray())!;
        Assert.Null(drop["campaignId"]); Assert.Equal(1,drop["changes"]!.GetValue<int>());
        var scheduled=await Send(setup.Panel,"POST",$"/panel/api/content/drops/{id}/schedule",new JsonObject{["version"]=drop["version"]!.DeepClone()}.ToJsonString());
        Assert.True(scheduled.Status==200,scheduled.Body); Assert.Equal("scheduled",scheduled.Json!["status"]!.GetValue<string>());
        Assert.Null(scheduled.Json["campaignId"]);
    }

    [Fact]
    public async Task SchedulePackEditorAndLineupRoutesStageOnlySelectedDropAndReserveUniqueIds()
    {
        using var setup=await CreateAsync();
        async Task<JsonObject> Request(int status,string method,string path,JsonObject? body=null)
        {
            var response=await Send(setup.Panel,method,path,body?.ToJsonString());
            Assert.True(response.Status==status,$"{method} {path}: {response.Status} {response.Body}");
            return response.Json!.AsObject();
        }
        var schedule=OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start=setup.Runtime.Game.Time.GetUtcNow().AddHours(1);
        var campaign=await schedule.SaveCampaignAsync(null,"Synthetic pack campaign",start.AddDays(3),0,startsAt:start);
        var first=await schedule.SaveDropAsync(null,campaign.Id,"Pack drop one",start,start.AddDays(1),false,0);
        var second=await schedule.SaveDropAsync(null,campaign.Id,"Pack drop two",start.AddHours(1),start.AddDays(2),false,0);
        var catalog=await Request(200,"GET","/panel/api/packs/catalog");
        var revision=catalog["revision"]!.GetValue<string>();
        var existing=catalog["packs"]!.AsArray().First(p=>p!["viewOnlyReason"] is null)!;
        var existingId=existing["id"]!.GetValue<long>();
        await Request(200,"PUT",$"/panel/api/packs/catalog/{existingId}?dropId={first.Id}",new JsonObject{
            ["revision"]=revision,["pack"]=new JsonObject{["name"]="Synthetic scheduled name"}});
        async Task<long> NewPack(long dropId,string name)
        {
            var created=await Request(201,"POST",$"/panel/api/packs/catalog?dropId={dropId}",new JsonObject{
                ["revision"]=revision,["pack"]=new JsonObject{["name"]=name,["size"]=3,["gold_weight"]=100,["rare_slots"]=1}});
            return created["id"]!.GetValue<long>();
        }
        var firstId=await NewPack(first.Id,"Synthetic first new pack");
        var secondId=await NewPack(second.Id,"Synthetic second new pack");
        Assert.NotEqual(firstId,secondId);
        var listing=catalog["listing"]!.DeepClone().AsArray();
        listing.Add(new JsonObject{["packId"]=firstId,["price"]=100,["enabled"]=true,["untradeable"]=false});
        await Request(200,"PUT",$"/panel/api/packs/lineup?dropId={first.Id}",new JsonObject{["revision"]=revision,["listing"]=listing});
        var selected=await Request(200,"GET",$"/panel/api/content/changeset?dropId={first.Id}");
        Assert.Equal(3,selected["pending"]!.GetValue<int>());
        var changes=selected["changeset"]!["changes"]!.AsArray();
        Assert.Equal("Synthetic scheduled name",changes.Single(c=>c!["area"]!.GetValue<string>()=="store_pack" && c["key"]!["packId"]!.GetValue<long>()==existingId)!["after"]!["pack"]!["name"]!.GetValue<string>());
        Assert.Contains(changes,c=>c!["area"]!.GetValue<string>()=="store_pack" && c["key"]!["packId"]!.GetValue<long>()==firstId && c["operation"]!.GetValue<string>()=="insert");
        Assert.Contains(changes.Single(c=>c!["area"]!.GetValue<string>()=="store_lineup")!["after"]!["listing"]!.AsArray(),p=>p!["packId"]!.GetValue<long>()==firstId);
        var other=await Request(200,"GET",$"/panel/api/content/changeset?dropId={second.Id}");
        Assert.Equal(1,other["pending"]!.GetValue<int>());
        Assert.Equal(0,(await Request(200,"GET","/panel/api/content/changeset"))["pending"]!.GetValue<int>());
        var projected=await Request(200,"GET",$"/panel/api/packs/catalog?dropId={first.Id}");
        Assert.Equal(revision,projected["revision"]!.GetValue<string>());
        Assert.Equal(existing["config"]!["name"]!.GetValue<string>(),projected["packs"]!.AsArray().Single(p=>p!["id"]!.GetValue<long>()==existingId)!["config"]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(projected["packs"]!.AsArray(),p=>p!["id"]!.GetValue<long>()==firstId);
        Assert.DoesNotContain(projected["packs"]!.AsArray(),p=>p!["id"]!.GetValue<long>()==secondId);
        first=await schedule.GetDropAsync(first.Id);
        await schedule.ScheduleAsync(first.Id,first.Version);
        var laterBaseline=await Request(200,"GET",$"/panel/api/packs/catalog?dropId={second.Id}");
        Assert.Equal(revision,laterBaseline["revision"]!.GetValue<string>());
        Assert.Equal("Synthetic scheduled name",laterBaseline["packs"]!.AsArray().Single(p=>p!["id"]!.GetValue<long>()==existingId)!["config"]!["name"]!.GetValue<string>());
        Assert.Contains(laterBaseline["packs"]!.AsArray(),p=>p!["id"]!.GetValue<long>()==firstId);
        Assert.DoesNotContain(laterBaseline["packs"]!.AsArray(),p=>p!["id"]!.GetValue<long>()==secondId);
        await Request(400,"POST",$"/panel/api/packs/catalog/{existingId}/delete?dropId={first.Id}",new JsonObject{["revision"]=revision});
        var unchanged=await Request(200,"GET","/panel/api/packs/catalog");
        Assert.Equal(revision,unchanged["revision"]!.GetValue<string>());
        Assert.DoesNotContain(unchanged["packs"]!.AsArray(),p=>p!["id"]!.GetValue<long>()==firstId || p["id"]!.GetValue<long>()==secondId);
        Assert.Equal(1,setup.Runtime.Game.Live.Current.Revision);
    }

    [Fact]
    public async Task ScheduleApiEndRequiresCurrentVersionAndRemovesLiveAvailability()
    {
        using var setup=await CreateAsync();
        var schedule=OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var campaign=await schedule.SaveCampaignAsync(null,"Synthetic end campaign",null,0);
        var now=setup.Runtime.Game.Time.GetUtcNow();
        var drop=await schedule.SaveDropAsync(null,campaign.Id,"Synthetic active objective",now.AddMinutes(-1),now.AddHours(1),false,0);
        await schedule.Draft(drop.ChangesetId).SetChangeAsync("content_window",new JsonObject{["kind"]="objective",["target"]="2"},
            ContentOperation.Update,new JsonObject{["enabled"]=true},null);
        drop=await schedule.GetDropAsync(drop.Id);
        await schedule.RunOnceAsync(publishNow:drop.Id,expectedVersion:drop.Version);
        var live=await schedule.GetDropAsync(drop.Id);
        var closed=await Send(setup.Panel,"GET",$"/panel/api/content/changeset?dropId={drop.Id}");
        Assert.True(closed.Status==200,closed.Body); Assert.False(closed.Json!["open"]!.GetValue<bool>());
        Assert.Equal(0,closed.Json["pending"]!.GetValue<int>());
        Assert.True(setup.Runtime.Game.Live.Current.Rules.Objectives.IsLive(2));
        var stale=await Send(setup.Panel,"POST",$"/panel/api/content/drops/{drop.Id}/end",new JsonObject{["version"]=drop.Version}.ToJsonString());
        Assert.Equal(409,stale.Status);
        var end=await Send(setup.Panel,"POST",$"/panel/api/content/drops/{drop.Id}/end",new JsonObject{["version"]=live.Version}.ToJsonString());
        Assert.True(end.Status==200,end.Body); Assert.Equal("ended",end.Json!["status"]!.GetValue<string>());
        Assert.False(setup.Runtime.Game.Live.Current.Rules.Objectives.IsLive(2));
        Assert.Equal(409,(await Send(setup.Panel,"POST",$"/panel/api/content/drops/{drop.Id}/end",new JsonObject{["version"]=live.Version}.ToJsonString())).Status);
    }

    [Fact]
    public async Task ScheduleApiRejectsOffsetlessDatesAndUnknownScopedDrafts()
    {
        using var setup=await CreateAsync();
        var campaign=await Send(setup.Panel,"POST","/panel/api/content/campaigns","{\"name\":\"Synthetic\"}");
        Assert.Equal(200,campaign.Status);
        var invalid=await Send(setup.Panel,"POST","/panel/api/content/drops",new JsonObject{
            ["campaignId"]=campaign.Json!["id"]!.DeepClone(),["name"]="Ambiguous time",["startsAt"]="2027-07-01T19:00:00"}.ToJsonString());
        Assert.Equal(400,invalid.Status);
        Assert.Equal(404,(await Send(setup.Panel,"GET","/panel/api/content/changeset?dropId=999999999")).Status);
    }
}
