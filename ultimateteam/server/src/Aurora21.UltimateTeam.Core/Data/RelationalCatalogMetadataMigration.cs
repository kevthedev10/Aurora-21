using Npgsql;

namespace Aurora21.UltimateTeam.Core.Data;

internal static class RelationalCatalogMetadataMigration
{
    internal const int CurrentVersion = 180;
    private static readonly string[] Resources =
    [
        "001-catalog-metadata.sql",
        "002-remove-attribute-provenance.sql",
        "003-unnamed-sudamericana-team.sql",
        "004-player-reference-native-ids.sql",
        "005-player-card-traits.sql",
        "006-player-card-cleanup.sql",
        "007-sbc-squad-items.sql",
        "008-remove-unused-card-designs.sql",
        "009-icon-swaps-one-name.sql",
        "010-professional-player-name.sql",
        "011-explicit-card-rarity-groups.sql",
        "012-next-gen-fgs-names.sql",
        "013-native-rarities.sql",
        "014-readable-card-type-names.sql",
        "015-remove-default-subtype.sql",
        "016-card-type-column-cleanup.sql",
        "017-remove-catalog-audit.sql",
        "018-remove-unused-club-imports.sql",
        "019-remove-unused-customisation-metadata.sql",
        "020-remove-empty-definition-extensions.sql",
        "021-remove-footballer-audit-columns.sql",
        "022-remove-item-definition-import-metadata.sql",
        "023-kit-item-response-format.sql",
        "024-remove-unused-relational-legacy-kv.sql",
        "025-manager-definition-references.sql",
        "026-remove-unused-manager-metadata.sql",
        "027-manager-item-league-reference.sql",
        "028-manager-owned-league.sql",
        "029-manager-default-leagues.sql",
        "030-confirm-manager-default-leagues.sql",
        "031-remove-market-player-attribute-overrides.sql",
        "032-market-player-snapshot-cleanup.sql",
        "033-readable-market-listing-id.sql",
        "034-remove-player-attribute-overrides.sql",
        "035-remove-empty-player-native-metadata.sql",
        "036-player-match-stats-json.sql",
        "037-readable-promo-player-card-id.sql",
        "038-remove-setup-artifact-versions.sql",
        "039-remove-tactic-player-reference.sql",
        "040-squad-and-trait-cleanup.sql",
        "041-store-packs.sql",
        "042-store-pack-player-filters.sql",
        "043-club-player-search.sql",
        "044-manager-league-corrections.sql",
        "045-club-item-id-bands.sql",
        "046-club-virtual-item-ids.sql",
        "047-card-attribute-records.sql",
        "048-player-pick-token-definition.sql",
        "049-store-pack-mechanics.sql",
        "050-remove-nonexistent-cards.sql",
        "051-fut-champions-red-picks.sql",
        "052-squad-battles-competition.sql",
        "053-club-cosmetic-cards.sql",
        "054-club-vanity-slots.sql",
        "055-rivals-skill.sql",
        "056-pro-player-shells.sql",
        "057-market-listing-duration.sql",
        "058-rivals-and-weekend-league.sql",
        "059-competition-reward-packs.sql",
        "060-squad-battles-retail.sql",
        "061-live-totw-rewards.sql",
        "062-panel-action-receipts.sql",
        "063-player-access.sql",
        "064-icon-swaps-token-packs.sql",
        "065-native-club-creation.sql",
        "066-developer-pro-cards.sql",
        "067-pro-cards-five-star.sql",
        "068-phase-pack-odds.sql",
        "069-item-reward-source.sql",
        "070-match-reward-gate.sql",
        "071-manager-league-cards.sql",
        "072-content-changesets.sql",
        "073-content-rules.sql",
        "074-custom-player-heads.sql",
        "075-remove-duplicate-rttf-cards.sql",
        "076-promo-groups.sql",
        "077-developer-cards-promo.sql",
        "078-promo-odds-groups.sql",
        "079-promo-schedules.sql",
        "080-pack-category-chances.sql",
        "081-gold-rare-weight-groups.sql",
        "082-icon-odds-category.sql",
        "083-totw-1-3-rating-groups.sql",
        "084-totw-player-reward-pool.sql",
        "085-name-fixed-totw-reward-pools.sql",
        "086-rename-draft-pool-rule.sql",
        "087-remove-phase-rules.sql",
        "088-rename-red-reward-rule.sql",
        "089-standard-card-weight-groups.sql",
        "090-disable-retail-red-picks.sql",
        "091-promo-events-and-buckets.sql",
        "092-special-pack-bucket-selection.sql",
        "093-pack-and-pick-promo-sources.sql",
        "094-promo-management-and-content-revisions.sql",
        "095-mixed-store-packs.sql",
        "096-contract-free-store.sql",
        "097-remove-testing-packs.sql",
        "098-merge-toty-promos.sql",
        "099-base-icon-rating-buckets.sql",
        "100-initial-icon-rotation.sql",
        "101-align-initial-totw-event.sql",
        "102-developer-cards-untradeable.sql",
        "103-initial-discord-access.sql",
        "104-merge-flashback-tevez.sql",
        "105-store-purchase-limits.sql",
        "106-enable-fifa-points-purchases.sql",
        "107-community-pack-limits.sql",
        "108-match-switches.sql",
        "109-upgrade-sbc-reward-packs.sql",
        "110-repeatable-sbc-and-upgrade-rewards.sql",
        "111-disable-ai-market-listings.sql",
        "112-rating-based-discard-rules.sql",
        "113-pack-odds-v2.sql",
        "114-sbc-rare-requirement-rarity.sql",
        "115-draft-odds-v2.sql",
        "116-tradeable-consumable-stacks.sql",
        "117-sbc-squad-formation.sql",
        "118-sbc-retail-rules.sql",
        "119-packs-and-pack-odds-v3.sql",
        "120-gold-and-base-icon-bucket-balance.sql",
        "121-retail-my-stadium-slots.sql",
        "122-club-vanity-items.sql",
        "123-my-stadium-pack.sql",
        "124-match-clock-opponent-player.sql",
        "125-champions-without-event-overrides.sql",
        "126-mode-reward-tables.sql",
        "127-champions-top25.sql",
        "128-champions-existing-four-top25.sql",
        "129-squad-battles-top25.sql",
        "130-champions-no-band-sizing.sql",
        "131-rivals-reward-tables.sql",
        "132-store-listing.sql",
        "133-squad-battles-reward-tiers.sql",
        "134-merge-players-reward-packs.sql",
        "135-draft-reward-tables.sql",
        "136-sbc-sets.sql",
        "137-my-stadium-item-ids.sql",
        "138-champions-no-minimum-wins.sql",
        "139-fut-stadium-tier-cards.sql",
        "140-vanity-items-pile.sql",
        "141-mode-completion-rewards.sql",
        "142-sbc-live-list.sql",
        "143-objective-live-starts.sql",
        "144-season-level-options.sql",
        "145-match-xp-per-mode.sql",
        "146-no-sbc-pack-xp.sql",
        "147-tokens-store-section.sql",
        "148-token-store-lineup.sql",
        "149-store-tradeability-defaults.sql",
        "150-non-player-drop-table.sql",
        "151-squad-foundations.sql",
        "152-remove-libertadores-flashback.sql",
        "153-remove-coins-per-point.sql",
        "154-squad-foundations-objectives.sql",
        "155-potm-one-sbc.sql",
        "156-gold-pack-mix.sql",
        "157-club-items-on-market.sql",
        "158-token-offer-id-collisions.sql",
        "159-sbc-set-rewards-backpay.sql",
        "160-unassign-duplicate-club-cards.sql",
        "161-kit-category-overrides.sql",
        "162-leaderboard-option-order.sql",
        "163-leaderboard-categories.sql",
        "164-retail-loyalty.sql",
        "165-draft-tactics.sql",
        "166-custom-seasons.sql",
        "167-content-schedule.sql",
        "168-campaign-windows.sql",
        "169-basic-foundations-objectives.sql",
        "170-sbc-daily-and-requirements.sql",
        "171-otw-campaign.sql",
        "172-sbc-daily-completion-counts.sql",
        "173-rulebreakers-campaign.sql",
        "174-totw-four-boundary.sql",
        "175-single-player-draft-tradeable.sql",
        "176-sqbt-refresh-anytime.sql",
        "177-special-price-cap.sql",
        "178-sf-retail-requirements.sql",
        "179-anti-cheat-logs.sql",
        "180-leaver-penalties.sql",
    ];

