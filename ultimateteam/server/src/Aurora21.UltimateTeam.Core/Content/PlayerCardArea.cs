using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>
/// FIFA 21 player card definitions as a content area (live-content-editing-plan.md, step 5). A change is keyed by the
/// card's native resource id, <c>{"resourceId": 158023}</c>, and its value is <c>{"card": {…}}</c> naming every editable
/// field:
/// <list type="bullet">
/// <item>the <c>player_card_definitions</c> columns the catalogue loader reads, under their column names: rating, position,
/// foot, weak foot, skill moves, work rates, the six face stats and the detailed attributes. Allowed text values are the
/// ones the catalogue already uses (positions, feet, work rates), not a list in code;</item>
/// <item>the card's in-game head: <c>head_resource_id</c> (show another card of the same player's head, or null for the
/// card's own) and <c>custom_head_id</c> (an uploaded head of the same player, migration 074, or null);</item>
/// <item><c>traits</c>: the ids of the card's visible traits (ultimateteam.traits). Hidden (CPU AI) traits are left as they are. A trait an
/// operator adds is recorded with source player id 0 and the hash of <see cref="OperatorTraitSource"/>, so it is never
/// mistaken for one read from FIFA's data.</item>
/// </list>
/// Insert adds a card based on an existing card, as migration 056 does: <c>{"base": 158023, "cardTypeId": 7, "card": {…}}</c>
/// copies the base card's definition, card and trait rows under the new resource id, which must be the player's asset id
/// in a later revision namespace (asset id + n × PlayerCatalog.ResourceRevisionSize, n ≥ 1), so FIFA 21 never reads it as
/// another base player, then writes the requested fields over the copy. Hiding a card is not done here: a card leaves packs
/// through rules/cards.json <c>grantOnlyResourceIds</c> (the Rules page).
/// </summary>
public sealed class PlayerCardArea : IContentArea
{
    public const string AreaName = "player_card";

    /// <summary>The provenance text whose SHA-256 marks a trait an operator added in the panel.</summary>
    public const string OperatorTraitSource = "aurora21-panel:operator-edit";

    /// <summary>Text columns, checked against the values the catalogue already uses.</summary>
    private static readonly string[] TextColumns = ["position", "preferred_foot", "work_rate_attack", "work_rate_defense"];

    /// <summary>Whole-number columns and their FIFA ranges.</summary>
    private static readonly (string Column, long Min, long Max)[] NumberColumns =
    [
        ("rating", 1, 99), ("weak_foot", 1, 5), ("skill_moves", 1, 5),
        ("face_slot_0", 1, 99), ("face_slot_1", 1, 99), ("face_slot_2", 1, 99), ("face_slot_3", 1, 99), ("face_slot_4", 1, 99), ("face_slot_5", 1, 99),
        ("acceleration", 1, 99), ("sprint_speed", 1, 99), ("positioning", 1, 99), ("finishing", 1, 99), ("shot_power", 1, 99),
        ("long_shots", 1, 99), ("volleys", 1, 99), ("penalties", 1, 99), ("vision", 1, 99), ("crossing", 1, 99),
        ("free_kick_accuracy", 1, 99), ("short_passing", 1, 99), ("long_passing", 1, 99), ("curve", 1, 99), ("agility", 1, 99),
        ("balance", 1, 99), ("reactions", 1, 99), ("ball_control", 1, 99), ("dribbling", 1, 99), ("composure", 1, 99),
        ("interceptions", 1, 99), ("heading_accuracy", 1, 99), ("marking", 1, 99), ("standing_tackle", 1, 99),
        ("sliding_tackle", 1, 99), ("jumping", 1, 99), ("stamina", 1, 99), ("strength", 1, 99), ("aggression", 1, 99),
        ("gk_diving", 1, 99), ("gk_handling", 1, 99), ("gk_kicking", 1, 99), ("gk_reflexes", 1, 99), ("gk_speed", 1, 99),
        ("gk_positioning", 1, 99),
    ];

    /// <summary>The head columns: null, or an id checked against the same player's cards and uploads.</summary>
    private static readonly string[] HeadColumns = ["head_resource_id", "custom_head_id"];

    private const string Traits = "traits";

    private static readonly string[] Columns = [.. TextColumns, .. NumberColumns.Select(column => column.Column), .. HeadColumns];

    public string Name => AreaName;

    // Catalogue rows go first; rules that name cards (grant-only lists, rewards) apply after them.
    public int ApplyOrder => 5;

