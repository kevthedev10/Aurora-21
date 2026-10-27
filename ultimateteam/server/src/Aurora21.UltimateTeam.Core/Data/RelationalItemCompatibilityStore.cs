using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Service-facing wire API backed solely by relational item/state tables.</summary>
public sealed class RelationalItemCompatibilityStore
{
    public RelationalItemCompatibilityStore(RelationalItemWireMapper mapper) { Mapper = mapper; Squads = new(mapper.Game); Customisation = new(mapper.Game); Drafts = new(mapper.Game); }
    internal RelationalSquadCompatibilityStore Squads { get; }
    internal RelationalCustomisationStore Customisation { get; }
    internal RelationalDraftStore Drafts { get; }
    public RelationalItemWireMapper Mapper { get; }
    public async ValueTask<List<JsonObject>> ListAsync(IUnitOfWork work, string? pile) =>
        (await work.ListRelationalItemsAsync(pile)).Select(Mapper.Render).ToList();
    public async ValueTask<List<(long Id, JsonObject Data)>> RowsAsync(IUnitOfWork work, string? pile) =>
        (await work.ListRelationalItemsAsync(pile)).Select(i => (i.Header.NativeItemId.Value, Mapper.Render(i))).ToList();
    public async ValueTask<JsonObject?> GetAsync(IUnitOfWork work, long id) =>
        await work.GetRelationalItemAsync(new NativeItemId(id)) is { } item ? Mapper.Render(item) : null;
    /// <summary>
    /// The typed aggregates for these ids, for a caller that may not need a rendered document at all.
    /// </summary>
    public async ValueTask<List<RelationalItemAggregate>> AggregatesAsync(IUnitOfWork work, IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return new();
        return await work.ListRelationalItemsAsync(
            nativeIds: ids.Distinct().Select(id => new NativeItemId(id)).ToArray());
    }

    public async ValueTask<List<JsonObject>> GetManyAsync(IUnitOfWork work, IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return new();
        var items = (await work.ListRelationalItemsAsync(nativeIds: ids.Distinct().Select(id => new NativeItemId(id)).ToArray()))
            .ToDictionary(i => i.Header.NativeItemId.Value);
        return ids.Distinct().Where(items.ContainsKey).Select(id => Mapper.Render(items[id])).ToList();
    }
    public async ValueTask<JsonObject?> ItemOfAsync(IUnitOfWork w, long owner, long id, bool locked) { var scoped=ClubScopedUnitOfWork.For(w,owner); if(locked)await scoped.LockRelationalClubAsync();return await GetAsync(scoped,id); }
    public ValueTask<bool> ExistsAsync(IUnitOfWork w, long id) => w.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@id)",("id",id));
    public ValueTask<bool> AnyAsync(IUnitOfWork w) => w.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.items WHERE club_id=@club)");
    public async ValueTask<int> CountAsync(IUnitOfWork w, string pile) => checked((int)await w.CountRelationalItemsAsync(pile));
    public async ValueTask<List<(long ResourceId, long ItemId)>> OwnedAsync(IUnitOfWork w, long[]? resourceIds = null, bool permanentOnly = false) =>
        (await w.RelationalOwnedPlayerIdentitiesAsync(resourceIds, permanentOnly)).Where(i=>i.ResourceId is not null)
            .Select(i=>(i.ResourceId!.Value.Value,i.NativeItemId.Value)).ToList();
    public async ValueTask<int> SaveAsync(IUnitOfWork w, long id, JsonObject wire)
    {
        if (wire.IntKeep("id") != id) throw new InvalidOperationException("Wire/storage item numbers differ; conversion requires an audit.");
        return await SaveManyAsync(w,new[]{wire});
    }
    public ValueTask<int> SaveOfAsync(IUnitOfWork w,long owner,long id,JsonObject wire) { return SaveAsync(ClubScopedUnitOfWork.For(w,owner),id,wire); }
    public async ValueTask<int> SaveManyAsync(IUnitOfWork w,IEnumerable<JsonObject> wires)
    {
        var input=wires.ToArray(); if(input.Length==0)return 0;
        await w.LockRelationalClubAsync();
        var existing=(await w.ListRelationalItemsAsync(nativeIds:input.Select(i=>new NativeItemId(i.IntKeep("id"))).ToArray()))
            .ToDictionary(i=>i.Header.NativeItemId.Value);
        var rows=input.Select(wire=>{existing.TryGetValue(wire.IntKeep("id"),out var old);
            return Mapper.Capture(w.ClubId,wire,old?.Header.Id??default,old?.Pick,old?.Header.RewardSource);}).ToArray();
        await w.SaveRelationalItemsAsync(rows);
        await Customisation.SynchronizeSavedAsync(w,input.Where(i=>i.Text("itemType") is "kit" or "custom" or "ball" or "stadium").ToArray());
        return rows.Length;
    }
    public ValueTask<bool> DeleteAsync(IUnitOfWork w,long id)=>w.DeleteRelationalItemAsync(new NativeItemId(id));
    public ValueTask<bool> DeleteOfAsync(IUnitOfWork w,long owner,long id) { return DeleteAsync(ClubScopedUnitOfWork.For(w,owner),id); }
    public ValueTask<int> DeletePileAsync(IUnitOfWork w,string pile)=>w.DeleteRelationalPileAsync(pile);
    public ValueTask<List<(long Id,string? Pile)>> WithResourceAsync(IUnitOfWork w,long resource)=>w.QueryAsync("""
        SELECT i.native_item_id,i.pile FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
        WHERE i.club_id=@club AND d.native_resource_id=@resource ORDER BY i.native_item_id
        """,r=>(r.GetInt64(0),r.IsDBNull(1)?null:r.GetString(1)),("resource",resource));
    public async ValueTask<int> DeleteResourceAsync(IUnitOfWork w,long resource)
    {
        await w.LockRelationalClubAsync();
        await w.DetachOnboardingKitsAsync("i.definition_id IN (SELECT definition_id FROM ultimateteam.item_definitions WHERE native_resource_id=@resource)",("resource",resource));
        return await w.ExecuteAsync("""
            DELETE FROM ultimateteam.items i USING ultimateteam.item_definitions d
            WHERE i.club_id=@club AND i.definition_id=d.definition_id AND d.native_resource_id=@resource
            """,("resource",resource));
    }
    public async ValueTask<long> AddPackAsync(IUnitOfWork w,long pack,string source,long? created)=>(await w.AddRelationalUnopenedPackAsync(pack,source,created)).Id;
    public async ValueTask<List<long>> PackIdsAsync(IUnitOfWork w)=>(await w.RelationalUnopenedPacksAsync()).Select(p=>p.PackId).ToList();
    public async ValueTask<bool> TakePackAsync(IUnitOfWork w,long pack)=>await w.TakeRelationalUnopenedPackAsync(pack) is not null;
}
