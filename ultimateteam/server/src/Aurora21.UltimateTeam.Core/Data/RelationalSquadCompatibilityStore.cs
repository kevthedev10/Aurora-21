using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Typed squad membership/roles/settings; wire JSON is reconstructed, never stored wholesale.</summary>
internal sealed class RelationalSquadCompatibilityStore(GameContext game)
{
    private static readonly HashSet<string> Typed = new(StringComparer.Ordinal)
        { "id", "squadId", "name", "squadName", "active", "formation", "players", "manager", "captain", "kicktakers", "actives", "club", "tactics", "personaId", "changed" };
    private const string SquadFormationMentality="BALANCED";
    private sealed record Member(int Slot,long Definition,long Footballer,long? Item,long Wire,long? Kit);
    private static async ValueTask ShareAsync(IUnitOfWork w)
    {
        if(!w.InTransaction)throw new InvalidOperationException("Squad reads require a root transaction.");
        await w.EnsureRelationalClubLockAsync(exclusive:false);
    }
    public async ValueTask<List<JsonObject>> ListAsync(IUnitOfWork w,long? nativeId=null)
    {
        await ShareAsync(w);
        // Each squad arrives with its slots (slot order), its owned manager (at most one: unique per squad) and
        // its roles (ordinal order) as JSON, so the list costs one statement instead of four.
        var rows=await w.QueryAsync("""
            -- query: squad.list
            SELECT q.id,q.native_squad_id,q.name,q.active,q.formation,q.extension_payload,q.wire_representation,
                COALESCE((SELECT json_agg(json_build_array(p.slot_index,COALESCE(i.native_item_id,p.concept_wire_id),p.kit_number) ORDER BY p.slot_index)
                    FROM ultimateteam.squad_players p LEFT JOIN ultimateteam.items i ON i.id=p.item_id WHERE p.squad_id=q.id),'[]')::text,
                (SELECT i.native_item_id FROM ultimateteam.squad_managers m JOIN ultimateteam.items i ON i.id=m.item_id WHERE m.squad_id=q.id),
                COALESCE((SELECT json_agg(json_build_array(r.role_code,r.ordinal,COALESCE(i.native_item_id,p.concept_wire_id)) ORDER BY r.ordinal)
                    FROM ultimateteam.squad_roles r JOIN ultimateteam.squad_players p ON p.id=r.squad_player_id
                    LEFT JOIN ultimateteam.items i ON i.id=p.item_id WHERE r.squad_id=q.id),'[]')::text
            FROM ultimateteam.squads q WHERE q.club_id=@club
            """+(nativeId is null ? "" : " AND q.native_squad_id=@native")+" ORDER BY q.active DESC,q.native_squad_id",
            r=>(Header:(Id:r.GetInt64(0),Native:r.GetInt64(1),Name:r.GetString(2),Active:r.GetBoolean(3),Formation:r.GetString(4),Payload:FutJson.ParseObject(r.GetString(5))!,Shape:r.IsDBNull(6)?new JsonObject():FutJson.ParseObject(r.GetString(6))!),
                Players:r.GetString(7),Manager:r.IsDBNull(8)?(long?)null:r.GetInt64(8),Roles:r.GetString(9)),("native",nativeId));
        if(rows.Count==0)return new();
        var headers=rows.Select(row=>row.Header).ToList();
        var players=rows.SelectMany(row=>JsonNode.Parse(row.Players)!.AsArray().Select(p=>(Squad:row.Header.Id,Slot:p![0]!.GetValue<int>(),
            Wire:p[1]!.GetValue<long>(),Kit:p[2] is null?(long?)null:p[2]!.GetValue<long>()))).ToList();
        var managers=rows.Where(row=>row.Manager is not null).Select(row=>(Squad:row.Header.Id,Wire:row.Manager!.Value)).ToList();
        var roles=rows.SelectMany(row=>JsonNode.Parse(row.Roles)!.AsArray().Select(x=>(Squad:row.Header.Id,Code:x![0]!.GetValue<string>(),
            Ordinal:x[1]!.GetValue<int>(),Wire:x[2]!.GetValue<long>()))).ToList();
        return headers.Select(h=>{
            var result=h.Payload?.DeepClone() as JsonObject??new();
            if(result.Any(e=>Typed.Contains(e.Key)))throw new InvalidOperationException("Squad extension shadows typed state.");
            result["id"]=h.Native;result["squadId"]=h.Native;result["name"]=h.Name;result["squadName"]=h.Name;result["active"]=h.Active;result["formation"]=h.Formation;
            var slots=players.Where(p=>p.Squad==h.Id).ToDictionary(p=>p.Slot,p=>{
                var extra=h.Shape?.Obj("slotExtras")?.Obj(p.Slot.ToString())?.DeepClone() as JsonObject??new();
                if(extra.ContainsKey("itemData")||extra.ContainsKey("itemId")||extra.ContainsKey("index")||extra.ContainsKey("kitNumber"))throw new InvalidOperationException("Squad slot extension shadows typed state.");
                extra["index"]=p.Slot;extra["itemId"]=p.Wire;if(p.Kit is not null)extra["kitNumber"]=p.Kit.Value;return extra;
            });
            foreach(var empty in (h.Shape?.Arr("emptySlots")??new JsonArray()).OfType<JsonObject>()) {
                if(empty.ContainsKey("itemId")||empty.ContainsKey("itemData"))throw new InvalidOperationException("Empty squad slot contains an item reference.");
                slots.Add(checked((int)empty.Int("index")),(JsonObject)empty.DeepClone());
            }
            var order=(h.Shape?.Arr("slotOrder")??new JsonArray()).Select(n=>checked((int)JsonValues.Int(n))).ToArray();
            if(order.Length==0)order=slots.Keys.Order().ToArray();
            result["players"]=new JsonArray(order.Select(i=>(JsonNode?)slots[i]).ToArray());
            var manager=managers.FirstOrDefault(m=>m.Squad==h.Id);
            if(manager.Wire>0)result["manager"]=new JsonArray(new JsonObject{["id"]=manager.Wire,["dream"]=false});
            var ownRoles=roles.Where(r=>r.Squad==h.Id).ToArray();
            var captain=ownRoles.FirstOrDefault(r=>r.Code=="captain");if(captain.Wire>0)result["captain"]=captain.Wire;
            result["kicktakers"]=new JsonArray(ownRoles.Where(r=>r.Code=="kicktaker").Select(r=>(JsonNode?)new JsonObject{["index"]=r.Ordinal,["id"]=r.Wire,["dream"]=game.Cards.IsConceptItemId(r.Wire)}).ToArray());
            return result;
        }).ToList();
    }
    public async ValueTask<JsonObject?> GetAsync(IUnitOfWork w,long id)=>(await ListAsync(w,id)).FirstOrDefault();