    public JsonObject NormalizeKey(JsonObject key)
    {
        if (!WholeNumber(key["resourceId"], out var id) || id < 1)
            throw new ContentChangeException(400, "resourceId must be a card's native resource id.");
        return new JsonObject { ["resourceId"] = id };
    }

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
    {
        var rows = await work.QueryAsync("""
            SELECT to_jsonb(p)::text FROM ultimateteam.player_card_definitions p
            JOIN ultimateteam.item_definitions d ON d.definition_id=p.definition_id
            WHERE d.family_id=1 AND d.game_year=2021 AND d.native_resource_id=@resource
            """, row => JsonNode.Parse(row.GetString(0))!.AsObject(), ("resource", Resource(key)));
        if (rows.Count == 0) return null;
        var card = new JsonObject();
        foreach (var column in Columns) card[column] = rows[0][column]?.DeepClone();
        var traits = await VisibleTraitsAsync(work, rows[0]["definition_id"]!.GetValue<long>());
        card[Traits] = new JsonArray(traits.Select(id => (JsonNode?)id).ToArray());
        return new JsonObject { ["card"] = card, ["cardTypeId"] = rows[0]["card_type_id"]!.DeepClone() };
    }

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation == ContentOperation.Hide)
            throw new ContentChangeException(400, "A card is not hidden here: to keep it out of packs, add its resource id to grantOnlyResourceIds in rules/cards.json.");
        if (operation == ContentOperation.Insert)
        {
            await NewCardAsync(work, key, after);
            return;
        }
        var live = await ReadAsync(work, key) ?? throw new ContentChangeException(404, "No player card has that resource id.");
        if (after is null || after.Any(field => field.Key is not ("card" or "cardTypeId"))) throw new ContentChangeException(400, "A player card edit contains card and an optional cardTypeId.");
        var (columns, traits) = await ChangesAsync(work, (JsonObject)live["card"]!, after["card"], await AssetAsync(work, Resource(key)));
        var rareflag = await work.ScalarAsync<long>("""
            SELECT r.native_rareflag FROM ultimateteam.player_card_definitions p
            JOIN ultimateteam.card_types t USING(card_type_id) JOIN ultimateteam.native_rarities r USING(native_rarity_id)
            JOIN ultimateteam.item_definitions d USING(definition_id) WHERE d.native_resource_id=@resource AND d.family_id=1
            """, ("resource", Resource(key)));
        WholeNumber(after["card"]!["rating"], out var rating);
        var targetRare = rareflag;
        if (after.ContainsKey("cardTypeId"))
        {
            if (!WholeNumber(after["cardTypeId"], out var type)) throw new ContentChangeException(400, "cardTypeId must be an existing card type.");
            var targets = await work.QueryAsync("SELECT r.native_rareflag FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r USING(native_rarity_id) WHERE t.card_type_id=@type", row => row.GetInt64(0), ("type", type));
            if (targets.Count == 0) throw new ContentChangeException(400, "That card type does not exist.");
            targetRare = targets[0];
            if ((rareflag is 12 or 84) != (targetRare is 12 or 84)) throw new ContentChangeException(400, "An Icon can only use Icon card types; other players cannot use Icon types.");
            if (targetRare is 89 or 92 or 93 or 94 && targetRare != rareflag) throw new ContentChangeException(400, "Icon Swaps types are unavailable in the player card editor.");
        }
        if (rareflag is 12 or 84 && rating < 85) throw new ContentChangeException(400, "Icon overall must be at least 85.");
        if (columns.Count == 0 && traits is null && (!after.ContainsKey("cardTypeId") || JsonNode.DeepEquals(after["cardTypeId"], live["cardTypeId"]))) throw new ContentChangeException(400, "Change at least one card field.");
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var resource = Resource(key);
        if (operation == ContentOperation.Insert)
        {
            var (baseDefinition, cardType) = await NewCardAsync(work, key, after);
            var definition = await work.ScalarAsync<long>("SELECT max(definition_id)+1 FROM ultimateteam.item_definitions");
            // The base card's rows under the new ids (migration 056's method); new cards list after every existing one.
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.item_definitions
                SELECT (jsonb_populate_record(NULL::ultimateteam.item_definitions, to_jsonb(d) || jsonb_build_object(
                    'definition_id', @definition, 'native_resource_id', @resource,
                    'catalog_order', (SELECT max(o.catalog_order)+1 FROM ultimateteam.item_definitions o WHERE o.family_id=1)))).*
                FROM ultimateteam.item_definitions d WHERE d.definition_id=@base
                """, ("definition", definition), ("resource", resource), ("base", baseDefinition));
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.player_card_definitions
                SELECT (jsonb_populate_record(NULL::ultimateteam.player_card_definitions, to_jsonb(p)
                    || jsonb_build_object('definition_id', @definition, 'card_type_id', @type, 'replacement_resource_ids', NULL))).*
                FROM ultimateteam.player_card_definitions p WHERE p.definition_id=@base
                """, ("definition", definition), ("type", cardType), ("base", baseDefinition));
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
                SELECT @definition, trait_id, source_native_player_id, source_sha256 FROM ultimateteam.player_card_traits WHERE definition_id=@base
                """, ("definition", definition), ("base", baseDefinition));
        }
        // An edit, or a new card that is now a copy of its base: the requested fields are written over the live row.
        var live = (JsonObject)((await ReadAsync(work, key)) ?? throw new ContentChangeException(404, "No player card has that resource id."))["card"]!;
        var (columns, traits) = await ChangesAsync(work, live, after!["card"], await AssetAsync(work, resource));
        var definitionId = await work.ScalarAsync<long>(
            "SELECT definition_id FROM ultimateteam.item_definitions WHERE family_id=1 AND game_year=2021 AND native_resource_id=@resource", ("resource", resource));
        foreach (var (column, value) in columns)
        {
            // The column comes from the fixed lists above, never from the request.
            await work.ExecuteAsync($"UPDATE ultimateteam.player_card_definitions SET {column}=@value WHERE definition_id=@definition",
                ("value", value ?? DBNull.Value), ("definition", definitionId));
        }
        if (traits is not null) await WriteTraitsAsync(work, definitionId, traits);
        var currentType = await work.ScalarAsync<long>("SELECT card_type_id FROM ultimateteam.player_card_definitions WHERE definition_id=@definition", ("definition", definitionId));
        var requestedType = WholeNumber(after!["cardTypeId"], out var explicitType) ? explicitType : currentType;
        WholeNumber(after!["card"]!["rating"], out var updatedRating);
        var normalizedType = await StandardTypeForRatingAsync(work, requestedType, updatedRating);
        if (normalizedType != currentType)
            await work.ExecuteAsync("UPDATE ultimateteam.player_card_definitions SET card_type_id=@type WHERE definition_id=@definition", ("type", normalizedType), ("definition", definitionId));
    }

    /// <summary>Checks a new card: a free resource id in the player's revision namespace, an existing base card and card type, valid fields.</summary>
    private async ValueTask<(long BaseDefinition, long CardType)> NewCardAsync(IUnitOfWork work, JsonObject key, JsonObject? after)
    {
        if (after is null || after.Count != 3 || !WholeNumber(after["base"], out var baseResource) || !WholeNumber(after["cardTypeId"], out var cardType))
            throw new ContentChangeException(400, "A new player card is {\"base\": resourceId, \"cardTypeId\": id, \"card\": {…}}.");
        if (await ReadAsync(work, key) is not null) throw new ContentChangeException(409, "A card with that resource id already exists.");
        var bases = await work.QueryAsync("""
            SELECT p.definition_id, d.native_asset_id FROM ultimateteam.item_definitions d
            JOIN ultimateteam.player_card_definitions p ON p.definition_id=d.definition_id
            WHERE d.family_id=1 AND d.game_year=2021 AND d.native_resource_id=@base
            """, row => (Definition: row.GetInt64(0), Asset: row.IsDBNull(1) ? 0L : row.GetInt64(1)), ("base", baseResource));
        if (bases.Count == 0) throw new ContentChangeException(404, "The base card does not exist.");
        var (baseDefinition, asset) = bases[0];
        var resource = Resource(key);
        if (asset <= 0 || resource <= asset || (resource - asset) % PlayerCatalog.ResourceRevisionSize != 0)
            throw new ContentChangeException(400, $"A new card's resource id is its player's asset id ({asset}) plus a whole number of {PlayerCatalog.ResourceRevisionSize}.");
        if (!await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.card_types WHERE card_type_id=@type)", ("type", cardType)))
            throw new ContentChangeException(400, "That card type does not exist.");
        var baseCard = (JsonObject)(await ReadAsync(work, new JsonObject { ["resourceId"] = baseResource }))!["card"]!;
        await ChangesAsync(work, baseCard, after["card"], asset);
        WholeNumber(after["card"]!["rating"], out var rating);
        var sourceRare = await work.ScalarAsync<long>("""
            SELECT r.native_rareflag FROM ultimateteam.player_card_definitions p
            JOIN ultimateteam.card_types t USING(card_type_id) JOIN ultimateteam.native_rarities r USING(native_rarity_id)
            WHERE p.definition_id=@base
            """, ("base", baseDefinition));
        var targetRare = await work.ScalarAsync<long>("""
            SELECT r.native_rareflag FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r USING(native_rarity_id)
            WHERE t.card_type_id=@type
            """, ("type", cardType));
        if (targetRare is 89 or 92 or 93 or 94) throw new ContentChangeException(400, "Icon Swaps tokens cannot be created with the player card editor.");
        if ((sourceRare is 12 or 84) != (targetRare is 12 or 84)) throw new ContentChangeException(400, "An Icon can only use Icon card types; other players cannot use Icon types.");
        if (sourceRare is 12 or 84 && rating < 85) throw new ContentChangeException(400, "Icon overall must be at least 85.");
        return (baseDefinition, await StandardTypeForRatingAsync(work, cardType, rating));
    }

    private static async Task<long> StandardTypeForRatingAsync(IUnitOfWork work, long type, long rating)
    {
        var level = rating >= 75 ? "gold" : rating >= 65 ? "silver" : "bronze";
        var matches = await work.QueryAsync("""
            SELECT target.card_type_id FROM ultimateteam.card_types original
            JOIN ultimateteam.native_rarities r ON r.native_rarity_id=original.native_rarity_id
            JOIN ultimateteam.native_rarities target_r ON target_r.native_rareflag =
                CASE WHEN r.native_rareflag=3 AND @level='bronze' THEN 1 ELSE r.native_rareflag END
            JOIN ultimateteam.card_types target ON target.native_rarity_id=target_r.native_rarity_id
            WHERE original.card_type_id=@type AND r.native_rareflag IN (0,1,3) AND lower(target.card_level)=@level
            ORDER BY target.card_type_id
            """, row => row.GetInt64(0), ("type", type), ("level", level));
        return matches.Count > 0 ? matches[0] : type;
    }

    /// <summary>
    /// The fields whose value differs from the live card, each checked; the change must name every editable field. Traits
    /// come back as the full new list of visible trait ids, or null when they do not change.
    /// </summary>
    private static async ValueTask<(List<(string Column, object? Value)> Columns, List<long>? Traits)> ChangesAsync(
        IUnitOfWork work, JsonObject live, JsonNode? fields, long asset)
    {
        if (fields is not JsonObject card)
            throw new ContentChangeException(400, "A player card change carries \"card\": {…} with every editable field.");
        foreach (var (name, _) in card)
            if (name != Traits && !Columns.Contains(name, StringComparer.Ordinal)) throw new ContentChangeException(400, $"{name} is not an editable player card field.");
        foreach (var name in Columns.Append(Traits))
            if (!card.ContainsKey(name)) throw new ContentChangeException(400, $"The change is missing {name}.");
        var changed = new List<(string, object?)>();
        foreach (var column in TextColumns)
        {
            if (JsonNode.DeepEquals(card[column], live[column])) continue;
            if (card[column] is not JsonValue value || value.GetValueKind() != JsonValueKind.String || value.GetValue<string>() is not { Length: > 0 } text)
                throw new ContentChangeException(400, $"{column} must be text.");
            var allowed = await work.QueryAsync($"SELECT DISTINCT {column} FROM ultimateteam.player_card_definitions WHERE {column} IS NOT NULL",
                row => row.GetString(0));
            if (!allowed.Contains(text, StringComparer.Ordinal))
                throw new ContentChangeException(400, $"{column} must be one of {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
            changed.Add((column, text));
        }
        foreach (var (column, min, max) in NumberColumns)
        {
            if (JsonNode.DeepEquals(card[column], live[column])) continue;
            if (!WholeNumber(card[column], out var number) || number < min || number > max)
                throw new ContentChangeException(400, $"{column} must be a whole number from {min} to {max}.");
            changed.Add((column, number));
        }
        foreach (var column in HeadColumns)
        {
            if (JsonNode.DeepEquals(card[column], live[column])) continue;
            if (card[column] is null) { changed.Add((column, null)); continue; }
            if (!WholeNumber(card[column], out var id) || id < 1) throw new ContentChangeException(400, $"{column} must be null or an id.");
            var samePlayer = column == "custom_head_id"
                ? await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.custom_player_heads WHERE head_id=@id AND native_asset_id=@asset)",
                    ("id", id), ("asset", asset))
                : await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.item_definitions WHERE family_id=1 AND native_resource_id=@id AND native_asset_id=@asset)",
                    ("id", id), ("asset", asset));
            if (!samePlayer)
                throw new ContentChangeException(400, column == "custom_head_id"
                    ? "The uploaded head does not exist or belongs to another player."
                    : "The head must be another card of the same player.");
            changed.Add((column, id));
        }
        List<long>? traits = null;
        if (!SameTraits(card[Traits], live[Traits]))
        {
            var ids = new List<long>();
            if (card[Traits] is not JsonArray list || list.Any(node => !WholeNumber(node, out _)))
                throw new ContentChangeException(400, "traits must be a list of trait ids.");
            foreach (var node in list) { WholeNumber(node, out var id); if (!ids.Contains(id)) ids.Add(id); }
            var visible = await work.QueryAsync("SELECT trait_id FROM ultimateteam.traits WHERE is_visible", row => row.GetInt64(0));
            if (ids.FirstOrDefault(id => !visible.Contains(id)) is var unknown and > 0)
                throw new ContentChangeException(400, $"Trait {unknown} is not a visible FIFA 21 trait.");
            traits = ids;
        }
        return (changed, traits);
    }

    private static bool SameTraits(JsonNode? a, JsonNode? b)
    {
        static string[] Codes(JsonNode? node) => node is JsonArray list
            ? list.Select(item => item?.ToJsonString() ?? "null").Order(StringComparer.Ordinal).ToArray() : [];
        return Codes(a).SequenceEqual(Codes(b), StringComparer.Ordinal);
    }

    private static async ValueTask<List<long>> VisibleTraitsAsync(IUnitOfWork work, long definition) => await work.QueryAsync("""
        SELECT t.trait_id FROM ultimateteam.player_card_traits l JOIN ultimateteam.traits t ON t.trait_id=l.trait_id
        WHERE l.definition_id=@definition AND t.is_visible ORDER BY t.trait_id
        """, row => row.GetInt64(0), ("definition", definition));

    /// <summary>Sets the card's visible traits to exactly <paramref name="ids"/>; hidden traits are untouched.</summary>
    private static async ValueTask WriteTraitsAsync(IUnitOfWork work, long definition, List<long> ids)
    {
        await work.ExecuteAsync("""
            DELETE FROM ultimateteam.player_card_traits l USING ultimateteam.traits t
            WHERE l.trait_id=t.trait_id AND l.definition_id=@definition AND t.is_visible AND NOT (t.trait_id = ANY(@ids))
            """, ("definition", definition), ("ids", ids.ToArray()));
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
            SELECT @definition, t.trait_id, 0, @source FROM ultimateteam.traits t
            WHERE t.trait_id = ANY(@ids) AND NOT EXISTS (SELECT 1 FROM ultimateteam.player_card_traits l WHERE l.definition_id=@definition AND l.trait_id=t.trait_id)
            """, ("definition", definition), ("ids", ids.ToArray()),
            ("source", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatorTraitSource)))));
    }

    private static async ValueTask<long> AssetAsync(IUnitOfWork work, long resource)
    {
        var assets = await work.QueryAsync(
            "SELECT native_asset_id FROM ultimateteam.item_definitions WHERE family_id=1 AND game_year=2021 AND native_resource_id=@resource",
            row => row.IsDBNull(0) ? 0L : row.GetInt64(0), ("resource", resource));
        return assets.FirstOrDefault();
    }

    private static long Resource(JsonObject key) => key["resourceId"]!.GetValue<long>();

    /// <summary>
    /// A JSON number without a fraction, however it is held (parsed text, a long or an int). Stricter than
    /// JsonValues.TryInt, which also reads numeric text, true/false and truncated fractions.
    /// </summary>
    private static bool WholeNumber(JsonNode? node, out long number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue(out long whole)) { number = whole; return true; }
        if (value.TryGetValue(out int small)) { number = small; return true; }
        if (value.TryGetValue(out JsonElement element) && element.TryGetInt64(out whole)) { number = whole; return true; }
        return false;
    }
}
