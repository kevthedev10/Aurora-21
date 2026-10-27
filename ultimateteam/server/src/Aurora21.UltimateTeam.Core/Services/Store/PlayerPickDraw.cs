using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>Pick previews, reveals and bonus players share the saved promo/bucket selector.</summary>
public static class PlayerPickDraw
{
    private static readonly string[] SourceFields = ["include_general_pool", "promo_codes", "category_chances", "allowed_rareflags", "allowed_team_ids", "allowed_nations"];
    internal static bool IsStandard(string category) => category is "gold_rare" or "gold_common" or "silver_rare" or "silver_common" or "bronze_rare" or "bronze_common";

    internal static JsonObject Configuration(JsonObject pack, JsonObject group)
    {
        var config = new JsonObject();
        foreach (var key in SourceFields)
            if (group.ContainsKey(key) || pack.ContainsKey(key)) config[key] = (group.Get(key) ?? pack.Get(key))?.DeepClone();
        config["min_rating"] = group.Int("min_rating");
        config["max_rating"] = group.Int("max_rating", Cards.MaxRating);
        config["promo_only"] = group.Bool("promo_only");
        if (group.Arr("rareflags") is { Count: > 0 } flags) config["allowed_rareflags"] = flags.DeepClone();
        if (group.Text("resource_pool") is { Length: > 0 } pool) config["resource_pool"] = pool;
        // A fixed pick (owner, 2026-10-04): the token offers exactly these cards, in this order, whatever the filters.
        if (group.Arr("choices") is { Count: > 0 } choices) config["fixed_choices"] = choices.DeepClone();
        return config;
    }

