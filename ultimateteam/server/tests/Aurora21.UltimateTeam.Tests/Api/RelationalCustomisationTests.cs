using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalCustomisationTests
{
    /// <summary>Santiago Bernabeu, the stadium the retired pack 12 set (migration 041; removed from the store by 097).</summary>
    private const long SantiagoBernabeu = 2;

    private const string G="/ut/game/fifa21";
    private static TestRuntime Create(Action<string>? observer=null,string? seedSql=null)
    {
        var connection=PostgresTestCluster.CreateInstalledDatabase();
        using(var source=NpgsqlDataSource.Create(connection))
        {
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
            // Pack configuration is read into a snapshot at start-up, so a fixture pack has to exist
            // before the runtime loads it.
            if(seedSql is not null)
            {
                using var command=source.CreateCommand(seedSql);
                command.ExecuteNonQuery();
            }
        }
        return TestRuntime.Create(connectionString:connection,relationalSlice:true,time:new TestRuntime.FixedTime(1700000000),commandObserver:observer);
    }
    private static JsonObject Kit(TestRuntime rt,long id,int index=0)
    {
        var k=rt.Game.Kits.Kits.ElementAt(index);
        return ItemFactory.KitItem(id,rt.Game.NowSeconds,k.IntKeep("resourceId"),"free","club",k.Int("teamid"),ItemFactory.KitType(k),2+k.Int("kitType"),k.Text("name"));
    }
    private static JsonObject Body(params (long Slot,long Item)[] bindings)=>new() {
        ["tiers"]=new JsonArray(new JsonObject{["slots"]=new JsonArray(bindings.Select(b=>(JsonNode?)new JsonObject{["slotId"]=b.Slot,["itemId"]=b.Item}).ToArray())})
    };
    private static JsonObject Slot(JsonObject response,long id)=>response.Arr("tiers")![0]!.AsObject().Arr("slots")!.OfType<JsonObject>().Single(s=>s.Int("slotId")==id);
    [Fact]
    public async Task ManagerLeaguesPersistOwnedValuesAndSeedFromDefinitions()
    {
        using var rt = Create();
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        var catalog = await Aurora21.UltimateTeam.Core.Catalog.RelationalCatalogLoader.LoadAsync(source);
        var league = catalog.Leagues.Single(l => l.NativeLeagueId == 13);
        var different = catalog.Leagues.First(l => l.NativeLeagueId > 0 && l.LeagueId != league.LeagueId);
        var resource = 1250425L; // Romuald Peyras: user-chosen Premier League default.
        var manager = rt.Fut.Items.BuildManager(resource,501,"club");
        manager["leagueId"] = league.NativeLeagueId;
        await rt.Game.RunAsync("manager-league",w => w.SaveItemAsync(manager),CancellationToken.None);
        async Task<object?> Stored(long item)
        {
            await using var command = source.CreateCommand("SELECT m.league_id FROM ultimateteam.manager_items m JOIN ultimateteam.items i ON i.id=m.item_id WHERE i.native_item_id=@item");
            command.Parameters.AddWithValue("item",item);
            return await command.ExecuteScalarAsync();
        }
        async Task Default(long id)
        {
            await using var command = source.CreateCommand("""
                BEGIN;
                ALTER TABLE ultimateteam.manager_definitions DISABLE TRIGGER lfv4_trg_r2_manager_definitions;
                UPDATE ultimateteam.manager_definitions m SET league_id=@league FROM ultimateteam.items i WHERE i.definition_id=m.definition_id AND i.native_item_id=501;
                SET CONSTRAINTS ALL IMMEDIATE;
                ALTER TABLE ultimateteam.manager_definitions ENABLE TRIGGER lfv4_trg_r2_manager_definitions;
                COMMIT;
                """);
            command.Parameters.AddWithValue("league",id);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(league.LeagueId,await Stored(501));
        manager["leagueId"] = 0;
        await rt.Game.RunAsync("manager-unassigned",w => w.SaveItemAsync(manager),CancellationToken.None);
        Assert.Equal(DBNull.Value,await Stored(501));
        manager["leagueId"] = long.MaxValue;
        await Assert.ThrowsAsync<Aurora21.UltimateTeam.Core.Config.RuntimeSetupException>(() => rt.Game.RunAsync("bad-manager-league",w => w.SaveItemAsync(manager),CancellationToken.None));
        Assert.Equal(DBNull.Value,await Stored(501));
        await Default(league.LeagueId);
        manager["leagueId"] = league.NativeLeagueId;
        await rt.Game.RunAsync("restore-owned-league",w => w.SaveItemAsync(manager),CancellationToken.None);
        Assert.Equal(league.LeagueId,await Stored(501));
        using var first = TestRuntime.Create(connectionString:rt.ConnectionString,relationalSlice:true,time:new TestRuntime.FixedTime(1700000000));
        var created = first.Fut.Items.BuildManager(resource,502,"club");
        await first.Game.RunAsync("create-with-default",w => w.SaveItemAsync(created),CancellationToken.None);
        Assert.Equal(league.LeagueId,await Stored(502));
        await Default(different.LeagueId);
        using var second = TestRuntime.Create(connectionString:rt.ConnectionString,relationalSlice:true,time:new TestRuntime.FixedTime(1700000000));
        Assert.Equal(league.NativeLeagueId,(await second.Game.ReadAsync(w => w.GetItemAsync(502),CancellationToken.None))!.Int("leagueId"));
        var fresh = second.Fut.Items.BuildManager(resource,503,"club");
        await second.Game.RunAsync("create-new-default",w => w.SaveItemAsync(fresh),CancellationToken.None);
        Assert.Equal(different.LeagueId,await Stored(503));
        created["leagueId"] = different.NativeLeagueId;
        await second.Game.RunAsync("change-owned-league",w => w.SaveItemAsync(created),CancellationToken.None);
        Assert.Equal(different.LeagueId,await Stored(502));
        Assert.Equal(league.LeagueId,await Stored(501));
        created["leagueId"] = 0;
        await second.Game.RunAsync("clear-owned-league",w => w.SaveItemAsync(created),CancellationToken.None);
        Assert.Equal(DBNull.Value,await Stored(502));
        Assert.Equal(0,(await second.Game.ReadAsync(w => w.GetItemAsync(502),CancellationToken.None))!.Int("leagueId"));
    }

    [Fact]
    public async Task KitsDoNotStoreAliasOmissionsAndPreserveOtherExtensionData()
    {
        using var rt = Create();
        var original = Kit(rt,101);
        var aliased = Kit(rt,102,1);
        aliased["itemId"] = 102;
        aliased["testUnknown"] = 4;
        await rt.Game.RunAsync("kit-format", w => w.SaveItemsAsync(new[] { original, aliased }), CancellationToken.None);
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using (var count = source.CreateCommand("SELECT count(*) FROM ultimateteam.item_extensions"))
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        var items = await rt.Game.ReadAsync(w => w.ListItemsAsync(), CancellationToken.None);
        var reopened = items.Single(i => i.Int("id")==101);
        Assert.True(JsonNode.DeepEquals(original,reopened));
        Assert.False(reopened.ContainsKey("itemId"));
        Assert.Equal(4, items.Single(i => i.Int("id")==102).Int("testUnknown"));
    }

    /// <summary>
    /// Live 10-02: an empty TIFO slot offered pitch trophies, one was saved into it, and the whole My Stadium save was
    /// refused with a 400. A vanity item for another slot is now skipped and the rest of the save is kept.
    /// </summary>
    [Fact]
    public async Task AVanityItemForAnotherSlotIsSkippedAndTheRestOfTheSaveKept()
    {
        using var rt=Create();await rt.Game.RunAsync("kits",w=>w.SaveItemsAsync(new[]{Kit(rt,101)}),CancellationToken.None);
        var vanity=rt.Game.Rules.Club.Vanity.Items().ToList();
        var trophy=vanity.First(item=>item.Subtype==508).ItemId;var tifo=vanity.First(item=>item.Subtype==500).ItemId;
        var response=rt.CallRaw("PUT",G+"/stadium",Body((3,101),(6,trophy)));Assert.Equal(200,response.Status);
        Assert.Equal(101,Slot(response.Body.AsObject(),3).Obj("itemData")!.Int("id"));
        Assert.False((await rt.Game.ReadAsync(w=>rt.Fut.Vanity.EquippedAsync(w),CancellationToken.None)).ContainsKey(6));
        Assert.Equal(200,rt.CallRaw("PUT",G+"/stadium",Body((6,tifo))).Status);
        Assert.Equal(tifo,(await rt.Game.ReadAsync(w=>rt.Fut.Vanity.EquippedAsync(w),CancellationToken.None))[6]);
    }

    [Fact]
    public async Task StadiumSlotsPersistPartialUpdatesAndRejectInvalidMandatoryClears()
    {
        using var rt=Create();await rt.Game.RunAsync("kits",w=>w.SaveItemsAsync(new[]{Kit(rt,101),Kit(rt,102,1)}),CancellationToken.None);
        var response=rt.CallRaw("PUT",G+"/stadium",Body((3,101),(4,102)));Assert.Equal(200,response.Status);
        Assert.Equal(101,Slot(response.Body.AsObject(),3).Obj("itemData")!.Int("id"));Assert.Equal(102,Slot(response.Body.AsObject(),4).Obj("itemData")!.Int("id"));
        Assert.Equal("activeHomeKit",(await rt.Game.ReadAsync(w=>w.GetItemAsync(101),CancellationToken.None))!.Str("itemState"));
        var ball=Slot(rt.CallObject("GET",G+"/stadium"),1).Obj("itemData")!.Int("id");
        Assert.Equal(200,rt.CallRaw("PUT",G+"/stadium",Body((1,ball),(6,0))).Status);
        var saved=await rt.Game.ReadAsync(w=>w.StadiumSlotBindingsAsync(),CancellationToken.None);Assert.Equal(101,saved.Int("3"));Assert.Equal(102,saved.Int("4"));Assert.Equal(ball,saved.Int("1"));
        // A refused save answers 200 (FIFA reads an error status as a server failure) and changes nothing.
        var bad=rt.CallRaw("PUT",G+"/stadium",Body((3,0)));Assert.Equal(200,bad.Status);
        Assert.Equal(101,Slot(rt.CallObject("GET",G+"/stadium"),3).Obj("itemData")!.Int("id"));
        await using var source=NpgsqlDataSource.Create(rt.ConnectionString);await using var cmd=source.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname='public'");Assert.Equal(0L,(long)(await cmd.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task EquipAndPileChangesSynchronizeBindingsAndProtectEquippedItemsFromQuickSell()
    {
        using var rt=Create();await rt.Game.RunAsync("kits",w=>w.SaveItemsAsync(new[]{Kit(rt,101),Kit(rt,102,1)}),CancellationToken.None);
        await rt.Fut.Inventory.EquipAsync(101,"activeHomeKit");await rt.Fut.Inventory.EquipAsync(102,"activeHomeKit");
        Assert.Equal("free",(await rt.Game.ReadAsync(w=>w.GetItemAsync(101),CancellationToken.None))!.Str("itemState"));
        Assert.Equal(102,(await rt.Game.ReadAsync(w=>w.StadiumSlotBindingsAsync(),CancellationToken.None)).Int("3"));
        Assert.Empty((await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[]{JsonValue.Create(102L)})).Deleted);
        await rt.Game.RunAsync("move",w=>rt.Fut.Inventory.UpdatePileAsync(w,102,"unassigned"),CancellationToken.None);
        Assert.Empty(await rt.Game.ReadAsync(w=>w.StadiumSlotBindingsAsync(),CancellationToken.None));
        Assert.Equal("free",(await rt.Game.ReadAsync(w=>w.GetItemAsync(102),CancellationToken.None))!.Str("itemState"));
        var corrupt=Kit(rt,103);corrupt["itemId"]=999;
        await Assert.ThrowsAsync<Aurora21.UltimateTeam.Core.Config.RuntimeSetupException>(()=>rt.Game.RunAsync("bad-alias",w=>w.SaveItemAsync(corrupt),CancellationToken.None));
        Assert.Null(await rt.Game.ReadAsync(w=>w.GetItemAsync(103),CancellationToken.None));
    }
    [Fact]
    public async Task VirtualDefaultsAndVenueSelectionReopenWithoutCreatingOwnedVirtualItems()
    {
        using var rt=Create();await rt.Game.RunAsync("kit",w=>w.SaveItemAsync(Kit(rt,101)),CancellationToken.None);
        await rt.Fut.Inventory.EquipAsync(101,"activeHomeKit");
        var virtuals=rt.Game.Rules.Club.RenderedDefaultActiveItems(rt.Game.ClubId).ToDictionary(i=>i.Str("itemState"));
        var body=Body((3,virtuals["activeHomeKit"].Int("id")),(5,virtuals["activeStadium"].Int("id")));body["selectedStadiumId"]=SantiagoBernabeu;
        var response=rt.CallObject("PUT",G+"/stadium",body);var stadium=Slot(response,5).Obj("itemData")!;
        // Only FUT Stadium tiers have a matching card face; ordinary venue selections retain the default card.
        Assert.Equal(6200058,stadium.Int("resourceId"));
        Assert.Equal(virtuals["activeStadium"].Int("id"),stadium.Int("id"));
        Assert.Equal(body.Int("selectedStadiumId"),stadium.Int("stadiumid"));
        Assert.Equal("free",(await rt.Game.ReadAsync(w=>w.GetItemAsync(101),CancellationToken.None))!.Str("itemState"));
        Assert.Single(await rt.Game.ReadAsync(w=>w.ListItemsAsync(),CancellationToken.None));
        var connection=rt.Detach();using var reopened=TestRuntime.Create(connectionString:connection,relationalSlice:true,time:new TestRuntime.FixedTime(1700000000));
        Assert.True(JsonNode.DeepEquals(response,reopened.CallObject("GET",G+"/stadium")));
        Assert.Equal(2,(await reopened.Game.ReadAsync(w=>w.StadiumSlotBindingsAsync(),CancellationToken.None)).Count);
    }
    [Fact]
    public async Task InvalidOrLateFailedStadiumSaveRollsBackVenueAndSlotStateTogether()
    {
        var fail=false;using var rt=Create(sql=>{if(fail&&sql.StartsWith("INSERT INTO ultimateteam.club_slot_bindings",StringComparison.Ordinal))throw new InvalidOperationException("synthetic late slot failure");});
        await rt.Game.RunAsync("kit",w=>w.SaveItemAsync(Kit(rt,101)),CancellationToken.None);
        var before=rt.CallObject("GET",G+"/stadium");var account=await rt.Game.ReadAsync(w=>w.RelationalAccountAsync(),CancellationToken.None);
        var bad=Body((3,999));bad["selectedStadiumId"]=rt.Game.Packs.GetPackConfig(12).Int("set_stadium_id");
        Assert.Equal(200,rt.CallRaw("PUT",G+"/stadium",bad).Status);Assert.Equal(account,await rt.Game.ReadAsync(w=>w.RelationalAccountAsync(),CancellationToken.None));
        var body=Body((3,101));body["selectedStadiumId"]=bad.Int("selectedStadiumId");fail=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>rt.Fut.Customisation.SaveStadiumAsync(body).AsTask());fail=false;
        Assert.Equal(account,await rt.Game.ReadAsync(w=>w.RelationalAccountAsync(),CancellationToken.None));Assert.True(JsonNode.DeepEquals(before,rt.CallObject("GET",G+"/stadium")));
    }
    [Fact]
    public async Task GrantAllUsesBulkItemPersistenceAndIsIdempotent()
    {
        var inserts=0;using var rt=Create(sql=>{if(sql.StartsWith("INSERT INTO ultimateteam.items(",StringComparison.Ordinal))Interlocked.Increment(ref inserts);});
        var grant=rt.CallObject("POST","/ultimateteam/customisation/grant-all",new JsonObject());Assert.True(grant.Bool("success"));
        Assert.Equal(rt.Game.Kits.Count+2,grant.Int("grantedCount"));Assert.Equal(1,inserts);
        Assert.Equal(rt.Game.Kits.Count+2,(await rt.Game.ReadAsync(w=>w.ListItemsAsync(),CancellationToken.None)).Count);
        Assert.Equal(0,rt.CallObject("POST","/ultimateteam/customisation/grant-all",new JsonObject()).Int("grantedCount"));
        Assert.Equal(2,(await rt.Game.ReadAsync(w=>w.StadiumSlotBindingsAsync(),CancellationToken.None)).Count);
        await using var source=NpgsqlDataSource.Create(rt.ConnectionString);await using var cmd=source.CreateCommand("SELECT count(*) FROM ultimateteam.item_extensions WHERE payload::jsonb ? 'itemId'");Assert.Equal(0L,(long)(await cmd.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task SelectorAndLocalKitStadiumPacksPersistTypedStateAndRollbackLateFailure()
    {
        var fail=false;using var rt=Create(sql=>{if(fail&&sql.StartsWith("UPDATE ultimateteam.clubs SET last_purchase_transaction_id",StringComparison.Ordinal))throw new InvalidOperationException("synthetic purchase failure");},
            // Migration 041 made store_packs the configuration on the relational backend, so a
            // synthetic pack is a row now. Writing pack_settings.json no longer reaches the server.
            seedSql:"INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,kit_stadium_pack) "
                +"VALUES(777,'store',true,'Synthetic local kit/stadium fixture',1,4,true); "
                +"INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,set_stadium_id) "
                +"VALUES(778,'store',false,'Synthetic stadium selector',1,1,2)");
        await rt.Game.RunAsync("reward",w=>w.AddUnopenedPackAsync(778,"fixture-selector"),CancellationToken.None);
        Assert.Equal(200,rt.CallRaw("POST",G+"/purchased/items",new JsonObject{["packId"]=778}).Status);
        Assert.Equal(rt.Game.Packs.GetPackConfig(778).Int("set_stadium_id"),await rt.Game.ReadAsync(w=>w.SelectedStadiumIdAsync(),CancellationToken.None));
        await rt.Game.RunAsync("reward",w=>w.AddUnopenedPackAsync(777,"fixture-custom-pack"),CancellationToken.None);
        // The store refuses a pack while Unassigned holds anything (Python server 328a5b1): the player deals with it first.
        rt.Game.Store().DeletePile("unassigned");
        Assert.Equal(200,rt.CallRaw("POST",G+"/purchased/items",new JsonObject{["packId"]=777}).Status);
        var items=await rt.Game.ReadAsync(w=>w.ListItemsAsync("unassigned"),CancellationToken.None);Assert.Equal(4,items.Count);Assert.Equal(3,items.Count(i=>i.Str("itemType")=="kit"));
        var stadium=items.Single(i=>i.Str("itemType")=="stadium");Assert.Equal(rt.Game.Packs.GetPackConfig(778).Int("set_stadium_id"),stadium.Int("stadiumid"));
        await rt.Game.RunAsync("reward",w=>w.AddUnopenedPackAsync(777,"fixture-rollback"),CancellationToken.None);rt.Game.Store().DeletePile("unassigned");
        var before=await rt.Game.ReadAsync(w=>w.RelationalAccountAsync(),CancellationToken.None);fail=true;
        Assert.Equal(500,rt.CallRaw("POST",G+"/purchased/items",new JsonObject{["packId"]=777}).Status);fail=false;
        Assert.Equal(before,await rt.Game.ReadAsync(w=>w.RelationalAccountAsync(),CancellationToken.None));
        // The failed pack left nothing behind.
        Assert.Empty(await rt.Game.ReadAsync(w=>w.ListItemsAsync("unassigned"),CancellationToken.None));
        Assert.Single(await rt.Game.ReadAsync(w=>w.UnopenedPackIdsAsync(),CancellationToken.None));
    }
    [Fact]
    public async Task EquipReplacesMissingNullHeaderRepresentationsAndConcurrentChoicesHaveOneWinner()
    {
        using var rt=Create();var missing=Kit(rt,101);missing.Remove("itemState");missing["pile"]=null;missing["timestamp"]=null;
        await rt.Game.RunAsync("kits",w=>w.SaveItemsAsync(new[]{missing,Kit(rt,102,1)}),CancellationToken.None);
        var selected=await rt.Fut.Inventory.EquipAsync(101,"activeHomeKit");Assert.Equal("activeHomeKit",selected.Str("itemState"));Assert.Equal("club",selected.Str("pile"));Assert.Equal(rt.Game.NowSeconds,selected.Int("timestamp"));
        await Task.WhenAll(rt.Fut.Inventory.EquipAsync(101,"activeHomeKit").AsTask(),rt.Fut.Inventory.EquipAsync(102,"activeHomeKit").AsTask());
        var bindings=await rt.Game.ReadAsync(w=>w.StadiumSlotBindingsAsync(),CancellationToken.None);Assert.Single(bindings);
        var all=await rt.Game.ReadAsync(w=>w.ListItemsAsync(),CancellationToken.None);var winner=Assert.Single(all,i=>i.Str("itemState")=="activeHomeKit");Assert.Equal(winner.Int("id"),bindings.Int("3"));
        Assert.Equal(winner.Int("id"),Slot(rt.CallObject("GET",G+"/stadium"),3).Obj("itemData")!.Int("id"));
    }
    [Fact]
    public async Task DirectSqlRejectsWrongSlotSubtypeAndForeignOwnedCustomisation()
    {
        using var rt=Create();await rt.Game.RunAsync("kit",w=>w.SaveItemAsync(Kit(rt,101)),CancellationToken.None);
        var other=await rt.Game.RunAsync("other",w=>w.EnsureRelationalClubAsync(new ClubIdentity(900000003,900000004),0),CancellationToken.None);
        await using var source=NpgsqlDataSource.Create(rt.ConnectionString);await using var c=await source.OpenConnectionAsync();
        await using(var t=await c.BeginTransactionAsync()) {
            await using var cmd=new NpgsqlCommand("""
                INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,pile,untradeable,owners,last_sale_price)
                SELECT @other,101,definition_id,4,0,'club',false,1,0 FROM ultimateteam.items WHERE club_id=@local AND native_item_id=101;
                INSERT INTO ultimateteam.customisation_items(item_id,is_locked_override) SELECT id,false FROM ultimateteam.items WHERE club_id=@other
                """,c,t);cmd.Parameters.AddWithValue("other",other.Account.ClubId);cmd.Parameters.AddWithValue("local",rt.Game.ClubId);await cmd.ExecuteNonQueryAsync();await t.CommitAsync();
        }
        foreach(var foreign in new[]{false,true}) {
            await using var t=await c.BeginTransactionAsync();await using var cmd=new NpgsqlCommand("INSERT INTO ultimateteam.club_slot_bindings(club_id,slot_id,reference_kind,item_id,definition_id) SELECT @local,@slot,'owned',id,definition_id FROM ultimateteam.items WHERE club_id=@owner AND native_item_id=101",c,t);
            cmd.Parameters.AddWithValue("local",rt.Game.ClubId);cmd.Parameters.AddWithValue("slot",foreign?3L:2L);cmd.Parameters.AddWithValue("owner",foreign?other.Account.ClubId:rt.Game.ClubId);await cmd.ExecuteNonQueryAsync();
            var error=await Assert.ThrowsAsync<PostgresException>(()=>t.CommitAsync());Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
        }
    }
}
