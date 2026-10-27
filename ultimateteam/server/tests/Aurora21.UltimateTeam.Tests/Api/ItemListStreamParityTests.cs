using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Store;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The market list and club search routes write their item arrays straight into the response instead of
/// copying every item into a node tree. That is only safe while the bytes are the ones the document
/// form produces, so each case compares the streamed route body with the rendered document on the
/// same club and clock — including the paths that still return a built document.
/// </summary>
public sealed class ItemListStreamParityTests
{
    private const string G = "/ut/game/fifa21";
    private const long Now = 1_700_000_000;

    [Theory]
    [InlineData("")]
    [InlineData("start=0&num=5")]
    [InlineData("start=500&num=5")]
    public async Task TheStreamedMarketSearchMatchesTheRenderedDocument(string query)
    {
        using var rt = TestRuntime.Create(marketTarget: 0, time: new TestRuntime.FixedTime(Now));
        var resourceId = rt.Game.Players.Ids.First();
        var player = rt.Game.Players.Find(resourceId)!;
        for (var index = 0; index < 12; index++)
        {
            var preview = rt.Fut.Items.BuildPlayer(resourceId, 800_000_000_000 + index, "market");
            var tradeId = rt.Game.Store().InsertMarketListing(MarketListingKind.Ai, "Stream parity", null, resourceId, preview,
                player.Rating, 150, 1_000, Now, Now + 3_600 + index);
            // Watched and unwatched auctions render different flags and bid states.
            if (index % 2 == 0)
            {
                rt.Game.Store().WatchMarketListing(tradeId);
            }
        }

        var response = await Get(rt, "/transfermarket", query);

        Assert.Equal(200, response.Status);
        var rendered = FutJson.Serialize(await rt.Fut.Market.SearchAsync(QueryString.Parse(query)));
        Assert.Equal(rendered, System.Text.Encoding.UTF8.GetString(response.Body));
        if (query == "")
        {
            // A passing empty page would prove nothing about the item writer.
            Assert.Equal(12, JsonNode.Parse(response.Body)!["auctionInfo"]!.AsArray().Count);
        }
    }

    [Theory]
    // The maintained default page, a filtered page on the joined SQL, an out-of-range page, and a
    // non-player search that still returns a built document.
    [InlineData("type=player&start=0&count=16", true)]
    [InlineData("type=player&start=0&count=16&level=gold", false)]
    [InlineData("type=player&start=5000&count=16", false)]
    [InlineData("type=manager&start=0&count=16", false)]
    public async Task TheStreamedClubSearchMatchesTheRenderedDocument(string query, bool requireItems)
    {
        using var rt = TestRuntime.Create(marketTarget: 0, time: new TestRuntime.FixedTime(Now));

        var response = await Get(rt, "/club", query);

        Assert.Equal(200, response.Status);
        var rendered = await rt.Game.ReadAsync(
            async work => FutJson.Serialize(await rt.Fut.Search.SearchAsync(work, QueryString.Parse(query), new JsonObject())),
            CancellationToken.None);
        Assert.Equal(rendered, System.Text.Encoding.UTF8.GetString(response.Body));
        if (requireItems)
        {
            Assert.NotEmpty(JsonNode.Parse(response.Body)!["itemData"]!.AsArray());
        }
    }

