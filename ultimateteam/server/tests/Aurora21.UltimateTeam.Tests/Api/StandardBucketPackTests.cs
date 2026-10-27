using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Helpers;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class StandardBucketPackTests
{
    [Fact]
    public void EveryRemainingStandardCategoryUsesItsSavedBucketWeights()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: """
            BEGIN;
            SELECT set_config('ultimateteam.content_publish','on',true);
            -- Normalize competing weights so imported defaults do not change this fixture.
            -- The explicit seeded stream makes the weighted draws reproducible.
            UPDATE ultimateteam.promo_buckets bucket SET draw_weight=1
            FROM ultimateteam.promos promo WHERE promo.promo_id=bucket.promo_id AND promo.category='standard_pool';
            INSERT INTO ultimateteam.promo_buckets(promo_id,code,name,draw_weight)
            SELECT promo_id,'spotlight','Spotlight',100000 FROM ultimateteam.promos
            WHERE category='standard_pool';
            WITH selected AS (
                SELECT DISTINCT ON (bucket.id) bucket.id AS bucket_id,member.id AS member_id
                FROM ultimateteam.promo_buckets bucket
                JOIN ultimateteam.promo_cards member USING(promo_id)
                JOIN ultimateteam.player_card_definitions card ON card.definition_id=member.player_card_definition_id
                WHERE bucket.code='spotlight' ORDER BY bucket.id,card.rating DESC,member.id
            )
            UPDATE ultimateteam.promo_cards member SET bucket_id=selected.bucket_id
            FROM selected WHERE member.id=selected.member_id;
            COMMIT;
            """);
        foreach (var category in new[] { "gold_common", "silver_common", "silver_rare", "bronze_common", "bronze_rare" })
        {
            var catalog = rt.Game.Live.Current.Catalog;
            var promo = catalog.Promos.Single(promo => promo.Code == category);
            var bucket = catalog.PromoBuckets.Single(bucket => bucket.PromoId == promo.PromoId && bucket.Code == "spotlight");
            var member = catalog.PromoCards.Single(member => member.BucketId == bucket.Id);
            var expected = catalog.DefinitionsById[member.PlayerCardDefinitionId].NativeResourceId!.Value.Value;
            var tier = category.Split('_')[0];
            var config = new JsonObject
            {
                ["size"] = 1, ["rare_slots"] = category.EndsWith("_rare") ? 1 : 0,
                ["category_chances"] = new JsonObject { ["icon"] = 0 },
                ["gold_weight"] = tier == "gold" ? 100 : 0,
                ["silver_weight"] = tier == "silver" ? 100 : 0,
                ["bronze_weight"] = tier == "bronze" ? 100 : 0,
            };
            var random = GameRandom.Seeded("standard-bucket-weights", category);
            var hits = Enumerable.Range(0, 20).Count(_ => rt.Fut.Packs.PreviewPlayerPack(config, random).Objects().Single().Int("resourceId") == expected);
            Assert.Equal(20, hits);
        }
    }
}
