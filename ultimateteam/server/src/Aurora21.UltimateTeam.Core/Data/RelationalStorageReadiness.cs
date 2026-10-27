using System.Data;
using Npgsql;

namespace Aurora21.UltimateTeam.Core.Data;

public enum RelationalStorageState
{
    Empty,
    Legacy,
    RelationalReady,
    UpgradeRequired,
    Incomplete,
    Unsupported,
}

public sealed record RelationalStorageStatus(RelationalStorageState State, string Detail);

/// <summary>Read-only storage classification; never creates a schema, club, or import marker.</summary>
public static class RelationalStorageReadiness
{
    public static Task<RelationalStorageStatus> InspectForRuntimeAsync(NpgsqlDataSource source, CancellationToken cancellationToken = default) => InspectAsync(source, cancellationToken, allowLegacy: true);

    public static async Task<RelationalStorageStatus> InspectAsync(NpgsqlDataSource source,
        CancellationToken cancellationToken = default, bool allowLegacy = false)
    {
        var package = RelationalSetupPackage.Load();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await ScalarAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken);
        var status = await InspectAsync(connection, transaction, package, cancellationToken, allowLegacy);
        await transaction.CommitAsync(cancellationToken);
        return status;
    }

    internal static async Task<RelationalStorageStatus> InspectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RelationalSetupPackage package, CancellationToken token, bool allowLegacy = false)
    {
        var major = Convert.ToInt32(await ScalarAsync(connection, transaction, "SHOW server_version_num", token)) / 10000;
        if (major != 18) return new(RelationalStorageState.Unsupported, $"PostgreSQL {major} is unsupported; expected 18.");
        var legacy = (bool)(await ScalarAsync(connection, transaction, """
            SELECT to_regclass('public.clubs') IS NOT NULL OR to_regclass('public.items') IS NOT NULL
                OR to_regclass('public.schema_migrations') IS NOT NULL
            """, token))!;
        var publicRelations = Convert.ToInt64(await ScalarAsync(connection, transaction, """
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r','p','v','m','f','S')
            """, token));
        var targetObjects = Convert.ToInt64(await ScalarAsync(connection, transaction, """
            SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'ultimateteam')
                 + (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = 'ultimateteam')
            """, token));
        if (targetObjects == 0)
            return legacy ? new(RelationalStorageState.Legacy, "Legacy PostgreSQL schema detected; relational upgrade is required.")
                : publicRelations != 0 ? new(RelationalStorageState.Unsupported, "Unrecognized public-schema storage; refusing a fresh install.")
                : new(RelationalStorageState.Empty, "No legacy or relational storage found.");
        if (!(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.schema_migrations') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Installed package is missing its migration ledger.");
        var version = Convert.ToInt32(await ScalarAsync(connection, transaction,
            "SELECT COALESCE(max(version), 0) FROM ultimateteam.schema_migrations", token));
        if (version < 0 || version > RelationalCatalogMetadataMigration.CurrentVersion) return new(RelationalStorageState.Unsupported, "Relational migration version is newer than this foundation supports.");
        if (version < 38)
        {
            var ledger = (bool)(await ScalarAsync(connection, transaction,
                "SELECT to_regclass('ultimateteam.setup_artifact_versions') IS NOT NULL", token))!;
            if (!ledger) return new(RelationalStorageState.Incomplete, "Populated relational namespace has no setup ledger.");
            var ledgerColumns = Convert.ToInt32(await ScalarAsync(connection, transaction, """
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = 'ultimateteam' AND table_name = 'setup_artifact_versions'
                  AND ((column_name IN ('version_code','package_sha256') AND data_type = 'text')
                       OR (column_name = 'id' AND data_type = 'bigint')
                       OR (column_name = 'applied_at' AND data_type = 'timestamp with time zone'))
                """, token));
            if (ledgerColumns != 4) return new(RelationalStorageState.Incomplete, "Malformed setup ledger.");
            await using (var command = new NpgsqlCommand(
                "SELECT version_code, package_sha256 FROM ultimateteam.setup_artifact_versions ORDER BY id", connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) return new(RelationalStorageState.Incomplete, "Setup ledger has no committed installation.");
                if (reader.IsDBNull(0) || reader.GetString(0) != package.VersionCode)
                    return new(RelationalStorageState.Unsupported, "Setup version is not supported by this server.");
                if (reader.IsDBNull(1) || reader.GetString(1) != package.PackageSha256)
                    return new(RelationalStorageState.Unsupported, "Installed setup package hash mismatch; no reseed is permitted.");
                if (await reader.ReadAsync(token)) return new(RelationalStorageState.Unsupported, "Unexpected additional setup versions.");
            }
        }
        else if ((bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.setup_artifact_versions') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Obsolete setup artifact table remains after its removal migration.");
        var requiredTables = version >= 8
            ? package.TableNames.Except(new[] { "card_designs", "card_type_designs" }).ToArray()
            : package.TableNames;
        if (version >= 13) requiredTables = requiredTables.Except(new[] { "card_rarity_groups" }).Concat(new[] { "native_rarities", "card_type_rarity_group_memberships" }).ToArray();
        if (version >= 17) requiredTables = requiredTables.Except(new[] { "catalog_sources" }).ToArray();
        if (version >= 18) requiredTables = requiredTables.Except(new[] { "club_imports" }).ToArray();
        if (version >= 20) requiredTables = requiredTables.Except(new[] { "definition_extensions" }).ToArray();
        if (version >= 24) requiredTables = requiredTables.Except(new[] { "legacy_kv" }).ToArray();
        if (version >= 31) requiredTables = requiredTables.Except(new[] { "market_player_attribute_overrides" }).ToArray();
        if (version >= 34) requiredTables = requiredTables.Except(new[] { "player_attribute_overrides" }).ToArray();
        if (version >= 36) requiredTables = requiredTables.Except(new[] { "player_stat_values", "market_player_stat_values" }).ToArray();
        if (version >= 38) requiredTables = requiredTables.Except(new[] { "setup_artifact_versions" }).ToArray();
        if (version >= 40) requiredTables = requiredTables.Except(new[] { "squad_extensions" }).ToArray();
        var expectedTables = Convert.ToInt32(await ScalarAsync(connection, transaction, """
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'ultimateteam' AND c.relkind IN ('r','p') AND c.relname = ANY(@tables)
            """, token, ("tables", requiredTables)));
        if (expectedTables != requiredTables.Length)
            return new(RelationalStorageState.Incomplete, "Installed package is missing required tables.");

        if (version >= 8 && (bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.card_designs') IS NOT NULL OR to_regclass('ultimateteam.card_type_designs') IS NOT NULL OR EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='promo_cards' AND column_name='design_override_id')", token))!)
            return new(RelationalStorageState.Incomplete, "Unused card design infrastructure remains after its removal migration.");
        if (version >= 1 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.catalog_compatibility_metadata') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Catalog metadata migration is missing its projection view.");
        if (version >= 2 && (bool)(await ScalarAsync(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
                WHERE table_schema='ultimateteam' AND table_name='player_card_definitions'
                    AND column_name='attribute_provenance')
            """, token))!)
            return new(RelationalStorageState.Incomplete, "Stat provenance removal is not reflected in the installed schema.");
        if (version >= 4 && (bool)(await ScalarAsync(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
                WHERE table_schema='ultimateteam' AND table_name='player_card_definitions'
                    AND column_name IN ('native_team_id','native_league_id','native_nation_id'))
            """, token))!)
            return new(RelationalStorageState.Incomplete, "Player native ID columns remain after their removal migration.");
        if (version >= 5 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.traits') IS NOT NULL AND to_regclass('ultimateteam.player_card_traits') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Player trait migration is missing required tables.");
        if (version >= 6 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_card_definitions' AND column_name='design_override_id')", token))!)
            return new(RelationalStorageState.Incomplete, "Player design override remains after its removal migration.");
        if (version >= 7 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.sbc_squad_items') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "SBC squad selection table is missing.");
        if (version >= 13 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.native_rarities') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types' AND column_name='native_rarity_id') AND NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types' AND column_name='native_rareflag')", token))!)
            return new(RelationalStorageState.Incomplete, "Native rarity normalization is incomplete.");
        if (version >= 14 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types' AND column_name='name' AND is_nullable='NO')", token))!)
            return new(RelationalStorageState.Incomplete, "Readable card type names are missing.");
        if (version >= 15 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types' AND column_name='native_default_subtype')", token))!)
            return new(RelationalStorageState.Incomplete, "Unused default subtype remains after removal.");
        if (version >= 16 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types' AND column_name='code')", token))!)
            return new(RelationalStorageState.Incomplete, "Unused card type code remains after removal.");
        if (version >= 17 && (bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.catalog_sources') IS NOT NULL OR EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND column_name IN ('source_id','metadata_source_id','mapping_status'))", token))!)
            return new(RelationalStorageState.Incomplete, "Removed catalog audit metadata remains.");
        if (version >= 18 && (bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.club_imports') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Unused relational import table remains.");
        if (version >= 19 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='customisation_definitions' AND column_name IN ('native_stadium_id','source_team_id','template_metadata'))", token))!)
            return new(RelationalStorageState.Incomplete, "Unused customisation metadata remains.");
        if (version >= 20 && (bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.definition_extensions') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Retired definition extension table remains.");
        if (version >= 21 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='footballers' AND column_name IN ('identity_status','date_of_birth_source'))", token))!)
            return new(RelationalStorageState.Incomplete, "Removed footballer audit columns remain.");
        if (version >= 22 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='item_definitions' AND column_name IN ('metadata_schema_revision','source_fields','source_missing_fields','source_card_id','source_card_url','resource_id_source','name_source'))", token))!)
            return new(RelationalStorageState.Incomplete, "Removed item import metadata remains.");
        if (version >= 24 && (bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.legacy_kv') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Unused relational legacy value table remains.");
        if (version >= 25 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_definitions' AND column_name='rarity_id' AND is_nullable='NO') AND NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_definitions' AND column_name IN ('common_name','native_rareflag','native_team_id','native_league_id','native_nation_id','team_id'))", token))!)
            return new(RelationalStorageState.Incomplete, "Manager references are not normalized.");
        if (version >= 26 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_definitions' AND column_name IN ('talk_rating','formation_id'))", token))!)
            return new(RelationalStorageState.Incomplete, "Unused manager metadata remains.");
        if (version >= 27 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_items' AND column_name='native_league_override')", token))!)
            return new(RelationalStorageState.Incomplete, "Raw manager league override remains.");
        if (version >= 28 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_items' AND column_name='league_id') AND NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_items' AND column_name='league_override_id')", token))!)
            return new(RelationalStorageState.Incomplete, "Owned manager league column is not current.");
        if (version >= 31 && (bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.market_player_attribute_overrides') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Unsupported market attribute override table remains.");
        if (version >= 32 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='market_player_snapshots' AND column_name='chemistry_style_definition_id') AND NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='market_player_snapshots' AND column_name IN ('loan_games','style_definition_id'))", token))!)
            return new(RelationalStorageState.Incomplete, "Market snapshot columns are not current.");
        if (version >= 33 && !(bool)(await ScalarAsync(connection, transaction,  $"SELECT count(*) FILTER(WHERE column_name='market_listing_id')={(version >= 36 ? 4 : 5)} AND count(*) FILTER(WHERE column_name='trade_id')=0 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name IN ('market_listings','market_item_snapshots','market_player_snapshots','market_player_stat_values','market_watch')", token))!)
            return new(RelationalStorageState.Incomplete, "Market listing identifier columns are not current.");
        if (version >= 34 && (bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.player_attribute_overrides') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Unsupported player attribute override table remains.");
        if (version >= 35 && (bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_card_definitions' AND column_name IN ('native_trait_1','native_trait_2','native_version'))", token))!)
            return new(RelationalStorageState.Incomplete, "Empty native player metadata columns remain.");
        if (version >= 37 && !(bool)(await ScalarAsync(connection, transaction, "SELECT count(*) FILTER(WHERE column_name='player_card_definition_id')=1 AND count(*) FILTER(WHERE column_name='definition_id')=0 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='promo_cards'", token))!)
            return new(RelationalStorageState.Unsupported, "Promo player-card reference column is missing or obsolete.");
        if (version >= 36 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.player_stat_values') IS NULL AND to_regclass('ultimateteam.market_player_stat_values') IS NULL AND (SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name IN ('player_items','market_player_snapshots') AND column_name='match_stats' AND data_type='jsonb' AND is_nullable='NO')=2", token))!)
            return new(RelationalStorageState.Incomplete, "Player match counter JSON storage is incomplete.");
        if (version >= 39 && !(bool)(await ScalarAsync(connection, transaction, "SELECT count(*) FILTER(WHERE column_name='squad_player_id')=0 AND count(*) FILTER(WHERE column_name='text_value' AND data_type='text')=1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='squad_tactic_instructions'", token))!)
            return new(RelationalStorageState.Incomplete, "Tactic instruction player reference remains or text value column is missing.");
        if (version >= 40 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.squad_extensions') IS NULL AND (SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='squads' AND ((column_name='extension_schema_revision' AND data_type='bigint' AND is_nullable='NO') OR (column_name='extension_payload' AND data_type='json' AND is_nullable='NO') OR (column_name='wire_representation' AND data_type='json')))=3 AND NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='traits' AND column_name IN('code','mapping_evidence'))", token))!)
            return new(RelationalStorageState.Incomplete, "Squad extension merge or trait cleanup is incomplete.");
        if (version >= 41 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.store_packs') IS NOT NULL AND to_regclass('ultimateteam.store_pack_rarity_weights') IS NOT NULL AND to_regclass('ultimateteam.store_pack_groups') IS NOT NULL AND to_regclass('ultimateteam.store_pack_art_bands') IS NOT NULL AND to_regclass('ultimateteam.store_pack_tiers') IS NOT NULL AND to_regclass('ultimateteam.store_pack_details') IS NOT NULL AND (SELECT count(*) FROM ultimateteam.store_pack_details)=1", token))!)
            return new(RelationalStorageState.Incomplete, "Store pack configuration tables are missing or incomplete.");
        if (version >= 42 && !(bool)(await ScalarAsync(connection, transaction, "SELECT count(*)=2 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='store_packs' AND column_name IN ('allowed_team_ids','allowed_nations') AND data_type='ARRAY'", token))!)
            return new(RelationalStorageState.Incomplete, "Store pack player filters are missing.");
        if (version >= 43 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.club_player_search') IS NOT NULL AND to_regclass('ultimateteam.lfv4_ix_club_player_search_default') IS NOT NULL AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='ultimateteam.items'::regclass AND tgname='lfv4_trg_club_player_search_items' AND tgenabled='O')", token))!)
            return new(RelationalStorageState.Incomplete, "Club player search projection is missing.");
        if (version >= 49 && !(bool)(await ScalarAsync(connection, transaction, "SELECT count(*) FILTER(WHERE table_name='store_packs')=11 AND count(*) FILTER(WHERE table_name='player_pick_items')=2 FROM information_schema.columns WHERE table_schema='ultimateteam' AND ((table_name='store_packs' AND column_name IN ('points_price','allowed_currencies','admin_only','promo_only','special_weights_exclusive','player_picks','bonus_player_count','bonus_min_rating','bonus_promo_only','random_consumable_count','random_manager_count')) OR (table_name='player_pick_items' AND column_name IN ('promo_only','rareflags')))", token))!)
            return new(RelationalStorageState.Incomplete, "Store pack mechanics or player pick filters are missing.");
        if (version >= 51 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_pick_items' AND column_name='label') AND EXISTS(SELECT 1 FROM ultimateteam.native_rarities WHERE native_rareflag=18)", token))!)
            return new(RelationalStorageState.Incomplete, "FUT Champions red picks are missing.");
        if (version >= 52 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.squad_battles_weeks') IS NOT NULL AND to_regclass('ultimateteam.squad_battles_pending') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Squad Battles competition tables are missing.");
        if (version >= 53 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='store_packs' AND column_name='identity_pack') AND EXISTS(SELECT 1 FROM ultimateteam.item_definitions WHERE family_id=4 AND native_resource_id BETWEEN 6200001 AND 6299999 AND native_resource_id<>6200058)", token))!)
            return new(RelationalStorageState.Incomplete, "Club cosmetic cards are missing.");
        if (version >= 54 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.club_vanity_slots') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "My Stadium vanity slots are missing.");
        if (version >= 122 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.club_vanity_items') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "My Stadium owned vanity items are missing.");
        if (version >= 123 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='store_packs' AND column_name='vanity_pack')", token))!)
            return new(RelationalStorageState.Incomplete, "My Stadium store packs are missing.");
        if (version >= 124 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='match_clock' AND column_name='opponent_player')", token))!)
            return new(RelationalStorageState.Incomplete, "The match clock's opponent player is missing.");
        if (version >= 55 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.rivals_pending') IS NOT NULL AND to_regclass('ultimateteam.club_records') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='rivals_progress' AND column_name='weekly_draws')", token))!)
            return new(RelationalStorageState.Incomplete, "Rivals skill tables are missing.");
        // 066 replaced 056's shells (Rice and Pogba removed) with the developers' Pro cards.
        if (version is >= 56 and < 66 && !(bool)(await ScalarAsync(connection, transaction, "SELECT count(*) = 3 FROM ultimateteam.item_definitions WHERE family_id=1 AND native_resource_id IN (168006538, 167983460, 167968024)", token))!)
            return new(RelationalStorageState.Incomplete, "Pro Player shells are missing.");
        if (version >= 66 && !(bool)(await ScalarAsync(connection, transaction, "SELECT count(*) FILTER (WHERE native_resource_id IN (167988548, 167996618, 167983460, 168019987, 167984358, 167961228)) = 6 AND count(*) FILTER (WHERE native_resource_id IN (167963031, 168006538, 167968024)) = 0 FROM ultimateteam.item_definitions WHERE family_id=1", token))!)
            return new(RelationalStorageState.Incomplete, "The developers' Pro cards are missing.");
        if (version >= 57 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='market_listings' AND column_name='duration_seconds')", token))!)
            return new(RelationalStorageState.Incomplete, "Market listing durations are missing.");
        if (version >= 58 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.rivals_week_results') IS NOT NULL AND to_regclass('ultimateteam.champion_results') IS NOT NULL AND to_regclass('ultimateteam.weekend_league_records') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Rivals and Weekend League tables are missing.");
        // Migrations are proven by their structure, not by particular pack rows: since migration 119 any unused pack may be deleted.
        if (version >= 59 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_pick_items' AND column_name='max_rating')", token))!)
            return new(RelationalStorageState.Incomplete, "Competition reward pick ceilings are missing.");
        if (version >= 60 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.squad_battles_sets') IS NOT NULL AND to_regclass('ultimateteam.squad_battles_results') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='squad_battles_pending' AND column_name='featured_squad_id')", token))!)
            return new(RelationalStorageState.Incomplete, "Squad Battles opponent sets and weekly results are missing.");
        if (version >= 61 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.champion_connection_claims') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_pick_items' AND column_name='resource_pool')", token))!)
            return new(RelationalStorageState.Incomplete, "Live Team of the Week rewards are missing.");
        if (version >= 64 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='store_packs' AND column_name='swap_token_rareflag')", token))!)
            return new(RelationalStorageState.Incomplete, "Icon Swaps token pack columns are missing.");
        if (version >= 68 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='store_packs' AND column_name='rated_rare_slots')", token))!)
            return new(RelationalStorageState.Incomplete, "Rated rare slots are missing.");
        if (version >= 69 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='items' AND column_name='reward_source')", token))!)
            return new(RelationalStorageState.Incomplete, "Item reward sources are missing.");
        if (version >= 70 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.match_clock') IS NOT NULL AND to_regclass('ultimateteam.match_rewards') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='unopened_packs' AND column_name='contents') AND EXISTS(SELECT 1 FROM ultimateteam.store_packs WHERE pack_id = 544)", token))!)
            return new(RelationalStorageState.Incomplete, "The match reward gate and Match Pack tables are missing.");
        if (version >= 71 && !(bool)(await ScalarAsync(connection, transaction, "SELECT (SELECT count(*) = 2 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='store_packs' AND column_name IN ('consumable_mix','manager_league_card_share')) AND EXISTS(SELECT 1 FROM ultimateteam.consumable_definitions WHERE consumable_kind='manager_league')", token))!)
            return new(RelationalStorageState.Incomplete, "Manager league cards are missing.");
        if (version >= 72 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.content_changesets') IS NOT NULL AND to_regclass('ultimateteam.content_changes') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Content changeset tables are missing.");
        if (version >= 73 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.content_rules') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Content rules table is missing.");
        if (version >= 94 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.icon_rotations') IS NOT NULL AND to_regclass('ultimateteam.content_table_revisions') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='promo_buckets' AND column_name='enabled')", token))!)
            return new(RelationalStorageState.Incomplete, "Promo management or content revision tracking is incomplete.");
        if (version >= 95 && !(bool)(await ScalarAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='store_packs' AND column_name='mixed_contents')", token))!)
            return new(RelationalStorageState.Incomplete, "Mixed pack contents are missing.");
        if (version >= 97 && !(bool)(await ScalarAsync(connection, transaction, "SELECT to_regclass('ultimateteam.owned_legacy_pack_definitions') IS NOT NULL", token))!)
            return new(RelationalStorageState.Incomplete, "Owned legacy pack compatibility definitions are missing.");
        return legacy && !allowLegacy ? new(RelationalStorageState.UpgradeRequired, "Both schemas exist; verified conversion/cutover is not implemented.")
            : new(RelationalStorageState.RelationalReady, "Installed relational setup matches this server; gameplay backend is available.");
    }

    internal static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync(token);
    }
}
