using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Club;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalMaintenanceTests
{
    private static TestRuntime Create(Action<string>? observer=null,string? connection=null)
    {
        if(connection is null){connection=PostgresTestCluster.CreateInstalledDatabase();using var source=NpgsqlDataSource.Create(connection);RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();}
        return TestRuntime.Create(connectionString:connection,relationalSlice:true,time:new TestRuntime.FixedTime(1700000000),commandObserver:observer);
    }
    private static Task<T> Read<T>(TestRuntime rt,Func<UnitOfWork,ValueTask<T>> action)=>rt.Game.ReadAsync(action,CancellationToken.None);
    private static Task<long> Scalar(TestRuntime rt,string sql)=>Read(rt,w=>w.ScalarAsync<long>(sql));
    // A club that has never been set up (its flag cleared): the only case starter setup runs for.
    private static Task ClearInitialized(TestRuntime rt)=>rt.Game.RunAsync("fixture-uninitialize",async w=>{await w.LockClubAsync();await w.ExecuteAsync("DELETE FROM ultimateteam.club_flags WHERE club_id=@club AND flag='relational_club_initialized_v1'");},CancellationToken.None);

    [Fact]
    public async Task AnInitializedClubIsNotSetUpAgainAfterACatalogueChangeAndRestart()
    {
        using var rt=Create();await FutServices.CreateAsync(rt.Game);
        var items=(await Read(rt,w=>w.ListItemsAsync())).Select(i=>i.ToJsonString()).ToList();
        // A catalogue change used to rescan every club at its next open. The catalogue is insert-only (its invariant
        // triggers refuse UPDATE and DELETE), so the change is a new card: a copy of base card 234378 under a new id.
        await rt.Game.RunAsync("fixture-new-card",w=>w.ExecuteAsync("""
            SET CONSTRAINTS ALL DEFERRED;
            CREATE TEMP TABLE new_card ON COMMIT DROP AS
            SELECT b.definition_id AS base,(SELECT max(definition_id) FROM ultimateteam.item_definitions)+1 AS id
            FROM ultimateteam.item_definitions b WHERE b.family_id=1 AND b.native_resource_id=234378;
            INSERT INTO ultimateteam.item_definitions
            SELECT (jsonb_populate_record(NULL::ultimateteam.item_definitions,to_jsonb(d)||jsonb_build_object('definition_id',n.id,
                'native_resource_id',184783754,'native_asset_id',234378,'catalog_order',(SELECT max(catalog_order)+1 FROM ultimateteam.item_definitions)))).*
            FROM new_card n JOIN ultimateteam.item_definitions d ON d.definition_id=n.base;
            INSERT INTO ultimateteam.player_card_definitions
            SELECT (jsonb_populate_record(NULL::ultimateteam.player_card_definitions,to_jsonb(p)||jsonb_build_object('definition_id',n.id,'rating',98))).*
            FROM new_card n JOIN ultimateteam.player_card_definitions p ON p.definition_id=n.base;
            """),CancellationToken.None);
        var writes=0;var connection=rt.Detach();
        using var reopened=Create(sql=>{if(sql.Contains("INSERT INTO ultimateteam.club_flags",StringComparison.Ordinal)||sql.StartsWith("INSERT INTO ultimateteam.items(",StringComparison.Ordinal))Interlocked.Increment(ref writes);},connection);
        await FutServices.CreateAsync(reopened.Game);
        Assert.Equal(0,writes);Assert.Equal(items,(await Read(reopened,w=>w.ListItemsAsync())).Select(i=>i.ToJsonString()));
    }

    [Fact]
    public async Task FullCompositionMaintainsOnceAndReopenKeepsAccountInventoryAndFlags()
    {
        var inserts=0;using var rt=Create(sql=>{if(sql.StartsWith("INSERT INTO ultimateteam.items(",StringComparison.Ordinal))Interlocked.Increment(ref inserts);});
        await FutServices.CreateAsync(rt.Game);var items=await Read(rt,w=>w.ListItemsAsync());var account=await Read(rt,w=>w.RelationalAccountAsync());
        Assert.Equal(1,inserts);Assert.Equal(1,await Scalar(rt,"SELECT count(*) FROM ultimateteam.club_flags WHERE club_id=@club"));
        Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.consumable_items"));
        Assert.Equal(rt.Game.Rules.Club.Squad.Starters,await Scalar(rt,"SELECT count(*) FROM ultimateteam.squad_players"));
        await FutServices.CreateAsync(rt.Game);Assert.Equal(1,inserts);Assert.Equal(account,await Read(rt,w=>w.RelationalAccountAsync()));
        var connection=rt.Detach();using var reopened=Create(connection:connection);await FutServices.CreateAsync(reopened.Game);
        Assert.Equal(account,await Read(reopened,w=>w.RelationalAccountAsync()));Assert.Equal(items.Select(i=>i.ToJsonString()),(await Read(reopened,w=>w.ListItemsAsync())).Select(i=>i.ToJsonString()));
        Assert.Equal(0,await Scalar(reopened,"SELECT count(*) FROM pg_tables WHERE schemaname='public'"));
    }

    [Fact]
    public async Task ReopeningLeavesVirtualKitsUsedConsumablesAndMutablePlayerStateAlone()
    {
        using var rt=Create();await FutServices.CreateAsync(rt.Game);
        var home=rt.Game.Rules.Club.RenderedDefaultActiveItems(rt.Game.ClubId).Single(i=>i.Text("itemState")=="activeHomeKit");
        var slot=rt.Game.Rules.Club.MyStadiumSlots.Select((s,i)=>(Slot:s,Id:s.SlotId??i+1)).Single(s=>s.Slot.ItemState=="activeHomeKit").Id;
        var player=(await Read(rt,w=>w.ListItemsAsync())).First(i=>i.Text("itemType")=="player");
        await rt.Game.RunAsync("fixture-state",async w=>{await w.LockClubAsync();await w.SetStadiumSlotBindingsAsync(new JsonObject{[slot.ToString()]=home.IntKeep("id")});
            player["contract"]=19;player["contracts"]=19;player["preferredPosition"]="LW";player["assists"]=23;await w.SaveItemAsync(player);
            await w.ExecuteAsync("DELETE FROM ultimateteam.items WHERE club_id=@club AND family_id=3");},CancellationToken.None);
        var bindings=await Read(rt,w=>w.StadiumSlotBindingsAsync());await new ClubMaintenanceService(rt.Fut).RunAsync();
        Assert.True(JsonNode.DeepEquals(bindings,await Read(rt,w=>w.StadiumSlotBindingsAsync())));Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.consumable_items"));
        var after=(await Read(rt,w=>w.GetItemAsync(player.IntKeep("id"))))!;Assert.Equal(19,after.Int("contract"));Assert.Equal("LW",after.Text("preferredPosition"));Assert.Equal(23,after.Int("assists"));
    }

    [Fact]
    public async Task InitialSetupRepairsActualActiveEmptySquadWithoutChoosingNonplayersOrDuplicateFootballers()
    {
        using var rt=Create();await FutServices.CreateAsync(rt.Game);
        await rt.Game.RunAsync("fixture-empty-active",async w=>{await w.LockClubAsync();await w.SaveSquadAsync(2,"Empty",false,new JsonObject{["id"]=2,["squadId"]=2,["name"]="Empty",["formation"]=rt.Game.Rules.Club.Squad.DefaultFormation,["players"]=new JsonArray()});await w.SetActiveSquadAsync(2);},CancellationToken.None);
        await ClearInitialized(rt);await new ClubMaintenanceService(rt.Fut).RunAsync();
        Assert.Equal(2,await Read(rt,w=>w.ActiveSquadIdAsync()));Assert.Equal(rt.Game.Rules.Club.Squad.Starters,await Scalar(rt,"SELECT count(*) FROM ultimateteam.squad_players p JOIN ultimateteam.squads s ON s.id=p.squad_id WHERE s.active"));
        Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.squad_players p JOIN ultimateteam.items i ON i.id=p.item_id WHERE i.family_id<>1"));
        var concept=rt.Game.Cards.ConceptItemId(rt.Game.Players.Entries.First().Key);
        await rt.Game.RunAsync("fixture-concept",w=>w.SaveSquadAsync(2,"Concept",true,new JsonObject{["id"]=2,["squadId"]=2,["name"]="Concept",["formation"]=rt.Game.Rules.Club.Squad.DefaultFormation,["players"]=new JsonArray(new JsonObject{["index"]=0,["itemId"]=concept})}),CancellationToken.None);
        var before=await Read(rt,w=>w.GetSquadAsync(2));await ClearInitialized(rt);await new ClubMaintenanceService(rt.Fut).RunAsync();Assert.True(JsonNode.DeepEquals(before,await Read(rt,w=>w.GetSquadAsync(2))));
    }

    [Fact]
    public async Task LateMaintenanceFailureAndConcurrentOpenDoNotLeavePartialGrants()
    {
        var fail=true;using var rt=Create(sql=>{if(fail&&sql.Contains("INSERT INTO ultimateteam.club_flags",StringComparison.Ordinal))throw new InvalidOperationException("synthetic maintenance failure");});
        var account=await Read(rt,w=>w.RelationalAccountAsync());await Assert.ThrowsAsync<InvalidOperationException>(()=>FutServices.CreateAsync(rt.Game));
        Assert.Equal(account,await Read(rt,w=>w.RelationalAccountAsync()));Assert.Empty(await Read(rt,w=>w.ListItemsAsync()));Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.club_flags"));fail=false;
        var second=Create(connection:rt.ConnectionString);try{await Task.WhenAll(Task.Run(()=>FutServices.CreateAsync(rt.Game)),Task.Run(()=>FutServices.CreateAsync(second.Game)));}finally{second.Detach();}
        Assert.Equal(1,await Scalar(rt,"SELECT count(*) FROM ultimateteam.club_flags"));Assert.Equal(1,await Scalar(rt,"SELECT count(*) FROM ultimateteam.squads"));
    }

    [Fact]
    public async Task LifecycleClearDeleteAndDeferredImportNeverTouchLegacyTables()
    {
        using var rt=Create();Assert.True(await Read(rt,w=>w.IsPristineAsync()));await FutServices.CreateAsync(rt.Game);Assert.False(await Read(rt,w=>w.IsPristineAsync()));
        var other=(await rt.Game.RunAsync("fixture-other",w=>w.EnsureClubAsync(new ClubIdentity(910081,910082),5000),CancellationToken.None)).ClubId;
        var player=(await Read(rt,w=>w.ListItemsAsync())).Last(i=>i.Text("itemType")=="player");var id=player.IntKeep("id");
        // This starter can be in a squad; use a verified extra definition as an unbound sale.
        await rt.Game.RunAsync("fixture-extra",async w=>{var item=rt.Fut.Items.BuildPlayer(rt.Game.Players.Entries.First().Key,await w.NextItemIdAsync(),"club");item["untradeable"]=false;await w.SaveItemAsync(item);id=item.IntKeep("id");},CancellationToken.None);
        var trade=(await rt.Fut.Market.ListAsync(id,150,1000,3600)).IntKeep("tradeId");var amount=(await Read(rt,async w=>(await w.MarketListingAsync(trade))!)).StartingBid;
        await rt.Game.UnitOfWork.RunAsync(other,"fixture-bid",async w=>{await w.LockMarketAsync();await w.MarketListingAsync(trade,true);await w.LockClubsAsync(new long?[]{other});await w.AddCreditsToAsync(other,-amount);await w.SetMarketBidAsync(trade,amount,other);},CancellationToken.None);
        await rt.Game.RunAsync("fixture-clear",w=>w.ClearClubAsync(1234),CancellationToken.None);
        Assert.True(await Read(rt,w=>w.IsPristineAsync()));Assert.Equal(1234,await Read(rt,w=>w.CreditsAsync()));Assert.Equal(5000,await rt.Game.UnitOfWork.RunReadAsync(other,w=>w.CreditsAsync(),CancellationToken.None));Assert.Empty(await Read(rt,w=>w.OwnMarketListingsAsync()));
        await rt.Game.RunAsync("fixture-delete",w=>w.DeleteClubAsync(),CancellationToken.None);Assert.Equal(1,await Scalar(rt,"SELECT count(*) FROM ultimateteam.clubs"));Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.market_listings"));
    }

    [Fact]
    public async Task LifecycleLateFailureRollsBackAndPopulatedDeletionKeepsOtherClub()
    {
        var fail=false;using var rt=Create(sql=>{if(fail&&sql.StartsWith("UPDATE ultimateteam.clubs SET credits=@credits",StringComparison.Ordinal))throw new InvalidOperationException("synthetic late clear failure");});
        const string tablesSql="SELECT tablename FROM pg_tables WHERE schemaname='ultimateteam' ORDER BY tablename";
        var tablesBefore=await Read(rt,w=>w.QueryAsync(tablesSql,r=>r.GetString(0)));
        await FutServices.CreateAsync(rt.Game);var account=await Read(rt,w=>w.RelationalAccountAsync());var items=await Read(rt,w=>w.ListItemsAsync());var squad=await Read(rt,w=>w.GetSquadAsync(1));
        fail=true;await Assert.ThrowsAsync<InvalidOperationException>(()=>rt.Game.RunAsync("fixture-failed-clear",w=>w.ClearClubAsync(1234),CancellationToken.None));fail=false;
        Assert.Equal(account,await Read(rt,w=>w.RelationalAccountAsync()));Assert.Equal(items.Select(i=>i.ToJsonString()),(await Read(rt,w=>w.ListItemsAsync())).Select(i=>i.ToJsonString()));Assert.True(JsonNode.DeepEquals(squad,await Read(rt,w=>w.GetSquadAsync(1))));
        var other=(await rt.Game.RunAsync("fixture-other",w=>w.EnsureClubAsync(new ClubIdentity(910091,910092),6789),CancellationToken.None)).ClubId;
        Assert.True(await rt.Game.RunAsync("fixture-populated-delete",w=>w.DeleteClubAsync(),CancellationToken.None));
        Assert.Equal(1,await Scalar(rt,"SELECT count(*) FROM ultimateteam.clubs"));Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.items"));Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.squad_players"));
        Assert.Equal(6789,await rt.Game.UnitOfWork.RunReadAsync(other,w=>w.CreditsAsync(),CancellationToken.None));
        // Clearing/deleting club data must preserve the complete installed schema.
        Assert.Equal(tablesBefore,await Read(rt,w=>w.QueryAsync(tablesSql,r=>r.GetString(0))));
    }

    [Fact]
    public async Task CoverageInventoryAccountsForEveryInstalledTableAndExistingOwnerFile()
    {
        using var rt=Create();var server=Path.GetFullPath(Path.Combine(TestRuntime.DataRoot,"..","..","server"));var coverage=JsonNode.Parse(File.ReadAllText(Path.Combine(server,"STORAGE-COVERAGE.json")))!.AsObject();
        var entries=coverage.Arr("tables")!.OfType<JsonObject>().ToArray();Assert.Equal(122,entries.Length);Assert.Equal(122,entries.Select(e=>e.Text("table")).Distinct().Count());
        var installed=await Read(rt,w=>w.QueryAsync("SELECT tablename FROM pg_tables WHERE schemaname='ultimateteam' ORDER BY tablename",r=>r.GetString(0)));
        Assert.Equal(entries.Select(e=>e.Text("table")).Order(),installed);
        foreach(var entry in entries){Assert.True(File.Exists(Path.Combine(server,entry.Text("owner"))),entry.Text("owner"));Assert.False(string.IsNullOrWhiteSpace(entry.Text("status")));}
    }
}