    internal static Dictionary<string, double> CategoryChances(JsonObject config)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (category, value) in config.Obj("category_chances") ?? new JsonObject())
        {
            if (IsStandard(category) || !double.TryParse(value?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var chance)
                || !double.IsFinite(chance) || chance is < 0 or > 1)
                throw new InvalidOperationException($"Invalid pick category chance for {category}");
            result.Add(category, chance);
        }
        if (result.Values.Sum() > 1.0000001) throw new InvalidOperationException("Pick category chances exceed 100%");
        return result;
    }

    public static void Validate(GameContext game, JsonObject pack, IReadOnlyDictionary<string, (string Category, string Status)>? promos = null, long sourcePackId = 0)
    {
        // A My Stadium pack draws no players, so player sources do not apply (VanityRules.PackIssue checks it instead).
        if (PackOpeningService.VanityPack(pack) is not null) return;
        var redReward = sourcePackId is >= 550 and <= 553 && game.Packs.Rules.Pack(sourcePackId) is not null;
        ValidateSource(game, pack, promos, redReward);
        var limits = game.Rules.Cards.PlayerPickToken;
        if (pack.Arr("player_picks") is not { Count: > 0 } groups)
        {
            var legacyCount = pack.Int("player_pick_count");
            if (legacyCount > 0 && (legacyCount < limits.MinChoices || legacyCount > limits.MaxChoices))
                throw new InvalidOperationException("Single-pick choices exceed the native pick limits");
            return;
        }
        var total = pack.Int("bonus_player_count");
        var permitted = new HashSet<string>(["count", "choice_count", "min_rating", "max_rating", "promo_only", "rareflags", "label", "resource_pool", "choices", .. SourceFields]);
        foreach (var node in groups)
        {
            if (node is not JsonObject group || group.Any(entry => !permitted.Contains(entry.Key)))
                throw new InvalidOperationException("A pick group contains unknown fields or is not an object");
            long Integer(string key, long fallback, long minimum, long maximum)
            {
                if (!group.ContainsKey(key)) return fallback;
                if (!JsonValues.IsStrictInt(group.Get(key), out var value) || value < minimum || value > maximum)
                    throw new InvalidOperationException($"Pick {key} must be an integer from {minimum} to {maximum}");
                return value;
            }
            total += Integer("count", 1, 1, limits.MaxTokensPerGroup);
            _ = Integer("choice_count", limits.MinChoices, limits.MinChoices, limits.MaxChoices);
            var min = Integer("min_rating", 0, 0, Cards.MaxRating);
            var max = Integer("max_rating", Cards.MaxRating, 0, Cards.MaxRating);
            if (min > max) throw new InvalidOperationException("Pick minimum rating exceeds maximum rating");
            if (group.Get("promo_only") is { } promoOnly && promoOnly.ToJsonString() is not ("true" or "false"))
                throw new InvalidOperationException("Pick promo_only must be true or false");
            if (group.Get("label") is { } label && (label is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > 100))
                throw new InvalidOperationException("Pick label must be text of at most 100 characters");
            if (group.Get("rareflags") is { } flags)
            {
                if (flags is not JsonArray array || array.Any(flag => !JsonValues.IsStrictInt(flag, out var id) || id < 0 || id == 18 && !redReward))
                    throw new InvalidOperationException("Pick rarities must be nonnegative integer ids; Champions reds are reward-only");
            }
            if (group.Get("choices") is { } fixedChoices)
            {
                var ids = fixedChoices is JsonArray list && list.All(choice => JsonValues.IsStrictInt(choice, out _))
                    ? list.Select(choice => JsonValues.Int(choice)).ToList() : null;
                if (ids is null || ids.Count < limits.MinChoices || ids.Count > limits.MaxChoices || ids.Distinct().Count() != ids.Count)
                    throw new InvalidOperationException($"A fixed pick offers {limits.MinChoices} to {limits.MaxChoices} different cards");
                if (ids.FirstOrDefault(id => !game.Players.Contains(id)) is var unknown and not 0)
                    throw new InvalidOperationException($"Fixed pick card {unknown} is not a player card");
                if (ids.Any(id => game.Players.Find(id)?.Rareflag == 18) && !redReward)
                    throw new InvalidOperationException("Champions reds are reward-only");
                if (ids.Any(id => game.Players.Find(id)?.Rareflag == 18 && !ResourcePools.IsGoldRed(game.Live.Snapshot, id)))
                    throw new InvalidOperationException("Champions rewards can only offer gold red cards");
                if (group.ContainsKey("choice_count") && group.Int("choice_count") != ids.Count)
                    throw new InvalidOperationException("A fixed pick's choice count is the number of its cards");
            }
            if (group.Text("resource_pool") is { Length: > 0 } named && named != ResourcePools.TotwOneToThree && !(redReward && named == ResourcePools.TotwOneToThreeRed))
                throw new InvalidOperationException("Store picks cannot name a reward-only or unknown resource pool");
            ValidateSource(game, Configuration(pack, group), promos, redReward);
        }
        if (total != pack.Int("size", 12) || total > game.Packs.Rules.MaxPackSize)
            throw new InvalidOperationException("Items per pack must equal the number of pick tokens plus bonus players");
        if (pack.Int("player_pick_count") > 0)
            throw new InvalidOperationException("Use pick groups or the legacy single-pick setting, not both");
        if (pack.Int("bonus_min_rating") is < 0 or > Cards.MaxRating)
            throw new InvalidOperationException("Bonus minimum rating is out of range");
    }

    private static void ValidateSource(GameContext game, JsonObject config, IReadOnlyDictionary<string, (string Category, string Status)>? promos, bool redReward = false)
    {
        if (config.Get("include_general_pool") is { } general && general.ToJsonString() is not ("true" or "false"))
            throw new InvalidOperationException("include_general_pool must be true or false");
        if (config.Get("promo_codes") is { } codes && (codes is not JsonArray array
            || array.Any(node => node is not JsonValue value || !value.TryGetValue<string>(out _))))
            throw new InvalidOperationException("promo_codes must contain promo codes");
        if (config.Get("category_chances") is { } odds && odds is not JsonObject)
            throw new InvalidOperationException("category_chances must be an object");
        var chances = CategoryChances(config);
        promos ??= game.Live.Current.Catalog.Promos.ToDictionary(p => p.Code, p => (p.OddsCategory, p.Status));
        var known = promos.Values.Select(promo => promo.Category).Concat(promos
            .Where(p => p.Value.Status == "active" && p.Key is not ("developer_cards" or "fut_champions"))
            .Select(p => p.Key)).Append(PackOddsRules.UpgradedCategory).ToHashSet();
        if (chances.Keys.Any(category => !known.Contains(category)))
            throw new InvalidOperationException("Choose an existing special odds category");
        // A specials-only pack or pick has no standard rest: its odds table, when it has one, shares out every slot.
        if (config.Bool("promo_only") && chances.Count > 0 && Math.Abs(chances.Values.Sum() - 1) > 0.0000001)
            throw new InvalidOperationException("A specials-only pack or pick needs odds that add up to exactly 100%");
        var requested = config.Arr("promo_codes")?.Select(n => n!.GetValue<string>()).ToArray() ?? [];
        foreach (var code in requested)
            if (!promos.TryGetValue(code, out var source) || source.Status != "active" || code is "fut_champions" or "developer_cards")
                throw new InvalidOperationException($"Unknown, inactive or restricted promo source: {code}");
        if (config.Get("include_general_pool") is not null && !config.Bool("include_general_pool") && requested.Length == 0)
            throw new InvalidOperationException("A pack without the general pool needs at least one specific promo");
        foreach (var key in new[] { "allowed_rareflags", "allowed_team_ids", "allowed_nations" })
            if (config.Get(key) is { } nodes && (nodes is not JsonArray list
                || list.Any(node => !JsonValues.IsStrictInt(node, out var id) || id < 0 || key == "allowed_rareflags" && id == 18 && !redReward)))
                throw new InvalidOperationException($"{key} must contain nonnegative integer ids; Champions reds are reward-only");
    }

    internal static List<long> Draw(FutServices fut, JsonObject config, int count, long sourcePackId = 0,
        GameRandom? random = null, ContentSnapshot? snapshot = null, DateTimeOffset? at = null)
    {
        var game = fut.Game;
        snapshot ??= game.Live.Snapshot;
        // A fixed pick offers its listed cards, in order; a card removed from the catalogue since is left out.
        if (config.Arr("fixed_choices") is { Count: > 0 } fixedChoices)
        {
            var fixedIds = fixedChoices.Select(choice => JsonValues.Int(choice)).Where(snapshot.Players.Contains).ToList();
            if (fixedIds.Any(id => snapshot.Players.Find(id)?.Rareflag == 18 && !ResourcePools.IsGoldRed(snapshot, id)))
                throw new InvalidOperationException("Champions rewards can only offer gold red cards");
            return fixedIds.Take(count).ToList();
        }
        var draw = GameContext.CaptureCardDraw(snapshot, at ?? game.Time.GetUtcNow(), config);
        var namedPool = config.Text("resource_pool");
        var pool = fut.Pools.Named(namedPool, snapshot);
        var min = config.Int("min_rating");
        var max = config.Int("max_rating", Cards.MaxRating);
        HashSet<long>? Values(string key) => config.Arr(key)?.Select(node => JsonValues.Int(node)).ToHashSet();
        var flags = Values("allowed_rareflags");
        var teams = Values("allowed_team_ids");
        var nations = Values("allowed_nations");
        var redReward = sourcePackId is >= 550 and <= 553 && namedPool == ResourcePools.TotwOneToThreeRed && flags?.Contains(18) == true;
        if (namedPool == ResourcePools.TotwOneToThreeRed && !redReward)
            throw new InvalidOperationException("Champions red picks are restricted to Champions rewards");
        var candidates = snapshot.Players.Entries.Where(entry => entry.Value.Rating >= min && entry.Value.Rating <= max
            && !snapshot.Cards.IsGrantOnly(entry.Key) && !snapshot.Cards.IsAquaPro(entry.Key)
            && snapshot.Cards.RarityAllowed(entry.Value.Rareflag, flags)
            && (flags is null || flags.Contains(entry.Value.Rareflag))
            && (teams is null || teams.Contains(entry.Value.Int("teamid")))
            && (nations is null || nations.Contains(entry.Value.Int("nation")))
            && (pool is null ? draw.General(entry.Key) : pool.Contains(entry.Key))
            && (entry.Value.Rareflag != 18 || redReward)
            && (entry.Value.Rareflag != 18 || ResourcePools.IsGoldRed(snapshot, entry.Key))
            && (!config.Bool("promo_only") || entry.Value.Rareflag > 1 || entry.Value.Special))
            .Select(entry => entry.Key).ToHashSet();
        var activePromos = pool is null ? draw.ActivePromos : null;
        // The pick's own odds table (pack odds v3): with no rows every choice is standard, and a specials-only pick without
        // rows weighs its eligible special categories equally.
        var chances = CategoryChances(config);
        var configured = chances.Count > 0;
        var used = new HashSet<long>();
        var result = new List<long>();
        random ??= GameRandom.Shared;
        while (result.Count < count)
        {
            var eligible = candidates.Where(id => !used.Contains(snapshot.Players.Find(id)!.IntOr("assetId", id, id))).ToHashSet();
            var categories = draw.Selector.EligibleCategories(eligible, activePromos, includePromoCodes: configured);
            var specials = categories.Where(category => !IsStandard(category)).ToHashSet();
            string? category = null;
            if (pool is not null || config.Bool("promo_only") || !categories.Any(IsStandard))
            {
                var weights = !configured || pool is not null ? specials.ToDictionary(code => code, _ => 1.0) : chances;
                var options = weights.Where(entry => entry.Value > 0 && specials.Contains(entry.Key)).ToArray();
                if (options.Length > 0) category = random.WeightedChoice(options, options.Select(entry => entry.Value).ToList()).Key;
            }
            else
            {
                var roll = random.NextDouble();
                var threshold = 0.0;
                foreach (var entry in chances)
                {
                    threshold += entry.Value;
                    if (roll < threshold) { if (specials.Contains(entry.Key)) category = entry.Key; break; }
                }
                if (category is null)
                {
                    var standard = categories.Where(IsStandard).ToArray();
                    category = standard.Contains("gold_rare") ? "gold_rare"
                        : standard.Length > 0 ? random.Choice(standard) : null;
                }
            }
            var chosen = category is not null ? draw.Selector.Pick(category, eligible, random, activePromoIds: activePromos) : null;
            if (chosen is null) throw new InvalidOperationException($"Only {result.Count} eligible choices satisfy this pick's sources, odds and filters");
            result.Add(chosen.Value);
            used.Add(snapshot.Players.Find(chosen.Value)!.IntOr("assetId", chosen.Value, chosen.Value));
        }
        return result;
    }
}
