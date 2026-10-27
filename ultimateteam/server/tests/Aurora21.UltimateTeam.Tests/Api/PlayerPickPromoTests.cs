using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;
using Npgsql;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class PlayerPickPromoTests
{
    private const string Seed = """
        INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,rare_slots,
            include_general_pool,promo_codes,category_chances,player_picks)
        VALUES(90,'store',true,'Synthetic TOTW 5 pick',100,1,100,0,false,ARRAY['totw_5'],
            '{"totw":1,"icon":0}','[{"count":1,"choice_count":3,"min_rating":80,"promo_only":true}]');
        """;

    private static long October20 => new DateTimeOffset(2026,10,20,12,0,0,TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>After the initial TOTW 1-3 event (migration 101: until 2027-01-01 19:00 Amsterdam), when no TOTW is scheduled.</summary>
    private static long AfterInitialTotw => new DateTimeOffset(2027,1,10,12,0,0,TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public async Task ExplicitPromoOddsSurvivePurchaseAndOnlyDrawThatPromo()
    {
        using var rt = TestRuntime.Create(marketTarget: 0,
            seedSql: Seed.Replace("{\"totw\":1,\"icon\":0}", "{\"totw_5\":1}"), time: new TestRuntime.FixedTime(October20));
        rt.Game.Store().AddCredits(1000);
        Assert.Equal(200, rt.CallRaw("POST", "/ut/game/fifa21/purchased/items", new JsonObject { ["packId"] = 90, ["currency"] = "COINS" }).Status);
        var token = rt.Game.Store().ListItems("unassigned").Single();
        var choices = await rt.Fut.Picks.RedeemAsync(token.Int("id"));
        var catalog = rt.Game.Catalog;
        var promo = catalog.Promos.Single(p => p.Code == "totw_5");
        var members = catalog.PromoCards.Where(p => p.PromoId == promo.PromoId)
            .Select(p => catalog.DefinitionsById[p.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet();
        Assert.Equal(3, choices.Count);
        Assert.All(choices.Objects(), choice => Assert.Contains(choice.Int("resourceId"), members));
    }

    [Fact]
    public async Task NewTokensKeepTheirSourcesAndRevealedChoicesSurvivePackEdits()
    {
        using var rt = TestRuntime.Create(marketTarget:0,seedSql:Seed,time:new TestRuntime.FixedTime(October20));
        rt.Game.Store().AddCredits(1000);
        Assert.Equal(200, rt.CallRaw("POST","/ut/game/fifa21/purchased/items",new JsonObject { ["packId"]=90,["currency"]="COINS" }).Status);
        var token = rt.Game.Store().ListItems("unassigned").Single();
        var tokenId = token.Int("id");
        var saved = rt.Game.InUnit(work => work.GetRelationalItemAsync(new NativeItemId(tokenId)))!.Pick!;
        Assert.Contains("totw_5", saved.DrawConfig);
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','on',true)");
            return await work.ExecuteAsync("UPDATE ultimateteam.store_packs SET promo_codes=ARRAY['team_of_the_year'],category_chances='{\"toty\":1,\"totw\":0,\"icon\":0}' WHERE pack_id=90");
        });
        await using (var source = NpgsqlDataSource.Create(rt.ConnectionString))
            rt.Game.Packs.Publish((await StorePackCatalog.LoadAsync(source)).Rules);
        var choices = await rt.Fut.Picks.RedeemAsync(tokenId);
        var catalog = rt.Game.Live.Current.Catalog;
        var promo = catalog.Promos.Single(promo => promo.Code == "totw_5");
        var members = catalog.PromoCards.Where(member => member.PromoId == promo.PromoId)
            .Select(member => catalog.DefinitionsById[member.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet();
        Assert.Equal(3, choices.Count);
        Assert.All(choices.Objects(), choice => Assert.Contains(choice.Int("resourceId"), members));
        Assert.Equal(3, choices.Objects().Select(choice => choice.Int("assetId")).Distinct().Count());
        Assert.True(JsonNode.DeepEquals(choices, await rt.Fut.Picks.RedeemAsync(tokenId)));
        var granted = await rt.Fut.Picks.SelectAsync(tokenId, JsonValue.Create(1));
        Assert.Equal(choices[1]!.AsObject().Int("id"), granted.Int("id"));
        Assert.Equal(choices[1]!.AsObject().Int("resourceId"), granted.Int("resourceId"));
        Assert.Null(rt.Game.Store().GetItem(tokenId));
    }

    [Fact]
    public async Task AnUnrevealedEventOnlyPickCannotFallBackAfterTheEventEnds()
    {
        var clock = new TestRuntime.MovableTime(new DateTimeOffset(2026,10,1,12,0,0,TimeSpan.Zero).ToUnixTimeSeconds());
        using var rt = TestRuntime.Create(marketTarget:0,seedSql:Seed + "UPDATE ultimateteam.store_packs SET include_general_pool=true,promo_codes=NULL WHERE pack_id=90;",time:clock);
        rt.Game.Store().AddCredits(1000);
        Assert.Equal(200, rt.CallRaw("POST","/ut/game/fifa21/purchased/items",new JsonObject { ["packId"]=90,["currency"]="COINS" }).Status);
        var tokenId = rt.Game.Store().ListItems("unassigned").Single().Int("id");
        clock.Now = AfterInitialTotw;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Fut.Picks.RedeemAsync(tokenId));
        Assert.NotNull(rt.Game.Store().GetItem(tokenId));
        Assert.Empty(rt.Game.InUnit(work => work.GetPlayerPickAsync(tokenId))!.Choices);
    }

    [Fact]
    public void PickPreviewsUseCategoryOddsAndEditedBucketsOutsideEvents()
    {
        const string bucketSeed = """
            BEGIN;
            SELECT set_config('ultimateteam.content_publish','on',true);
            INSERT INTO ultimateteam.promo_buckets(promo_id,code,name,draw_weight)
            SELECT promo_id,'spotlight','Spotlight',100000 FROM ultimateteam.promos WHERE code='team_of_the_year';
            WITH selected AS (
                SELECT bucket.id AS bucket_id,min(member.id) AS member_id
                FROM ultimateteam.promo_buckets bucket JOIN ultimateteam.promo_cards member USING(promo_id)
                WHERE bucket.code='spotlight' GROUP BY bucket.id
            ) UPDATE ultimateteam.promo_cards member SET bucket_id=selected.bucket_id
            FROM selected WHERE member.id=selected.member_id;
            COMMIT;
            """;
        using var rt = TestRuntime.Create(marketTarget:0,seedSql:bucketSeed,time:new TestRuntime.FixedTime(October20));
        var config = new JsonObject
        {
            ["size"]=1,["include_general_pool"]=false,["promo_codes"]=new JsonArray("totw_5","team_of_the_year"),
            ["category_chances"]=new JsonObject { ["totw"]=0.25,["toty"]=0.75,["icon"]=0 },
            ["player_picks"]=new JsonArray(new JsonObject { ["count"]=1,["choice_count"]=3,["min_rating"]=80,["promo_only"]=true }),
        };
        var catalog = rt.Game.Live.Current.Catalog;
        var promo = catalog.Promos.Single(promo => promo.Code == "team_of_the_year");
        var spotlight = catalog.PromoBuckets.Single(bucket => bucket.PromoId == promo.PromoId && bucket.Code == "spotlight");
        var member = catalog.PromoCards.Single(member => member.BucketId == spotlight.Id);
        var resource = catalog.DefinitionsById[member.PlayerCardDefinitionId].NativeResourceId!.Value.Value;
        var first = Enumerable.Range(0,200).Select(_ => rt.Fut.Packs.PreviewPlayerPack(config).Objects().First().Int("resourceId")).ToArray();
        var toty = first.Where(id => rt.Game.Live.Current.PromoCardSelector.Contains("toty",id)).ToArray();
        Assert.InRange(toty.Length,110,190);
        Assert.InRange(toty.Count(id => id == resource),toty.Length-2,toty.Length);
        config["category_chances"] = new JsonObject { ["totw"]=0,["toty"]=0,["icon"]=0 };
        Assert.Throws<InvalidOperationException>(() => rt.Fut.Packs.PreviewPlayerPack(config));
        config["promo_codes"] = new JsonArray("fut_champions");
        Assert.Throws<InvalidOperationException>(() => rt.Fut.Packs.PreviewPlayerPack(config));
    }

    [Fact]
    public void OrdinaryPlayerPacksUseTheSameExplicitPromoSources()
    {
        var activeEventTime = new DateTimeOffset(2026,9,30,12,0,0,TimeSpan.Zero).ToUnixTimeSeconds();
        using var rt = TestRuntime.Create(marketTarget:0,time:new TestRuntime.FixedTime(activeEventTime));
        var config = new JsonObject { ["size"]=2,["rare_slots"]=2,["gold_weight"]=100,
            ["promo_only"]=true,["include_general_pool"]=false,["promo_codes"]=new JsonArray("totw_5"),
            ["category_chances"]=new JsonObject { ["totw"]=1,["icon"]=0 } };
        var catalog = rt.Game.Live.Current.Catalog;
        var promoId = catalog.Promos.Single(promo => promo.Code == "totw_5").PromoId;
        var members = catalog.PromoCards.Where(member => member.PromoId == promoId)
            .Select(member => catalog.DefinitionsById[member.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet();
        Assert.All(rt.Fut.Packs.PreviewPlayerPack(config).Objects(), card => Assert.Contains(card.Int("resourceId"),members));
    }

    [Fact]
    public void GeneralOnlyGeneralPlusNamedAndNamedOnlySourcesStayDistinct()
    {
        // Inside the initial TOTW 1-3 and icon windows, which open 2026-09-30 19:00 Amsterdam (17:00 UTC, migrations 100-101).
        var activeEventTime = new DateTimeOffset(2026,9,30,18,0,0,TimeSpan.Zero);
        using var rt = TestRuntime.Create(marketTarget:0,time:new TestRuntime.FixedTime(activeEventTime.ToUnixTimeSeconds()));
        var catalog = rt.Game.Live.Current.Catalog;
        long Member(string code)
        {
            var promo = catalog.Promos.Single(promo => promo.Code == code);
            var card = catalog.PromoCards.First(card => card.PromoId == promo.PromoId);
            return catalog.DefinitionsById[card.PlayerCardDefinitionId].NativeResourceId!.Value.Value;
        }
        var standard = Member("gold_rare");
        var scheduled = Member("totw_1");
        var specific = Member("totw_5");
        var config = new JsonObject { ["include_general_pool"]=true,["promo_codes"]=new JsonArray() };
        var general = rt.Game.CaptureCardDrawAt(activeEventTime,config).General;
        Assert.True(general(standard)); Assert.True(general(scheduled)); Assert.False(general(specific));
        config["promo_codes"] = new JsonArray("totw_5");
        var combined = rt.Game.CaptureCardDrawAt(activeEventTime,config).General;
        Assert.True(combined(standard)); Assert.True(combined(scheduled)); Assert.True(combined(specific));
        config["include_general_pool"] = false;
        var named = rt.Game.CaptureCardDrawAt(activeEventTime,config).General;
        Assert.False(named(standard)); Assert.False(named(scheduled)); Assert.True(named(specific));
    }
}
