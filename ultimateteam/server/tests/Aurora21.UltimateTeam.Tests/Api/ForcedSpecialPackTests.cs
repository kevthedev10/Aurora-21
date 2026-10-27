using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class ForcedSpecialPackTests
{
    private const string Seed = """
        BEGIN;
        SELECT set_config('ultimateteam.content_publish','on',true);
        INSERT INTO ultimateteam.promo_events(code,name,enabled,starts_at,ends_at)
        VALUES ('forced_toty_test','Forced TOTY test',true,'2026-09-30 00:00:00+00','2026-10-01 00:00:00+00');
        INSERT INTO ultimateteam.promo_event_promos(event_id,promo_id)
        SELECT event.event_id,promo.promo_id FROM ultimateteam.promo_events event,ultimateteam.promos promo
        WHERE event.code='forced_toty_test' AND promo.code='team_of_the_year';
        INSERT INTO ultimateteam.promo_buckets(promo_id,code,name,draw_weight)
        SELECT promo_id,'spotlight','Spotlight',100000 FROM ultimateteam.promos WHERE code='team_of_the_year';
        WITH selected AS (
            SELECT bucket.id AS bucket_id,min(member.id) AS member_id
            FROM ultimateteam.promo_buckets bucket JOIN ultimateteam.promo_cards member USING(promo_id)
            WHERE bucket.code='spotlight' GROUP BY bucket.id
        )
        UPDATE ultimateteam.promo_cards member SET bucket_id=selected.bucket_id
        FROM selected WHERE member.id=selected.member_id;
        INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,rare_slots,promo_only,category_chances)
        VALUES(90,'store',true,'Synthetic forced TOTY',1000,2,100,2,true,'{"totw":0,"icon":0,"toty":1}');
        COMMIT;
        """;

    // Inside the initial TOTW 1-3 and icon windows, which open 2026-09-30 19:00 Amsterdam (17:00 UTC, migrations 100-101).
    private static long Start => new DateTimeOffset(2026,9,30,18,0,0,TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void ForcedSlotsUseCategoryThenSavedPromoBuckets()
    {
        using var rt = TestRuntime.Create(marketTarget:0,seedSql:Seed,time:new TestRuntime.FixedTime(Start));
        var catalog = rt.Game.Live.Current.Catalog;
        var promo = catalog.Promos.Single(promo => promo.Code == "team_of_the_year");
        var bucket = catalog.PromoBuckets.Single(bucket => bucket.PromoId == promo.PromoId && bucket.Code == "spotlight");
        var member = catalog.PromoCards.Single(member => member.BucketId == bucket.Id);
        var spotlight = catalog.DefinitionsById[member.PlayerCardDefinitionId].NativeResourceId!.Value.Value;
        var config = new JsonObject
        {
            ["size"]=1,["rare_slots"]=1,["gold_weight"]=100,
            ["special_weights_exclusive"]=true,["special_weights"]=new JsonObject(),
            ["guaranteed_specials"]=1,["category_chances"]=new JsonObject { ["totw"]=0.25,["toty"]=0.75,["icon"]=0 },
        };
        var cards = Enumerable.Range(0,400).Select(_ => rt.Fut.Packs.PreviewPlayerPack(config).Objects().Single().Int("resourceId")).ToArray();
        var toty = cards.Where(id => rt.Game.Live.Current.PromoCardSelector.Contains("toty",id)).ToArray();
        Assert.InRange(toty.Length,260,340);
        Assert.InRange(toty.Count(id => id == spotlight),toty.Length-2,toty.Length);
        Assert.All(cards,id => Assert.True(rt.Game.Live.Current.PromoCardSelector.Contains("toty",id)
            || rt.Game.Live.Current.PromoCardSelector.Contains("totw",id)));

        config["category_chances"]=new JsonObject { ["totw"]=0,["toty"]=1,["icon"]=0 };
        config["allowed_rareflags"]=new JsonArray(1);
        Assert.Throws<InvalidOperationException>(() => rt.Fut.Packs.PreviewPlayerPack(config));
    }

    [Fact]
    public void ExpiredPromoOnlyPackRefusesToFillWithStandardCardsAndRollsBackPayment()
    {
        var clock = new TestRuntime.MovableTime(Start);
        using var rt = TestRuntime.Create(marketTarget:0,seedSql:Seed,time:clock);
        rt.Game.Store().AddCredits(10000);
        var opened = rt.CallRaw("POST","/ut/game/fifa21/purchased/items",new JsonObject { ["packId"]=90,["currency"]="COINS" });
        Assert.Equal(200,opened.Status);
        var items = rt.Game.Store().ListItems("unassigned");
        Assert.Equal(2,items.Count);
        Assert.Equal(2,items.Select(item => item.Int("assetId")).Distinct().Count());
        Assert.All(items,item => Assert.True(rt.Game.Live.Current.PromoCardSelector.Contains("toty",item.Int("resourceId"))));
        rt.Game.Store().DeletePile("unassigned");
        clock.Now = Start + 86400;
        var credits = rt.Game.Store().Credits();
        var refused = rt.CallRaw("POST","/ut/game/fifa21/purchased/items",new JsonObject { ["packId"]=90,["currency"]="COINS" });
        Assert.Equal(500,refused.Status);
        Assert.Equal(credits,rt.Game.Store().Credits());
        Assert.Empty(rt.Game.Store().ListItems("unassigned"));
    }

    [Fact]
    public void ExplicitZeroCategoriesCannotBeBypassedByAPromoOnlyPack()
    {
        using var rt = TestRuntime.Create(marketTarget:0,time:new TestRuntime.FixedTime(Start));
        var config = new JsonObject
        {
            ["size"]=1,["rare_slots"]=1,["gold_weight"]=100,["promo_only"]=true,
            ["category_chances"]=new JsonObject { ["totw"]=0,["icon"]=0 },
        };
        Assert.Throws<InvalidOperationException>(() => rt.Fut.Packs.PreviewPlayerPack(config));
    }
}
