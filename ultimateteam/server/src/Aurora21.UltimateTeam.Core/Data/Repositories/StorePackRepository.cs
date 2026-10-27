using System.Globalization;
using Npgsql;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// Store pack configuration, which migration 041 moved out of rules/packs.json, store_packs.json and
/// the personal pack_settings.json. The rows are the merged result those three files produced, so
/// reading them reproduces the configuration the server already served.
///
/// A pack's nullable columns mean "not set for this pack", exactly as an absent JSON key did, so the
/// projection back to the wire shape omits them rather than inventing a value.
/// </summary>
public static class StorePackRepository
{
    /// <summary>Every configured pack of one kind, as the wire-shaped documents callers expect.</summary>
    public static async ValueTask<JsonObject> StorePacksAsync(this IUnitOfWork work, string kind = "store")
    {
        var packs = new JsonObject();
        foreach (var (packId, config) in await PackRowsAsync(work, kind))
        {
            packs[packId.ToString(CultureInfo.InvariantCulture)] = config;
        }
        return packs;
    }

    /// <summary>One pack's configuration, or null when no row carries that id and kind.</summary>
    public static async ValueTask<JsonObject?> StorePackAsync(this IUnitOfWork work, long packId, string kind) =>
        (await PackRowsAsync(work, kind, packId)).FirstOrDefault().Config;

    /// <summary>The default draw weight of every special rarity, keyed by native rareflag.</summary>
    public static async ValueTask<JsonObject> RarityWeightsAsync(this IUnitOfWork work)
    {
        var weights = new JsonObject();
        foreach (var (rareflag, weight) in await work.QueryAsync(
            "SELECT native_rareflag, weight FROM ultimateteam.store_pack_rarity_weights ORDER BY native_rareflag",
            reader => (reader.GetInt64(0), reader.GetInt32(1))))
        {
            weights[rareflag.ToString(CultureInfo.InvariantCulture)] = weight;
        }
        return weights;
    }

    private static async ValueTask<List<(long PackId, JsonObject Config)>> PackRowsAsync(
        IUnitOfWork work, string kind, long? packId = null)
    {
        var filter = packId is null ? string.Empty : " AND pack_id = @pack";
        var parameters = packId is null
            ? new (string, object?)[] { ("kind", kind) }
            : new (string, object?)[] { ("kind", kind), ("pack", packId.Value) };
        return await work.QueryAsync($"""
            SELECT pack_id, enabled, name, bio, price, size, gold_weight, silver_weight, bronze_weight,
                   rare_slots, manager_chance, chem_style_chance, min_rating, store_group,
                   player_pick_count, guaranteed_specials, allow_unweighted_specials, all_consumables,
                   all_kits, set_stadium_id, allowed_rareflags, special_weights, max_rating, kit_stadium_pack,
                   allowed_team_ids, allowed_nations, points_price, allowed_currencies, admin_only, promo_only,
                   special_weights_exclusive, player_picks, bonus_player_count, bonus_min_rating, bonus_promo_only,
                   random_consumable_count, random_manager_count, identity_pack, resource_pool, swap_token_rareflag,
                   rated_rare_slots, consumable_mix, manager_league_card_share,
                   category_chances, include_general_pool, promo_codes, mixed_contents, purchase_limit, community_limit,
                   vanity_pack, store_listed, store_untradeable, store_section
            FROM (
                SELECT * FROM ultimateteam.store_packs
                UNION ALL
                SELECT * FROM ultimateteam.owned_legacy_pack_definitions
            ) definitions WHERE kind = @kind{filter} ORDER BY pack_id
            """, Project, parameters);
    }

