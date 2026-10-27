using Aurora21.UltimateTeam.Tests.Api;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class ScheduledCategoryPackTests
{
    [Fact]
    public async Task ConfiguredCategoryDrawsOnlyWhileItsPromoEventIsActive()
    {
        const string seed = """
            BEGIN;
            SELECT set_config('ultimateteam.content_publish', 'on', true);
            INSERT INTO ultimateteam.promo_events(code, name, enabled, starts_at, ends_at)
            VALUES ('test_toty', 'Test TOTY', true, '2026-09-30 00:00:00+00', '2026-10-01 00:00:00+00');
            INSERT INTO ultimateteam.promo_event_promos(event_id, promo_id)
            SELECT schedule.event_id, promo.promo_id
            FROM ultimateteam.promo_events schedule, ultimateteam.promos promo
            WHERE schedule.code='test_toty' AND promo.code='team_of_the_year';
            UPDATE ultimateteam.store_packs
            SET category_chances='{"totw":0,"icon":0,"toty":0.9}'::jsonb
            WHERE pack_id=400;
            COMMIT;
            """;
        var clock = new TestRuntime.MovableTime(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        using var rt = TestRuntime.Create(time: clock, marketTarget: 0, seedSql: seed);
        var totyIds = new HashSet<long>();
        await using (var source = NpgsqlDataSource.Create(rt.ConnectionString))
        await using (var command = source.CreateCommand("""
            SELECT definition.native_resource_id
            FROM ultimateteam.promo_cards card
            JOIN ultimateteam.promos promo USING(promo_id)
            JOIN ultimateteam.item_definitions definition ON definition.definition_id=card.player_card_definition_id
            WHERE promo.code='team_of_the_year'
            """))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) totyIds.Add(reader.GetInt64(0));
        Assert.NotEmpty(totyIds);

        var activeCards = Enumerable.Range(0, 4).SelectMany(_ => rt.Fut.Packs.RollPlayerPack(400)).ToArray();
        Assert.Contains(activeCards, totyIds.Contains);

        clock.Now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var expiredCards = Enumerable.Range(0, 4).SelectMany(_ => rt.Fut.Packs.RollPlayerPack(400)).ToArray();
        Assert.DoesNotContain(expiredCards, totyIds.Contains);
    }
}
