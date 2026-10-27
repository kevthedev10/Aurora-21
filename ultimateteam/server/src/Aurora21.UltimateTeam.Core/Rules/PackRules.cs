using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class PackArtBand
{
    public required long BelowPackId { get; set; }
    public required long ArtId { get; set; }
}

public sealed class RareFallbackRatings
{
    public required long Gold { get; set; }
    public required long Silver { get; set; }
    public required long Bronze { get; set; }
}

public sealed class TierRules
{
    public required long GoldMinRating { get; set; }
    public required long SilverMinRating { get; set; }
    public required RareFallbackRatings RareFallbackMinRating { get; set; }
}

public sealed class EliteWeightRules
{
    public required long FromRating { get; set; }
    public required double PerRating { get; set; }
    public required double Maximum { get; set; }
    public required double Minimum { get; set; }
}

/// <summary>
/// rules/packs.json and the pack tables: every pack definition, rarity weights and pack art. Since pack odds v3
/// (migration 119) there is one kind of pack: the store lineup sells some, rewards name some, and the rest are unassigned.
/// </summary>
public sealed class PackRules : IJsonOnDeserialized
{
    public required JsonObject SpecialRarityWeights { get; set; }
    public required long NextPackId { get; set; }
    public required JsonObject StorePacks { get; set; }
    [JsonIgnore]
    public JsonObject TokenOffers { get; set; } = new();
    public required List<string> AlwaysPresentPackIds { get; set; }
    public required int MaxPackSize { get; set; }
    public required int MaxKitPackSize { get; set; }
    public required JsonObject CustomPack { get; set; }
    public required Dictionary<string, double> CustomPackManagerChance { get; set; }
    public required List<string> ObsoletePackFiveNames { get; set; }
    public required Dictionary<long, string> DisplayOnlyPackNames { get; set; }
    public required SortedDictionary<long, long> PlayerPickPacks { get; set; }
    public required Dictionary<string, long> StoreGroups { get; set; }
    public required Dictionary<string, string> StoreGroupAliases { get; set; }
    public required List<PackArtBand> PackArt { get; set; }
    public required long SpecialPackArtId { get; set; }
    public required Dictionary<string, long> StoreGroupArtIds { get; set; }
    public required long StoreArtMaxPackId { get; set; }
    public required TierRules Tiers { get; set; }
    public required EliteWeightRules EliteWeight { get; set; }

    /// <summary>Every pack name the client may display, by pack id, including display-only packs.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<long, string> PackNames { get; private set; } = new SortedDictionary<long, string>();

    [JsonIgnore]
    public IReadOnlySet<long> PlayerPickPackIds { get; private set; } = new HashSet<long>();

    public void OnDeserialized()
    {
        // Numbers read from the file are written like computed ones (see JsonMaterializer).
        SpecialRarityWeights = (JsonObject)JsonMaterializer.Materialize(SpecialRarityWeights)!;
        StorePacks = (JsonObject)JsonMaterializer.Materialize(StorePacks)!;
        CustomPack = (JsonObject)JsonMaterializer.Materialize(CustomPack)!;
        InitializeDerived();
    }

    /// <summary>
    /// The lookups computed from the configuration, without re-materializing its numbers. Rules built
    /// from the database already carry each number with the scale it was stored at, and materializing
    /// would render a stored 0.0 as 0 and change what the client receives.
    /// </summary>
    internal void InitializeDerived()
    {

        var names = new SortedDictionary<long, string>();
        foreach (var (key, pack) in StorePacks)
        {
            if (pack is JsonObject config && config.Text("name") is { Length: > 0 } name)
                names[long.Parse(key, CultureInfo.InvariantCulture)] = name;
        }
        foreach (var (id, name) in DisplayOnlyPackNames)
        {
            names[id] = name;
        }
        PackNames = names;
        PlayerPickPackIds = PlayerPickPacks.Values.ToHashSet();
    }

    public string PackName(long packId) => PackNames.TryGetValue(packId, out var name) ? name : $"Pack {packId}";

    /// <summary>A copy of a pack's configuration, or null when no pack has that id.</summary>
    public JsonObject? Pack(long packId) =>
        StorePacks.Obj(packId.ToString(CultureInfo.InvariantCulture))?.Copy();

    /// <summary>The pack art band (1-4) a pack id draws when the pack names no art of its own.</summary>
    public long ArtId(long packId) => PackArt.FirstOrDefault(band => packId < band.BelowPackId)?.ArtId ?? SpecialPackArtId;

    /// <summary>
    /// A pack's tile art, in the store and in My Packs alike: the art of its store category (bronze, silver, gold,
    /// special) when it names one. A pack without a category draws by its id: low ids by their dominant tier, higher
    /// ids by the id bands, as the former reward packs always have. Sending the pack id as the asset id drew a blank
    /// player silhouette.
    /// </summary>
    public long StoreArtId(long packId, JsonObject config)
    {
        var requested = config.Str("store_group").ToLowerInvariant();
        var group = StoreGroupAliases.GetValueOrDefault(requested, requested);
        if (StoreGroupArtIds.TryGetValue(group, out var art)) return art;
        if (packId > StoreArtMaxPackId && config.Obj("mixed_contents") is not { Count: > 0 }) return ArtId(packId);
        return StoreGroupArtIds[Models.PackTierMix.For(config, 1).Dominant];
    }

    /// <summary>The currencies a pack is sold for, in store order: its <c>allowed_currencies</c>, or coins and points.</summary>
    public static IReadOnlyList<string> AllowedCurrencies(JsonObject config)
    {
        var configured = (config.Arr("allowed_currencies") ?? new JsonArray())
            .Select(node => JsonValues.Text(node).Trim().ToLowerInvariant()).ToHashSet();
        var allowed = StoreCurrencies.Where(configured.Contains).ToList();
        return allowed.Count > 0 ? allowed : StoreCurrencies;
    }

    private static readonly string[] StoreCurrencies = { "coins", "points" };

    /// <summary>The reward pack for an N+ player pick; unknown ratings get the lowest one.</summary>
    public long PlayerPickPack(long minRating) =>
        PlayerPickPacks.TryGetValue(minRating, out var packId) ? packId : PlayerPickPacks.First().Value;

    /// <summary>A card's tier: the catalog's own tier when it names one, otherwise by rating.</summary>
    public string TierFor(long rating, string catalogTier = "") =>
        catalogTier is "gold" or "silver" or "bronze" ? catalogTier
        : rating >= Tiers.GoldMinRating ? "gold"
        : rating >= Tiers.SilverMinRating ? "silver"
        : "bronze";

    /// <summary>Whether a base card of this tier and rating joins the rare pool when the catalog has no rare flag.</summary>
    public bool IsFallbackRare(string tier, long rating) => tier switch
    {
        "gold" => rating >= Tiers.RareFallbackMinRating.Gold,
        "silver" => rating >= Tiers.RareFallbackMinRating.Silver,
        _ => rating >= Tiers.RareFallbackMinRating.Bronze,
    };

    /// <summary>Restrained selection boost for elite ratings.</summary>
    public double EliteWeightFor(long rating) =>
        rating < EliteWeight.FromRating ? 1.0 : Math.Min(EliteWeight.Maximum, 1.0 + (rating - EliteWeight.FromRating + 1) * EliteWeight.PerRating);
}
