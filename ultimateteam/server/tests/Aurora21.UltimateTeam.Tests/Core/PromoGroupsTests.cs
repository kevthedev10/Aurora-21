using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Tests.Api;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Migrations 076-077: historical and manual promo assignments, plus the grant-only Developer Cards group.
/// </summary>
public sealed class PromoGroupsTests
{
    private const string Special = "(r.native_rareflag > 1 OR t.is_special)";

    /// <summary>The 21 Squad Foundations cards (rarity 87), which migration 151 moved out of Objective Players.</summary>
    private static readonly long[] SquadFoundations =
    [
        50500862, 50508641, 50517070, 50520590, 50532126, 50534311, 50539540, 50542746, 50543835, 50545639, 50553771,
        50554529, 50555289, 50556142, 50557810, 50563512, 50568641, 50571529, 50577752, 67342042, 84123366,
    ];
    private const string CardTypes = """
        JOIN ultimateteam.card_types t ON t.card_type_id = pc.card_type_id
        JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id
        """;

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> MembershipHashAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT md5(string_agg(p.code || ':' || d.native_resource_id, ',' ORDER BY p.code, d.native_resource_id))
            FROM ultimateteam.promo_cards m JOIN ultimateteam.promos p USING(promo_id)
            JOIN ultimateteam.item_definitions d ON d.definition_id=m.player_card_definition_id
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task OnlyExplicitlyExcludedSpecialCardsHaveNoGroupAndEveryGroupStartsOn()
    {
        using var rt = TestRuntime.Create();
        // Main's six current Pro cards remain in the catalogue. The three retired cards are the only Pro removals.
        Assert.Equal(new long[] { 167961228, 167983460, 167984358, 167988548, 167996618, 168019987 },
            await ResourceIdsAsync(rt, """
                SELECT d.native_resource_id FROM ultimateteam.player_card_definitions pc
                JOIN ultimateteam.item_definitions d ON d.definition_id=pc.definition_id
                WHERE d.native_resource_id IN (167961228,167983460,167984358,167988548,167996618,168019987)
                ORDER BY d.native_resource_id
                """));
        var excluded = CsvRows("fifa21_promo_groups.csv").Where(row => row.Group == "-")
            .Select(row => row.Resource)
            .Except([167963031L, 168006538L, 167968024L, 167988548L, 167996618L, 167983460L])
            .Order().ToArray();
        Assert.Equal(11, excluded.Length);
        Assert.Equal(excluded, await ResourceIdsAsync(rt, $"""
            SELECT d.native_resource_id FROM ultimateteam.player_card_definitions pc
            JOIN ultimateteam.item_definitions d ON d.definition_id=pc.definition_id {CardTypes}
            WHERE {Special} AND NOT EXISTS (SELECT 1 FROM ultimateteam.promo_cards m WHERE m.player_card_definition_id = pc.definition_id)
            ORDER BY d.native_resource_id
            """));
        // Ordinary cards belong to the always-available standard pools. Migration 104 moved both
        // Libertadores Flashback Tevez versions into one Flashbacks card, leaving their old group empty.
        Assert.Equal(0, await ScalarAsync(rt, $"""
            SELECT count(*) FROM ultimateteam.promo_cards m JOIN ultimateteam.promos p USING(promo_id)
            JOIN ultimateteam.player_card_definitions pc ON pc.definition_id = m.player_card_definition_id
            {CardTypes} WHERE NOT {Special} AND p.code <> 'gold_rare' AND p.category <> 'standard_pool'
            """));
        // Migration 152 removed Libertadores Flashback, which migration 104 had left empty; every other promo has cards.
        Assert.Equal(0, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE code='libertadores_flashback'"));
        Assert.Equal(0, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos p WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.promo_cards m WHERE m.promo_id = p.promo_id)"));
        Assert.Equal(0, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE NOT enabled OR starts_at IS NOT NULL OR ends_at IS NOT NULL"));
        Assert.Equal(0, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE category='unsorted'"));
    }

    [Fact]
    public async Task HistoricalAndManualGroupsHoldTheirAssignedCards()
    {
        using var rt = TestRuntime.Create();
        var historical = CsvRows("fifa21_promo_group_cards.csv");
        var manual = CsvRows("fifa21_promo_groups.csv").Where(row => row.Group != "-").ToList();
        Assert.Equal(107, historical.Select(row => row.Group).Distinct().Count());
        Assert.Equal(109, historical.Concat(manual).Select(row => row.Group).Distinct().Count());
        // Migration 151 added Squad Foundations, split from Objective Players; migration 152 removed the empty Libertadores Flashback.
        Assert.Equal(108, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE category IN ('historical_squad','event_item_group','icon_tier') AND code <> 'gold_rare'"));
        string MergedCode(string code) => code switch { "toty_12th_man" => "team_of_the_year", "toty_honourable_mentions_pack_squad" or "toty_honourable_mentions_objective_sbc" => "toty_honourable_mentions", _ => code };
        // Migration 104 removed duplicate Tevez 50474649 and moved survivor 67251865 to Flashbacks; migration 151 moved the
        // Squad Foundations cards out of Objective Players.
        var assignments = historical.Concat(manual).Where(row => row.Resource != 50474649)
            .Select(row => (Group: row.Resource == 67251865 ? "flashbacks"
                : row.Group == "objective_players" && SquadFoundations.Contains(row.Resource) ? "squad_foundations" : MergedCode(row.Group), row.Resource));
        foreach (var group in assignments.GroupBy(row => row.Group))
        {
            var expected = await ResourceIdsAsync(rt, $"""
                SELECT d.native_resource_id FROM ultimateteam.item_definitions d
                WHERE d.family_id = 1 AND d.native_resource_id IN ({string.Join(",", group.Select(row => row.Resource).Distinct())})
                ORDER BY d.native_resource_id
                """);
            var actual = await ResourceIdsAsync(rt, $"""
                SELECT d.native_resource_id FROM ultimateteam.promo_cards m
                JOIN ultimateteam.promos p ON p.promo_id = m.promo_id
                JOIN ultimateteam.item_definitions d ON d.definition_id=m.player_card_definition_id
                WHERE p.code = '{group.Key}' ORDER BY d.native_resource_id
                """);
            Assert.True(expected.SequenceEqual(actual),
                $"{group.Key}: missing [{string.Join(",", expected.Except(actual))}], unexpected [{string.Join(",", actual.Except(expected))}]");
        }
        // Migration 050 removed three historical cards; migration 104 merged the duplicate Tevez.
        Assert.Equal(4, historical.Count + manual.Count - await ScalarAsync(rt, $"""
            SELECT count(*) FROM ultimateteam.promo_cards m JOIN ultimateteam.promos p ON p.promo_id = m.promo_id
            WHERE p.category IN ('historical_squad','event_item_group','icon_tier') AND p.code NOT IN ('developer_cards','gold_rare')
            """));
    }

    [Fact]
    public async Task BaseIconBucketsKeepRatingTotalsAndMarkDesirablePlayers()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        const string membersSql = """
        SELECT d.native_resource_id FROM ultimateteam.promo_cards c
        JOIN ultimateteam.promos p USING(promo_id)
        JOIN ultimateteam.item_definitions d ON d.definition_id=c.player_card_definition_id
        WHERE p.code='base_icons' ORDER BY d.native_resource_id
        """;
        Assert.NotEmpty(await ResourceIdsAsync(rt, membersSql));
        Assert.Equal(7, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_buckets b JOIN ultimateteam.promos p USING(promo_id)
            JOIN (VALUES('rating_85',35::numeric),('rating_86',28),('rating_87',20),
                ('rating_88',10),('rating_89',5),('rating_90',1.5),('rating_91',0.5)) expected(code,weight)
                ON b.code=expected.code AND b.draw_weight=expected.weight
            WHERE p.code='base_icons' AND b.enabled
            """));
        Assert.Equal(7, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promo_buckets b JOIN ultimateteam.promos p USING(promo_id) WHERE p.code='base_icons' AND b.enabled"));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_cards c JOIN ultimateteam.promos p USING(promo_id)
            JOIN ultimateteam.promo_buckets b ON b.id=c.bucket_id
            JOIN ultimateteam.player_card_definitions pc ON pc.definition_id=c.player_card_definition_id
            JOIN ultimateteam.item_definitions d ON d.definition_id=pc.definition_id
            WHERE p.code='base_icons' AND (NOT b.enabled OR b.code <> 'rating_' || pc.rating)
            """));
        Assert.Equal(2, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_cards c JOIN ultimateteam.promos p USING(promo_id)
            JOIN ultimateteam.promo_buckets b ON b.id=c.bucket_id
            JOIN ultimateteam.player_card_definitions pc ON pc.definition_id=c.player_card_definition_id
            JOIN ultimateteam.item_definitions d ON d.definition_id=pc.definition_id
            WHERE p.code='base_icons' AND d.native_resource_id IN (17015642,17015649)
              AND pc.rating=86 AND b.code='rating_86' AND c.draw_reduction=5
            """));
        Assert.Equal(3, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_buckets b JOIN ultimateteam.promos p USING(promo_id)
            WHERE p.code IN ('mid_icons','prime_icons','prime_icon_moments') AND b.code='default' AND b.enabled AND b.draw_weight=1
            """));
    }

    [Fact]
    public async Task GoldRareGroupsKeepTheirRatingRanges()
    {
        using var rt = TestRuntime.Create();
        Assert.Equal(1, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE code='gold_rare' AND odds_category='gold_rare'"));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.player_card_definitions card
            LEFT JOIN ultimateteam.promo_cards member ON member.player_card_definition_id=card.definition_id
            LEFT JOIN ultimateteam.promos promo ON promo.promo_id=member.promo_id AND promo.code='gold_rare'
            WHERE card.card_type_id=5 AND promo.promo_id IS NULL
            """));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_cards member
            JOIN ultimateteam.promos promo ON promo.promo_id=member.promo_id
            JOIN ultimateteam.promo_buckets grp ON grp.id=member.bucket_id
            JOIN ultimateteam.player_card_definitions card ON card.definition_id=member.player_card_definition_id
            WHERE promo.code='gold_rare' AND (NOT grp.enabled OR grp.code <> CASE
                WHEN card.rating BETWEEN 75 AND 79 THEN 'rating_75_79'
                WHEN card.rating BETWEEN 80 AND 81 THEN 'rating_80_81'
                WHEN card.rating BETWEEN 82 AND 83 THEN 'rating_82_83'
                WHEN card.rating BETWEEN 84 AND 85 THEN 'rating_84_85'
                WHEN card.rating BETWEEN 86 AND 87 THEN 'rating_86_87'
                WHEN card.rating BETWEEN 88 AND 89 THEN 'rating_88_89'
                WHEN card.rating BETWEEN 90 AND 99 THEN 'rating_90_99' END)
            """));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_buckets grp
            JOIN ultimateteam.promos promo USING(promo_id)
            WHERE promo.code IN ('totw_1','totw_2','totw_3')
              AND grp.draw_weight <> CASE grp.code
                WHEN 'rating_0_64' THEN 1 WHEN 'rating_65_74' THEN 1
                WHEN 'rating_75_79' THEN 15 WHEN 'rating_80_81' THEN 27
                WHEN 'rating_82_83' THEN 30 WHEN 'rating_84_85' THEN 15
                WHEN 'rating_86_87' THEN 9 WHEN 'rating_88_89' THEN 3
                WHEN 'rating_90_99' THEN 1 END
            """));
    }

    [Fact]
    public async Task StandardCardsKeepTheirPoolsAndNonGoldCommonsKeepUniformGroups()
    {
        using var rt = TestRuntime.Create();
        Assert.Equal(5, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE category='standard_pool'"));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promos promo
            JOIN ultimateteam.promo_buckets grp USING(promo_id)
            WHERE promo.category='standard_pool' AND promo.code<>'gold_common'
              AND (promo.odds_category <> promo.code OR promo.draw_weight <> 1
                   OR grp.code <> 'default' OR grp.draw_weight <> 1)
            """));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.player_card_definitions card
            LEFT JOIN ultimateteam.promo_cards member ON member.player_card_definition_id=card.definition_id
            LEFT JOIN ultimateteam.promos promo ON promo.promo_id=member.promo_id
                AND promo.code=CASE card.card_type_id
                    WHEN 1 THEN 'bronze_common' WHEN 2 THEN 'gold_common'
                    WHEN 3 THEN 'silver_common' WHEN 4 THEN 'bronze_rare'
                    WHEN 6 THEN 'silver_rare' END
            WHERE card.card_type_id IN (1,2,3,4,6) AND promo.promo_id IS NULL
            """));
    }

    [Fact]
    public async Task EachCompetitionGroupHoldsEveryCardOfItsCardTypes()
    {
        using var rt = TestRuntime.Create();
        foreach (var (code, flags) in new[] { ("ucl", "47,48"), ("sudamericana", "52"), ("libertadores", "53"), ("fut_champions", "18") })
        {
            var cards = await ScalarAsync(rt, $"SELECT count(*) FROM ultimateteam.player_card_definitions pc {CardTypes} WHERE r.native_rareflag IN ({flags})");
            var members = await ScalarAsync(rt, $"SELECT count(*) FROM ultimateteam.promo_cards m JOIN ultimateteam.promos p ON p.promo_id = m.promo_id WHERE p.code = '{code}'");
            Assert.True(cards > 0 && cards == members, $"{code}: {members} members of {cards} cards");
        }
    }

    [Fact]
    public async Task SquadFoundationsIsItsOwnCardTypeAndPromoWithObjectivePlayersSettings()
    {
        using var rt = TestRuntime.Create();
        // Every rarity 87 card is a Squad Foundations card, named so, in its own promo and no longer in Objective Players.
        Assert.Equal(SquadFoundations.Order(), await ResourceIdsAsync(rt, $"""
            SELECT d.native_resource_id FROM ultimateteam.player_card_definitions pc
            JOIN ultimateteam.item_definitions d ON d.definition_id=pc.definition_id {CardTypes}
            WHERE r.native_rareflag = 87 AND t.name = 'Squad Foundations' AND r.name = 'Squad Foundations' ORDER BY d.native_resource_id
            """));
        Assert.Equal(SquadFoundations.Order(), await ResourceIdsAsync(rt, """
            SELECT d.native_resource_id FROM ultimateteam.promo_cards m
            JOIN ultimateteam.promos p ON p.promo_id=m.promo_id JOIN ultimateteam.promo_buckets b ON b.id=m.bucket_id AND b.code='default'
            JOIN ultimateteam.item_definitions d ON d.definition_id=m.player_card_definition_id
            WHERE p.code='squad_foundations' ORDER BY d.native_resource_id
            """));
        Assert.Equal(0, await ScalarAsync(rt, $"""
            SELECT count(*) FROM ultimateteam.promo_cards m JOIN ultimateteam.promos p ON p.promo_id=m.promo_id
            JOIN ultimateteam.item_definitions d ON d.definition_id=m.player_card_definition_id
            WHERE p.code <> 'squad_foundations' AND d.native_resource_id IN ({string.Join(",", SquadFoundations)})
            """));
        // Objective Players keeps its other objective cards; rarity 98 keeps its name.
        Assert.True(await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promo_cards m JOIN ultimateteam.promos p USING(promo_id) WHERE p.code='objective_players'") > 0);
        Assert.Equal(1, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r ON r.native_rarity_id=t.native_rarity_id WHERE r.native_rareflag=98 AND t.name='Objective Player'"));
        Assert.Equal(1, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promos n JOIN ultimateteam.promos o ON o.code='objective_players'
            WHERE n.code='squad_foundations' AND n.name='Squad Foundations' AND n.status=o.status AND n.enabled=o.enabled
              AND n.category=o.category AND n.odds_category=o.odds_category AND n.draw_weight=o.draw_weight
              AND n.starts_at IS NOT DISTINCT FROM o.starts_at AND n.ends_at IS NOT DISTINCT FROM o.ends_at
            """));
        // The catalogue reports the new name, as the panel and the store show it.
        Assert.Equal("Squad Foundations", rt.Game.Players.Find(50520590)!.Text("rarityName"));
    }

    [Fact]
    public async Task DeveloperCardsGroupContainsEveryProCard()
    {
        using var rt = TestRuntime.Create();
        var proCards = await ResourceIdsAsync(rt, """
            SELECT d.native_resource_id FROM ultimateteam.player_card_definitions pc
            JOIN ultimateteam.item_definitions d ON d.definition_id=pc.definition_id
            JOIN ultimateteam.card_types t ON t.card_type_id=pc.card_type_id
            JOIN ultimateteam.native_rarities r ON r.native_rarity_id=t.native_rarity_id
            WHERE r.native_rareflag=10 ORDER BY d.native_resource_id
            """);
        var grouped = await ResourceIdsAsync(rt, """
            SELECT d.native_resource_id FROM ultimateteam.promo_cards m
            JOIN ultimateteam.promos p ON p.promo_id=m.promo_id
            JOIN ultimateteam.item_definitions d ON d.definition_id=m.player_card_definition_id
            WHERE p.code='developer_cards' ORDER BY d.native_resource_id
            """);
        Assert.Equal(6, proCards.Length);
        Assert.Equal(proCards, grouped);
    }

    [Fact]
    public async Task PromosWithoutSeededRatingBucketsStartWithOneWeightedBucket()
    {
        using var rt = TestRuntime.Create();
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM (
                SELECT promo.promo_id FROM ultimateteam.promos promo
                LEFT JOIN ultimateteam.promo_buckets grp ON grp.promo_id=promo.promo_id
                WHERE promo.code NOT IN ('gold_common','gold_rare','totw_1','totw_2','totw_3','base_icons')
                GROUP BY promo.promo_id HAVING count(grp.id) <> 1
            ) invalid
            """));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promos promo
            JOIN ultimateteam.promo_buckets grp ON grp.promo_id=promo.promo_id
            WHERE promo.code NOT IN ('gold_common','gold_rare','totw_1','totw_2','totw_3','base_icons')
              AND (promo.draw_weight <> 1 OR grp.draw_weight <> 1 OR grp.code <> 'default')
            """));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_cards card
            JOIN ultimateteam.promo_buckets grp ON grp.id=card.bucket_id
            JOIN ultimateteam.promos promo ON promo.promo_id=card.promo_id
            WHERE promo.code NOT IN ('gold_common','gold_rare','totw_1','totw_2','totw_3','base_icons')
              AND (card.promo_id <> grp.promo_id OR grp.code <> 'default')
            """));
        Assert.Equal(36, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE odds_category='totw'"));
        Assert.Equal(1, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE code='base_icons' AND odds_category='icon'"));
        Assert.Equal(1, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promos WHERE odds_category='toty'"));
    }

    [Fact]
    public async Task InitialTotwPromosHaveOnlyPopulatedRatingGroups()
    {
        using var rt = TestRuntime.Create();
        Assert.Equal(3, await ScalarAsync(rt, """
            SELECT count(DISTINCT promo.promo_id)
            FROM ultimateteam.promos promo
            JOIN ultimateteam.promo_buckets grp USING(promo_id)
            WHERE promo.code IN ('totw_1','totw_2','totw_3') AND grp.code LIKE 'rating_%'
            """));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_buckets grp
            JOIN ultimateteam.promos promo USING(promo_id)
            WHERE promo.code IN ('totw_1','totw_2','totw_3')
              AND (grp.code='default' OR NOT EXISTS (
                SELECT 1 FROM ultimateteam.promo_cards member WHERE member.bucket_id=grp.id))
            """));
        Assert.Equal(0, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_cards member
            JOIN ultimateteam.promos promo ON promo.promo_id=member.promo_id
            JOIN ultimateteam.promo_buckets grp ON grp.id=member.bucket_id
            JOIN ultimateteam.player_card_definitions card ON card.definition_id=member.player_card_definition_id
            WHERE promo.code IN ('totw_1','totw_2','totw_3')
              AND grp.code <> CASE
                WHEN card.rating BETWEEN 0 AND 64 THEN 'rating_0_64'
                WHEN card.rating BETWEEN 65 AND 74 THEN 'rating_65_74'
                WHEN card.rating BETWEEN 75 AND 79 THEN 'rating_75_79'
                WHEN card.rating BETWEEN 80 AND 81 THEN 'rating_80_81'
                WHEN card.rating BETWEEN 82 AND 83 THEN 'rating_82_83'
                WHEN card.rating BETWEEN 84 AND 85 THEN 'rating_84_85'
                WHEN card.rating BETWEEN 86 AND 87 THEN 'rating_86_87'
                WHEN card.rating BETWEEN 88 AND 89 THEN 'rating_88_89'
                WHEN card.rating BETWEEN 90 AND 99 THEN 'rating_90_99' END
            """));
    }

    [Fact]
    public async Task APromoCanSplitItsCardsIntoWeightedSets()
    {
        using var rt = TestRuntime.Create();
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            INSERT INTO ultimateteam.promo_buckets(promo_id, code, name, draw_weight)
            SELECT promo_id, 'rare_singleton', 'Rare singleton', 0.1 FROM ultimateteam.promos WHERE code='totw_1';
            SELECT set_config('ultimateteam.content_publish', 'on', true);
            UPDATE ultimateteam.promo_cards card SET bucket_id = grp.id
            FROM ultimateteam.promo_buckets grp
            WHERE grp.promo_id=card.promo_id AND grp.code='rare_singleton'
              AND card.id=(SELECT min(member.id) FROM ultimateteam.promo_cards member WHERE member.promo_id=grp.promo_id);
            """, connection, transaction))
            await command.ExecuteNonQueryAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT count(*) FROM ultimateteam.promo_cards card
            JOIN ultimateteam.promo_buckets grp ON grp.id=card.bucket_id
            JOIN ultimateteam.promos promo ON promo.promo_id=card.promo_id
            WHERE promo.code='totw_1' AND grp.code='rare_singleton' AND grp.draw_weight=0.1
            """, connection, transaction))
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InitialScheduleHasOnlyTotwOneToThreeUntilTheTotwFourBoundary()
    {
        using var rt = TestRuntime.Create();
        Assert.Equal(1, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_events
            WHERE code='initial_totw_1_3' AND enabled
              AND starts_at='2026-09-30 17:00:00+00'::timestamptz
              AND ends_at='2026-10-07 18:00:00+00'::timestamptz
            """));
        Assert.Equal(3, await ScalarAsync(rt, """
            SELECT count(*) FROM ultimateteam.promo_event_promos link
            JOIN ultimateteam.promo_events schedule USING(event_id)
            JOIN ultimateteam.promos promo USING(promo_id)
            WHERE schedule.code='initial_totw_1_3' AND link.enabled
              AND promo.code IN ('totw_1', 'totw_2', 'totw_3')
            """));
        Assert.Equal(3, await ScalarAsync(rt, "SELECT count(*) FROM ultimateteam.promo_event_promos"));
    }

    [Fact]
    public async Task InstalledScheduleAvailabilityReloadsFromDatabase()
    {
        using var rt = TestRuntime.Create();
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        static async Task<DefinitionId> CardInAsync(NpgsqlDataSource source, string code)
        {
            await using var command = source.CreateCommand("""
                SELECT card.player_card_definition_id FROM ultimateteam.promo_cards card
                JOIN ultimateteam.promos promo USING(promo_id)
                WHERE promo.code=@code ORDER BY card.id LIMIT 1
                """);
            command.Parameters.AddWithValue("code", code);
            return new DefinitionId(Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        var icon = await CardInAsync(source, "base_icons");
        var midIcon = await CardInAsync(source, "mid_icons");
        var totwOne = await CardInAsync(source, "totw_1");
        var totwFour = await CardInAsync(source, "totw_4");
        var start = new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero);
        var during = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2027, 1, 1, 18, 0, 0, TimeSpan.Zero);
        var totwEnd = new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
        var beforeReload = rt.Game.Live.Snapshot.PromoAvailability;
        Assert.True(beforeReload.IsAvailable(icon, during));
        Assert.False(beforeReload.IsAvailable(icon, end));
        Assert.True(beforeReload.IsAvailable(midIcon, end));
        Assert.False(beforeReload.IsAvailable(totwOne, start.AddTicks(-1)));
        Assert.True(beforeReload.IsAvailable(totwOne, start));
        Assert.True(beforeReload.IsAvailable(totwOne, during));
        Assert.True(beforeReload.IsAvailable(totwOne, totwEnd.AddTicks(-1)));
        Assert.False(beforeReload.IsAvailable(totwOne, totwEnd));
        Assert.False(beforeReload.IsAvailable(totwFour, during));

        await using var connection = await source.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand("""
            SELECT set_config('ultimateteam.content_publish', 'on', true);
            UPDATE ultimateteam.promo_events SET enabled=false WHERE code='initial_totw_1_3';
            """, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        Assert.True(beforeReload.IsAvailable(totwOne, during));
        await rt.Game.ReloadContentAsync();
        Assert.False(rt.Game.Live.Snapshot.PromoAvailability.IsAvailable(totwOne, during));
        Assert.True(rt.Game.Live.Snapshot.PromoAvailability.IsAvailable(icon, during));
    }

    private static List<(string Group, long Resource)> CsvRows(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "tools", name));
        var header = lines[0].TrimStart('﻿').Split(',');
        var group = Array.IndexOf(header, "group_key");
        var resource = Array.IndexOf(header, "resource_id");
        // The key and resource columns come before any quoted field, so a plain split reads them.
        return lines.Skip(1).Where(line => line.Length > 0)
            .Select(line => line.Split(',')).Select(parts => (parts[group], long.Parse(parts[resource], System.Globalization.CultureInfo.InvariantCulture))).ToList();
    }

    private static async Task<long[]> ResourceIdsAsync(TestRuntime rt, string sql)
    {
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var command = source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools", "fifa21_promo_group_cards.csv"))) return directory.FullName;
        throw new InvalidOperationException("Could not find tools/fifa21_promo_group_cards.csv above the test output.");
    }

    private static async Task<long> ScalarAsync(TestRuntime rt, string sql)
    {
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
