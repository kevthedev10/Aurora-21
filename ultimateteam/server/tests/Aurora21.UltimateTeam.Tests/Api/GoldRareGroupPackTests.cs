using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class GoldRareGroupPackTests
{
    [Fact]
    public void OrdinaryGoldRareSlotsUseSavedGroupWeights()
    {
        const string seed = """
            BEGIN;
            SELECT set_config('ultimateteam.content_publish', 'on', true);
            UPDATE ultimateteam.promo_buckets grp
            SET draw_weight = CASE WHEN grp.code='rating_90_99' THEN 100000 ELSE 0.000001 END
            FROM ultimateteam.promos promo
            WHERE promo.promo_id=grp.promo_id AND promo.code='gold_rare';
            COMMIT;
            """;
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: seed,
            time: new TestRuntime.FixedTime(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));
        var rareCards = Enumerable.Range(0, 40).SelectMany(_ => rt.Fut.Packs.RollPlayerPack(304)).ToArray();
        var baseGoldRares = rareCards.Select(id => rt.Game.Players.Find(id)!)
            .Where(card => card.Rareflag == 1).ToArray();
        Assert.True(baseGoldRares.Length >= 35, $"Expected at least 35 base Gold Rares, got {baseGoldRares.Length}");
        Assert.All(baseGoldRares, card => Assert.InRange(card.Rating, 90, 99));
    }
}
