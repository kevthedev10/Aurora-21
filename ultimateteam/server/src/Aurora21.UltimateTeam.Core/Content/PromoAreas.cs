using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>Shared editor contracts. Codes are stable identities; card lists use native resource IDs.</summary>
public static class PromoEdits
{
    public static readonly IReadOnlyDictionary<string, long> StandardTypes = new Dictionary<string, long>
    {
        ["bronze_common"] = 1, ["gold_common"] = 2, ["silver_common"] = 3,
        ["bronze_rare"] = 4, ["gold_rare"] = 5, ["silver_rare"] = 6,
    };

    public static string Text(JsonObject row, string field, int maximum = 120) =>
        row[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum
            ? text : throw new ContentChangeException(400, $"{field} must contain 1–{maximum} characters.");

    public static string Code(JsonObject row, string field = "code")
    {
        var code = Text(row, field, 80);
        if (!Regex.IsMatch(code, "^[a-z][a-z0-9_]*$")) throw new ContentChangeException(400, $"{field} uses lowercase letters, digits and underscores, starting with a letter.");
        return code;
    }

    public static JsonObject Key(JsonObject row) => new() { ["code"] = Code(row) };

    public static void Fields(JsonObject row, params string[] fields)
    {
        if (row.Any(p => !fields.Contains(p.Key, StringComparer.Ordinal)))
            throw new ContentChangeException(400, $"Unknown fields; expected {string.Join(", ", fields)}.");
    }

    public static bool Enabled(JsonObject row) => row["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled)
        ? enabled : throw new ContentChangeException(400, "enabled must be true or false.");

    public static decimal Weight(JsonObject row)
    {
        if (row["draw_weight"] is not JsonValue value || !value.TryGetValue<decimal>(out var weight)
            || weight <= 0 || weight > 999999 || decimal.Round(weight, 6) != weight)
            throw new ContentChangeException(400, "draw_weight must be positive, at most 999999, with at most six decimal places.");
        return weight;
    }

    public static (DateTime Start, DateTime End) Window(JsonObject row)
    {
        DateTime Parse(string field)
        {
            var text = Text(row, field, 40);
            if (!Regex.IsMatch(text, @"(Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
                throw new ContentChangeException(400, $"{field} must be an ISO date with a UTC offset.");
            return time.UtcDateTime;
        }
        var start = Parse("starts_at");
        var end = Parse("ends_at");
        if (start >= end) throw new ContentChangeException(400, "starts_at must be before ends_at.");
        return (start, end);
    }

    public static long[] Cards(JsonObject bucket)
    {
        if (bucket["cards"] is not JsonArray { Count: <= 30000 } list)
            throw new ContentChangeException(400, "Each bucket needs a cards list of native resource IDs.");
        var cards = list.Select(node => node is JsonValue value && value.TryGetValue<long>(out var id) && id > 0
            ? id : throw new ContentChangeException(400, "cards must contain positive native resource IDs.")).ToArray();
        if (cards.Distinct().Count() != cards.Length) throw new ContentChangeException(400, "A bucket lists the same card twice.");
        return cards;
    }

    public static Dictionary<long, decimal> CardReductions(JsonObject row)
    {
        if (!row.ContainsKey("card_reductions")) return new();
        if (row["card_reductions"] is not JsonObject { Count: <= 30000 } values)
            throw new ContentChangeException(400, "card_reductions must map native resource IDs to reduction factors.");
        var result = new Dictionary<long, decimal>();
        foreach (var (key, node) in values)
        {
            if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var resource) || resource <= 0
                || key != resource.ToString(CultureInfo.InvariantCulture) || node is not JsonValue value
                || !value.TryGetValue<decimal>(out var reduction) || reduction < 1 || reduction > 999999
                || decimal.Round(reduction, 6) != reduction)
                throw new ContentChangeException(400, "Card reductions need positive resource IDs and numeric factors from 1 to 999999, with up to six decimal places.");
            result.Add(resource, reduction);
        }
        return result;
    }

    public static async ValueTask<Dictionary<string, (string Category, string Status)>> PromosAsync(IUnitOfWork work, bool pending = true)
    {
        var rows = await work.QueryAsync("SELECT code, odds_category, status FROM ultimateteam.promos", r => (r.GetString(0), r.GetString(1), r.GetString(2)));
        var result = rows.ToDictionary(r => r.Item1, r => (Category: r.Item2, Status: r.Item3));
        if (!pending) return result;
        var changes = await work.QueryAsync("""
            SELECT c.target_key->>'code', c.operation, c.after_value::text FROM ultimateteam.content_changes c
            JOIN ultimateteam.content_changesets s USING(changeset_id) WHERE ((nullif(current_setting('ultimateteam.draft_id',true),'') IS NULL AND s.status='open') OR s.changeset_id=nullif(current_setting('ultimateteam.draft_id',true),'')::bigint) AND c.area='promo'
            """, r => (Code: r.GetString(0), Operation: r.GetString(1), After: r.IsDBNull(2) ? null : JsonNode.Parse(r.GetString(2))!.AsObject()));
        foreach (var change in changes)
            result[change.Code] = change.After is { } after
                ? (Text(after, "odds_category", 80), Text(after, "status"))
                : (result[change.Code].Category, "inactive");
        return result;
    }

    // New campaigns own special content; their editor must leave the independent pools/calendar alone.
    // Publication also revalidates historical drafts, so this restriction applies to editing only.
    internal static ValueTask<bool> CampaignEditorAsync(IUnitOfWork work) => work.ScalarAsync<bool>("""
        SELECT coalesce(current_setting('ultimateteam.content_publish',true),'')<>'on' AND EXISTS(
          SELECT 1 FROM ultimateteam.content_drops d JOIN ultimateteam.content_campaigns c USING(campaign_id)
          WHERE d.changeset_id=nullif(current_setting('ultimateteam.draft_id',true),'')::bigint
            AND c.starts_at IS NOT NULL)
        """);

    internal static bool IsTotw(string code, string? category = null) => category == "totw" || code == "silver_totw" || Regex.IsMatch(code, "^totw_[0-9]+$");
}

/// <summary>A promo and all its weighted buckets publish together, so card moves are atomic.</summary>
public sealed class PromoArea : IContentArea
{
    public string Name => "promo";
    public int ApplyOrder => 6;
    public JsonObject NormalizeKey(JsonObject key) => PromoEdits.Key(key);

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
    {
        var code = PromoEdits.Code(key);
        var rows = await work.QueryAsync("SELECT promo_id,name,odds_category,draw_weight,status FROM ultimateteam.promos WHERE code=@code",
            r => (Id: r.GetInt64(0), Row: new JsonObject { ["name"] = r.GetString(1), ["odds_category"] = r.GetString(2), ["draw_weight"] = r.GetDecimal(3), ["status"] = r.GetString(4) }), ("code", code));
        if (rows.Count == 0) return null;
        var (id, row) = rows[0];
        var buckets = await work.QueryAsync("""
            SELECT b.id,b.code,b.name,b.draw_weight,b.enabled FROM ultimateteam.promo_buckets b
            WHERE b.promo_id=@promo AND (b.enabled OR EXISTS(SELECT 1 FROM ultimateteam.promo_cards c WHERE c.bucket_id=b.id)) ORDER BY b.code
            """, r => (Id: r.GetInt64(0), Row: new JsonObject { ["code"] = r.GetString(1), ["name"] = r.GetString(2), ["draw_weight"] = r.GetDecimal(3), ["enabled"] = r.GetBoolean(4) }), ("promo", id));
        var members = await work.QueryAsync("""
            SELECT c.bucket_id,d.native_resource_id,c.draw_reduction FROM ultimateteam.promo_cards c
            JOIN ultimateteam.item_definitions d ON d.definition_id=c.player_card_definition_id
            WHERE c.promo_id=@promo ORDER BY d.native_resource_id
            """, r => (Bucket: r.GetInt64(0), Resource: r.GetInt64(1), Reduction: r.GetDecimal(2)), ("promo", id));
        var array = new JsonArray();
        foreach (var bucket in buckets)
        {
            bucket.Row["cards"] = new JsonArray(members.Where(m => m.Bucket == bucket.Id).Select(m => (JsonNode?)JsonValue.Create(m.Resource)).ToArray());
            array.Add(bucket.Row);
        }
        row["buckets"] = array;
        var reductions = new JsonObject();
        foreach (var member in members.Where(m => m.Reduction > 1))
            reductions[member.Resource.ToString(CultureInfo.InvariantCulture)] = member.Reduction;
        row["card_reductions"] = reductions;
        return row;
    }

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var code = PromoEdits.Code(key);
        var builtin = PromoEdits.StandardTypes.ContainsKey(code) || PromoAvailability.IsIconTier(code);
        if (operation == ContentOperation.Hide)
        {
            if (builtin) throw new ContentChangeException(400, "Standard pool promos cannot be hidden.");
            return;
        }
        var row = after ?? throw new ContentChangeException(400, "A promo definition is required.");
        PromoEdits.Fields(row, "name", "odds_category", "draw_weight", "status", "buckets", "card_reductions");
        PromoEdits.Text(row, "name");
        var category = PromoEdits.Code(row, "odds_category");
        PromoEdits.Weight(row);
        var status = PromoEdits.Text(row, "status");
        if (status is not ("active" or "inactive")) throw new ContentChangeException(400, "status is active or inactive.");
        if (builtin && (status != "active" || category != (PromoAvailability.IsIconTier(code) ? "icon" : code)))
            throw new ContentChangeException(400, "Standard pool promos retain their active status and category.");
        if (category == "icon" && !PromoAvailability.IsIconTier(code) || PromoEdits.StandardTypes.ContainsKey(category) && code != category)
            throw new ContentChangeException(400, "Standard categories belong to their standard pool promo.");
        if (row["buckets"] is not JsonArray { Count: > 0 and <= 200 } buckets)
            throw new ContentChangeException(400, "A promo needs 1–200 buckets.");
        var allCards = new HashSet<long>();
        var codes = new HashSet<string>();
        foreach (var node in buckets)
        {
            var bucket = node as JsonObject ?? throw new ContentChangeException(400, "Each bucket must be an object.");
            PromoEdits.Fields(bucket, "code", "name", "draw_weight", "cards", "enabled");
            if (bucket.ContainsKey("enabled")) PromoEdits.Enabled(bucket);
            if (!codes.Add(PromoEdits.Code(bucket))) throw new ContentChangeException(400, "Bucket codes must be unique within a promo.");
            PromoEdits.Text(bucket, "name", 80);
            PromoEdits.Weight(bucket);
            foreach (var resource in PromoEdits.Cards(bucket))
                if (!allCards.Add(resource)) throw new ContentChangeException(400, "A card may belong to only one bucket within a promo.");
        }
        var reductions = PromoEdits.CardReductions(row);
        if (reductions.Keys.Any(resource => !allCards.Contains(resource)))
            throw new ContentChangeException(400, "Card reductions may only name members of this promo.");
        if (code == "gold_common" && reductions.Values.Any(value => value > 1))
            throw new ContentChangeException(400, "Gold Common uses rating-only weights, without desirable-player reductions.");
        var identities = await work.QueryAsync("""
            SELECT d.native_resource_id,p.card_type_id,r.native_rareflag FROM ultimateteam.item_definitions d
            JOIN ultimateteam.player_card_definitions p USING(definition_id)
            JOIN ultimateteam.card_types t ON t.card_type_id=p.card_type_id
            JOIN ultimateteam.native_rarities r USING(native_rarity_id)
            WHERE d.game_year=2021 AND d.native_resource_id=ANY(@cards)
            """, r => (Resource: r.GetInt64(0), Type: r.GetInt64(1), Rare: r.IsDBNull(2) ? 0L : r.GetInt64(2)), ("cards", allCards.ToArray()));
        var types = identities.ToDictionary(r => r.Resource, r => (r.Type, r.Rare));
        // New cards can be staged before their promo membership in the same changeset.
        var pendingCards = await work.QueryAsync("""
            SELECT (c.target_key->>'resourceId')::bigint,(c.after_value->>'cardTypeId')::bigint,r.native_rareflag
            FROM ultimateteam.content_changes c JOIN ultimateteam.content_changesets s USING(changeset_id)
            JOIN ultimateteam.card_types t ON t.card_type_id=(c.after_value->>'cardTypeId')::bigint
            JOIN ultimateteam.native_rarities r USING(native_rarity_id)
            WHERE ((nullif(current_setting('ultimateteam.draft_id',true),'') IS NULL AND s.status='open') OR s.changeset_id=nullif(current_setting('ultimateteam.draft_id',true),'')::bigint) AND c.area='player_card' AND c.operation='insert'
            """, r => (Resource: r.GetInt64(0), Type: r.GetInt64(1), Rare: r.IsDBNull(2) ? 0L : r.GetInt64(2)));
        foreach (var card in pendingCards) types[card.Resource] = (card.Type, card.Rare);
        foreach (var card in allCards)
        {
            if (!types.TryGetValue(card, out var identity)) throw new ContentChangeException(400, $"Unknown player resource ID {card}.");
            if (PromoEdits.StandardTypes.TryGetValue(code, out var expected) && identity.Type != expected)
                throw new ContentChangeException(400, $"Card {card} does not have the {code} card type.");
            if (identity.Rare == 18 && code != "fut_champions" || identity.Rare == 10 && code != "developer_cards")
                throw new ContentChangeException(400, "Reward-only and Developer cards retain their dedicated promos.");
            if ((identity.Rare is 12 or 84) != PromoAvailability.IsIconTier(code))
                throw new ContentChangeException(400, "Icon cards belong only to Icon tier promos.");
        }
        // Icon tier membership is curated from native data; edits may split/reweight it, not relabel another tier.
        if (PromoAvailability.IsIconTier(code))
        {
            var current = await ReadAsync(work, key);
            var allowed = current!["buckets"]!.AsArray().SelectMany(b => PromoEdits.Cards(b!.AsObject())).ToHashSet();
            if (!allCards.SetEquals(allowed)) throw new ContentChangeException(400, "Icon buckets must retain all cards from this tier's curated membership.");
        }
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var code = PromoEdits.Code(key);
        if (operation == ContentOperation.Hide)
        {
            await work.ExecuteAsync("UPDATE ultimateteam.promos SET status='inactive' WHERE code=@code", ("code", code));
            return;
        }
        var row = after!;
        var id = await work.ScalarAsync<long>("""
            INSERT INTO ultimateteam.promos(code,name,status,odds_category,draw_weight)
            VALUES(@code,@name,@status,@category,@weight) ON CONFLICT(code) DO UPDATE
            SET name=excluded.name,status=excluded.status,odds_category=excluded.odds_category,draw_weight=excluded.draw_weight RETURNING promo_id
            """, ("code", code), ("name", PromoEdits.Text(row, "name")), ("status", PromoEdits.Text(row, "status")),
            ("category", PromoEdits.Code(row, "odds_category")), ("weight", PromoEdits.Weight(row)));
        await work.ExecuteAsync("UPDATE ultimateteam.promo_buckets SET enabled=false WHERE promo_id=@id", ("id", id));
        var resources = new List<long>();
        foreach (var node in row["buckets"]!.AsArray())
        {
            var bucket = node!.AsObject();
            var bucketId = await work.ScalarAsync<long>("""
                INSERT INTO ultimateteam.promo_buckets(promo_id,code,name,draw_weight,enabled) VALUES(@promo,@code,@name,@weight,@enabled)
                ON CONFLICT(promo_id,code) DO UPDATE SET name=excluded.name,draw_weight=excluded.draw_weight,enabled=excluded.enabled RETURNING id
                """, ("promo", id), ("code", PromoEdits.Code(bucket)), ("name", PromoEdits.Text(bucket, "name", 80)), ("weight", PromoEdits.Weight(bucket)),
                ("enabled", !bucket.ContainsKey("enabled") || PromoEdits.Enabled(bucket)));
            var cards = PromoEdits.Cards(bucket);
            resources.AddRange(cards);
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.promo_cards(promo_id,player_card_definition_id,bucket_id)
                SELECT @promo,d.definition_id,@bucket FROM ultimateteam.item_definitions d
                JOIN ultimateteam.player_card_definitions p USING(definition_id) WHERE d.game_year=2021 AND d.native_resource_id=ANY(@cards)
                ON CONFLICT(promo_id,player_card_definition_id) DO UPDATE SET bucket_id=excluded.bucket_id
                """, ("promo", id), ("bucket", bucketId), ("cards", cards));
        }
        await work.ExecuteAsync("""
            DELETE FROM ultimateteam.promo_cards c USING ultimateteam.item_definitions d
            WHERE c.player_card_definition_id=d.definition_id AND c.promo_id=@promo AND NOT(d.native_resource_id=ANY(@cards))
            """, ("promo", id), ("cards", resources.ToArray()));
        // Older drafts without this field keep existing marks. New snapshots include
        // an explicit map (including {}), so publish and History revert can clear marks.
        if (row.ContainsKey("card_reductions"))
        {
            var reductions = PromoEdits.CardReductions(row);
            await work.ExecuteAsync("UPDATE ultimateteam.promo_cards SET draw_reduction=1 WHERE promo_id=@promo", ("promo", id));
            if (reductions.Count > 0)
                await work.ExecuteAsync("""
                    UPDATE ultimateteam.promo_cards c SET draw_reduction=seed.reduction
                    FROM ultimateteam.item_definitions d,
                        unnest(@resources::bigint[],@reductions::numeric[]) AS seed(resource,reduction)
                    WHERE c.promo_id=@promo AND c.player_card_definition_id=d.definition_id AND d.native_resource_id=seed.resource
                    """, ("promo", id), ("resources", reductions.Keys.ToArray()), ("reductions", reductions.Values.ToArray()));
        }
    }
}

/// <summary>Timed definitions are separately keyed from their promo membership and weights.</summary>
public sealed class PromoTimingArea(bool iconRotation) : IContentArea
{
    public string Name => iconRotation ? "icon_rotation" : "promo_event";
    public int ApplyOrder => 7;
    public JsonObject NormalizeKey(JsonObject key) => PromoEdits.Key(key);
    private string Table => iconRotation ? "icon_rotations" : "promo_events";

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
    {
        var rows = await work.QueryAsync($"SELECT name,enabled,starts_at,ends_at FROM ultimateteam.{Table} WHERE code=@code",
            r => new JsonObject { ["name"] = r.GetString(0), ["enabled"] = r.GetBoolean(1), ["starts_at"] = r.GetDateTime(2).ToString("O"), ["ends_at"] = r.GetDateTime(3).ToString("O") }, ("code", PromoEdits.Code(key)));
        if (rows.Count == 0) return null;
        var row = rows[0];
        var codes = await work.QueryAsync(iconRotation
            ? "SELECT p.code FROM ultimateteam.icon_rotations r JOIN ultimateteam.promos p USING(promo_id) WHERE r.code=@code"
            : "SELECT p.code FROM ultimateteam.promo_events e JOIN ultimateteam.promo_event_promos l USING(event_id) JOIN ultimateteam.promos p USING(promo_id) WHERE e.code=@code AND l.enabled ORDER BY p.code",
            r => r.GetString(0), ("code", PromoEdits.Code(key)));
        if (iconRotation) row["promo"] = codes.Single();
        else row["promos"] = new JsonArray(codes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        return row;
    }

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var campaignEditor = await PromoEdits.CampaignEditorAsync(work);
        if (campaignEditor && iconRotation)
            throw new ContentChangeException(400, "Icon rotations are managed independently of campaigns.");
        if (campaignEditor)
        {
            var knownPromos = await PromoEdits.PromosAsync(work);
            var current = await ReadAsync(work, key);
            foreach (var code in new[] { current, after }.Where(row => row is not null).SelectMany(row => (row!["promos"] as JsonArray)?.Select(n => n?.ToString() ?? "") ?? []))
                if (PromoEdits.IsTotw(code, knownPromos.TryGetValue(code, out var promo) ? promo.Category : null))
                    throw new ContentChangeException(400, "TOTW events are managed independently of campaigns.");
        }
        if (operation == ContentOperation.Hide) return;
        var row = after ?? throw new ContentChangeException(400, "A timed definition is required.");
        PromoEdits.Fields(row, "name", "enabled", "starts_at", "ends_at", iconRotation ? "promo" : "promos");
        PromoEdits.Text(row, "name");
        PromoEdits.Enabled(row);
        PromoEdits.Window(row);
        var known = await PromoEdits.PromosAsync(work);
        var codes = iconRotation ? new[] { PromoEdits.Code(row, "promo") }
            : row["promos"] is JsonArray { Count: > 0 and <= 200 } list
                ? list.Select(n => n is JsonValue v && v.TryGetValue<string>(out var c) ? c : throw new ContentChangeException(400, "promos contains promo codes.")).ToArray()
                : throw new ContentChangeException(400, "An event needs a promos list.");
        if (codes.Distinct().Count() != codes.Length) throw new ContentChangeException(400, "Duplicate promo code.");
        var scheduledDraft = await work.ScalarAsync<bool>("SELECT nullif(current_setting('ultimateteam.draft_id',true),'') IS NOT NULL AND coalesce(current_setting('ultimateteam.content_publish',true),'')<>'on'");
        foreach (var code in codes)
        {
            _ = PromoEdits.Code(new JsonObject { ["code"]=code });
            if (!scheduledDraft && (!known.TryGetValue(code, out var promo) || PromoEdits.Enabled(row) && promo.Status != "active"))
                throw new ContentChangeException(400, $"Unknown or inactive promo {code}.");
            if (iconRotation ? !PromoAvailability.IsIconTier(code) : PromoAvailability.IsIconTier(code) || PromoEdits.StandardTypes.ContainsKey(code) || code is "fut_champions" or "developer_cards")
                throw new ContentChangeException(400, iconRotation ? "A rotation selects one Icon tier." : "Events may contain ordinary special promos only.");
        }
        // Whole-snapshot validation checks final overlap, including several pending changes together.
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var code = PromoEdits.Code(key);
        if (operation == ContentOperation.Hide)
        {
            await work.ExecuteAsync($"UPDATE ultimateteam.{Table} SET enabled=false WHERE code=@code", ("code", code));
            return;
        }
        var row = after!;
        var (start, end) = PromoEdits.Window(row);
        var parameters = new List<(string, object?)> { ("code", code), ("name", PromoEdits.Text(row, "name")), ("enabled", PromoEdits.Enabled(row)), ("start", start), ("end", end) };
        if (iconRotation)
        {
            parameters.Add(("promo", PromoEdits.Code(row, "promo")));
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.icon_rotations(code,name,enabled,starts_at,ends_at,promo_id)
                SELECT @code,@name,@enabled,@start,@end,promo_id FROM ultimateteam.promos WHERE code=@promo
                ON CONFLICT(code) DO UPDATE SET name=excluded.name,enabled=excluded.enabled,starts_at=excluded.starts_at,ends_at=excluded.ends_at,promo_id=excluded.promo_id
                """, parameters.ToArray());
        }
        else
        {
            var id = await work.ScalarAsync<long>("""
                INSERT INTO ultimateteam.promo_events(code,name,enabled,starts_at,ends_at) VALUES(@code,@name,@enabled,@start,@end)
                ON CONFLICT(code) DO UPDATE SET name=excluded.name,enabled=excluded.enabled,starts_at=excluded.starts_at,ends_at=excluded.ends_at RETURNING event_id
                """, parameters.ToArray());
            await work.ExecuteAsync("UPDATE ultimateteam.promo_event_promos SET enabled=false WHERE event_id=@id", ("id", id));
            var promos = row["promos"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.promo_event_promos(event_id,promo_id,enabled) SELECT @id,promo_id,true FROM ultimateteam.promos WHERE code=ANY(@codes)
                ON CONFLICT(event_id,promo_id) DO UPDATE SET enabled=true
                """, ("id", id), ("codes", promos));
        }
    }
}
