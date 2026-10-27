using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalSquadTests
{
    private const string G="/ut/game/fifa21";
    private static TestRuntime Create(Action<string>? observer=null)
    {
        var connection=PostgresTestCluster.CreateInstalledDatabase();
        using(var source=NpgsqlDataSource.Create(connection))RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        return TestRuntime.Create(connectionString:connection,relationalSlice:true,commandObserver:observer,time:new TestRuntime.FixedTime(1700000000));
    }
    private static JsonObject Squad(long id,params long[] players)=>new() {
        ["id"]=id,["squadId"]=id,["name"]="Synthetic squad",["squadName"]="Synthetic squad",["formation"]="f442",
        ["players"]=new JsonArray(players.Select((p,i)=>(JsonNode?)new JsonObject{["index"]=i,["itemId"]=p,["kitNumber"]=i+1,["loyaltyBonus"]=1}).ToArray()),["kicktakers"]=new JsonArray()
    };
    [Fact]
    public async Task RoutesCreateListActivateSaveTacticsAndDeleteWithoutLegacyTables()
    {
        using var rt=Create();
        var one=rt.CallObject("POST",G+"/squad",new JsonObject{["squadName"]="First"}).Int("id");
        var two=rt.CallObject("POST",G+"/squad",new JsonObject{["squadName"]="Second"}).Int("id");
        Assert.Equal(1,one);Assert.Equal(2,two);
        Assert.True(await rt.Game.RunAsync("activate",w=>w.SetActiveSquadAsync(two),CancellationToken.None));
        Assert.Equal(two,rt.CallObject("GET",G+"/squad/active").Int("id"));
        var saved=await rt.Game.ReadAsync(w=>w.GetSquadAsync(one),CancellationToken.None);saved!.Remove("active");
        Assert.Equal(200,rt.CallRaw("PUT",G+"/squad/1",saved).Status);
        Assert.Equal(two,await rt.Game.ReadAsync(w=>w.ActiveSquadIdAsync(),CancellationToken.None));
        rt.Call("POST",G+"/tactics?squadId=2",new JsonObject{["tactics"]=new JsonArray(new JsonObject{["tactic"]="BALANCED",["width"]=50,["enabled"]=true,["formation"]="f442"})});
        var tactics=rt.CallObject("GET",G+"/squad/active").Arr("tactics")!;
        Assert.Single(tactics);Assert.Equal(50,tactics[0]!.AsObject().Int("width"));Assert.True(tactics[0]!.AsObject().Bool("enabled"));
        // FIFA deletes its active squad before switching: the remaining squad becomes active.
        Assert.Equal(200,rt.CallRaw("DELETE",G+"/squad/2").Status);
        Assert.Equal(one,rt.CallObject("GET",G+"/squad/active").Int("id"));
        Assert.Equal(one,await rt.Game.ReadAsync(w=>w.ActiveSquadIdAsync(),CancellationToken.None));
        // The last squad is protected; a separate inactive squad can still be deleted.
        Assert.Equal(400,rt.CallRaw("DELETE",G+"/squad/1").Status);
        var spare=rt.CallObject("POST",G+"/squad",new JsonObject{["squadName"]="Spare"}).Int("id");
        Assert.Equal(200,rt.CallRaw("DELETE",G+"/squad/"+spare).Status);
        Assert.Single(rt.CallObject("GET",G+"/squad/list").Arr("squad")!);
        await using var source=NpgsqlDataSource.Create(rt.ConnectionString);
        await using var command=source.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname='public'");
        Assert.Equal(0L,(long)(await command.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task OwnedConceptRolesAndEmptySlotsReopenWithCurrentItemState()
    {
        using var rt=Create();
        var owned=rt.Fut.Items.BuildPlayer(50524277,101,"club");
        var conceptResource=await rt.Game.ReadAsync(w=>w.ScalarAsync<long>("SELECT d.native_resource_id FROM ultimateteam.item_definitions d JOIN ultimateteam.player_card_definitions p ON p.definition_id=d.definition_id WHERE d.game_year=2021 AND p.footballer_id<>(SELECT p2.footballer_id FROM ultimateteam.player_card_definitions p2 JOIN ultimateteam.item_definitions d2 ON d2.definition_id=p2.definition_id WHERE d2.game_year=2021 AND d2.native_resource_id=50524277) ORDER BY d.catalog_order LIMIT 1"),CancellationToken.None);
        var concept=rt.Game.Cards.ConceptItemId(conceptResource);
        await rt.Game.RunAsync("own",w=>w.SaveItemAsync(owned),CancellationToken.None);
        var squad=Squad(1,101,concept);squad["active"]=true;squad["captain"]=concept;
        squad["kicktakers"]=new JsonArray(new JsonObject{["index"]=0,["id"]=concept},new JsonObject{["index"]=2,["id"]=101},new JsonObject{["index"]=4,["id"]=123});
        squad.Arr("players")!.Add(new JsonObject{["index"]=7,["kitNumber"]=0});squad["customUnknown"]="retained";
        squad["actives"]=new JsonArray(owned.DeepClone());squad["club"]=new JsonArray(owned.DeepClone());
        var savedReply=rt.CallRaw("PUT",G+"/squad/1",squad);Assert.True(savedReply.Status==200,savedReply.Body.ToJsonString());
        await rt.Game.RunAsync("loan-games",async w=>{var item=(await w.GetItemAsync(101))!;item["loans"]=3;item["untradeable"]=true;await w.SaveItemAsync(item);},CancellationToken.None);
        var before=rt.CallObject("GET",G+"/squad/active");Assert.Equal(concept,before.Int("captain"));
        Assert.Equal(3,before.Arr("players")![0]!.AsObject().Obj("itemData")!.Int("contract"));
        Assert.Equal(concept,before.Arr("kicktakers")![0]!.AsObject().Int("id"));
        Assert.Equal(101,before.Arr("kicktakers")![2]!.AsObject().Int("id"));
        await using(var source=NpgsqlDataSource.Create(rt.ConnectionString)) {
            await using var command=source.CreateCommand("SELECT extension_payload::text || wire_representation::text FROM ultimateteam.squads");
            var extension=(string)(await command.ExecuteScalarAsync())!;Assert.Contains("customUnknown",extension);
            Assert.DoesNotContain("itemData",extension);Assert.DoesNotContain("statsList",extension);Assert.DoesNotContain("itemId",extension);
        }
        var connection=rt.Detach();using var reopened=TestRuntime.Create(connectionString:connection,relationalSlice:true,time:new TestRuntime.FixedTime(1700000000));
        Assert.True(JsonNode.DeepEquals(before,reopened.CallObject("GET",G+"/squad/active")));
        var stored=(await reopened.Game.ReadAsync(w=>w.GetSquadAsync(1),CancellationToken.None))!;
        Assert.Equal(new long[]{0,1,7},stored.Arr("players")!.OfType<JsonObject>().Select(p=>p.Int("index")));
        Assert.Equal("retained",stored.Str("customUnknown"));
    }
    [Fact]
    public async Task QuickSellingASquadCardEmptiesItsSlotsInEverySquadAndPaysOnce()
    {
        // The club screen quick sells a squad card first and saves the squad without it afterwards.
        using var rt=Create();
        var resources=rt.Game.Players.Entries.Where(entry=>entry.Value.Rareflag==0&&entry.Value.Rating is >= 75 and <= 78)
            .GroupBy(entry=>entry.Value.Int("assetId")).Select(group=>group.First().Key).Take(2).ToArray();
        await rt.Game.RunAsync("own",w=>w.SaveItemsAsync(new[]{rt.Fut.Items.BuildPlayer(resources[0],101,"club"),rt.Fut.Items.BuildPlayer(resources[1],102,"club")}),CancellationToken.None);
        var squad=Squad(1,101,102);squad["active"]=true;squad["captain"]=101;
        squad["kicktakers"]=new JsonArray(new JsonObject{["index"]=0,["id"]=101},new JsonObject{["index"]=1,["id"]=102});
        Assert.Equal(200,rt.CallRaw("PUT",G+"/squad/1",squad).Status);
        Assert.Equal(200,rt.CallRaw("PUT",G+"/squad/2",Squad(2,101)).Status);
        var balance=await rt.Game.ReadAsync(w=>w.CreditsAsync(),CancellationToken.None);

        var discarded=await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[]{JsonValue.Create(101L)});
        Assert.Equal(new long[]{101},discarded.Deleted);
        Assert.Equal(balance+rt.Game.Cards.DiscardValue(resources[0]),discarded.Credits);
        var replay=await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[]{JsonValue.Create(101L)});
        Assert.Empty(replay.Deleted);Assert.Equal(discarded.Credits,replay.Credits);

        var one=(await rt.Game.ReadAsync(w=>w.GetSquadAsync(1),CancellationToken.None))!;
        var slots=one.Arr("players")!.OfType<JsonObject>().ToArray();
        Assert.Equal(new long[]{0,1},slots.Select(p=>p.Int("index")));
        Assert.False(slots[0].ContainsKey("itemId"));Assert.Equal(1,slots[0].Int("loyaltyBonus"));
        Assert.Equal(102,slots[1].Int("itemId"));
        Assert.False(one.ContainsKey("captain"));
        Assert.Equal(new long[]{102},one.Arr("kicktakers")!.OfType<JsonObject>().Select(k=>k.Int("id")));
        var two=(await rt.Game.ReadAsync(w=>w.GetSquadAsync(2),CancellationToken.None))!;
        Assert.False(two.Arr("players")!.OfType<JsonObject>().Single().ContainsKey("itemId"));
        Assert.Equal(200,rt.CallRaw("GET",G+"/squad/active").Status);
        // The client's follow-up save without the card still lands.
        Assert.Equal(200,rt.CallRaw("PUT",G+"/squad/1",Squad(1,0,102)).Status);
    }
    [Fact]
    public async Task DifferentCardVersionsOfOneFootballerAreRejectedWithoutChangingSquad()
    {
        using var rt=Create();
        var resources=await rt.Game.ReadAsync(w=>w.QueryAsync("""
            SELECT d.native_resource_id FROM ultimateteam.item_definitions d JOIN ultimateteam.player_card_definitions p ON p.definition_id=d.definition_id
            WHERE d.game_year=2021 AND p.footballer_id=(SELECT p2.footballer_id FROM ultimateteam.player_card_definitions p2 JOIN ultimateteam.item_definitions d2 ON d2.definition_id=p2.definition_id WHERE d2.game_year=2021 GROUP BY p2.footballer_id HAVING count(*)>1 ORDER BY p2.footballer_id LIMIT 1)
            ORDER BY d.catalog_order LIMIT 2
            """,r=>r.GetInt64(0)),CancellationToken.None);
        await rt.Game.RunAsync("own",w=>w.SaveItemsAsync(new[]{rt.Fut.Items.BuildPlayer(resources[0],101,"club"),rt.Fut.Items.BuildPlayer(resources[1],102,"club")}),CancellationToken.None);
        await rt.Fut.Inventory.SaveSquadAsync(Squad(1,101));
        var error=rt.CallRaw("PUT",G+"/squad/1",Squad(1,101,102));Assert.Equal(400,error.Status);Assert.Equal("DUPLICATE_FOOTBALLER_IN_SQUAD",error.Body.AsObject().Str("reason"));
        var stored=(await rt.Game.ReadAsync(w=>w.GetSquadAsync(1),CancellationToken.None))!;Assert.Single(stored.Arr("players")!);
        var mixed=rt.CallRaw("PUT",G+"/squad/1",Squad(1,101,rt.Game.Cards.ConceptItemId(resources[1])));Assert.Equal(400,mixed.Status);
    }
    [Fact]
    public async Task ConcurrentCreationUsesDistinctNumbersAndLateFailureRollsBackMembershipAndActiveFlag()
    {
        var fail=false;using var rt=Create(sql=>{if(fail&&sql.Contains("INSERT INTO ultimateteam.squad_roles",StringComparison.Ordinal))throw new InvalidOperationException("synthetic late failure");});
        var created=await Task.WhenAll(Enumerable.Range(0,5).Select(_=>rt.Fut.Inventory.CreateSquadAsync(new JsonObject()).AsTask()));
        Assert.Equal(new long[]{1,2,3,4,5},created.Select(s=>s.Int("id")).Order());
        await rt.Game.RunAsync("activate",w=>w.SetActiveSquadAsync(1),CancellationToken.None);
        var before=await rt.Game.ReadAsync(w=>w.ListSquadsAsync(),CancellationToken.None);
        fail=true;var replacement=Squad(2);replacement["active"]=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>rt.Fut.Inventory.SaveSquadAsync(replacement).AsTask());fail=false;
        var after=await rt.Game.ReadAsync(w=>w.ListSquadsAsync(),CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(new JsonArray(before.Select(s=>(JsonNode?)s).ToArray()),new JsonArray(after.Select(s=>(JsonNode?)s).ToArray())));
    }
    [Fact]
    public async Task ClubLocalNumbersRemainIsolatedAndDirectSqlRejectsForeignOwnershipAndDuplicateIdentity()
    {
        using var rt=Create();await rt.Game.RunAsync("own",w=>w.SaveItemAsync(rt.Fut.Items.BuildPlayer(50524277,101,"club")),CancellationToken.None);
        await rt.Fut.Inventory.SaveSquadAsync(Squad(1,101));
        var other=await rt.Game.RunAsync("other",w=>w.EnsureRelationalClubAsync(new ClubIdentity(900000003,900000004),0),CancellationToken.None);
        await using var source=NpgsqlDataSource.Create(rt.ConnectionString);await using var connection=await source.OpenConnectionAsync();
        await using(var transaction=await connection.BeginTransactionAsync()) {
            await using var command=new NpgsqlCommand("""
                INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,pile,untradeable,owners,last_sale_price)
                SELECT @other,101,d.definition_id,1,0,'club',false,1,0 FROM ultimateteam.item_definitions d WHERE d.game_year=2021 AND d.native_resource_id=50524277;
                INSERT INTO ultimateteam.player_items(item_id,contracts,fitness,injury_games,suspension,loan_games,morale,training,assists,lifetime_assists,duplicate_acknowledged)
                SELECT id,7,99,0,0,0,50,0,0,0,false FROM ultimateteam.items WHERE club_id=@other;
                INSERT INTO ultimateteam.squads(club_id,native_squad_id,name,active,formation) VALUES(@other,1,'Other',false,'f442');
                INSERT INTO ultimateteam.squad_players(squad_id,slot_index,definition_id,footballer_id,reference_kind,item_id)
                SELECT q.id,0,i.definition_id,p.footballer_id,'owned',i.id FROM ultimateteam.squads q JOIN ultimateteam.items i ON i.club_id=q.club_id JOIN ultimateteam.player_card_definitions p ON p.definition_id=i.definition_id WHERE q.club_id=@other;
                """,connection,transaction);command.Parameters.AddWithValue("other",other.Account.ClubId);await command.ExecuteNonQueryAsync();await transaction.CommitAsync();
        }
        var local=(await rt.Game.ReadAsync(w=>w.ListSquadsAsync(),CancellationToken.None));Assert.Single(local);Assert.Equal("Synthetic squad",local[0].Str("name"));
        await using(var transaction=await connection.BeginTransactionAsync()) {
            await using var command=new NpgsqlCommand("UPDATE ultimateteam.squad_players SET item_id=(SELECT id FROM ultimateteam.items WHERE club_id=@other AND native_item_id=101) WHERE squad_id=(SELECT id FROM ultimateteam.squads WHERE club_id=@local AND native_squad_id=1)",connection,transaction);
            command.Parameters.AddWithValue("other",other.Account.ClubId);command.Parameters.AddWithValue("local",rt.Game.ClubId);await command.ExecuteNonQueryAsync();
            var error=await Assert.ThrowsAsync<PostgresException>(()=>transaction.CommitAsync());Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);Assert.Contains("does not match squad club",error.MessageText);
        }
        await using(var transaction=await connection.BeginTransactionAsync()) {
            await using var command=new NpgsqlCommand("INSERT INTO ultimateteam.squad_players(squad_id,slot_index,definition_id,footballer_id,reference_kind,concept_wire_id) SELECT p.squad_id,8,p.definition_id,p.footballer_id,'concept',@concept FROM ultimateteam.squad_players p JOIN ultimateteam.squads q ON q.id=p.squad_id WHERE q.club_id=@local",connection,transaction);
            command.Parameters.AddWithValue("local",rt.Game.ClubId);command.Parameters.AddWithValue("concept",rt.Game.Cards.ConceptItemId(50524277));await command.ExecuteNonQueryAsync();
            var error=await Assert.ThrowsAsync<PostgresException>(()=>transaction.CommitAsync());Assert.Equal(PostgresErrorCodes.UniqueViolation,error.SqlState);Assert.Equal("lfv4_uq_squad_players_2",error.ConstraintName);
        }
    }
    [Fact]
    public async Task ExplicitOwnedManagerReferenceRendersThatManagerInsteadOfTheHighestRatedFallback()
    {
        using var rt=Create();var resources=await rt.Game.ReadAsync(w=>w.QueryAsync("SELECT d.native_resource_id FROM ultimateteam.item_definitions d JOIN ultimateteam.manager_definitions m ON m.definition_id=d.definition_id WHERE d.game_year=2021 ORDER BY m.rating DESC,d.catalog_order LIMIT 2",r=>r.GetInt64(0)),CancellationToken.None);
        await rt.Game.RunAsync("managers",w=>w.SaveItemsAsync(new[]{rt.Fut.Items.BuildManager(resources[0],101,"club"),rt.Fut.Items.BuildManager(resources[1],102,"club")}),CancellationToken.None);
        var squad=Squad(1);squad["manager"]=new JsonArray(new JsonObject{["id"]=102,["dream"]=false});
        Assert.Equal(200,rt.CallRaw("PUT",G+"/squad/1",squad).Status);
        Assert.Equal(102,rt.CallObject("GET",G+"/squad/1").Arr("manager")![0]!.AsObject().Int("id"));
        var stored=(await rt.Game.ReadAsync(w=>w.GetSquadAsync(1),CancellationToken.None))!;Assert.Equal(102,stored.Arr("manager")![0]!.AsObject().Int("id"));
    }
    [Fact]
    public async Task SquadRouteCleansOnlyUnreferencedStaleLoansFromRelationalOnboarding()
    {
        using var rt=Create();await rt.Game.RunAsync("own",w=>w.SaveItemsAsync(new[]{rt.Fut.Items.BuildPlayer(50524277,101,"club",4),rt.Fut.Items.BuildPlayer(50524277,102,"club",4)}),CancellationToken.None);
        await rt.Game.RunAsync("loan-state",w=>w.ExecuteAsync("""
            INSERT INTO ultimateteam.onboarding(club_id,complete,badge_choice,badge_item_id,loan_offer_id,loan_item_id,loan_resource_id) VALUES(@club,false,0,0,0,101,50524277);
            INSERT INTO ultimateteam.onboarding_stale_loans(club_id,ordinal,wire_item_id) VALUES(@club,0,102)
            """),CancellationToken.None);
        Assert.Equal(200,rt.CallRaw("PUT",G+"/squad/1",Squad(1,101)).Status);
        Assert.NotNull(await rt.Game.ReadAsync(w=>w.GetItemAsync(101),CancellationToken.None));Assert.Null(await rt.Game.ReadAsync(w=>w.GetItemAsync(102),CancellationToken.None));
        Assert.Equal(0,await rt.Game.ReadAsync(w=>w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.onboarding_stale_loans WHERE club_id=@club"),CancellationToken.None));
        Assert.Equal(101,await rt.Game.ReadAsync(w=>w.ScalarAsync<long>("SELECT loan_item_id FROM ultimateteam.onboarding WHERE club_id=@club"),CancellationToken.None));
    }
    [Fact]
    public async Task UnknownOwnedReferencesAndNestedTacticsFailWithoutStoringUnmappedDocuments()
    {
        using var rt=Create();await rt.Fut.Inventory.SaveSquadAsync(Squad(1));
        var bad=rt.CallRaw("PUT",G+"/squad/1",Squad(1,999));Assert.Equal(400,bad.Status);Assert.Equal("SQUAD_PLAYER_NOT_OWNED",bad.Body.AsObject().Str("reason"));
        var duplicateSlots=Squad(1,0,0);duplicateSlots.Arr("players")![1]!.AsObject()["index"]=0;
        Assert.Equal(400,rt.CallRaw("PUT",G+"/squad/1",duplicateSlots).Status);
        await Assert.ThrowsAsync<ClubRuleException>(()=>rt.Game.RunAsync("bad-tactic",w=>w.SaveSquadTacticAsync(1,"BALANCED",new JsonObject{["instructions"]=new JsonArray(1)}),CancellationToken.None));
        Assert.Empty(await rt.Game.ReadAsync(w=>w.SquadTacticsAsync(1),CancellationToken.None));
    }
}
