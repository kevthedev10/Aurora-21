using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data;

internal sealed class RelationalCustomisationStore
{
    private readonly GameContext _game;
    private readonly Dictionary<long, MyStadiumSlot> _slots;
    private readonly Dictionary<string, long> _states;
    internal sealed record Binding(long Slot,long Definition,long? Item,long Native,string State);
    private sealed record Selection(long Slot,long Definition,long? Item,long Native,string State);

    internal RelationalCustomisationStore(GameContext game)
    {
        _game=game;
        _slots=game.Rules.Club.MyStadiumSlots.Select((s,i)=>(Id:s.SlotId??i+1,Slot:s)).ToDictionary(s=>s.Id,s=>s.Slot);
        _states=_slots.ToDictionary(s=>s.Value.ItemState,s=>s.Key,StringComparer.Ordinal);
    }
    // The club's own virtual default for a slot, with the club's virtual item id.
    private JsonObject? Default(MyStadiumSlot slot,long clubId) => _game.Rules.Club.RenderedDefaultActiveItems(clubId).SingleOrDefault(i=>i.Text("itemState")==slot.ItemState);

    public async ValueTask<List<Binding>> ReadAsync(IUnitOfWork w)=>(await ReadWithStadiumAsync(w)).Bindings;

    /// <summary>The slot bindings and the club's stored stadium, which the active club items need together, in one statement.</summary>
    public async ValueTask<(List<Binding> Bindings,long? SelectedStadiumId)> ReadWithStadiumAsync(IUnitOfWork w)
    {
        if(!w.InTransaction)throw new InvalidOperationException("Slot reads require a root transaction.");
        await w.EnsureRelationalClubLockAsync(exclusive:false);
        // Starts from the club row, so the stadium is returned when there are no bindings (one row with a null slot).
        var rows=await w.QueryAsync("""
            -- query: customisation.slot-bindings
            SELECT c.selected_stadium_id,b.slot_id,b.definition_id,b.item_id,b.reference_kind,i.native_item_id,d.native_resource_id,i.item_state,i.pile
            FROM ultimateteam.clubs c LEFT JOIN (ultimateteam.club_slot_bindings b JOIN ultimateteam.item_definitions d ON d.definition_id=b.definition_id
            LEFT JOIN ultimateteam.items i ON i.id=b.item_id) ON b.club_id=c.id WHERE c.id=@club ORDER BY b.slot_id
            """,r=>(Stadium:r.IsDBNull(0)?(long?)null:r.GetInt64(0),Slot:r.IsDBNull(1)?(long?)null:r.GetInt64(1),Definition:r.IsDBNull(2)?0:r.GetInt64(2),Item:r.IsDBNull(3)?(long?)null:r.GetInt64(3),Kind:r.IsDBNull(4)?"":r.GetString(4),Native:r.IsDBNull(5)?0:r.GetInt64(5),Resource:r.IsDBNull(6)?0:r.GetInt64(6),State:r.IsDBNull(7)?null:r.GetString(7),Pile:r.IsDBNull(8)?null:r.GetString(8)));
        var result=new List<Binding>();
        foreach(var row in rows.Where(r=>r.Slot is not null)) {
            if(!_slots.TryGetValue(row.Slot!.Value,out var slot))throw new NotSupportedException("Unknown persisted customisation slot.");
            var native=row.Native;
            if(row.Kind=="verified virtual default") {
                var template=Default(slot,w.ClubId);
                if(template is null||template.Int("resourceId")!=row.Resource)throw new NotSupportedException("Persisted virtual selection has no verified rule template.");
                native=template.Int("id");
            } else if(row.Kind!="owned"||native==0||row.State!=slot.ItemState||row.Pile!="club")throw new InvalidOperationException("Slot binding and owned item state differ; audit required.");
            result.Add(new(row.Slot.Value,row.Definition,row.Item,native,slot.ItemState));
        }
        return (result,rows.Count>0?rows[0].Stadium:null);
    }
    public async ValueTask<JsonObject> WireBindingsAsync(IUnitOfWork w)
    {
        var result=new JsonObject();foreach(var b in await ReadAsync(w))result[b.Slot.ToString(CultureInfo.InvariantCulture)]=b.Native;return result;
    }
    public async ValueTask<int> SetAsync(IUnitOfWork w,JsonObject bindings)
    {
        await w.LockClubAsync();
        var requests=new Dictionary<long,long>();
        foreach(var kv in bindings) {
            if(!long.TryParse(kv.Key,NumberStyles.None,CultureInfo.InvariantCulture,out var slot)||!_slots.ContainsKey(slot)||!JsonValues.TryInt(kv.Value,out var native)||native<0)throw new ClubRuleException("INVALID_CUSTOMISATION_BINDING");
            requests.Add(slot,native);
        }
        var ids=requests.Values.Where(i=>i>0).Distinct().ToArray();
        var owned=(await w.QueryAsync("""
            SELECT i.native_item_id,i.id,i.definition_id,d.native_item_type,d.native_subtype,k.is_locked_override
            FROM ultimateteam.items i JOIN ultimateteam.customisation_items k ON k.item_id=i.id
            JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id WHERE i.club_id=@club AND i.native_item_id=ANY(@ids)
            """,r=>(Native:r.GetInt64(0),Id:r.GetInt64(1),Definition:r.GetInt64(2),Type:r.GetString(3),Subtype:r.IsDBNull(4)?0:r.GetInt64(4),Locked:!r.IsDBNull(5)&&r.GetBoolean(5)),("ids",ids))).ToDictionary(i=>i.Native);
        var resources=requests.Select(r=>Default(_slots[r.Key],w.ClubId)?.Int("resourceId")??0).Distinct().ToArray();
        var defaults=(await w.QueryAsync("SELECT native_resource_id,definition_id FROM ultimateteam.item_definitions WHERE game_year=2021 AND family_id=4 AND native_resource_id=ANY(@resources)",r=>(Resource:r.GetInt64(0),Definition:r.GetInt64(1)),("resources",resources))).ToDictionary(d=>d.Resource,d=>d.Definition);
        var selections=new List<Selection>();
        foreach(var request in requests) {
            var slot=_slots[request.Key];var template=Default(slot,w.ClubId);
            if(request.Value==0) { if(!slot.Emptiable||slot.Locked)throw new ClubRuleException("CUSTOMISATION_SLOT_CANNOT_BE_CLEARED");continue; }
            if(owned.TryGetValue(request.Value,out var item)) {
                if(slot.Locked||item.Locked)throw new ClubRuleException("CUSTOMISATION_SLOT_OR_ITEM_LOCKED");
                if(item.Type!=slot.ItemType||item.Subtype!=slot.CardSubType)throw new ClubRuleException("CUSTOMISATION_ITEM_DOES_NOT_MATCH_SLOT");
                selections.Add(new(request.Key,item.Definition,item.Id,item.Native,slot.ItemState));
            } else if(template is not null && template.Int("id")==request.Value && defaults.TryGetValue(template.Int("resourceId"),out var definition))selections.Add(new(request.Key,definition,null,request.Value,slot.ItemState));
            else throw new ClubRuleException("CUSTOMISATION_ITEM_NOT_OWNED");
        }
        if(selections.Where(s=>s.Item is not null).Select(s=>s.Item).Distinct().Count()!=selections.Count(s=>s.Item is not null))throw new ClubRuleException("CUSTOMISATION_ITEM_ASSIGNED_TWICE");
        await ApplyAsync(w,selections,requests.Keys.ToArray(),stamp:true);
        return 1;
    }
    private async ValueTask ApplyAsync(IUnitOfWork w,IReadOnlyList<Selection> selected,long[] slots,bool stamp)
    {
        var itemIds=selected.Where(s=>s.Item is not null).Select(s=>s.Item!.Value).ToArray();
        // Moving one instance to another slot also releases its previous binding.
        await w.ExecuteAsync("DELETE FROM ultimateteam.club_slot_bindings WHERE club_id=@club AND (slot_id=ANY(@slots) OR item_id=ANY(@items))",("slots",slots),("items",itemIds));
        var changed=await w.QueryAsync("UPDATE ultimateteam.items SET item_state='free' WHERE club_id=@club AND family_id=4 AND (item_state=ANY(@states) OR id=ANY(@items)) RETURNING id",r=>r.GetInt64(0),("states",slots.Select(s=>_slots[s].ItemState).ToArray()),("items",itemIds));
        var rows=new JsonArray(selected.Select(s=>(JsonNode?)new JsonObject{["slot"]=s.Slot,["definition"]=s.Definition,["item"]=s.Item,["state"]=s.State}).ToArray());
        await w.ExecuteAsync("UPDATE ultimateteam.items i SET item_state=r.state,pile='club'"+(stamp?",timestamp=@time":"")+" FROM json_to_recordset(@rows) AS r(slot bigint,definition bigint,item bigint,state text) WHERE i.club_id=@club AND i.id=r.item",("rows",Json(rows)),("time",w.NowSeconds));
        // Explicit equipment changes must replace stale missing/null/string descriptors
        // for the header fields they write, while retaining unrelated compatibility data.
        await w.ExecuteAsync("""
            UPDATE ultimateteam.item_extensions e SET wire_representation=(
                SELECT jsonb_object_agg(p.key,CASE WHEN p.key IN ('missing','nulls','stringNumbers') THEN
                    COALESCE((SELECT jsonb_agg(a.v) FROM jsonb_array_elements(p.value) a(v)
                        WHERE NOT ((a.v #>> '{}')='itemState' OR (e.item_id=ANY(@selected) AND
                            ((a.v #>> '{}')='pile' OR (@stamp AND (a.v #>> '{}')='timestamp'))))), '[]'::jsonb)
                    ELSE p.value END) FROM jsonb_each(e.wire_representation::jsonb) p
                )::json WHERE e.item_id=ANY(@changed) AND e.wire_representation IS NOT NULL
            """,("changed",changed.ToArray()),("selected",itemIds),("stamp",stamp));
        await w.ExecuteAsync("""
            INSERT INTO ultimateteam.club_slot_bindings(club_id,slot_id,reference_kind,item_id,definition_id)
            SELECT @club,r.slot,CASE WHEN r.item IS NULL THEN 'verified virtual default' ELSE 'owned' END,r.item,r.definition
            FROM json_to_recordset(@rows) AS r(slot bigint,definition bigint,item bigint,state text)
            """,("rows",Json(rows)));
    }
    public async ValueTask SynchronizeSavedAsync(IUnitOfWork w,IReadOnlyList<JsonObject> saved)
    {
        if(saved.Count==0)return;
        var ids=saved.Select(i=>i.Int("id")).ToArray();
        var rows=await w.QueryAsync("""
            SELECT i.id,i.native_item_id,i.definition_id,i.item_state,i.pile,d.native_item_type,d.native_subtype,k.is_locked_override
            FROM ultimateteam.items i JOIN ultimateteam.customisation_items k ON k.item_id=i.id
            JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id WHERE i.club_id=@club AND i.native_item_id=ANY(@ids)
            """,r=>(Id:r.GetInt64(0),Native:r.GetInt64(1),Definition:r.GetInt64(2),State:r.IsDBNull(3)?"":r.GetString(3),Pile:r.IsDBNull(4)?"":r.GetString(4),Type:r.GetString(5),Subtype:r.IsDBNull(6)?0:r.GetInt64(6),Locked:!r.IsDBNull(7)&&r.GetBoolean(7)),("ids",ids));
        var selected=new List<Selection>();var released=new List<long>();
        foreach(var item in rows) {
            if(item.Pile!="club"||!_states.TryGetValue(item.State,out var slotId)) { released.Add(item.Id);continue; }
            var slot=_slots[slotId];
            if(slot.Locked||item.Locked||item.Type!=slot.ItemType||item.Subtype!=slot.CardSubType)throw new ClubRuleException("CUSTOMISATION_ITEM_DOES_NOT_MATCH_SLOT");
            selected.Add(new(slotId,item.Definition,item.Id,item.Native,item.State));
        }
        if(selected.Select(s=>s.Slot).Distinct().Count()!=selected.Count)throw new ClubRuleException("CUSTOMISATION_SLOT_ASSIGNED_TWICE");
        if(released.Count>0) {
            await w.ExecuteAsync("DELETE FROM ultimateteam.club_slot_bindings WHERE club_id=@club AND item_id=ANY(@items)",("items",released.ToArray()));
            await w.ExecuteAsync("UPDATE ultimateteam.items SET item_state='free' WHERE club_id=@club AND id=ANY(@items) AND item_state=ANY(@states)",("items",released.ToArray()),("states",_states.Keys.ToArray()));
        }
        if(selected.Count>0)await ApplyAsync(w,selected,selected.Select(s=>s.Slot).ToArray(),stamp:false);
    }
    public async ValueTask<JsonObject> EquipAsync(IUnitOfWork w,long id,string state)
    {
        if(!_states.TryGetValue(state,out var slot))throw new ClubRuleException("unsupported club customisation slot");
        await SetAsync(w,new JsonObject{[slot.ToString(CultureInfo.InvariantCulture)]=id});
        return (await w.GetItemAsync(id))!;
    }
    public ValueTask<bool> IsBoundAsync(IUnitOfWork w,long native) => w.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.club_slot_bindings b JOIN ultimateteam.items i ON i.id=b.item_id WHERE b.club_id=@club AND i.native_item_id=@native)",("native",native));
}
