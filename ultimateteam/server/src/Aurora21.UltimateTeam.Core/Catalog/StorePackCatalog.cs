using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// Store pack configuration read once from the relational tables migration 041 introduced.
///
/// This is deliberately a sibling of <see cref="RelationalCatalogSnapshot"/> rather than part of it:
/// that snapshot is the catalogue proper, while pack configuration changes on its own: the panel publishes it live
/// through <see cref="Config.PackSettings"/> without a content reload.
///
/// </summary>
public sealed class StorePackCatalog
{
    private StorePackCatalog(PackRules rules) => Rules = rules;

    /// <summary>The pack rules as the rest of the server consumes them, sourced from the database.</summary>
    public PackRules Rules { get; }

    public static async Task<StorePackCatalog> LoadAsync(NpgsqlDataSource source, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await RelationalStorageReadiness.ScalarAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken);
        var unit = new UnitOfWork(connection, transaction, 0, TimeProvider.System, null, cancellationToken);
        var catalog = await LoadAsync(unit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return catalog;
    }

    public static async Task<StorePackCatalog> LoadAsync(IUnitOfWork work, CancellationToken cancellationToken = default)
    {
        // One kind of pack since migration 119: the store lineup, rewards and panel grants all read the same rows.
        var storePacks = await work.StorePacksAsync();
        var template = await work.StorePackAsync(0, "template")
            ?? throw new RuntimeSetupException("Store pack configuration has no custom-pack template row.");
        var weights = await work.RarityWeightsAsync();

        var flags = await work.QueryAsync(
            "SELECT pack_id, name, always_present, display_only, player_pick_min_rating, kind FROM ultimateteam.store_packs ORDER BY pack_id",
            reader => (Id: reader.GetInt64(0), Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                Always: reader.GetBoolean(2), Display: reader.GetBoolean(3),
                Pick: reader.IsDBNull(4) ? (long?)null : reader.GetInt32(4), Kind: reader.GetString(5)));

        var groups = await work.QueryAsync(
            "SELECT name, category_id, art_id, alias_of FROM ultimateteam.store_pack_groups ORDER BY name",
            reader => (Name: reader.GetString(0), Category: reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1),
                Art: reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2), Alias: reader.IsDBNull(3) ? null : reader.GetString(3)));

        var bands = await work.QueryAsync(
            "SELECT below_pack_id, art_id FROM ultimateteam.store_pack_art_bands ORDER BY below_pack_id",
            reader => new PackArtBand { BelowPackId = reader.GetInt64(0), ArtId = reader.GetInt64(1) });

        var tiers = await work.QueryAsync(
            "SELECT tier, min_rating, rare_fallback_min_rating FROM ultimateteam.store_pack_tiers",
            reader => (Tier: reader.GetString(0), Min: reader.IsDBNull(1) ? (long?)null : reader.GetInt32(1),
                Fallback: (long)reader.GetInt32(2)));

        var details = (await work.QueryAsync("""
            SELECT max_pack_size, max_kit_pack_size,
                   special_pack_art_id, store_art_max_pack_id, elite_from_rating, elite_per_rating,
                   elite_maximum, elite_minimum
            FROM ultimateteam.store_pack_details
            """, reader => new
        {
            MaxPackSize = reader.GetInt32(0),
            MaxKitPackSize = reader.GetInt32(1),
            SpecialPackArtId = reader.GetInt64(2),
            StoreArtMaxPackId = reader.GetInt64(3),
            EliteFrom = reader.GetInt64(4),
            ElitePer = (double)reader.GetDecimal(5),
            EliteMax = (double)reader.GetDecimal(6),
            EliteMin = (double)reader.GetDecimal(7),
        })).SingleOrDefault() ?? throw new RuntimeSetupException("Store pack configuration has no details row.");

        long Tier(string name, bool fallback) => tiers.Where(t => t.Tier == name)
            .Select(t => fallback ? t.Fallback : t.Min ?? 0).FirstOrDefault();

        var rules = new PackRules
        {
            SpecialRarityWeights = weights,
            // Derived rather than stored: the next id after every pack (the template and display rows are not packs).
            NextPackId = await work.ScalarAsync<long>("""
                SELECT greatest(coalesce((SELECT max(pack_id) FROM ultimateteam.store_packs),0),
                    coalesce((SELECT max(pack_id) FROM ultimateteam.owned_legacy_pack_definitions),0),
                    coalesce((SELECT max(offer_id) FROM ultimateteam.token_store_listings),0),
                    coalesce((SELECT max(pack_id) FROM ultimateteam.token_store_listings),0))+1
                """),
            StorePacks = storePacks,
            AlwaysPresentPackIds = flags.Where(f => f.Always)
                .Select(f => f.Id.ToString(CultureInfo.InvariantCulture)).ToList(),
            MaxPackSize = details.MaxPackSize,
            MaxKitPackSize = details.MaxKitPackSize,
            CustomPack = template,
            // Per-pack manager chances are columns on the rows now, so no separate id map survives.
            CustomPackManagerChance = new Dictionary<string, double>(),
            // Only existed to spot a stale settings file; a primary key makes that impossible.
            ObsoletePackFiveNames = new List<string>(),
            DisplayOnlyPackNames = flags.Where(f => f.Display && f.Name is not null)
                .ToDictionary(f => f.Id, f => f.Name!),
            PlayerPickPacks = new SortedDictionary<long, long>(
                flags.Where(f => f.Pick is not null).ToDictionary(f => f.Pick!.Value, f => f.Id)),
            StoreGroups = groups.Where(g => g.Alias is null && g.Category is not null)
                .ToDictionary(g => g.Name, g => g.Category!.Value),
            StoreGroupAliases = groups.Where(g => g.Alias is not null).ToDictionary(g => g.Name, g => g.Alias!),
            PackArt = bands.ToList(),
            SpecialPackArtId = details.SpecialPackArtId,
            StoreGroupArtIds = groups.Where(g => g.Alias is null && g.Art is not null)
                .ToDictionary(g => g.Name, g => g.Art!.Value),
            StoreArtMaxPackId = details.StoreArtMaxPackId,
            Tiers = new TierRules
            {
                GoldMinRating = Tier("gold", fallback: false),
                SilverMinRating = Tier("silver", fallback: false),
                RareFallbackMinRating = new RareFallbackRatings
                {
                    Gold = Tier("gold", fallback: true),
                    Silver = Tier("silver", fallback: true),
                    Bronze = Tier("bronze", fallback: true),
                },
            },
            EliteWeight = new EliteWeightRules
            {
                FromRating = details.EliteFrom,
                PerRating = details.ElitePer,
                Maximum = details.EliteMax,
                Minimum = details.EliteMin,
            },
        };
        rules.InitializeDerived();
        rules.TokenOffers = await work.TokenOffersAsync(storePacks);
        return new StorePackCatalog(rules);
    }
}
