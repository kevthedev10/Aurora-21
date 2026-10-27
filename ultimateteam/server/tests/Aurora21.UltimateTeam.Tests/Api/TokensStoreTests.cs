using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class TokensStoreTests
{
    private const string G = "/ut/game/fifa21";

    private static TestRuntime Create(bool automaticArt = false)
    {
        var runtime = TestRuntime.Create(marketTarget: 0, seedSql: $$"""
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,points_price,size,
                gold_weight,silver_weight,bronze_weight,rare_slots,store_group,store_section)
            VALUES (90,'store',true,'Synthetic Tokens Pack',500,25,1,100,0,0,0,{{(automaticArt ? "NULL" : "'gold'")}},'standard');
            INSERT INTO ultimateteam.token_store_listings(pack_id,offer_id,price,enabled,untradeable) VALUES(90,1000,25,true,true);
            """);
        runtime.Game.Rules.Club.FifaPointsPurchases = true;
        return runtime;
    }

    [Fact]
    public void TokensPlacementKeepsGoldArtworkAndRejectsCoinPurchases()
    {
        using var runtime = Create();
        var tile = runtime.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects().Single(p => p["id"].L() == 1000);
        Assert.Equal("kits", tile["displayGroup"]!["value"]!.GetValue<string>());
        Assert.Equal(3, tile["assetId"].L());
        Assert.Equal(new[] { "points" }, tile["currencies"].Objects().Select(c => c["name"]!.GetValue<string>()));
        var before = (runtime.Game.Store().Credits(), runtime.Game.Store().FifaPoints());
        Assert.Equal(400, runtime.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 1000, ["currency"] = "COINS" }).Status);
        Assert.Equal(before, (runtime.Game.Store().Credits(), runtime.Game.Store().FifaPoints()));
        runtime.Game.Store().AddUnopenedPack(90, "synthetic-grant");
        runtime.Game.Store().AddFifaPoints(25);
        var opened = runtime.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 1000, ["currency"] = "POINTS" });
        Assert.Equal("PURCHASECOMPLETE", opened["state"]!.GetValue<string>());
        Assert.Equal(before.Item1, runtime.Game.Store().Credits());
        Assert.Equal(before.Item2, runtime.Game.Store().FifaPoints());
        Assert.True(Assert.Single(runtime.Game.Store().ListItems("unassigned")).Bool("untradeable"));
        Assert.Equal(1, runtime.CallObject("GET", $"{G}/store/purchasegroup/mypacks")["purchase"].Objects().Single(p => p["id"].L() == 90)["quantity"].L());
    }

    [Fact]
    public void AutomaticArtworkMatchesTheOriginalPackInBothStores()
    {
        using var runtime = Create(automaticArt: true);
        var tiles = runtime.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects().ToArray();
        Assert.Equal(tiles.Single(tile => tile["id"].L() == 90)["assetId"].L(), tiles.Single(tile => tile["id"].L() == 1000)["assetId"].L());
    }

    [Fact]
    public void DisablingTokenPurchasesHidesTokensPacksAndRefusesPayment()
    {
        using var runtime = Create();
        runtime.Game.Rules.Club.FifaPointsPurchases = false;
        Assert.DoesNotContain(runtime.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects(), p => p["id"].L() == 1000);
        Assert.Equal(400, runtime.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 1000, ["currency"] = "POINTS" }).Status);
    }

    [Fact]
    public void InsufficientTokensDoNotChargeCoinsOrCreateItems()
    {
        using var runtime = Create();
        runtime.Game.Store().AddFifaPoints(-runtime.Game.Store().FifaPoints());
        var credits = runtime.Game.Store().Credits();
        var items = runtime.Game.Store().ListItems("unassigned").Count;
        var result = runtime.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 1000, ["currency"] = "POINTS" });
        Assert.Equal("INSUFFICIENTFUNDS", result["state"]!.GetValue<string>());
        Assert.Equal(1000, result["packId"].L());
        Assert.Equal(credits, runtime.Game.Store().Credits());
        Assert.Equal(0, runtime.Game.Store().FifaPoints());
        Assert.Equal(items, runtime.Game.Store().ListItems("unassigned").Count);
    }

    [Fact]
    public void TheSamePackHasACoinOfferAndATokenOfferWithSeparateTradeability()
    {
        using var runtime = Create();
        var tiles = runtime.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects().ToArray();
        var regular = tiles.Single(p => p["id"].L() == 90);
        Assert.Equal(new[] { "coins" }, regular["currencies"].Objects().Select(c => c["name"]!.GetValue<string>()));
        Assert.Equal(500, regular["currencies"].Objects().Single()["funds"].L());
        var before = (runtime.Game.Store().Credits(), runtime.Game.Store().FifaPoints());
        Assert.Equal(400, runtime.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 90, ["currency"] = "POINTS" }).Status);
        Assert.Equal(before, (runtime.Game.Store().Credits(), runtime.Game.Store().FifaPoints()));
        var opened = runtime.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 90, ["currency"] = "COINS" });
        Assert.Equal("PURCHASECOMPLETE", opened["state"]!.GetValue<string>());
        Assert.Equal(before.Item1 - 500, runtime.Game.Store().Credits());
        Assert.Equal(before.Item2, runtime.Game.Store().FifaPoints());
    }

    [Fact]
    public void CoinListingsDoNotFollowTheRetiredGlobalStoreRule()
    {
        using var runtime = Create();
        runtime.Game.Rules.Tradeability.Sources["store"] = true;
        var opened = runtime.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 90, ["currency"] = "COINS" });
        Assert.Equal("PURCHASECOMPLETE", opened["state"]!.GetValue<string>());
        Assert.False(Assert.Single(runtime.Game.Store().ListItems("unassigned")).Bool("untradeable"));
    }

    [Fact]
    public void StoreLocalisationIncludesTokensCategory()
    {
        using var runtime = Create();
        var response = runtime.Router.Handle(new FutRequest("GET", "/fut/packs/loc/storepackdescriptions.en_us.xml",
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        Assert.Equal(200, response.Status);
        Assert.Contains("resname=\"FUT_STORE_CAT_KITS_NAME\"><source>Tokens</source>", Encoding.UTF8.GetString(response.Body));
    }
}
