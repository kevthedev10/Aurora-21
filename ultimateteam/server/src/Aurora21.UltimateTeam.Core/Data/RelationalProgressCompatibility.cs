using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Data;

internal static class RelationalProgressCompatibility
{
    internal static async ValueTask<PlayerPickState?> Pick(IUnitOfWork w,long token)
    {
        var item=await w.GetRelationalItemAsync(new NativeItemId(token));
        if(item?.Pick is not {} p)return null;
        var choices=await w.RelationalPlayerPickChoicesAsync(new NativeItemId(token));
        return new PlayerPickState(p.MinRating,p.ChoiceCount,new JsonArray(choices.Select(c=>(JsonNode?)w.RelationalItems.Mapper.RenderChoice(c,token)).ToArray()));
    }
    internal static async ValueTask<int> SavePick(IUnitOfWork w,long token,PlayerPickState state)
    {
        await w.LockRelationalClubAsync();
        var item=await w.GetRelationalItemAsync(new NativeItemId(token));
        if(item?.Pick is not {} p)throw new InvalidOperationException("Player pick token not found.");
        if(state.MinRating!=p.MinRating||state.ChoiceCount!=p.ChoiceCount)throw new InvalidOperationException("Pick token rules cannot change after creation.");
        var prior=await w.RelationalPlayerPickChoicesAsync(new NativeItemId(token));
        if(prior.Count!=0)
        {
            if(prior.Count!=state.Choices.Count||prior.Where((c,index)=>c.ReservedWireItemId.Value!=((JsonObject)state.Choices[index]!).IntKeep("id")
                || !JsonNode.DeepEquals(w.RelationalItems.Mapper.RenderChoice(c,token),state.Choices[index])).Any())
                throw new InvalidOperationException("Revealed choices are immutable.");
            return 0;
        }
        if(state.Choices.Count==0)return 0;
        if(p.State!="unrevealed"||state.Choices.Count!=p.ChoiceCount)throw new InvalidOperationException("Invalid pick reveal count/state.");
        var captured=state.Choices.OfType<JsonObject>().Select((wire,index)=>(wire,index,item:w.RelationalItems.Mapper.Capture(w.ClubId,wire))).ToArray();
        if(captured.Length!=state.Choices.Count||captured.Select(c=>c.item.Header.DefinitionId).Distinct().Count()!=captured.Length)
            throw new InvalidOperationException("Pick choices must be distinct players.");
        var last=(await w.RelationalAccountAsync()).LastAllocatedNativeItemId.Value;
        if(captured.Any(c=>c.item.Header.FamilyId!=1||c.item.Header.NativeItemId.Value<=0||c.item.Header.NativeItemId.Value>last)
            ||captured.Select(c=>c.item.Header.NativeItemId).Distinct().Count()!=captured.Length)
            throw new InvalidOperationException("Pick choices require distinct reserved native numbers.");
        var eligible=await w.ScalarAsync<long>("""
            SELECT count(*) FROM ultimateteam.player_card_definitions p JOIN ultimateteam.item_definitions d ON d.definition_id=p.definition_id
            WHERE p.definition_id=ANY(@definitions) AND p.rating>=@min AND d.game_year=(SELECT game_year FROM ultimateteam.item_definitions WHERE definition_id=@tokenDefinition)
            """,("definitions",captured.Select(c=>c.item.Header.DefinitionId.Value).ToArray()),("min",p.MinRating),("tokenDefinition",item.Header.DefinitionId.Value));
        if(eligible!=captured.Length)throw new InvalidOperationException("Ineligible player pick choice.");
        if(await w.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.items WHERE club_id=@club AND native_item_id=ANY(@ids))",
            ("ids",captured.Select(c=>c.item.Header.NativeItemId.Value).ToArray())))throw new InvalidOperationException("Choice numbers are already owned.");
        await w.ExecuteAsync("""
            INSERT INTO ultimateteam.player_pick_choices(token_item_id,ordinal,definition_id,reserved_wire_item_id,timestamp,is_duplicate)
            SELECT @item,ordinal,definition_id,reserved_wire_item_id,timestamp,is_duplicate FROM json_to_recordset(@rows)
            AS x(ordinal integer,definition_id bigint,reserved_wire_item_id bigint,timestamp bigint,is_duplicate boolean)
            """,("item",item.Header.Id.Value),("rows",RelationalItemRepository.Rows(captured.Select(c=>new {
                ordinal=c.index,definition_id=c.item.Header.DefinitionId.Value,reserved_wire_item_id=c.item.Header.NativeItemId.Value,
                timestamp=c.item.Header.Timestamp,is_duplicate=c.wire.Bool("duplicate") }))));
        return await w.ExecuteAsync("UPDATE ultimateteam.player_pick_items SET state='revealed' WHERE item_id=@item",("item",item.Header.Id.Value));
    }
    internal static async ValueTask<int> DeletePick(IUnitOfWork w,long token)
    {
        // Existing selection deletes the token before this call; its choices have already cascaded.
        if(await w.ItemExistsAsync(token))throw new InvalidOperationException("Delete the token and grant the selected item in one transaction.");
        return 0;
    }
    /// <summary>
    /// The club's objective header, counters and claims in one statement. A missing header reads as zero XP and
    /// unclaimed; counters come in stat-code order and claims in scope, ordinal order, as separate reads did. Season XP and
    /// the Season Pass claims are the given season's (migration 166): stored under another season, the club's progress in
    /// this one comes from its season history (none yet: 0 XP, no level claimed).
    /// </summary>
    internal static async ValueTask<ObjectivesState> Objectives(IUnitOfWork w,long season)
    {
        var (storedSeason,xp,claimed,statsJson,claimsJson,baselinesJson,historyXp,historyJson)=(await w.QueryAsync("""
            -- query: objectives.state
            SELECT o.season_id, o.user_xp, o.reward_claimed,
                COALESCE((SELECT json_agg(json_build_array(c.stat_code, c.value) ORDER BY c.stat_code)
                    FROM ultimateteam.objective_counters c WHERE c.club_id=@club), '[]')::text,
                COALESCE((SELECT json_agg(json_build_array(c.scope, c.claim_id) ORDER BY c.scope, c.ordinal)
                    FROM ultimateteam.objective_claims c WHERE c.club_id=@club), '[]')::text,
                COALESCE((SELECT json_agg(json_build_array(b.group_id, b.stat_code, b.value))
                    FROM ultimateteam.objective_baselines b WHERE b.club_id=@club), '[]')::text,
                h.user_xp, COALESCE(h.claims, '[]')::text
            FROM (SELECT) AS one LEFT JOIN ultimateteam.objectives o ON o.club_id=@club
                LEFT JOIN ultimateteam.objective_season_history h ON h.club_id=@club AND h.season_id=@season
            """,r=>(Season:r.IsDBNull(0)?(long?)null:r.GetInt64(0),Xp:r.IsDBNull(1)?0L:r.GetInt64(1),Claimed:!r.IsDBNull(2)&&r.GetBoolean(2),
                Stats:r.GetString(3),Claims:r.GetString(4),Baselines:r.GetString(5),HistoryXp:r.IsDBNull(6)?0L:r.GetInt64(6),History:r.GetString(7)),
            ("season",season))).Single();
        var stats=JsonNode.Parse(statsJson)!.AsArray().Select(s=>(Code:s![0]!.GetValue<string>(),Value:s[1]!.GetValue<long>())).ToList();
        var claims=JsonNode.Parse(claimsJson)!.AsArray().Select(c=>(Scope:c![0]!.GetValue<string>(),Id:c[1]!.GetValue<long>())).ToList();
        if(storedSeason is { } stored && stored!=season)
        {
            // The stored Season Pass progress is another season's: this season's comes from the history.
            xp=historyXp;
            claims=claims.Where(c=>!IsSeasonScope(c.Scope)).Concat(JsonNode.Parse(historyJson)!.AsArray()
                .Select(c=>(Scope:c![0]!.GetValue<string>(),Id:c[1]!.GetValue<long>()))).ToList();
        }
        return new ObjectivesState(xp,claimed,claims.Where(c=>c.Scope=="level").Select(c=>c.Id).ToArray()) {
            SeasonId=season,
            Stats=stats.ToDictionary(s=>s.Code,s=>s.Value), ClaimedObjectives=claims.Where(c=>c.Scope=="objective").Select(c=>c.Id).ToArray(),
            ClaimedGroups=claims.Where(c=>c.Scope=="group").Select(c=>c.Id).ToArray(),
            LevelOptions=claims.Where(c=>c.Scope=="level_option").ToDictionary(c=>c.Id/ObjectivesState.LevelOptionScale,c=>c.Id%ObjectivesState.LevelOptionScale),
            Baselines=JsonNode.Parse(baselinesJson)!.AsArray().ToDictionary(b=>(b![0]!.GetValue<long>(),b[1]!.GetValue<string>()),b=>b![2]!.GetValue<long>()) };
    }
    /// <summary>The claim scopes that belong to one season: the Season Pass levels and the options picked for them.</summary>
    private static bool IsSeasonScope(string scope) => scope is "level" or "level_option";
    /// <summary>
    /// Stores the state in one statement: the header, counters and claims are upserted (a row is only rewritten
    /// when its value changed) and counters or claims no longer present are deleted. The stored rows equal what
    /// deleting and re-inserting everything produced; surviving rows keep their surrogate id, which nothing reads.
    /// The deletes and upserts touch disjoint keys, so no row is modified twice within the statement. Saving under another
    /// season than the stored one first moves the stored season's XP and Season Pass claims to the season history (every CTE
    /// reads the rows as they were before the statement) and takes this season's history row out, as it is live again.
    /// </summary>
    internal static async ValueTask<int> SaveObjectives(IUnitOfWork w,ObjectivesState state)
    {
        await w.LockRelationalClubAsync();
        var options=state.LevelOptions.Where(o=>o.Value>0).Select(o=>o.Key*ObjectivesState.LevelOptionScale+o.Value).Order().ToList();
        var claims=new[]{("level",state.ClaimedLevels),("objective",state.ClaimedObjectives),("group",state.ClaimedGroups),("level_option",(IReadOnlyList<long>)options)}
            .SelectMany(s=>s.Item2.Select((id,index)=>new {scope=s.Item1,claim_id=id,ordinal=index}));
        await w.ExecuteAsync("""
            -- query: objectives.save
            WITH archived AS (
                INSERT INTO ultimateteam.objective_season_history(club_id,season_id,user_xp,claims)
                SELECT o.club_id, o.season_id, o.user_xp,
                    COALESCE((SELECT jsonb_agg(jsonb_build_array(c.scope, c.claim_id) ORDER BY c.scope, c.ordinal)
                        FROM ultimateteam.objective_claims c WHERE c.club_id=@club AND c.scope IN ('level','level_option')), '[]')
                FROM ultimateteam.objectives o WHERE o.club_id=@club AND o.season_id<>@season
                ON CONFLICT(club_id,season_id) DO UPDATE SET user_xp=excluded.user_xp,claims=excluded.claims,archived_at=now()
            ), resumed AS (
                DELETE FROM ultimateteam.objective_season_history h WHERE h.club_id=@club AND h.season_id=@season
                    AND EXISTS (SELECT 1 FROM ultimateteam.objectives o WHERE o.club_id=@club AND o.season_id<>@season)
            ), header AS (
                INSERT INTO ultimateteam.objectives(club_id,user_xp,reward_claimed,season_id) VALUES(@club,@xp,@claimed,@season)
                ON CONFLICT(club_id) DO UPDATE SET user_xp=excluded.user_xp,reward_claimed=excluded.reward_claimed,season_id=excluded.season_id
                WHERE (objectives.user_xp,objectives.reward_claimed,objectives.season_id) IS DISTINCT FROM (excluded.user_xp,excluded.reward_claimed,excluded.season_id)
            ), stats AS (
                SELECT stat_code,value FROM json_to_recordset(@stats) AS x(stat_code text,value bigint)
            ), removed_stats AS (
                DELETE FROM ultimateteam.objective_counters c WHERE c.club_id=@club
                    AND NOT EXISTS (SELECT 1 FROM stats s WHERE s.stat_code=c.stat_code)
            ), saved_stats AS (
                INSERT INTO ultimateteam.objective_counters(club_id,stat_code,value) SELECT @club,stat_code,value FROM stats
                ON CONFLICT(club_id,stat_code) DO UPDATE SET value=excluded.value
                WHERE objective_counters.value IS DISTINCT FROM excluded.value
            ), claims AS (
                SELECT scope,claim_id,ordinal FROM json_to_recordset(@claims) AS x(scope text,claim_id bigint,ordinal integer)
            ), removed_claims AS (
                DELETE FROM ultimateteam.objective_claims c WHERE c.club_id=@club
                    AND NOT EXISTS (SELECT 1 FROM claims k WHERE k.scope=c.scope AND k.claim_id=c.claim_id)
            )
            INSERT INTO ultimateteam.objective_claims(club_id,scope,claim_id,ordinal) SELECT @club,scope,claim_id,ordinal FROM claims
            ON CONFLICT(club_id,scope,claim_id) DO UPDATE SET ordinal=excluded.ordinal
            WHERE objective_claims.ordinal IS DISTINCT FROM excluded.ordinal
            """,("xp",state.UserXp),("claimed",state.RewardClaimed),("season",state.SeasonId),
            ("stats",RelationalItemRepository.Rows(state.Stats.Select(s=>new {stat_code=s.Key,value=s.Value}))),
            ("claims",RelationalItemRepository.Rows(claims)));
        return 1;
    }
}