    [Fact]
    public async Task TheStreamedMarketListsMatchTheRenderedDocuments()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, time: new TestRuntime.FixedTime(Now));
        var resourceId = rt.Game.Players.Ids.First();
        var player = rt.Game.Players.Find(resourceId)!;
        var aiTrades = new List<long>();
        for (var index = 0; index < 6; index++)
        {
            var preview = rt.Fut.Items.BuildPlayer(resourceId, 800_000_000_000 + index, "market");
            var tradeId = rt.Game.Store().InsertMarketListing(MarketListingKind.Ai, "Stream parity", null, resourceId, preview,
                player.Rating, 150, 1_000, Now, Now + 3_600 + index);
            aiTrades.Add(tradeId);
            if (index % 2 == 0)
            {
                rt.Game.Store().WatchMarketListing(tradeId);
            }
        }
        // The transfer list holds this club's own listings and an unlisted trade-pile card, which render
        // through different auction arguments.
        async Task<long> AddPlayer(int index, string pile) => await rt.Game.RunAsync("parity-player", async work =>
        {
            var item = rt.Fut.Items.BuildPlayer(rt.Game.Players.Entries.ElementAt(index).Key, await work.NextItemIdAsync(), pile);
            item["untradeable"] = false;
            await work.SaveItemAsync(item);
            return item.IntKeep("id");
        }, CancellationToken.None);
        foreach (var index in new[] { 1, 2 })
        {
            rt.CallObject("POST", G + "/auctionhouse", new JsonObject
            {
                ["itemId"] = await AddPlayer(index, "club"), ["startingBid"] = 150, ["buyNowPrice"] = 1000, ["duration"] = 3600,
            });
        }
        _ = await AddPlayer(3, "trade");

        await AssertParity(rt, "/tradepile", "", () => rt.Fut.Market.TradePileAsync(QueryString.Parse(""), CancellationToken.None), 3);
        await AssertParity(rt, "/auctionhouse", "", () => rt.Fut.Market.TradePileAsync(QueryString.Parse(""), CancellationToken.None), 3);
        await AssertParity(rt, "/watchlist", "", () => rt.Fut.Market.WatchlistAsync(QueryString.Parse(""), CancellationToken.None), 3);
        var ids = string.Join(",", aiTrades);
        await AssertParity(rt, "/trade/status/lite", "tradeIds=" + ids,
            () => rt.Fut.Market.StatusAsync(aiTrades, CancellationToken.None), aiTrades.Count);

        // The status written after a bid is the same streamed body as the standalone status route.
        var bid = await rt.Router.HandleAsync(new FutRequest("PUT", G + "/trade/" + aiTrades[1] + "/bid",
            Array.Empty<KeyValuePair<string, string>>(), System.Text.Encoding.UTF8.GetBytes("{\"bid\":150}")));
        Assert.Equal(200, bid.Status);
        Assert.Equal(FutJson.Serialize(await rt.Fut.Market.StatusAsync(new[] { aiTrades[1] }, CancellationToken.None)),
            System.Text.Encoding.UTF8.GetString(bid.Body));
    }

    [Fact]
    public void ThePackResponseWritesTheSameBytesAsItsDocument()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, time: new TestRuntime.FixedTime(Now));
        // Pack contents are random, so the equivalence is checked on fixed contents: the same arguments
        // written straight to bytes and rendered as the document the other callers receive.
        var args = PackArgs(rt);
        var document = FutJson.Serialize(TemplateLibrary.Core.Object(StoreService.PurchaseCompleteTemplate, PackArgs(rt)));
        Assert.Equal(document, System.Text.Encoding.UTF8.GetString(TemplateLibrary.Core.Bytes(StoreService.PurchaseCompleteTemplate, args)));
        Assert.Equal(12 * 6, System.Text.RegularExpressions.Regex.Matches(document, "\"resourceId\"").Count);
    }

    internal static TemplateArgs PackArgs(TestRuntime rt)
    {
        var items = new JsonArray(rt.Game.Players.Ids.OrderBy(id => id).Take(12)
            .Select((id, index) => (JsonNode?)rt.Fut.Native.PackItem(rt.Fut.Items.BuildPlayer(id, 900_000_000_000 + index, "unassigned")))
            .ToArray());
        return new TemplateArgs
        {
            AdoptNodes = true,
            ["packId"] = 1,
            ["transactionId"] = 77,
            ["numberItems"] = items.Count,
            ["duplicates"] = new JsonArray(new JsonObject { ["itemId"] = 900_000_000_000, ["duplicateItemId"] = 5 }),
            ["items"] = items,
            ["credits"] = 1234,
            ["points"] = 56,
            ["unopenedPacks"] = 2,
        };
    }

    private static async Task AssertParity(TestRuntime rt, string path, string query, Func<ValueTask<JsonObject>> document, int auctions)
    {
        var response = await Get(rt, path, query);
        Assert.Equal(200, response.Status);
        Assert.Equal(FutJson.Serialize(await document()), System.Text.Encoding.UTF8.GetString(response.Body));
        Assert.Equal(auctions, JsonNode.Parse(response.Body)!["auctionInfo"]!.AsArray().Count);
    }

    private static async Task<FutResponse> Get(TestRuntime rt, string path, string query) =>
        await rt.Router.HandleAsync(new FutRequest("GET", G + path + (query.Length > 0 ? "?" + query : ""),
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
}
