using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Squad storage backed by the relational schema.</summary>
public static class SquadRepository
{
    public static ValueTask<List<JsonObject>> ListSquadsAsync(this IUnitOfWork w) => w.RelationalItems.Squads.ListAsync(w);
    public static ValueTask<JsonObject?> GetSquadAsync(this IUnitOfWork w,long id) => w.RelationalItems.Squads.GetAsync(w,id);
    public static async ValueTask<(long Id,JsonObject Data)?> FirstSquadAsync(this IUnitOfWork w) {
        var first=(await w.ListSquadsAsync()).FirstOrDefault();return first is null ? null : (first.Int("id"),first);
    }
    public static ValueTask<bool> AnySquadsAsync(this IUnitOfWork w) => w.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.squads WHERE club_id=@club)");
    public static async ValueTask<long> ActiveSquadIdAsync(this IUnitOfWork w) => await w.ScalarAsync<long?>("SELECT native_squad_id FROM ultimateteam.squads WHERE club_id=@club AND active ORDER BY native_squad_id LIMIT 1") ?? 1;
    public static ValueTask<bool?> SquadActiveAsync(this IUnitOfWork w,long id) => w.ScalarAsync<bool?>("SELECT active FROM ultimateteam.squads WHERE club_id=@club AND native_squad_id=@id",("id",id));
    public static ValueTask<bool> AnyActiveSquadAsync(this IUnitOfWork w) => w.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.squads WHERE club_id=@club AND active)");
    public static async ValueTask<bool> SetActiveSquadAsync(this IUnitOfWork w,long id) {
        await w.LockClubAsync();if(await w.SquadActiveAsync(id) is null)return false;
        await w.ExecuteAsync("UPDATE ultimateteam.squads SET active=false WHERE club_id=@club AND active AND native_squad_id<>@id",("id",id));
        return await w.ExecuteAsync("UPDATE ultimateteam.squads SET active=true WHERE club_id=@club AND native_squad_id=@id",("id",id))>0;
    }
    public static async ValueTask<long> AllocateSquadIdAsync(this IUnitOfWork w) {
        await w.LockClubAsync();return Math.Max(1,await w.ScalarAsync<long>("SELECT COALESCE(MAX(native_squad_id),0)+1 FROM ultimateteam.squads WHERE club_id=@club"));
    }
    public static ValueTask SaveSquadAsync(this IUnitOfWork w,long id,string name,bool active,JsonObject data) => w.RelationalItems.Squads.SaveAsync(w,id,name,active,data);
    public static async ValueTask<int> UpdateSquadDataAsync(this IUnitOfWork w,long id,bool active,JsonObject data) {
        await w.LockClubAsync();
        if(await w.SquadActiveAsync(id) is null)return 0;
        await w.SaveSquadAsync(id,data.Str("name",data.Str("squadName")),active,data);return 1;
    }
    public static ValueTask<bool> DeleteSquadAsync(this IUnitOfWork w,long id) => w.RelationalItems.Squads.DeleteAsync(w,id);
    public static ValueTask<Dictionary<string,JsonObject>> SquadTacticsAsync(this IUnitOfWork w,long id) => w.RelationalItems.Squads.TacticsAsync(w,id);
    public static ValueTask<int> SaveSquadTacticAsync(this IUnitOfWork w,long id,string mentality,JsonObject data) => w.RelationalItems.Squads.SaveTacticAsync(w,id,mentality,data);
}
