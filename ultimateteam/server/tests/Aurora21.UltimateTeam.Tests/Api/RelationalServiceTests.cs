using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalServiceTests
{
    /// <summary>The store's Bronze Pack (migration 095).</summary>
    private const long BronzePack = 70;

    [Fact]
    public async Task DirectSqlEnforcesDefinitionFamilyAndQuantityBounds()
    {
        var connection=PostgresTestCluster.CreateInstalledDatabase();
        try {
            await using var source=NpgsqlDataSource.Create(connection);
            await RelationalSchemaBootstrap.InstallFreshAsync(source);
            await using(var cmd=source.CreateCommand("""
                INSERT INTO ultimateteam.clubs(persona_id,nucleus_id,credits,fifa_points,next_item_id,sid,token,established,last_purchase_transaction_id,last_match_create_is_draft,last_draft_slot)
                VALUES(1,1,100,0,900000000000,'synthetic','synthetic',0,0,false,1)
                """))await cmd.ExecuteNonQueryAsync();
            await using var c=await source.OpenConnectionAsync();
            await using(var t=await c.BeginTransactionAsync()) {
                await using var cmd=new NpgsqlCommand("""
                    INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,untradeable,owners,last_sale_price)
                    SELECT 1,1,definition_id,1,0,false,1,0 FROM ultimateteam.item_definitions WHERE family_id=2 ORDER BY definition_id LIMIT 1;
                    INSERT INTO ultimateteam.player_items(item_id,contracts,fitness,injury_games,suspension,loan_games,morale,training,assists,lifetime_assists,duplicate_acknowledged)
                    SELECT id,7,99,0,0,0,50,0,0,0,false FROM ultimateteam.items;
                    """,c,t);
                await cmd.ExecuteNonQueryAsync();
                var error=await Assert.ThrowsAsync<PostgresException>(()=>t.CommitAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
                Assert.Contains("family differs",error.MessageText);
            }
            foreach(var quantity in new[]{(0L,0L),(1L,2L),(1L,-1L)}) {
                await using var t=await c.BeginTransactionAsync();
                await using var cmd=new NpgsqlCommand("INSERT INTO ultimateteam.consumable_items(item_id,quantity,untradeable_quantity) VALUES(999,@quantity,@untradeable)",c,t);
                cmd.Parameters.AddWithValue("quantity",quantity.Item1);cmd.Parameters.AddWithValue("untradeable",quantity.Item2);
                var error=await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
                Assert.Equal("lfv4_ck_consumable_items_quantities",error.ConstraintName);
            }
            await using(var cmd=new NpgsqlCommand("""
                BEGIN;
                INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,untradeable,owners,last_sale_price)
                SELECT 1,9,definition_id,3,0,false,1,0 FROM ultimateteam.item_definitions WHERE family_id=3 ORDER BY definition_id LIMIT 1;
                INSERT INTO ultimateteam.consumable_items(item_id,quantity,untradeable_quantity) SELECT id,1,0 FROM ultimateteam.items WHERE native_item_id=9;
                COMMIT;
                """,c))await cmd.ExecuteNonQueryAsync();
            await using(var t=await c.BeginTransactionAsync()) {
                await using var cmd=new NpgsqlCommand("DELETE FROM ultimateteam.consumable_items",c,t);
                await cmd.ExecuteNonQueryAsync();
                var error=await Assert.ThrowsAsync<PostgresException>(()=>t.CommitAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
                Assert.Contains("must have exactly",error.MessageText);
            }
        } finally { PostgresTestCluster.DropDatabase(connection); }
    }

    [Fact]
    public async Task ExistingFactoriesRoundTripTypedStateAndKnownRepresentationWithoutStoringDocuments()
    {
        using var runtime=Create();
        var game=runtime.Game; var fut=runtime.Fut;
        var mapper=(await game.ReadAsync(w=>ValueTask.FromResult(w.RelationalItems!.Mapper),CancellationToken.None));
        var player=fut.Items.BuildPlayer(50524277,123,"club",4);
        player["contract"]="0"; player["contracts"]=0;
        player["rareflag"]=1; player["testUnknown"]="kept";
        var manager=fut.Items.BuildManager(game.Managers.BestManagerId()!.Value,124,"club");
        var style=game.Cards.ChemistryStyles.First();
        var stack=fut.Items.BuildConsumable(style.ResourceId,125,"club",3);
        var kit=game.Kits.Kits.First();
        var kitWire=ItemFactory.KitItem(126,game.NowSeconds,kit.IntKeep("resourceId"),"free","club",kit.Int("teamid"),ItemFactory.KitType(kit),2+kit.Int("kitType"),kit.Text("name"));
        var originals=new[]{player,manager,stack,kitWire};
        foreach(var wire in originals) Assert.True(JsonNode.DeepEquals(wire,mapper.Render(mapper.Capture(game.ClubId,wire))),wire.ToJsonString());
        var corrupt=mapper.Capture(game.ClubId,manager) with { Extension=new ItemExtensionState(default,1,new CatalogJson("{}"),
            new CatalogJson("{\"overrides\":{\"id\":999}}")) };
        Assert.Throws<RuntimeSetupException>(()=>mapper.Render(corrupt));
        await game.RunAsync("save-families",w=>w.SaveItemsAsync(originals),CancellationToken.None);
        var loaded=await game.ReadAsync(w=>w.ListItemsAsync(),CancellationToken.None);
        Assert.Equal(originals.Length,loaded.Count);
        for(var i=0;i<originals.Length;i++)Assert.True(JsonNode.DeepEquals(originals[i],loaded[i]));
        await game.RunAsync("change-typed-state",async w=>{
            var item=(await w.GetItemAsync(123))!;item["contract"]="5";item["contracts"]=5;item["statsList"]!.AsArray()[0]!.AsObject()["value"]=8;
            await w.SaveItemAsync(item);
        },CancellationToken.None);
        var changed=(await game.ReadAsync(w=>w.GetItemAsync(123),CancellationToken.None))!;
        Assert.Equal("5",changed["contract"]!.GetValue<string>());Assert.Equal(5,changed.Int("contracts"));
        Assert.Equal(8,changed["statsList"]![0]!.AsObject().Int("value"));
        await using var source=NpgsqlDataSource.Create(runtime.ConnectionString);
        await using var cmd=source.CreateCommand("SELECT payload::text FROM ultimateteam.item_extensions ORDER BY item_id LIMIT 1");
        var payload=(string)(await cmd.ExecuteScalarAsync())!;
        Assert.Contains("testUnknown",payload);Assert.DoesNotContain("statsList",payload);Assert.DoesNotContain("resourceId",payload);
        await AssertNoLegacyTables(source);
    }

    [Fact]
    public async Task ExistingPackAndPickServicesPersistAndReplayUsingOnlyRelationalTables()
    {
        using var runtime=Create();var game=runtime.Game;var fut=runtime.Fut;
        var player=game.Players.Ids.First();
        await game.RunAsync("own-card",async w=>{await fut.Items.MakePlayerAsync(w,player);},CancellationToken.None);
        var token=await game.RunAsync("pick-token",w=>fut.Items.MakePlayerPickAsync(w,0,4,previewResourceId:player),CancellationToken.None);
        var choices=await fut.Picks.RedeemAsync(token.Int("id"));
        Assert.Equal(4,choices.Count);Assert.True(JsonNode.DeepEquals(choices,await fut.Picks.RedeemAsync(token.Int("id"))));
        var selected=await fut.Picks.SelectAsync(token.Int("id"),JsonValue.Create(0));
        Assert.Equal(choices[0]!.AsObject().Int("id"),selected.Int("id"));
        await Assert.ThrowsAsync<ClubRuleException>(()=>fut.Picks.SelectAsync(token.Int("id"),JsonValue.Create(0)));
        await game.RunAsync("reward",w=>w.AddUnopenedPackAsync(BronzePack,"synthetic-test"),CancellationToken.None);
        // The store refuses a pack while Unassigned holds anything (Python server 328a5b1): the picked card goes first.
        await game.RunAsync("clear-unassigned",w=>w.DeletePileAsync("unassigned"),CancellationToken.None);
        // Existing purchase service also persists the pack-open objective counter and XP.
        var response=await fut.Store.OpenPackAsync(new JsonObject{["packId"]=BronzePack},"application/json",Array.Empty<byte>(),new Aurora21.UltimateTeam.Core.Web.QueryValues(),CancellationToken.None);
        Assert.NotNull(response);
        Assert.Equal(200,response.Status);
        Assert.Empty(await game.ReadAsync(w=>w.UnopenedPackIdsAsync(),CancellationToken.None));
        Assert.True((await game.ReadAsync(w=>w.ObjectivesAsync(),CancellationToken.None)).Stats.GetValueOrDefault("packs")>0);
        Assert.NotEmpty(await game.ReadAsync(w=>w.ListItemsAsync("unassigned"),CancellationToken.None));
        await using var source=NpgsqlDataSource.Create(runtime.ConnectionString);await AssertNoLegacyTables(source);
    }

    [Fact]
    public async Task InventoryServicesApplyConsumeDuplicateMoveAndQuickSellWithTypedState()
    {
        using var runtime=Create();var game=runtime.Game;var fut=runtime.Fut;
        var style=game.Cards.ChemistryStyles.First(s=>!s.Goalkeeper);
        var player=await game.RunAsync("player",w=>fut.Items.MakePlayerAsync(w,50524277),CancellationToken.None);
        var stack=await game.RunAsync("stack",w=>fut.Items.MakeConsumableAsync(w,style.ResourceId,stackCount:2),CancellationToken.None);
        await fut.Inventory.ApplyConsumableAsync(style.ResourceId,player.Int("id"));
        var changed=(await game.ReadAsync(w=>w.GetItemAsync(player.Int("id")),CancellationToken.None))!;
        Assert.Equal(style.PlayStyle,changed.Int("playStyle"));
        Assert.Equal(1,(await game.ReadAsync(w=>w.GetItemAsync(stack.Int("id")),CancellationToken.None))!.Int("stackCount"));
        await fut.Inventory.ApplyConsumableAsync(style.ResourceId,player.Int("id"));
        Assert.Null(await game.ReadAsync(w=>w.GetItemAsync(stack.Int("id")),CancellationToken.None));
        var duplicate=await game.RunAsync("duplicate",w=>fut.Items.MakePlayerAsync(w,50524277,"unassigned"),CancellationToken.None);
        await fut.Inventory.ApplyItemUpdatesAsync(new JsonObject { ["itemData"]=new JsonObject{["id"]=duplicate.Int("id"),["pile"]="club"} });
        var held=(await game.ReadAsync(w=>w.GetItemAsync(duplicate.Int("id")),CancellationToken.None))!;
        Assert.Equal("unassigned",held.Str("pile"));Assert.True(held.Bool("duplicate"));
        await fut.Inventory.DiscardItemsAsync(new JsonNode?[]{JsonValue.Create(duplicate.Int("id"))});
        Assert.Null(await game.ReadAsync(w=>w.GetItemAsync(duplicate.Int("id")),CancellationToken.None));
    }

    [Fact]
    public async Task PurchasedRoutePersistsRevealAcrossReopenAndLateFailureRollsBackAllState()
    {
        using(var runtime=Create()) {
            await runtime.Game.RunAsync("reward",w=>w.AddUnopenedPackAsync(BronzePack,"route-test"),CancellationToken.None);
            runtime.CallObject("POST","/ut/game/fifa21/purchased/items",new JsonObject{["packId"]=BronzePack});
            var before=runtime.CallObject("GET","/ut/game/fifa21/purchased/items");
            var connection=runtime.Detach();
            using var reopened=TestRuntime.Create(connectionString:connection,relationalSlice:true);
            Assert.True(JsonNode.DeepEquals(before,reopened.CallObject("GET","/ut/game/fifa21/purchased/items")));
            Assert.Empty(await reopened.Game.ReadAsync(w=>w.UnopenedPackIdsAsync(),CancellationToken.None));
            Assert.Equal(1,(await reopened.Game.ReadAsync(w=>w.ObjectivesAsync(),CancellationToken.None)).Stats.GetValueOrDefault("packs"));
        }
        using(var runtime=Create(sql=>{
            if(sql.StartsWith("UPDATE ultimateteam.clubs SET last_purchase_transaction_id",StringComparison.Ordinal))
                throw new InvalidOperationException("injected after grant and objective persistence");
        })) {
            var game=runtime.Game;
            await game.RunAsync("reward",w=>w.AddUnopenedPackAsync(BronzePack,"rollback-test"),CancellationToken.None);
            var before=await game.ReadAsync(w=>w.RelationalAccountAsync(),CancellationToken.None);
            var reply=runtime.CallRaw("POST","/ut/game/fifa21/purchased/items",new JsonObject{["packId"]=BronzePack});
            Assert.Equal(500,reply.Status);
            Assert.Equal(before,await game.ReadAsync(w=>w.RelationalAccountAsync(),CancellationToken.None));
            Assert.Single(await game.ReadAsync(w=>w.UnopenedPackIdsAsync(),CancellationToken.None));
            Assert.Empty(await game.ReadAsync(w=>w.ListItemsAsync(),CancellationToken.None));
            Assert.Equal(0,(await game.ReadAsync(w=>w.ObjectivesAsync(),CancellationToken.None)).UserXp);
        }
    }

    private static TestRuntime Create(Action<string>? observer = null)
    {
        var connection=PostgresTestCluster.CreateInstalledDatabase();
        // Migration 048 installs the Player Pick token definition.
        using(var source=NpgsqlDataSource.Create(connection)) RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        return TestRuntime.Create(connectionString:connection,relationalSlice:true,commandObserver:observer);
    }
    private static async Task AssertNoLegacyTables(NpgsqlDataSource source)
    {
        await using var command=source.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname='public'");
        Assert.Equal(0L,(long)(await command.ExecuteScalarAsync())!);
    }
}
