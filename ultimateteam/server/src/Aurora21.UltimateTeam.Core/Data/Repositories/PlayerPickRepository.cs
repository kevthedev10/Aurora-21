using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Stable relational choices; previews never become owned item rows.</summary>
public static class PlayerPickRepository
{
    public static ValueTask<List<PlayerPickChoiceRecord>> RelationalPlayerPickChoicesAsync(this IUnitOfWork work, NativeItemId token) =>
        work.QueryAsync("""
            SELECT c.id,c.token_item_id,c.ordinal,c.definition_id,c.reserved_wire_item_id,c.timestamp,c.is_duplicate
            FROM ultimateteam.player_pick_choices c JOIN ultimateteam.items i ON i.id=c.token_item_id
            WHERE i.club_id=@club AND i.native_item_id=@token ORDER BY c.ordinal
            """, RelationalItemRepository.ReadPlayerPickChoiceRecord, ("token", token.Value));

    /// <summary>
    /// Records the pick-group filters of a token just made (store packs 48-65): its choices are specials only, or limited
    /// to these rarities. Set once; the item save keeps them.
    /// </summary>
    public static ValueTask<int> SetPlayerPickFiltersAsync(this IUnitOfWork work, long tokenId, bool promoOnly, IReadOnlyCollection<long> rareflags,
        long maxRating = 99, string? resourcePool = null, string? drawConfig = null) =>
        work.ExecuteAsync("""
            UPDATE ultimateteam.player_pick_items p SET promo_only=@promo, rareflags=@rareflags, max_rating=@max, resource_pool=@pool, draw_config=@config::jsonb
            FROM ultimateteam.items i WHERE i.id=p.item_id AND i.club_id=@club AND i.native_item_id=@token
            """, ("promo", promoOnly), ("rareflags", rareflags.ToArray()), ("max", (int)maxRating), ("pool", resourcePool), ("config", drawConfig), ("token", tokenId));

    /// <summary>The caller draws definitions once. Repeated reveal returns the original reserved choices.</summary>
    public static async ValueTask<List<PlayerPickChoiceRecord>> RevealRelationalPlayerPickAsync(this IUnitOfWork work,
        NativeItemId token, IReadOnlyList<(DefinitionId Definition, bool Duplicate)> choices)
    {
        await work.LockRelationalClubAsync();
        var item = await work.GetRelationalItemAsync(token);
        if (item?.Pick is not { } pick) throw new InvalidOperationException("Player pick token not found.");
        if (pick.State == "revealed") return await work.RelationalPlayerPickChoicesAsync(token);
        if (pick.State != "unrevealed") throw new InvalidOperationException("Player pick is not revealable.");
        if (choices.Count != pick.ChoiceCount || choices.Count == 0 || choices.Select(c => c.Definition).Distinct().Count() != choices.Count)
            throw new ArgumentException("Pick choices must match the token count and be distinct.", nameof(choices));
        var eligible = await work.ScalarAsync<long>("""
            SELECT count(*) FROM ultimateteam.player_card_definitions p
            JOIN ultimateteam.item_definitions d ON d.definition_id=p.definition_id
            WHERE p.definition_id=ANY(@definitions) AND p.rating>=@minimum
              AND d.game_year=(SELECT game_year FROM ultimateteam.item_definitions WHERE definition_id=@tokenDefinition)
            """, ("definitions", choices.Select(c => c.Definition.Value).ToArray()), ("minimum", pick.MinRating),
            ("tokenDefinition", item.Header.DefinitionId.Value));
        if (eligible != choices.Count) throw new ArgumentException("Pick choices must be eligible player definitions for the token's game year.", nameof(choices));
        var range = await work.ReserveNativeItemIdsAsync(choices.Count);
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.player_pick_choices(token_item_id,ordinal,definition_id,reserved_wire_item_id,timestamp,is_duplicate)
            SELECT @item,ordinal,definition_id,reserved_wire_item_id,timestamp,is_duplicate FROM json_to_recordset(@rows)
            AS x(ordinal integer,definition_id bigint,reserved_wire_item_id bigint,timestamp bigint,is_duplicate boolean)
            """, ("item", item.Header.Id.Value), ("rows", RelationalItemRepository.Rows(choices.Select((c, index) => new {
                ordinal = index, definition_id = c.Definition.Value, reserved_wire_item_id = checked(range.First.Value + index),
                timestamp = work.NowSeconds, is_duplicate = c.Duplicate }))));
        await work.ExecuteAsync("UPDATE ultimateteam.player_pick_items SET state='revealed' WHERE item_id=@item", ("item", item.Header.Id.Value));
        return await work.RelationalPlayerPickChoicesAsync(token);
    }

    /// <summary>Selection/grant commit together. The factory supplies typed owned state for the revealed choice.</summary>
    public static async ValueTask<RelationalItemHeader> SelectRelationalPlayerPickAsync(this IUnitOfWork work,
        NativeItemId token, long choiceId, Func<PlayerPickChoiceRecord, RelationalItemAggregate> buildItem)
    {
        await work.LockRelationalClubAsync();
        var item = await work.GetRelationalItemAsync(token);
        if (item?.Pick?.State != "revealed") throw new InvalidOperationException("Player pick is not revealed or has already been redeemed.");
        var choice = (await work.RelationalPlayerPickChoicesAsync(token)).SingleOrDefault(c => c.Id == choiceId)
            ?? throw new ArgumentException("Selected choice does not belong to this token.", nameof(choiceId));
        var granted = buildItem(choice);
        if (granted.Header.Id.Value != 0 || granted.Header.ClubId != work.ClubId || granted.Header.FamilyId != 1
            || granted.Header.DefinitionId != choice.DefinitionId || granted.Header.NativeItemId != choice.ReservedWireItemId
            || granted.Header.Timestamp != choice.Timestamp || granted.Header.Pile != "unassigned")
            throw new ArgumentException("Grant must preserve the revealed definition, native number and timestamp.", nameof(buildItem));
        if (await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@native)",
            ("native", choice.ReservedWireItemId.Value)))
            throw new InvalidOperationException("Reserved choice number is already owned.");
        await work.DeleteRelationalItemAsync(token); // Cascades choices, as in the existing selection lifecycle.
        return (await work.SaveRelationalItemsAsync(new[] { granted })).Single();
    }
}
