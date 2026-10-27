using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Web;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    private static string ResetPatch(long persona) => new JsonObject
        { ["op"]="resetClub",["confirm"]=true,["confirmPersonaId"]=persona }.ToJsonString();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClubResetClearsOwnedStateAndRequiresNativeOnboardingEvenForLocalClubs(bool local)
    {
        using var setup = await CreateAsync();
        var player = local ? setup.Registry.Local.Player : PlayerIdentity.Of(DiscordId);
        var persona = player.PersonaId;
        var lease = await setup.Registry.AcquireAsync(player);
        var session = lease.Session;
        var target = session.Game;
        var originalSid = target.Sid;
        Assert.False(await session.Fut.Onboarding.IsAwaitingCreationAsync());
        Assert.NotEmpty(await target.ReadAsync(w => w.ListItemsAsync(), CancellationToken.None));
        await target.RunAsync("synthetic-reset-state", async work =>
        {
            await work.ExecuteAsync("UPDATE ultimateteam.clubs SET credits=12345,fifa_points=123 WHERE id=@club");
            await work.AddUnopenedPackAsync(304, "operator", untradeable: true);
            await work.ExecuteAsync("INSERT INTO ultimateteam.club_pack_purchases(club_id,pack_id,purchases) VALUES(@club,304,3)");
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.rivals_progress(club_id,skill_rating,division,placed,wins,draws,losses,weekly_score,weekly_games,fc_points,form)
                VALUES(@club,800,6,true,3,1,2,1200,6,250,'WWD')
                """);
            await work.SetFlagAsync("synthetic_old_progress");
        }, CancellationToken.None);
        lease.Dispose();

        var otherPersona = local ? PlayerIdentity.Of(DiscordId).PersonaId : setup.Registry.Local.Player.PersonaId;
        async Task<string> OtherHash() => await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<string>("""
            SELECT md5(to_jsonb(c)::text || coalesce((SELECT string_agg(to_jsonb(i)::text,',' ORDER BY i.id)
                FROM ultimateteam.items i WHERE i.club_id=c.id),'')) FROM ultimateteam.clubs c WHERE persona_id=@persona
            """, ("persona",otherPersona)));
        var otherBefore = await OtherHash();
        var reply = await Send(setup.Panel,"PATCH",$"/panel/api/players/{persona}",ResetPatch(persona));
        Assert.Equal(200,reply.Status);
        Assert.Equal(0,reply.Json!["club"]!["coins"]!.GetValue<long>());
        Assert.Equal(0,reply.Json["club"]!["points"]!.GetValue<long>());
        Assert.Equal(0,reply.Json["inventory"]!["total"]!.GetValue<long>());
        Assert.Equal(0,reply.Json["club"]!["unopenedPacks"]!.GetValue<long>());
        Assert.Equal(otherBefore,await OtherHash());
        Assert.NotEqual(originalSid,target.Sid);
        Assert.Equal(target.Sid,await target.ReadAsync(w => w.SidAsync(),CancellationToken.None));
        Assert.True(await session.Fut.Onboarding.IsAwaitingCreationAsync());
        await new Aurora21.UltimateTeam.Core.Services.Club.ClubMaintenanceService(session.Fut).RunAsync();
        using var source = NpgsqlDataSource.Create(setup.Runtime.ConnectionString);
        // Independent inventory of all current owning tables, including cascaded child state.
        await using var columns = source.CreateCommand("""
            SELECT table_name FROM information_schema.columns WHERE table_schema='ultimateteam'
                AND column_name='club_id' AND table_name<>'club_flags' ORDER BY table_name
            """);
        var tables = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        foreach (var table in tables)
        {
            await using var count = source.CreateCommand($"SELECT count(*) FROM ultimateteam.\"{table}\" WHERE club_id=@club");
            count.Parameters.AddWithValue("club",target.ClubId);
            Assert.True((long)(await count.ExecuteScalarAsync())! == 0,$"{table} retained reset club state");
        }
        Assert.True(setup.Issuer.Access.Find(DiscordId) is not null);
        Assert.Equal(1,await target.ReadAsync(w => w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.panel_edits WHERE persona_id=@persona AND patch->>'op'='resetClub'",("persona",persona)),CancellationToken.None));

        FutResponse Call(string method,string path,JsonObject? body=null) => session.Router.Handle(new FutRequest(method,
            "/ut/game/fifa21"+path,[],body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString())));
        Assert.Equal(465,Call("GET","/userMassInfo").Status);
        var accountInfo = JsonNode.Parse(Call("GET","/v2/user/accountinfo").Body)!;
        Assert.Empty(accountInfo["userAccountInfo"]!["personas"]![0]!["userClubList"]!.AsArray());
        Assert.Equal(200,Call("POST","/user/action/ONBOARDING_COUNTRY_1_SELECTED").Status);
        Assert.Equal(200,Call("POST","/user",new JsonObject { ["clubName"]="Fresh Club",["clubAbbr"]="NEW" }).Status);
        Assert.False(await session.Fut.Onboarding.IsAwaitingCreationAsync());
        Assert.Equal(200,Call("GET","/userMassInfo").Status);
        Assert.NotEmpty(await target.ReadAsync(w => w.ListItemsAsync(),CancellationToken.None));
        // The persisted reset state survives a new service instance; completed onboarding does not repeat.
        var reopened = await FutServices.CreateAsync(target);
        Assert.False(await reopened.Onboarding.IsAwaitingCreationAsync());
    }

    [Fact]
    public async Task ClubResetRequiresExactConfirmationAndAnIdleOfflinePlayer()
    {
        using var setup = await CreateAsync();
        var player = PlayerIdentity.Of(DiscordId);
        var path = $"/panel/api/players/{player.PersonaId}";
        Assert.Equal(400,(await Send(setup.Panel,"PATCH",path,"{\"op\":\"resetClub\"}")).Status);
        Assert.Equal(400,(await Send(setup.Panel,"PATCH",path,ResetPatch(player.PersonaId+1))).Status);
        using (var lease = await setup.Registry.AcquireAsync(player))
            Assert.Equal(409,(await Send(setup.Panel,"PATCH",path,ResetPatch(player.PersonaId))).Status);
        using (var presence = setup.Issuer.Presence!.Enter(DiscordId,() => {}))
            Assert.Equal(409,(await Send(setup.Panel,"PATCH",path,ResetPatch(player.PersonaId))).Status);
        var reset = setup.Registry.BeginClubReset(player.PersonaId);
        var waiting = setup.Registry.AcquireAsync(player);
        Assert.False(waiting.IsCompleted);
        Assert.Null(setup.Registry.TryAcquireExisting(player.Id));
        reset.Dispose();
        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(await admitted.Session.Game.ReadAsync(w => w.ListItemsAsync(),CancellationToken.None));
    }

    [Fact]
    public async Task ClubResetRollsBackAllStateIfAuditWriteFailsAndReleasesAdmission()
    {
        using var setup = await CreateAsync();
        var player = PlayerIdentity.Of(DiscordId);
        using var source = NpgsqlDataSource.Create(setup.Runtime.ConnectionString);
        await using (var fail = source.CreateCommand("""
            CREATE FUNCTION ultimateteam.synthetic_reject_reset() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.patch->>'op'='resetClub' THEN RAISE EXCEPTION 'synthetic reset rollback'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER synthetic_reject_reset BEFORE INSERT ON ultimateteam.panel_edits
                FOR EACH ROW EXECUTE FUNCTION ultimateteam.synthetic_reject_reset();
            """)) await fail.ExecuteNonQueryAsync();
        var before = await Send(setup.Panel,"GET",$"/panel/api/players/{player.PersonaId}");
        Assert.Equal(500,(await Send(setup.Panel,"PATCH",$"/panel/api/players/{player.PersonaId}",ResetPatch(player.PersonaId))).Status);
        var after = await Send(setup.Panel,"GET",$"/panel/api/players/{player.PersonaId}");
        Assert.True(JsonNode.DeepEquals(before.Json!["club"],after.Json!["club"]));
        Assert.True(JsonNode.DeepEquals(before.Json["inventory"],after.Json["inventory"]));
        Assert.True(JsonNode.DeepEquals(before.Json["edits"],after.Json["edits"]));
        using var admitted = await setup.Registry.AcquireAsync(player).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await admitted.Session.Fut.Onboarding.IsAwaitingCreationAsync());
    }
}