    private static (long PackId, JsonObject Config) Project(NpgsqlDataReader reader)
    {
        var config = new JsonObject();
        Bool(reader, 1, config, "enabled");
        Text(reader, 2, config, "name");
        Text(reader, 3, config, "bio");
        Int(reader, 4, config, "price");
        Int(reader, 5, config, "size");
        Int(reader, 6, config, "gold_weight");
        Int(reader, 7, config, "silver_weight");
        Int(reader, 8, config, "bronze_weight");
        Int(reader, 9, config, "rare_slots");
        Number(reader, 10, config, "manager_chance");
        Number(reader, 11, config, "chem_style_chance");
        Int(reader, 12, config, "min_rating");
        Text(reader, 13, config, "store_group");
        Int(reader, 14, config, "player_pick_count");
        Int(reader, 15, config, "guaranteed_specials");
        Bool(reader, 16, config, "allow_unweighted_specials");
        Bool(reader, 17, config, "all_consumables");
        Bool(reader, 18, config, "all_kits");
        Int(reader, 19, config, "set_stadium_id");
        if (!reader.IsDBNull(20))
        {
            var flags = new JsonArray();
            foreach (var flag in reader.GetFieldValue<long[]>(20))
            {
                flags.Add(flag);
            }
            config["allowed_rareflags"] = flags;
        }
        config["special_weights"] = reader.IsDBNull(21)
            ? new JsonObject()
            : FutJson.ParseObject(reader.GetString(21)) ?? new JsonObject();
        Int(reader, 22, config, "max_rating");
        Bool(reader, 23, config, "kit_stadium_pack");
        LongArray(reader, 24, config, "allowed_team_ids");
        LongArray(reader, 25, config, "allowed_nations");
        Int(reader, 26, config, "points_price");
        if (!reader.IsDBNull(27))
        {
            config["allowed_currencies"] = new JsonArray(reader.GetFieldValue<string[]>(27).Select(value => (JsonNode?)value).ToArray());
        }
        // Defaults (false) are omitted, as an absent key was.
        if (reader.GetBoolean(28)) config["admin_only"] = true;
        Bool(reader, 29, config, "promo_only");
        if (reader.GetBoolean(30)) config["special_weights_exclusive"] = true;
        if (!reader.IsDBNull(31)) config["player_picks"] = JsonNode.Parse(reader.GetString(31));
        Int(reader, 32, config, "bonus_player_count");
        Int(reader, 33, config, "bonus_min_rating");
        Bool(reader, 34, config, "bonus_promo_only");
        Int(reader, 35, config, "random_consumable_count");
        Int(reader, 36, config, "random_manager_count");
        if (!reader.IsDBNull(37)) config["identity_pack"] = JsonNode.Parse(reader.GetString(37));
        // A named card pool (fixed TOTW 1–3 rewards; migrations 061 and 085).
        Text(reader, 38, config, "resource_pool");
        // An Icon Swaps token pack: the token rarity it opens (migration 064).
        Int(reader, 39, config, "swap_token_rareflag");
        // Rare slots with a rating floor (migration 068).
        if (!reader.IsDBNull(40)) config["rated_rare_slots"] = JsonNode.Parse(reader.GetString(40));
        // Manager league cards (migration 071): a consumables pack's count of each kind, and the share of a player pack's
        // manager slot that is a manager league card.
        if (!reader.IsDBNull(41)) config["consumable_mix"] = JsonNode.Parse(reader.GetString(41));
        Number(reader, 42, config, "manager_league_card_share");
        config["category_chances"] = FutJson.ParseObject(reader.GetString(43)) ?? new JsonObject();
        Bool(reader, 44, config, "include_general_pool");
        if (!reader.IsDBNull(45)) config["promo_codes"] = new JsonArray(reader.GetFieldValue<string[]>(45).Select(code => (JsonNode?)code).ToArray());
        if (!reader.IsDBNull(46)) config["mixed_contents"] = JsonNode.Parse(reader.GetString(46));
        Int(reader, 47, config, "purchase_limit");
        Int(reader, 48, config, "community_limit");
        // A My Stadium pack (migration 123): its slots of vanity card subtypes.
        if (!reader.IsDBNull(49)) config["vanity_pack"] = JsonNode.Parse(reader.GetString(49));
        // The coin store listing and its purchase tradeability (null defaults to tradeable).
        Bool(reader, 50, config, "store_listed");
        Bool(reader, 51, config, "store_untradeable");
        Text(reader, 52, config, "store_section");
        return (reader.GetInt64(0), config);
    }

    private static void LongArray(NpgsqlDataReader reader, int index, JsonObject config, string key)
    {
        if (reader.IsDBNull(index)) return;
        var values = new JsonArray();
        foreach (var value in reader.GetFieldValue<long[]>(index)) values.Add(value);
        config[key] = values;
    }

    private static void Text(NpgsqlDataReader reader, int index, JsonObject config, string key)
    {
        if (!reader.IsDBNull(index)) config[key] = reader.GetString(index);
    }

    private static void Bool(NpgsqlDataReader reader, int index, JsonObject config, string key)
    {
        if (!reader.IsDBNull(index)) config[key] = reader.GetBoolean(index);
    }

    private static void Int(NpgsqlDataReader reader, int index, JsonObject config, string key)
    {
        if (!reader.IsDBNull(index)) config[key] = Convert.ToInt64(reader.GetValue(index), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Chances are written back as whole-number doubles where the source had them, matching the
    /// formatting the client already receives.
    /// </summary>
    private static void Number(NpgsqlDataReader reader, int index, JsonObject config, string key)
    {
        if (reader.IsDBNull(index)) return;
        // numeric keeps the scale it was stored with, so 0.0 stays 0.0 instead of collapsing to 0.
        var value = reader.GetFieldValue<decimal>(index);
        config[key] = JsonNode.Parse(value.ToString(CultureInfo.InvariantCulture));
    }
}
