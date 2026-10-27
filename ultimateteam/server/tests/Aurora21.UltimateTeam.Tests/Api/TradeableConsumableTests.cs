using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Pack consumables are tradeable as built (retail FIFA 21 chemistry styles and position changes): only an untradeable
/// grant (rules/tradeability.json) restricts the stack, a restricted stack cannot reach the transfer market, and a
/// tradeable stack lists whole, is found by its own type and settles to the buyer with its quantity.
/// </summary>
public sealed class TradeableConsumableTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>A synthetic store pack that always opens two chemistry styles and one position modifier.</summary>
    private static TestRuntime Create(string? connection = null, long persona = 910001) => TestRuntime.Create(connectionString: connection, marketTarget: 0,
        seedSql: connection is null ? """
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,random_consumable_count,consumable_mix,store_group)
            VALUES (94,'store',true,'Synthetic consumable pack',1,3,3,'{"chemistry_style":2,"position_modifier":1}','special')
            """ : null,
        prepareRoot: root => { Directory.CreateDirectory(Path.Combine(root, "user-config")); File.WriteAllText(Path.Combine(root, "user-config", "config.json"),
            new JsonObject { ["persona_id"] = persona, ["nucleus_id"] = persona + 100000, ["display_name"] = "Trade" + persona }.ToJsonString()); });

    private static List<JsonObject> OpenConsumablePack(TestRuntime rt)
    {
        rt.Game.Store().DeletePile("unassigned");
        rt.Game.InUnit(uow => uow.AddCreditsToAsync(uow.ClubId, 1000));
        var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 94 });
        Assert.Equal("PURCHASECOMPLETE", opened["state"].S());
        return rt.Game.Store().ListItems("unassigned");
    }

    private static Task<List<T>> QueryAsync<T>(TestRuntime rt, string sql, Func<NpgsqlDataReader, T> read, params (string Name, object? Value)[] parameters) =>
        rt.Game.ReadAsync(async w => await w.QueryAsync(sql, read, parameters), CancellationToken.None);

    [Fact]
    public async Task PackConsumablesOpenTradeable()
    {
        using var rt = Create();
        var items = OpenConsumablePack(rt);
        var styles = items.Where(item => item.Text("itemType") == "training").ToList();
        var modifiers = items.Where(item => item.Text("itemType") == "development").ToList();
        Assert.Equal(2, styles.Count);
        Assert.Single(modifiers);

        // The wire shows a tradeable stack, and quick sell pays the rules value, not the old untradeable cap.
        Assert.All(items, item => { Assert.False(item.Bool("untradeable")); Assert.Equal(0, item.Int("untradeableCount")); });
        Assert.All(styles, style => Assert.Equal(rt.Game.Rules.Cards.ChemistryStyles.DiscardValue, style.Int("discardValue")));

        // The stored stack carries the same shape, so the market and quick sell see a tradeable stack.
        foreach (var item in items)
        {
            Assert.False(rt.Game.Store().GetItem(item.IntKeep("id"))!.Bool("untradeable"));
            var counts = await QueryAsync(rt, """
                SELECT s.untradeable_quantity FROM ultimateteam.consumable_items s
                JOIN ultimateteam.items i ON i.id=s.item_id WHERE i.native_item_id=@id
                """, row => row.GetInt64(0), ("id", item.IntKeep("id")));
            Assert.Equal(0, counts.Single());
        }
    }

    [Fact]
    public async Task AnUntradeableGrantRestrictsTheWholeStack()
    {
        using var rt = Create();
        var resource = rt.Game.Cards.ChemistryStyles.Select(style => style.ResourceId).First();
        var stack = await rt.Game.RunAsync("restrict-stack", async uow =>
        {
            var item = rt.Fut.Items.BuildConsumable(resource, await uow.NextItemIdAsync(), "club", 3);
            rt.Fut.Items.MarkReward(item, RewardSources.MatchPack, untradeable: true);
            await uow.SaveItemAsync(item);
            return item;
        }, CancellationToken.None);
        Assert.True(stack.Bool("untradeable"));
        Assert.Equal(3, stack.Int("untradeableCount"));
        // A Match Pack item quick-sells for nothing, on the wire and out of the stored stack.
        Assert.Equal(0, stack.Int("discardValue"));
        Assert.True(rt.Game.Store().GetItem(stack.IntKeep("id"))!.Bool("untradeable"));
        var counts = await QueryAsync(rt, """
            SELECT s.untradeable_quantity FROM ultimateteam.consumable_items s
            JOIN ultimateteam.items i ON i.id=s.item_id WHERE i.native_item_id=@id
            """, row => row.GetInt64(0), ("id", stack.IntKeep("id")));
        Assert.Equal(3, counts.Single());

        var refused = rt.CallObject("POST", $"{G}/auctionhouse", new JsonObject { ["itemId"] = stack.IntKeep("id"), ["startingBid"] = 200, ["buyNowPrice"] = 1000 });
        Assert.Equal("this card is untradeable", refused["reason"].S());
    }

    [Fact]
    public async Task ATradeableStackListsSearchesAndSettles()
    {
        using var seller = Create();
        var buyer = Create(seller.ConnectionString, 910002);
        try
        {
            var style = OpenConsumablePack(seller).First(item => item.Text("itemType") == "training");
            var quantity = style.Int("stackCount");
            var listed = seller.CallObject("POST", $"{G}/auctionhouse",
                new JsonObject { ["itemId"] = style.IntKeep("id"), ["startingBid"] = 200, ["buyNowPrice"] = 1000 });
            var trade = listed.IntKeep("tradeId");
            Assert.True(trade > 0);
            Assert.Contains(style.IntKeep("id"), (await seller.Game.ReadAsync(w => w.LiveMarketListedItemsAsync(new[] { style.IntKeep("id") }), CancellationToken.None)).ToList());
            Assert.Contains(trade, seller.CallObject("GET", $"{G}/tradepile?offset=0&count=10")["auctionInfo"]!.AsArray()
                .Select(row => row!.AsObject().IntKeep("tradeId")));

            // A training search finds it; a player search does not.
            Assert.Contains(trade, buyer.CallObject("GET", $"{G}/transfermarket?type=training")["auctionInfo"]!.AsArray()
                .Select(row => row!.AsObject().IntKeep("tradeId")));
            Assert.DoesNotContain(trade, buyer.CallObject("GET", $"{G}/transfermarket?type=player")["auctionInfo"]!.AsArray()
                .Select(row => row!.AsObject().IntKeep("tradeId")));

            var sellerCoins = await seller.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None);
            var purchase = buyer.CallRaw("POST", $"{G}/trade/{trade}/buy");
            Assert.Equal(200, purchase.Status);

            // The buyer receives the whole stack, tradeable, and the seller's stack is gone.
            var bought = Assert.Single(buyer.Game.Store().ListItems("unassigned"));
            Assert.Equal(style.Int("resourceId"), bought.Int("resourceId"));
            Assert.Equal(quantity, bought.Int("stackCount"));
            Assert.False(bought.Bool("untradeable"));
            Assert.Equal(2, bought.Int("owners"));
            Assert.Null(await seller.Game.ReadAsync(w => w.GetItemAsync(style.IntKeep("id")), CancellationToken.None));
            Assert.Equal("closed", (await seller.Game.ReadAsync(w => w.MarketListingAsync(trade), CancellationToken.None))!.State);
            Assert.Equal(sellerCoins + seller.Game.Rules.Market.SellerProceeds(1000), await seller.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None));
        }
        finally { buyer.Detach(); }
    }


}