    public async ValueTask SaveAsync(IUnitOfWork w,long native,string name,bool active,JsonObject wire)
    {
        if(native<=0||wire.Int("id",native)!=native||wire.Int("squadId",native)!=native)throw new ClubRuleException("INVALID_SQUAD_ID");
        await w.LockClubAsync();
        if(wire.Get("players") is not null and not JsonArray)throw new ClubRuleException("INVALID_SQUAD_SLOT");
        if(wire.Get("manager") is not null and not JsonArray || wire.Arr("manager") is { Count: > 1 })throw new ClubRuleException("INVALID_SQUAD_MANAGER");
        var slots=(wire.Arr("players")??new JsonArray()).OfType<JsonObject>().ToArray();
        if(wire.Arr("players") is { } rawSlots && rawSlots.Count!=slots.Length)throw new ClubRuleException("INVALID_SQUAD_SLOT");
        var indices=slots.Select((s,i)=>!s.ContainsKey("index") ? i : JsonValues.TryInt(s.Get("index"),out var slot) && slot>=0 && slot<23 ? (int)slot : -1).ToArray();
        if(indices.Distinct().Count()!=indices.Length||indices.Any(i=>i<0||i>=game.Rules.Club.Squad.Slots||i>=23))throw new ClubRuleException("INVALID_SQUAD_SLOT");
        long Reference(JsonObject s)=>JsonValues.Int(s.Get("itemId")??s.Obj("itemData")?.Get("id"));
        var refs=slots.Select(Reference).Where(i=>i>0).Distinct().ToArray();
        var owned=(await w.QueryAsync("""
            SELECT i.native_item_id,i.id,i.definition_id,p.footballer_id,i.family_id FROM ultimateteam.items i
            LEFT JOIN ultimateteam.player_card_definitions p ON p.definition_id=i.definition_id
            WHERE i.club_id=@club AND i.native_item_id=ANY(@refs)
            """,r=>(Wire:r.GetInt64(0),Id:r.GetInt64(1),Definition:r.GetInt64(2),Footballer:r.IsDBNull(3)?0:r.GetInt64(3),Family:r.GetInt64(4)),("refs",refs))).ToDictionary(i=>i.Wire);
        var conceptResources=refs.Where(i=>!owned.ContainsKey(i)&&game.Cards.IsConceptItemId(i)).Select(game.Cards.ConceptResourceId).ToArray();
        var concepts=(await w.QueryAsync("""
            SELECT d.native_resource_id,d.definition_id,p.footballer_id FROM ultimateteam.item_definitions d
            JOIN ultimateteam.player_card_definitions p ON p.definition_id=d.definition_id
            WHERE d.game_year=2021 AND d.native_resource_id=ANY(@resources)
            """,r=>(Resource:r.GetInt64(0),Definition:r.GetInt64(1),Footballer:r.GetInt64(2)),("resources",conceptResources))).ToDictionary(c=>c.Resource);
        var members=new List<Member>();var extras=new JsonObject();var empty=new JsonArray();
        for(var n=0;n<slots.Length;n++) {
            var s=slots[n];var index=indices[n];var id=Reference(s);
            if(id<=0) { var e=(JsonObject)s.DeepClone();e.Remove("itemId");e.Remove("itemData");e["index"]=index;empty.Add(e);continue; }
            long? kit=s.Get("kitNumber") is null ? null : s.Int("kitNumber");
            if(owned.TryGetValue(id,out var o)) {
                if(o.Family!=1)throw new ClubRuleException("SQUAD_ITEM_IS_NOT_PLAYER");
                members.Add(new(index,o.Definition,o.Footballer,o.Id,id,kit));
            } else if(game.Cards.IsConceptItemId(id)&&concepts.TryGetValue(game.Cards.ConceptResourceId(id),out var c))members.Add(new(index,c.Definition,c.Footballer,null,id,kit));
            else throw new ClubRuleException("SQUAD_PLAYER_NOT_OWNED");
            var extra=new JsonObject();foreach(var kv in s)if(kv.Key is not("index" or "itemId" or "itemData" or "kitNumber"))extra[kv.Key]=kv.Value?.DeepClone();
            extras[index.ToString()]=extra;
        }
        if(members.Select(m=>m.Footballer).Distinct().Count()!=members.Count)throw new ClubRuleException("DUPLICATE_FOOTBALLER_IN_SQUAD");
        var managerWire=(wire.Arr("manager")??new JsonArray()).OfType<JsonObject>().FirstOrDefault();
        var managerNative=managerWire is null ? 0 : (managerWire.Obj("itemData")??managerWire).Int("id");
        var manager=managerNative<=0 ? default : (await w.QueryAsync("SELECT id,definition_id FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@native AND family_id=2",r=>(Id:r.GetInt64(0),Definition:r.GetInt64(1)),("native",managerNative))).FirstOrDefault();
        if(managerNative>0&&manager.Id==0)throw new ClubRuleException("SQUAD_MANAGER_NOT_OWNED");
        if(active)await w.ExecuteAsync("UPDATE ultimateteam.squads SET active=false WHERE club_id=@club AND active");
        var payload=new JsonObject();foreach(var kv in wire)if(!Typed.Contains(kv.Key))payload[kv.Key]=kv.Value?.DeepClone();
        var shape=new JsonObject{["slotExtras"]=extras,["emptySlots"]=empty,["slotOrder"]=new JsonArray(indices.Select(i=>(JsonNode?)JsonValue.Create(i)).ToArray())};
        var squad=await w.ScalarAsync<long>("""
            INSERT INTO ultimateteam.squads(club_id,native_squad_id,name,active,formation,extension_schema_revision,extension_payload,wire_representation) VALUES(@club,@native,@name,@active,@formation,1,@payload,@shape)
            ON CONFLICT(club_id,native_squad_id) DO UPDATE SET name=excluded.name,active=excluded.active,formation=excluded.formation,extension_schema_revision=excluded.extension_schema_revision,extension_payload=excluded.extension_payload,wire_representation=excluded.wire_representation RETURNING id
            """,("native",native),("name",name),("active",active),("formation",wire.Str("formation",game.Rules.Club.Squad.DefaultFormation)),("payload",Json(payload)),("shape",Json(shape)));
        // The squad screen's formation is the Balanced game plan's. A formation picked on the squad screen arrives in this
        // PUT alone (no tactics POST), and FIFA applies the saved Balanced tactic's formation when it reloads the squad, so
        // a stale one there put the squad back to its old formation (10-05, 4-3-3(4) shown as 4-2-3-1).
        await w.ExecuteAsync("""
            UPDATE ultimateteam.squad_tactic_settings s SET text_value=@formation FROM ultimateteam.squad_tactics t
            WHERE s.tactic_id=t.id AND t.squad_id=@squad AND t.mentality=@mentality AND s.setting_code='formation' AND s.text_value IS DISTINCT FROM @formation
            """,("squad",squad),("mentality",SquadFormationMentality),("formation",wire.Str("formation",game.Rules.Club.Squad.DefaultFormation)));
        var rows=new JsonArray(members.Select(m=>(JsonNode?)new JsonObject{["slot"]=m.Slot,["definition"]=m.Definition,["footballer"]=m.Footballer,["item"]=m.Item,["concept"]=m.Item is null ? m.Wire : null,["kit"]=m.Kit}).ToArray());
        const string input="json_to_recordset(@rows) AS r(slot integer,definition bigint,footballer bigint,item bigint,concept bigint,kit bigint)";
        await w.ExecuteAsync("DELETE FROM ultimateteam.squad_roles WHERE squad_id=@squad",("squad",squad));
        await w.ExecuteAsync("DELETE FROM ultimateteam.squad_players WHERE squad_id=@squad AND NOT(slot_index=ANY(@slots))",("squad",squad),("slots",members.Select(m=>m.Slot).ToArray()));
        await w.ExecuteAsync($"UPDATE ultimateteam.squad_players p SET definition_id=r.definition,footballer_id=r.footballer,item_id=r.item,concept_wire_id=r.concept,kit_number=r.kit,reference_kind=CASE WHEN r.item IS NULL THEN 'concept' ELSE 'owned' END FROM {input} WHERE p.squad_id=@squad AND p.slot_index=r.slot",("squad",squad),("rows",Json(rows)));
        await w.ExecuteAsync($"INSERT INTO ultimateteam.squad_players(squad_id,slot_index,definition_id,footballer_id,reference_kind,item_id,concept_wire_id,kit_number) SELECT @squad,r.slot,r.definition,r.footballer,CASE WHEN r.item IS NULL THEN 'concept' ELSE 'owned' END,r.item,r.concept,r.kit FROM {input} WHERE NOT EXISTS(SELECT 1 FROM ultimateteam.squad_players p WHERE p.squad_id=@squad AND p.slot_index=r.slot)",("squad",squad),("rows",Json(rows)));
        await w.ExecuteAsync("DELETE FROM ultimateteam.squad_managers WHERE squad_id=@squad",("squad",squad));
        if(manager.Id>0)await w.ExecuteAsync("INSERT INTO ultimateteam.squad_managers(squad_id,reference_kind,item_id,definition_id) VALUES(@squad,'owned',@item,@definition)",("squad",squad),("item",manager.Id),("definition",manager.Definition));
        var memberByWire=members.ToDictionary(m=>m.Wire);var roles=new JsonArray();
        void AddRole(string code,int ordinal,long id) { if(memberByWire.TryGetValue(id,out var member))roles.Add(new JsonObject{["code"]=code,["ordinal"]=ordinal,["slot"]=member.Slot}); }
        AddRole("captain",0,wire.Int("captain"));
        var kicks=(wire.Arr("kicktakers")??new JsonArray()).OfType<JsonObject>().GroupBy(k=>JsonValues.TryInt(k.Get("index"),out var index) ? index : -1).Select(g=>g.Last());
        foreach(var kick in kicks) { var ordinal=JsonValues.TryInt(kick.Get("index"),out var index) ? index : -1;if(ordinal>=0&&ordinal<game.Rules.Club.Squad.KickTakers)AddRole("kicktaker",checked((int)ordinal),kick.Int("id")); }
        await w.ExecuteAsync("""
            INSERT INTO ultimateteam.squad_roles(squad_id,role_code,ordinal,squad_player_id)
            SELECT @squad,r.code,r.ordinal,p.id FROM json_to_recordset(@roles) AS r(code text,ordinal integer,slot integer)
            JOIN ultimateteam.squad_players p ON p.squad_id=@squad AND p.slot_index=r.slot
            """,("squad",squad),("roles",Json(roles)));
    }
    public async ValueTask<bool> DeleteAsync(IUnitOfWork w,long native)
    {
        await w.LockClubAsync();
        var id=await w.ScalarAsync<long?>("SELECT id FROM ultimateteam.squads WHERE club_id=@club AND native_squad_id=@native",("native",native));
        if(id is null)return false;
        await w.ExecuteAsync("DELETE FROM ultimateteam.squad_roles WHERE squad_id=@id; DELETE FROM ultimateteam.squad_players WHERE squad_id=@id; DELETE FROM ultimateteam.squad_managers WHERE squad_id=@id",("id",id));
        return await w.ExecuteAsync("DELETE FROM ultimateteam.squads WHERE club_id=@club AND id=@id",("id",id))>0;
    }
    // Key order of the captured FIFA 21 tactics save (2026-09-17); other keys follow in ordinal order.
    private static readonly string[] NativeTacticKeyOrder = { "squadId", "tactic", "formation", "positions", "instructions", "styles" };
    private static int TacticKeyRank(string key) => Array.IndexOf(NativeTacticKeyOrder, key) is var rank and >= 0 ? rank : NativeTacticKeyOrder.Length;
    public async ValueTask<Dictionary<string,JsonObject>> TacticsAsync(IUnitOfWork w,long native)
    {
        await ShareAsync(w);
        // Settings (kind 0) and instruction entries (kind 1) share one statement; each kind keeps the order and
        // column reads its own statement had.
        var rows=await w.QueryAsync("""
            -- query: squad.tactics
            SELECT 0 AS kind,t.mentality,s.setting_code AS code,s.integer_value,s.boolean_value,s.text_value,NULL::integer AS ordinal,false AS unsupported
            FROM ultimateteam.squad_tactics t JOIN ultimateteam.squads q ON q.id=t.squad_id
            LEFT JOIN ultimateteam.squad_tactic_settings s ON s.tactic_id=t.id
            WHERE q.club_id=@club AND q.native_squad_id=@native
            UNION ALL
            SELECT 1,t.mentality,i.instruction_code,i.integer_value,NULL::boolean,NULL::text,i.ordinal,i.text_value IS NOT NULL
            FROM ultimateteam.squad_tactic_instructions i JOIN ultimateteam.squad_tactics t ON t.id=i.tactic_id
            JOIN ultimateteam.squads q ON q.id=t.squad_id
            WHERE q.club_id=@club AND q.native_squad_id=@native
            ORDER BY kind,mentality,code,ordinal
            """,r=>(Kind:r.GetInt32(0),Mentality:r.GetString(1),Code:r.IsDBNull(2)?null:r.GetString(2),
                Setting:!r.IsDBNull(3)?JsonValue.Create(r.GetInt64(3)):!r.IsDBNull(4)?JsonValue.Create(r.GetBoolean(4)):!r.IsDBNull(5)?JsonValue.Create(r.GetString(5)):null,
                Index:r.IsDBNull(6)?0:r.GetInt32(6),Entry:r.IsDBNull(3)?(long?)null:r.GetInt64(3),Unsupported:r.GetBoolean(7)),("native",native));
        var settings=rows.Where(r=>r.Kind==0).Select(r=>(r.Mentality,r.Code,Value:r.Setting)).ToList();
        var entries=rows.Where(r=>r.Kind==1).Select(r=>(r.Mentality,Code:r.Code!,r.Index,Value:r.Entry,r.Unsupported)).ToList();
        if(entries.Any(e=>e.Unsupported||e.Value is null))throw new NotSupportedException("Text tactic instructions have no traced wire mapping; audit required.");
        return settings.GroupBy(r=>r.Mentality).ToDictionary(g=>g.Key,g=>{
            var fields=new List<(string Key,JsonNode? Value)>();
            foreach(var r in g)if(r.Code is not null)fields.Add((r.Code,r.Value));
            foreach(var list in entries.Where(e=>e.Mentality==g.Key).GroupBy(e=>e.Code,StringComparer.Ordinal)) {
                if(fields.Any(f=>f.Key==list.Key))throw new InvalidOperationException("Tactic setting and instruction list share a field name; audit required.");
                fields.Add((list.Key,new JsonArray(list.Select(e=>(JsonNode?)new JsonObject{["index"]=e.Index,["value"]=e.Value!.Value}).ToArray())));
            }
            return TacticDocument(fields);
        },StringComparer.Ordinal);
    }
    /// <summary>
    /// Scalar fields become typed settings. Each captured list field (positions, instructions, styles and
    /// any other list of the same shape) becomes ordered instruction rows: code = field, ordinal = index,
    /// integer value = value. Values are stored as sent; their meaning is not interpreted.
    /// </summary>
    public async ValueTask<int> SaveTacticAsync(IUnitOfWork w,long native,string mentality,JsonObject wire)
    {
        var (values,lists)=TacticRows(wire);
        await w.LockClubAsync();
        var squad=await w.ScalarAsync<long?>("SELECT id FROM ultimateteam.squads WHERE club_id=@club AND native_squad_id=@native",("native",native));
        if(squad is null)throw new ClubRuleException("SQUAD_NOT_FOUND");
        var tactic=await w.ScalarAsync<long>("INSERT INTO ultimateteam.squad_tactics(squad_id,mentality) VALUES(@squad,@mentality) ON CONFLICT(squad_id,mentality) DO UPDATE SET mentality=excluded.mentality RETURNING id",("squad",squad),("mentality",mentality));
        if(await w.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.squad_tactic_instructions WHERE tactic_id=@tactic AND text_value IS NOT NULL)",("tactic",tactic)))
            throw new NotSupportedException("Text tactic instructions have no traced wire mapping; audit required.");
        // A save replaces the mentality's whole entry, as the legacy document upsert did.
        await w.ExecuteAsync("DELETE FROM ultimateteam.squad_tactic_settings WHERE tactic_id=@tactic",("tactic",tactic));
        await w.ExecuteAsync("DELETE FROM ultimateteam.squad_tactic_instructions WHERE tactic_id=@tactic",("tactic",tactic));
        await w.ExecuteAsync("INSERT INTO ultimateteam.squad_tactic_settings(tactic_id,setting_code,integer_value,boolean_value,text_value) SELECT @tactic,r.code,r.integer,r.boolean,r.text FROM json_to_recordset(@values) AS r(code text,integer bigint,boolean boolean,text text)",("tactic",tactic),("values",Json(values)));
        await w.ExecuteAsync("INSERT INTO ultimateteam.squad_tactic_instructions(tactic_id,instruction_code,ordinal,integer_value) SELECT @tactic,r.code,r.ordinal,r.value FROM json_to_recordset(@lists) AS r(code text,ordinal integer,value bigint)",("tactic",tactic),("lists",Json(lists)));
        return 1;
    }
    /// <summary>
    /// A posted tactic entry as typed rows: each scalar field becomes a setting row, each captured list field
    /// (positions, instructions, styles ...) its ordered {code, ordinal, value} entries. Shared with Draft tactics.
    /// </summary>
    internal static (JsonArray Values,JsonArray Lists) TacticRows(JsonObject wire)
    {
        var values=new JsonArray();var lists=new JsonArray();
        foreach(var kv in wire) {
            if(kv.Value is JsonArray list) {
                // Only the captured shape is accepted: a non-empty list of {index, value} integers in ascending index order.
                if(list.Count==0)throw new ClubRuleException("UNSUPPORTED_TACTIC_FIELD");
                var previous=-1L;
                foreach(var node in list) {
                    if(node is not JsonObject entry||entry.Count!=2||!JsonValues.IsStrictInt(entry.Get("index"),out var index)||!JsonValues.IsStrictInt(entry.Get("value"),out var value)
                        ||index<=previous||index>int.MaxValue)throw new ClubRuleException("UNSUPPORTED_TACTIC_FIELD");
                    previous=index;
                    lists.Add(new JsonObject{["code"]=kv.Key,["ordinal"]=index,["value"]=value});
                }
                continue;
            }
            var row=new JsonObject{["code"]=kv.Key};
            if(kv.Value is JsonValue v && v.TryGetValue<bool>(out var b))row["boolean"]=b;
            else if(kv.Value is JsonValue n && n.TryGetValue<long>(out var i))row["integer"]=i;
            else if(kv.Value is JsonValue s && s.TryGetValue<string>(out var text))row["text"]=text;
            else throw new ClubRuleException("UNSUPPORTED_TACTIC_FIELD");
            values.Add(row);
        }
        return (values,lists);
    }
    /// <summary>A tactic entry rebuilt from its fields in the native key order.</summary>
    internal static JsonObject TacticDocument(IEnumerable<(string Key,JsonNode? Value)> fields)
    {
        var result=new JsonObject();
        foreach(var f in fields.OrderBy(f=>TacticKeyRank(f.Key)).ThenBy(f=>f.Key,StringComparer.Ordinal))result[f.Key]=f.Value;
        return result;
    }
    internal ValueTask<bool> IsItemBoundAsync(IUnitOfWork w,long native) => w.ScalarAsync<bool>("""
        SELECT EXISTS(SELECT 1 FROM ultimateteam.squad_players p JOIN ultimateteam.squads q ON q.id=p.squad_id JOIN ultimateteam.items i ON i.id=p.item_id WHERE q.club_id=@club AND i.native_item_id=@native)
            OR EXISTS(SELECT 1 FROM ultimateteam.squad_managers m JOIN ultimateteam.squads q ON q.id=m.squad_id JOIN ultimateteam.items i ON i.id=m.item_id WHERE q.club_id=@club AND i.native_item_id=@native)
        """,("native",native));
    /// <summary>
    /// Takes owned items out of every squad of the club: each slot holding one becomes an empty slot (as a save of the
    /// squad without that card records it, the shape migration 050 writes), its roles go, and a manager leaves its squad.
    /// </summary>
    internal async ValueTask ReleaseItemsAsync(IUnitOfWork w,long[] natives)
    {
        if(natives.Length==0)return;
        await w.LockClubAsync();
        await w.ExecuteAsync("""
            -- query: squad.release-items
            WITH released AS (
                SELECT p.id,p.squad_id,p.slot_index FROM ultimateteam.squad_players p
                JOIN ultimateteam.squads q ON q.id=p.squad_id JOIN ultimateteam.items i ON i.id=p.item_id
                WHERE q.club_id=@club AND i.club_id=@club AND i.native_item_id=ANY(@native)),
            roles AS (DELETE FROM ultimateteam.squad_roles WHERE squad_player_id IN (SELECT id FROM released)),
            players AS (DELETE FROM ultimateteam.squad_players WHERE id IN (SELECT id FROM released)),
            managers AS (DELETE FROM ultimateteam.squad_managers m USING ultimateteam.squads q,ultimateteam.items i
                WHERE q.id=m.squad_id AND i.id=m.item_id AND q.club_id=@club AND i.club_id=@club AND i.native_item_id=ANY(@native))
            UPDATE ultimateteam.squads q SET wire_representation=jsonb_set(jsonb_set(q.wire_representation::jsonb,
                    '{emptySlots}',COALESCE(q.wire_representation::jsonb->'emptySlots','[]'::jsonb)||r.empties),
                    '{slotExtras}',COALESCE(q.wire_representation::jsonb->'slotExtras','{}'::jsonb)-r.slots)::json
            FROM (SELECT p.squad_id,
                      jsonb_agg(COALESCE(q2.wire_representation::jsonb->'slotExtras'->p.slot_index::text,'{}'::jsonb)
                          ||jsonb_build_object('index',p.slot_index) ORDER BY p.slot_index) AS empties,
                      array_agg(p.slot_index::text) AS slots
                  FROM released p JOIN ultimateteam.squads q2 ON q2.id=p.squad_id GROUP BY p.squad_id) r
            WHERE q.id=r.squad_id AND q.wire_representation IS NOT NULL
            """,("native",natives));
    }
}
