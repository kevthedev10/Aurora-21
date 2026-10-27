using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class StorePurchaseLimitTests
{
    private const string Seed = """
        INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,points_price,size,gold_weight,rare_slots,
            manager_chance,chem_style_chance,purchase_limit)
        VALUES(90,'store',true,'Synthetic limited pack',100,25,1,100,1,0,0,3);
        INSERT INTO ultimateteam.token_store_listings(pack_id,offer_id,price,enabled,untradeable) VALUES(90,1000,25,true,false);
        """;

    private static JsonObject? Tile(TestRuntime rt) => rt.CallObject("GET", "/ut/game/fifa21/store/purchasegroup/all")
        .Arr("purchase")!.Objects().SingleOrDefault(tile => tile.Int("id") == 90);
    private static long Count(TestRuntime rt) => rt.Game.InUnit(work => work.ScalarAsync<long>(
        "SELECT coalesce((SELECT purchases FROM ultimateteam.club_pack_purchases WHERE club_id=@club AND pack_id=90),0)"));
    private static int Buy(TestRuntime rt, string currency = "COINS") => rt.CallRaw("POST", "/ut/game/fifa21/purchased/items",
        new JsonObject { ["packId"] = currency == "POINTS" ? 1000 : 90, ["currency"] = currency }).Status;

    [Fact]
    public void RemainingAllowancePointsPaymentAndOwnedPacksStayConsistent()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: Seed);
        var store = rt.Game.Store();
        store.AddCredits(1000); store.AddFifaPoints(100);
        var credits = store.Credits(); var points = store.FifaPoints();
        Assert.True(rt.Game.Rules.Club.FifaPointsPurchases);
        Assert.Equal(3, Tile(rt)!.Int("quantity"));
        Assert.Equal(-1, Tile(rt)!.Int("globalAvailableQuantity"));
        Assert.Equal(3, Tile(rt)!.Int("purchaseLimit"));
        Assert.Equal("promo", Tile(rt)!.Str("dealType"));
        Assert.Equal("QUANTITY", Tile(rt)!.Str("saleType"));
        Assert.Equal(0, Tile(rt)!.Int("pointsPrice"));
        Assert.Equal(new[] { "coins" }, Tile(rt)!.Arr("currencies")!.Objects().Select(currency => currency.Str("name")));
        var tokenTile = rt.CallObject("GET", "/ut/game/fifa21/store/purchasegroup/all").Arr("purchase")!.Objects().Single(tile => tile.Int("id") == 1000);
        Assert.Equal(25, tokenTile.Int("pointsPrice"));
        for (var index = 1; index <= 3; index++)
        {
            Assert.Equal(200, Buy(rt, index == 2 ? "POINTS" : "COINS"));
            store.DeletePile("unassigned");
            Assert.Equal(index, Count(rt));
            if (index < 3)
            {
                Assert.Equal(3 - index, Tile(rt)!.Int("quantity"));
                Assert.Equal(-1, Tile(rt)!.Int("globalAvailableQuantity"));
                Assert.Equal(index, Tile(rt)!.Int("purchaseCount"));
            }
        }
        Assert.Null(Tile(rt));
        Assert.Equal(credits - 200, store.Credits()); Assert.Equal(points - 25, store.FifaPoints());
        Assert.Equal(400, Buy(rt));
        Assert.Equal(3, Count(rt)); Assert.Equal(credits - 200, store.Credits());
        store.AddUnopenedPack(90, "synthetic-grant");
        Assert.Equal(200, Buy(rt));
        Assert.Equal(3, Count(rt)); Assert.Equal(credits - 200, store.Credits()); Assert.Equal(points - 25, store.FifaPoints());
    }

    [Fact]
    public void UnlimitedPacksDoNotAdvertiseFiniteAvailableStock()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: Seed.Replace(
            "100,25,1,100,1,0,0,3", "100,25,1,100,1,0,0,0"));
        Assert.Equal(0, Tile(rt)!.Int("purchaseLimit"));
        Assert.Equal("", Tile(rt)!.Str("dealType"));
        Assert.Equal("NONE", Tile(rt)!.Str("saleType"));
        Assert.Equal(-1, Tile(rt)!.Int("globalAvailableQuantity"));
        Assert.Equal(0, Tile(rt)!.Int("quantity"));
    }

    [Fact]
    public async Task CommunityStockAllowsOnlyOneOfTwoClubsToBuyTheLastPack()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: Seed + """
            UPDATE ultimateteam.store_packs SET purchase_limit=0,community_limit=1 WHERE pack_id=90;
            """);
        Assert.Equal(0, Tile(rt)!.Int("purchaseLimit"));
        Assert.Equal(1, Tile(rt)!.Int("globalAvailableQuantity"));
        Assert.Equal(1, Tile(rt)!.Int("quantity"));
        using var registry = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 3 }, shared: true);
        using var first = registry.Acquire(PlayerIdentity.Of("555555"));
        using var second = registry.Acquire(PlayerIdentity.Of("666666"));
        foreach (var club in new[] { first, second }) club.Session.Game.Store().AddCredits(1000);
        var firstCredits = first.Session.Game.Store().Credits();
        var secondCredits = second.Session.Game.Store().Credits();
        int Purchase(PlayerLease club) => club.Session.Router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("POST",
            "/ut/game/fifa21/purchased/items", Array.Empty<KeyValuePair<string, string>>(),
            "{\"packId\":90,\"currency\":\"COINS\"}"u8.ToArray())).Status;
        var results = await Task.WhenAll(Task.Run(() => Purchase(first)), Task.Run(() => Purchase(second)));
        Assert.Equal(new[] { 200, 400 }, results.Order().ToArray());
        Assert.Equal(1, rt.Game.InUnit(work => work.ScalarAsync<long>("SELECT purchases FROM ultimateteam.community_pack_purchases WHERE pack_id=90")));
        Assert.Null(Tile(rt));
        var winner = results[0] == 200 ? first : second;
        var loser = results[0] == 400 ? first : second;
        var winnerCredits = results[0] == 200 ? firstCredits : secondCredits;
        var loserCredits = results[0] == 400 ? firstCredits : secondCredits;
        Assert.Equal(winnerCredits - 100, winner.Session.Game.Store().Credits());
        Assert.Equal(loserCredits, loser.Session.Game.Store().Credits());
        Assert.Empty(loser.Session.Game.Store().ListItems("unassigned"));
        winner.Session.Game.Store().DeletePile("unassigned");
        winner.Session.Game.Store().AddUnopenedPack(90, "synthetic-community-grant");
        Assert.Equal(200, Purchase(winner));
        Assert.Equal(winnerCredits - 100, winner.Session.Game.Store().Credits());
        Assert.Equal(1, rt.Game.InUnit(work => work.ScalarAsync<long>("SELECT purchases FROM ultimateteam.community_pack_purchases WHERE pack_id=90")));
    }

    [Fact]
    public void FailedCommunityDrawDoesNotConsumeSharedStockOrCoins()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: Seed + """
            UPDATE ultimateteam.store_packs SET purchase_limit=0,community_limit=5,
                include_general_pool=false,promo_codes=ARRAY['team_of_the_year'],
                category_chances='{"totw":1}',promo_only=true WHERE pack_id=90;
            """);
        rt.Game.Store().AddCredits(1000);
        var credits = rt.Game.Store().Credits();
        Assert.Equal(500, Buy(rt));
        Assert.Equal(credits, rt.Game.Store().Credits());
        Assert.Equal(0, rt.Game.InUnit(work => work.ScalarAsync<long>("SELECT coalesce((SELECT purchases FROM ultimateteam.community_pack_purchases WHERE pack_id=90),0)")));
        Assert.Equal(5, Tile(rt)!.Int("globalAvailableQuantity"));
    }

    [Fact]
    public void FailedGenerationRollsBackPaymentAndAllowance()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: Seed + """
            UPDATE ultimateteam.store_packs SET include_general_pool=false,promo_codes=ARRAY['team_of_the_year'],
                category_chances='{"totw":1}',promo_only=true WHERE pack_id=90;
            """);
        rt.Game.Store().AddCredits(1000);
        var credits = rt.Game.Store().Credits();
        Assert.Equal(500, Buy(rt));
        Assert.Equal(credits, rt.Game.Store().Credits());
        Assert.Equal(0, Count(rt));
        Assert.Empty(rt.Game.Store().ListItems("unassigned"));
        Assert.Equal(3, Tile(rt)!.Int("quantity"));
    }

    [Fact]
    public void ReopeningAClubRetainsUsageAndAnotherClubHasItsOwnAllowance()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: Seed);
        var identity = PlayerIdentity.Of("333333");
        using (var registry = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 2 }, shared: true))
        using (var club = registry.Acquire(identity))
        {
            club.Session.Game.Store().AddCredits(1000);
            Assert.Equal(200, club.Session.Router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("POST",
                "/ut/game/fifa21/purchased/items", Array.Empty<KeyValuePair<string, string>>(),
                "{\"packId\":90,\"currency\":\"COINS\"}"u8.ToArray())).Status);
            club.Session.Game.Store().DeletePile("unassigned");
        }
        using var reopened = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 3 }, shared: true);
        using var same = reopened.Acquire(identity);
        using var other = reopened.Acquire(PlayerIdentity.Of("444444"));
        long Purchases(PlayerLease club) => club.Session.Game.InUnit(work => work.ScalarAsync<long>(
            "SELECT coalesce((SELECT purchases FROM ultimateteam.club_pack_purchases WHERE club_id=@club AND pack_id=90),0)"));
        Assert.Equal(1, Purchases(same));
        Assert.Equal(0, Purchases(other));
    }


}