    internal static async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token,
        Func<string, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? afterFile = null)
    {
        var version = Convert.ToInt32(await RelationalStorageReadiness.ScalarAsync(connection, transaction,
            "SELECT COALESCE(max(version),0) FROM ultimateteam.schema_migrations", token));
        if (version < 0 || version > CurrentVersion)
            throw new InvalidOperationException("Unsupported catalog metadata migration version.");
        // Develop used 125/126 for SBC/stadium before those migrations became 136/137 here.
        // Recognize that lineage by its tables; preserve its content and replay our missing reward migrations.
        if (version is 125 or 126 && (bool)(await RelationalStorageReadiness.ScalarAsync(connection, transaction,
                "SELECT to_regclass('ultimateteam.sbc_sets') IS NOT NULL AND to_regclass('ultimateteam.champions_reward_ranks') IS NULL", token))!)
        {
            await RelationalStorageReadiness.ScalarAsync(connection, transaction,
                "DELETE FROM ultimateteam.schema_migrations WHERE version IN (125,126)", token);
            version = 124;
        }
        // Before this merge the panel branch used 140-145 without develop's vanity stored column.
        // Its migrations are replay-safe: preserve club/content state and replay them under 141-146.
        if (version is >= 140 and <= 145 && !(bool)(await RelationalStorageReadiness.ScalarAsync(connection, transaction,
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='club_vanity_items' AND column_name='stored')", token))!)
        {
            await RelationalStorageReadiness.ScalarAsync(connection, transaction,
                "DELETE FROM ultimateteam.schema_migrations WHERE version>=140", token);
            version = 139;
        }
        // Develop's 141 changed the drop table; this branch's 141 retired Match Pack 544.
        // Develop also used 142/143 for gold mixes/market club items. Their numeric ledger cannot
        // distinguish filenames. Recognize the unretired definition or retained completion rules,
        // replay missing panel migrations atomically, and keep already-applied gold mixes/operator edits.
        var developLineage = version is >= 141 and <= 143 && !(bool)(await RelationalStorageReadiness.ScalarAsync(connection, transaction,
                """
                SELECT coalesce((SELECT admin_only FROM ultimateteam.store_packs WHERE pack_id=544),false)
                    AND NOT EXISTS(SELECT 1 FROM ultimateteam.content_rules
                        WHERE file='seasons' AND path->>0 IN ('matchPack','matchRewards','matchCoinBonuses'))
                """, token))!;
        var keepDevelopGoldMix = developLineage && version >= 142;
        if (developLineage)
        {
            await RelationalStorageReadiness.ScalarAsync(connection, transaction,
                "DELETE FROM ultimateteam.schema_migrations WHERE version>=141", token);
            version = 140;
        }
        for (var index = version; index < Resources.Length; index++)
        {
            if (keepDevelopGoldMix && Resources[index] == "156-gold-pack-mix.sql")
            {
                await RelationalStorageReadiness.ScalarAsync(connection, transaction,
                    "INSERT INTO ultimateteam.schema_migrations(version) VALUES(156)", token);
                continue;
            }
            using var stream = typeof(RelationalCatalogMetadataMigration).Assembly.GetManifestResourceStream(
                "DatabaseSetup/migrations/" + Resources[index]) ?? throw new InvalidOperationException("Missing catalog metadata migration.");
            using var reader = new StreamReader(stream);
            await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(token), connection, transaction) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(token);
            if (afterFile is not null) await afterFile("migrations/" + Resources[index], connection, transaction, token);
        }
    }
}
