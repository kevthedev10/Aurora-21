using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Ordered catalog reads, one query per table. Caller supplies a consistent read transaction.</summary>
public static class CatalogRepository
{
    public static async ValueTask<RelationalCatalogSnapshot> LoadCatalogSnapshotAsync(this IUnitOfWork work)
    {
        var footballers = await work.QueryAsync(
            "SELECT footballer_id, native_footballer_id, first_name, last_name, common_name FROM ultimateteam.footballers ORDER BY footballer_id",
            r => new FootballerRecord(
                new FootballerId(r.GetInt64(0)),
                r.IsDBNull(1) ? null : r.GetInt64(1),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        var nations = await work.QueryAsync(
            "SELECT nation_id, native_nation_id, name FROM ultimateteam.nations ORDER BY nation_id",
            r => new NationRecord(
                r.GetInt64(0),
                r.GetInt64(1),
                r.GetString(2)));
        var leagues = await work.QueryAsync(
            "SELECT league_id, native_league_id, name FROM ultimateteam.leagues ORDER BY league_id",
            r => new LeagueRecord(
                r.GetInt64(0),
                r.GetInt64(1),
                r.GetString(2)));
        var teams = await work.QueryAsync(
            "SELECT team_id, native_team_id, name FROM ultimateteam.teams ORDER BY team_id",
            r => new TeamRecord(
                r.GetInt64(0),
                r.GetInt64(1),
                r.GetString(2)));
        var families = await work.QueryAsync(
            "SELECT family_id, code, name FROM ultimateteam.item_families ORDER BY family_id",
            r => new ItemFamilyRecord(
                r.GetInt16(0),
                r.GetString(1),
                r.GetString(2)));
        var definitions = await work.QueryAsync(
            "SELECT definition_id, family_id, game_year, native_resource_id, native_asset_id, native_card_db_id, native_card_asset_id, native_item_type, native_subtype, catalog_order, revision, status, display_name FROM ultimateteam.item_definitions ORDER BY catalog_order, definition_id",
            r => new ItemDefinitionRecord(
                new DefinitionId(r.GetInt64(0)),
                r.GetInt16(1),
                r.GetInt32(2),
                r.IsDBNull(3) ? null : new NativeResourceId(r.GetInt64(3)),
                r.IsDBNull(4) ? null : r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt64(8),
                r.GetInt64(9),
                r.GetInt64(10),
                r.GetString(11),
                r.IsDBNull(12) ? null : r.GetString(12)));
        var cardtypes = await work.QueryAsync(
            "SELECT t.card_type_id, t.name, t.card_level, r.native_rareflag, t.is_special FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r USING(native_rarity_id) ORDER BY t.card_type_id",
            r => new CardTypeRecord(
                r.GetInt64(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.GetBoolean(4)));
        var promos = await work.QueryAsync(
            "SELECT promo_id, code, name, starts_at, ends_at, status, enabled, odds_category, draw_weight FROM ultimateteam.promos ORDER BY promo_id",
            r => new PromoRecord(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.IsDBNull(4) ? null : r.GetDateTime(4),
                r.GetString(5),
                r.GetBoolean(6),
                r.GetString(7),
                r.GetDecimal(8)));
        var promocards = await work.QueryAsync(
            "SELECT c.id, c.promo_id, c.player_card_definition_id, c.bucket_id, c.draw_reduction FROM ultimateteam.promo_cards c JOIN ultimateteam.promo_buckets b ON b.id=c.bucket_id WHERE b.enabled ORDER BY c.id",
            r => new PromoCardRecord(
                r.GetInt64(0),
                r.GetInt64(1),
                new DefinitionId(r.GetInt64(2)),
                r.GetInt64(3),
                r.GetDecimal(4)));
        var promoBuckets = await work.QueryAsync(
            "SELECT id, promo_id, code, name, draw_weight, enabled FROM ultimateteam.promo_buckets ORDER BY id",
            r => new PromoBucketRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetBoolean(5)));
        var iconRotations = await work.QueryAsync(
            "SELECT id, code, name, promo_id, enabled, starts_at, ends_at FROM ultimateteam.icon_rotations ORDER BY id",
            r => new IconRotationRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetBoolean(4), r.GetDateTime(5), r.GetDateTime(6)));
        var databaseRevision = await work.ScalarAsync<long>("SELECT coalesce(max(revision),0) FROM ultimateteam.content_table_revisions");
        var promoEvents = await work.QueryAsync(
            "SELECT event_id, code, name, enabled, starts_at, ends_at FROM ultimateteam.promo_events ORDER BY event_id",
            r => new PromoEventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetDateTime(4), r.GetDateTime(5)));
        var promoEventPromos = await work.QueryAsync(
            "SELECT event_id, promo_id, enabled FROM ultimateteam.promo_event_promos ORDER BY event_id, promo_id",
            r => new PromoEventPromoRecord(r.GetInt64(0), r.GetInt64(1), r.GetBoolean(2)));
        var raritygroups = await work.QueryAsync(
            "SELECT id, card_type_id, native_group_id FROM ultimateteam.card_type_rarity_group_memberships ORDER BY id",
            r => new CardRarityGroupRecord(
                r.GetInt64(0),
                r.GetInt64(1),
                r.GetInt64(2)));
        var players = await work.QueryAsync(
            "SELECT p.definition_id, p.footballer_id, p.card_type_id, p.rating, p.position, p.team_id, p.league_id, p.nation_id, t.native_team_id, l.native_league_id, n.native_nation_id, p.preferred_foot, p.weak_foot, p.skill_moves, p.work_rate_attack, p.work_rate_defense, COALESCE(trait_masks.trait1,0), COALESCE(trait_masks.trait2,0), NULL::bigint AS native_version, p.face_slot_0, p.face_slot_1, p.face_slot_2, p.face_slot_3, p.face_slot_4, p.face_slot_5, p.acceleration, p.sprint_speed, p.positioning, p.finishing, p.shot_power, p.long_shots, p.volleys, p.penalties, p.vision, p.crossing, p.free_kick_accuracy, p.short_passing, p.long_passing, p.curve, p.agility, p.balance, p.reactions, p.ball_control, p.dribbling, p.composure, p.interceptions, p.heading_accuracy, p.marking, p.standing_tackle, p.sliding_tackle, p.jumping, p.stamina, p.strength, p.aggression, p.gk_diving, p.gk_handling, p.gk_kicking, p.gk_reflexes, p.gk_speed, p.gk_positioning, p.head_resource_id, p.custom_head_id FROM ultimateteam.player_card_definitions p LEFT JOIN (SELECT links.definition_id, bit_or(CASE WHEN trait.mask_word=1 THEN trait.bit_value ELSE 0 END) AS trait1, bit_or(CASE WHEN trait.mask_word=2 THEN trait.bit_value ELSE 0 END) AS trait2 FROM ultimateteam.player_card_traits links JOIN ultimateteam.traits trait ON trait.trait_id=links.trait_id GROUP BY links.definition_id) trait_masks ON trait_masks.definition_id=p.definition_id LEFT JOIN ultimateteam.teams t ON t.team_id=p.team_id LEFT JOIN ultimateteam.leagues l ON l.league_id=p.league_id LEFT JOIN ultimateteam.nations n ON n.nation_id=p.nation_id ORDER BY p.definition_id",
            r => new PlayerCardDefinitionRecord(
                new DefinitionId(r.GetInt64(0)),
                new FootballerId(r.GetInt64(1)),
                r.GetInt64(2),
                r.GetInt64(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetInt64(10),
                r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetInt64(12),
                r.IsDBNull(13) ? null : r.GetInt64(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15),
                r.IsDBNull(16) ? null : r.GetInt64(16),
                r.IsDBNull(17) ? null : r.GetInt64(17),
                r.IsDBNull(18) ? null : r.GetInt64(18),
                r.IsDBNull(19) ? null : r.GetInt64(19),
                r.IsDBNull(20) ? null : r.GetInt64(20),
                r.IsDBNull(21) ? null : r.GetInt64(21),
                r.IsDBNull(22) ? null : r.GetInt64(22),
                r.IsDBNull(23) ? null : r.GetInt64(23),
                r.IsDBNull(24) ? null : r.GetInt64(24),
                r.IsDBNull(25) ? null : r.GetInt64(25),
                r.IsDBNull(26) ? null : r.GetInt64(26),
                r.IsDBNull(27) ? null : r.GetInt64(27),
                r.IsDBNull(28) ? null : r.GetInt64(28),
                r.IsDBNull(29) ? null : r.GetInt64(29),
                r.IsDBNull(30) ? null : r.GetInt64(30),
                r.IsDBNull(31) ? null : r.GetInt64(31),
                r.IsDBNull(32) ? null : r.GetInt64(32),
                r.IsDBNull(33) ? null : r.GetInt64(33),
                r.IsDBNull(34) ? null : r.GetInt64(34),
                r.IsDBNull(35) ? null : r.GetInt64(35),
                r.IsDBNull(36) ? null : r.GetInt64(36),
                r.IsDBNull(37) ? null : r.GetInt64(37),
                r.IsDBNull(38) ? null : r.GetInt64(38),
                r.IsDBNull(39) ? null : r.GetInt64(39),
                r.IsDBNull(40) ? null : r.GetInt64(40),
                r.IsDBNull(41) ? null : r.GetInt64(41),
                r.IsDBNull(42) ? null : r.GetInt64(42),
                r.IsDBNull(43) ? null : r.GetInt64(43),
                r.IsDBNull(44) ? null : r.GetInt64(44),
                r.IsDBNull(45) ? null : r.GetInt64(45),
                r.IsDBNull(46) ? null : r.GetInt64(46),
                r.IsDBNull(47) ? null : r.GetInt64(47),
                r.IsDBNull(48) ? null : r.GetInt64(48),
                r.IsDBNull(49) ? null : r.GetInt64(49),
                r.IsDBNull(50) ? null : r.GetInt64(50),
                r.IsDBNull(51) ? null : r.GetInt64(51),
                r.IsDBNull(52) ? null : r.GetInt64(52),
                r.IsDBNull(53) ? null : r.GetInt64(53),
                r.IsDBNull(54) ? null : r.GetInt64(54),
                r.IsDBNull(55) ? null : r.GetInt64(55),
                r.IsDBNull(56) ? null : r.GetInt64(56),
                r.IsDBNull(57) ? null : r.GetInt64(57),
                r.IsDBNull(58) ? null : r.GetInt64(58),
                r.IsDBNull(59) ? null : r.GetInt64(59),
                r.IsDBNull(60) ? null : r.GetInt64(60),
                r.IsDBNull(61) ? null : r.GetInt64(61)));
        var managers = await work.QueryAsync(
            "SELECT m.definition_id, m.first_name, m.last_name, m.rating, r.native_rareflag, COALESCE(l.native_league_id,0), n.native_nation_id, m.league_id, m.nation_id, m.negotiation FROM ultimateteam.manager_definitions m JOIN ultimateteam.native_rarities r ON r.native_rarity_id=m.rarity_id LEFT JOIN ultimateteam.leagues l USING(league_id) LEFT JOIN ultimateteam.nations n USING(nation_id) ORDER BY m.definition_id",
            r => new ManagerDefinitionRecord(
                new DefinitionId(r.GetInt64(0)),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetDecimal(3),
                r.IsDBNull(4) ? null : r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetInt64(9)));
        var consumables = await work.QueryAsync(
            "SELECT definition_id, consumable_kind, rating, native_consumable_type, native_effect_id, source_position, target_position, goalkeeper_only FROM ultimateteam.consumable_definitions ORDER BY definition_id",
            r => new ConsumableDefinitionRecord(
                new DefinitionId(r.GetInt64(0)),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetBoolean(7)));
        var customisations = await work.QueryAsync(
            "SELECT definition_id, customisation_kind, name, native_team_id, team_id, native_kit_type, native_category, rating, native_rareflag, pack_eligible FROM ultimateteam.customisation_definitions ORDER BY definition_id",
            r => new CustomisationDefinitionRecord(
                new DefinitionId(r.GetInt64(0)),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetInt64(8),
                r.GetBoolean(9)));
        var extensions = await work.QueryAsync(
            "SELECT definition_id, schema_revision, payload FROM ultimateteam.catalog_compatibility_metadata ORDER BY definition_id",
            r => new DefinitionExtensionRecord(
                new DefinitionId(r.GetInt64(0)),
                r.GetInt64(1),
                new CatalogJson(r.GetString(2))));
        return new RelationalCatalogSnapshot(
            footballers, nations, leagues, teams, families, definitions, cardtypes, promos, promocards,
            promoBuckets, promoEvents, promoEventPromos, raritygroups, players, managers, consumables,
            customisations, extensions, iconRotations, databaseRevision);
    }
}
