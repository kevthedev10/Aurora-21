using Aurora21.UltimateTeam.Core.Data.Repositories;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>The HTTP contract that connects FIFA's transfer-market calls to the shared market.</summary>
public sealed class MarketTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public void MarketRoutesListWatchClearAndPopulateTheHub()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        var store = rt.Game.Store();
        var squadItems = store.ListSquads().SelectMany(SquadDocuments.ItemIds).ToHashSet();
        var item = store.ListItems("club").First(candidate =>
            ClubInventoryService.IsPlayer(candidate) && !candidate.Bool("untradeable") && !squadItems.Contains(candidate.Int("id")));
        var itemId = item.Int("id");

        var emptyPile = rt.CallRaw("GET", $"{G}/tradepile");
        Assert.Equal(200, emptyPile.Status);
        Assert.Equal("no-store", Header(emptyPile.Response, "Cache-Control"));
        Assert.Equal(0, emptyPile.Body["count"].L());
        Assert.Empty(emptyPile.Body["itemData"].A());

        var listed = rt.CallRaw("POST", $"{G}/auctionhouse", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = itemId }),
            ["startingBid"] = "150",
            ["buyNowPrice"] = 1000,
            ["duration"] = 3600,
        });
        Assert.Equal(200, listed.Status);
        Assert.Equal("no-store", Header(listed.Response, "Cache-Control"));
        Assert.True(JsonValues.IsStrictInt(listed.Body["tradeId"], out var tradeId), listed.Body.ToJsonString());
        Assert.True(tradeId > 0);
        Assert.Equal(tradeId, listed.Body["id"].L());
        Assert.Equal("trade", store.GetItem(itemId)!.Text("pile"));

        var pile = rt.CallObject("GET", $"{G}/auctionhouse");
        Assert.Equal(1, pile["count"].L());
        // FIFA's transfer-list decoder requires this compatibility collection even though
        // the actual listing items are nested under auctionInfo[].itemData.
        Assert.Empty(pile["itemData"].A());
        Assert.Equal("active", pile["auctionInfo"]![0]!["tradeState"].S());
        Assert.Equal(tradeId, pile["auctionInfo"]![0]!["tradeId"].L());

        var hub = rt.CallObject("GET", $"{G}/hub");
        Assert.Equal(1, hub["auctionCount"].L());
        Assert.Equal((1L, 1L, 0L), (hub["tradePile"]!["count"].L(), hub["tradePile"]!["selling"].L(), hub["tradePile"]!["sold"].L()));
        Assert.Equal((0L, 0L, 0L), (hub["watchlist"]!["count"].L(), hub["watchlist"]!["outbid"].L(), hub["watchlist"]!["winning"].L()));

        // All three request locations are accepted and duplicates are removed.
        var watched = rt.CallObject("POST", $"{G}/watchlist?tradeId={tradeId}", new JsonObject
        {
            ["tradeId"] = tradeId,
            ["auctionInfo"] = new JsonArray(new JsonObject { ["id"] = tradeId }),
        });
        Assert.Equal(1, watched["count"].L());
        Assert.Equal(tradeId, watched["auctionInfo"]![0]!["tradeId"].L());

        var unwatched = rt.CallObject("DELETE", $"/ut/delete/game/fifa21/watchlist?tradeid={tradeId}");
        Assert.Equal(0, unwatched["count"].L());

        // This is the exact refresh FIFA sends while a Transfer List row is selected.
        var status = rt.CallObject("GET", $"{G}/trade/status/lite?tradeIds={tradeId}&includeBankInfo=true");
        Assert.Single(status["auctionInfo"].A());
        Assert.Equal(tradeId, status["auctionInfo"]![0]!["tradeId"].L());
        Assert.Equal(3600, status["auctionInfo"]![0]!["expires"].L());

        var cleared = rt.CallObject("DELETE", $"/ut/delete/game/fifa21/trade/{tradeId}");
        Assert.Equal(0, cleared["count"].L());
        Assert.Equal("club", store.GetItem(itemId)!.Text("pile"));
    }

    [Fact]
    public void PriceLimitsAndRejectedActionsKeepTheMarketWireContract()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        var resourceId = rt.Game.Store().ListItems("club").First(ClubInventoryService.IsPlayer).Int("resourceId");

        var limits = rt.CallRaw("GET", $"{G}/marketdata/pricelimits?defIds={resourceId},999999999");
        Assert.Equal(200, limits.Status);
        Assert.Equal("no-store", Header(limits.Response, "Cache-Control"));
        Assert.Equal(2, limits.Body.A().Count);
        Assert.Equal(resourceId, limits.Body[0]!["resourceId"].L());
        Assert.Equal(999999999, limits.Body[1]!["resourceId"].L());

        var defaultLimits = rt.CallRaw("GET", $"{G}/marketdata/pricelimits").Body.A();
        Assert.Equal(0, Assert.Single(defaultLimits)!["resourceId"].L());

        var rejected = rt.CallRaw("POST", $"{G}/auctionhouse", new JsonObject
        {
            ["itemId"] = resourceId,
            ["startingBid"] = "not-a-number",
        });
        Assert.Equal(200, rejected.Status);
        Assert.Equal("no-store", Header(rejected.Response, "Cache-Control"));
        Assert.Empty(rejected.Body["auctionInfo"].A());
        Assert.Equal("invalid starting bid", rejected.Body["reason"].S());
        Assert.Equal(rt.Game.Store().Credits(), rejected.Body["credits"].L());
    }

    [Fact]
    public void ActiveListingCannotBeDiscardedMovedSquaddedOrConsumed()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        var store = rt.Game.Store();
        var squadItems = store.ListSquads().SelectMany(SquadDocuments.ItemIds).ToHashSet();
        var item = store.ListItems("club").First(candidate =>
            ClubInventoryService.IsPlayer(candidate) && !candidate.Bool("untradeable") && !squadItems.Contains(candidate.Int("id")));
        var itemId = item.Int("id");
        var tradeId = rt.CallObject("POST", $"{G}/auctionhouse", new JsonObject
        {
            ["itemId"] = itemId,
            ["startingBid"] = 150,
            ["buyNowPrice"] = 1000,
        })["tradeId"].L();

        var discard = rt.CallObject("DELETE", $"{G}/item/{itemId}");
        Assert.Empty(discard["items"].A());
        Assert.NotNull(store.GetItem(itemId));

        var moved = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonObject { ["id"] = itemId, ["pile"] = "club" },
        })["itemData"]![0]!;
        Assert.False(moved["success"].B());
        Assert.Equal(ClubInventoryService.ListedItemReason, moved["reason"].S());
        Assert.Equal("trade", store.GetItem(itemId)!.Text("pile"));

        var squad = store.GetSquad(1)!.Copy();
        squad.Arr("players")![0] = new JsonObject { ["index"] = 0, ["itemId"] = itemId };
        var squadSave = rt.CallRaw("PUT", $"{G}/squad/1", squad);
        Assert.Equal(400, squadSave.Status);
        Assert.Equal(ClubInventoryService.ListedItemReason, squadSave.Body["reason"].S());

        // A new club starts without consumables (they come from packs), so the club is given a chemistry style here.
        rt.Game.InUnit(w => rt.Fut.Items.MakeConsumableAsync(w, rt.Game.Cards.ChemistryStyles[0].ResourceId));
        var consumable = store.ListItems("club").First(candidate =>
            Aurora21.UltimateTeam.Core.Items.NativeItems.IsConsumable(candidate)
            && (rt.Game.Cards.ChemistryStyle(candidate.Int("resourceId")) is not null
                || rt.Game.Cards.PositionModifier(candidate.Int("resourceId")) is not null));
        var apply = rt.CallRaw("PUT", $"{G}/item/resource/{consumable.Int("resourceId")}", new JsonObject { ["itemId"] = itemId });
        Assert.Equal(400, apply.Status);
        Assert.Equal(ClubInventoryService.ListedItemReason, apply.Body["reason"].S());

        // Challenge ids are SbcCatalog.FirstSetId + index * SetIdStep + position, never 1: the old
        // literal was rejected as SBC_UNKNOWN_CHALLENGE before this reached the listed-item guard.
        // CH_1 takes exactly one player, so a single listed card is a well-formed submission.
        var exchange = rt.Game.Sbc.Challenges.First(candidate => candidate.Key == "CH_1");
        var sbcSave = rt.CallRaw("PUT", $"{G}/sbs/challenge/{exchange.Id}/squad", new JsonObject
        {
            ["players"] = new JsonArray(new JsonObject { ["index"] = 10, ["itemData"] = new JsonObject { ["id"] = itemId } }),
        });
        // Listing moved the card to the trade pile, so the SBC pile guard refuses it. A refused save answers 200 (an error
        // status sends FIFA back through login) and stores nothing, so the card cannot be submitted.
        Assert.Equal(200, sbcSave.Status);
        Assert.Equal(0, store.SbcSquadCount(exchange.Id));

        Assert.NotNull(store.LiveMarketListingForItem(itemId));
        Assert.Equal(tradeId, store.LiveMarketListingForItem(itemId)!.TradeId);
    }

    [Fact]
    public void CrossClubBuyPaysAnEvictedSellerAndCreatesARealUnassignedItem()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 2 }, shared: true);
        const string sellerId = "111111";
        const string buyerId = "222222";
        long tradeId;
        long sellerClubId;
        long sellerItemId;
        long sellerCredits;
        long resourceId;
        long originalOwners;

        using (var seller = players.Acquire(PlayerIdentity.Of(sellerId)))
        {
            var store = seller.Session.Game.Store();
            var item = ListablePlayer(store);
            sellerClubId = store.ClubId;
            sellerItemId = item.Int("id");
            sellerCredits = store.Credits();
            resourceId = item.Int("resourceId");
            originalOwners = item.Int("owners");
            tradeId = seller.Session.Fut.Market.List(sellerItemId, 150, 1000, 3600)["tradeId"].L();
            // The seller's own listing shows their own card.
            Assert.Contains(seller.Session.Fut.Market.TradePile()["auctionInfo"].Objects(),
                auction => auction["tradeId"].L() == tradeId && auction["itemData"]!.AsObject().Int("id") == sellerItemId);
        }

        using var buyer = players.Acquire(PlayerIdentity.Of(buyerId));
        Assert.False(players.IsOpen(sellerId));
        var buyerStore = buyer.Session.Game.Store();
        var buyerCredits = buyerStore.Credits();
        var price = buyerStore.MarketListing(tradeId)!.BuyNow;
        // Other clubs see the trade's market id before the buy, and the buy keeps it: FIFA caches items by id, and a buy
        // answered with a new id turned the card into "NOT FOUND" (live 10-05).
        var marketId = ClubRepository.MarketItemIdOf(tradeId);
        Assert.Equal(marketId, Assert.Single(buyer.Session.Fut.Market.Status(new[] { tradeId })["auctionInfo"].Objects())["itemData"]!.AsObject().Int("id"));
        buyer.Session.Fut.Market.BuyNow(tradeId, price);

        Assert.Equal(buyerCredits - price, buyerStore.Credits());
        // The seller receives the price less the transfer tax (rules/market.json sellerTaxPercent).
        Assert.Equal(sellerCredits + buyer.Session.Game.Rules.Market.SellerProceeds(price), buyerStore.CreditsOf(sellerClubId));
        Assert.Null(buyerStore.ItemOf(sellerClubId, sellerItemId));
        var bought = buyerStore.ListItems("unassigned").Single(item => item.Int("lastSalePrice") == price && item.Int("resourceId") == resourceId);
        Assert.Equal(marketId, bought.Int("id"));
        Assert.Equal(Math.Max(2, originalOwners + 1), bought.Int("owners"));
        // Retail loyalty: a bought card arrives without the seller's loyalty bonus or club games.
        Assert.Equal(0, bought.Int("loyaltyBonus"));
        Assert.All(bought["statsList"]!.AsArray().OfType<JsonObject>(), row => Assert.Equal(0, row.Int("value")));
        Assert.False(bought.Bool("untradeable"));
        Assert.False(bought.Bool("duplicate"));
        Assert.True(buyer.Session.Fut.Inventory.WouldDuplicateOwnedPlayer(bought.Int("id")));
        Assert.Equal(MarketState.Closed, buyerStore.MarketListing(tradeId)!.State);
        Assert.Contains(tradeId, buyerStore.MarketWatchIds());
        // The sale is in the operator's market log (market_trades), seller, buyer and price.
        var logged = buyer.Session.Game.InUnit(async uow => (await uow.QueryAsync("""
            SELECT sale, seller_club_id, buyer_club_id, resource_id, price, seller_proceeds FROM ultimateteam.market_trades WHERE trade_id = @trade
            """, row => (row.GetString(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4), row.GetInt64(5)), ("trade", tradeId))).Single());
        Assert.Equal((MarketSale.BuyNow, sellerClubId, buyerStore.ClubId, resourceId, price, buyer.Session.Game.Rules.Market.SellerProceeds(price)), logged);
        var won = Assert.Single(buyer.Session.Fut.Market.Status(new[] { tradeId })["auctionInfo"].Objects());
        Assert.Equal("buyNow", won["bidState"].S());
        // The sold trade names the buyer's copy, so moving or squadding it straight from the buy response finds it.
        AssertNamesBoughtCopy(won, bought, price);
        AssertNamesBoughtCopy(Assert.Single(buyer.Session.Fut.Market.Watchlist()["auctionInfo"].Objects()), bought, price);
    }

    [Fact]
    public async Task ClaimedWinLeavesTransferTargetsAndAClubCardIsNotItsOwnDuplicate()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var buyer = players.Acquire(PlayerIdentity.Of("222222"));
        var tradeId = seller.Session.Fut.Market.List(ListablePlayer(seller.Session.Game.Store()).Int("id"), 150, 1000, 3600)["tradeId"].L();
        var buyerStore = buyer.Session.Game.Store();
        var price = buyerStore.MarketListing(tradeId)!.BuyNow;
        buyer.Session.Fut.Market.BuyNow(tradeId, price);
        var bought = buyerStore.ListItems("unassigned").Single(item => item.Int("lastSalePrice") == price);
        Assert.Equal(1, buyer.Session.Fut.Market.Watchlist()["count"].L());

        // Unclaimed: a card still in Unassigned keeps its won trade on Transfer Targets.
        await buyer.Session.Fut.Inventory.ApplyItemUpdatesAsync(new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = bought.Int("id"), ["pile"] = "purchased", ["tradeId"] = tradeId }),
        });
        Assert.Single(buyer.Session.Fut.Market.Watchlist()["auctionInfo"].Objects());

        // Claimed: once the copy leaves Unassigned the trade is gone, so it cannot be claimed again.
        await buyer.Session.Fut.Inventory.ApplyItemUpdatesAsync(new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = bought.Int("id"), ["pile"] = "trade", ["tradeId"] = tradeId }),
        });
        var watchlist = buyer.Session.Fut.Market.Watchlist();
        Assert.Equal(0, watchlist["count"].L());
        Assert.Empty(watchlist["auctionInfo"].Objects());
        Assert.Equal(0, (await buyer.Session.Fut.Market.CountersAsync()).WatchCount);

        // Sending a card that is already in the club to the club again leaves it there, not in Unassigned.
        var squadItems = buyerStore.ListSquads().SelectMany(SquadDocuments.ItemIds).ToHashSet();
        var clubCard = buyerStore.ListItems("club").First(item => ClubInventoryService.IsPlayer(item) && !squadItems.Contains(item.Int("id")));
        var resent = await buyer.Session.Fut.Inventory.ApplyItemUpdatesAsync(new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = clubCard.Int("id"), ["pile"] = "club" }),
        });
        Assert.False(resent.ContainsKey("duplicateItemIdList"));
        Assert.Equal("club", buyerStore.GetItem(clubCard.Int("id"))!.Str("pile"));
    }

    private static void AssertNamesBoughtCopy(JsonObject auction, JsonObject bought, long price)
    {
        var shown = auction["itemData"]!.AsObject();
        Assert.Equal(bought.Int("id"), shown.Int("id"));
        Assert.Equal(price, shown.Int("lastSalePrice"));
        Assert.Equal(bought.Int("owners"), shown.Int("owners"));
    }

    [Fact]
    public async Task ConcurrentBuyNowSettlesExactlyOnce()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var first = players.Acquire(PlayerIdentity.Of("222222"));
        using var second = players.Acquire(PlayerIdentity.Of("333333"));
        var item = ListablePlayer(seller.Session.Game.Store());
        var sellerCredits = seller.Session.Game.Store().Credits();
        var firstCredits = first.Session.Game.Store().Credits();
        var secondCredits = second.Session.Game.Store().Credits();
        var tradeId = seller.Session.Fut.Market.List(item.Int("id"), 150, 1000, 3600)["tradeId"].L();
        var price = seller.Session.Game.Store().MarketListing(tradeId)!.BuyNow;
        using var start = new ManualResetEventSlim();

        static bool TryBuy(SharedMarketService market, long trade, long offered, ManualResetEventSlim gate)
        {
            gate.Wait();
            try
            {
                market.BuyNow(trade, offered);
                return true;
            }
            catch (MarketException)
            {
                return false;
            }
        }

        var attempts = new[]
        {
            Task.Run(() => TryBuy(first.Session.Fut.Market, tradeId, price, start)),
            Task.Run(() => TryBuy(second.Session.Fut.Market, tradeId, price, start)),
        };
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        Assert.Equal(sellerCredits + seller.Session.Game.Rules.Market.SellerProceeds(price), seller.Session.Game.Store().Credits());
        Assert.Equal(firstCredits + secondCredits - price, first.Session.Game.Store().Credits() + second.Session.Game.Store().Credits());
        Assert.Equal(1, first.Session.Game.Store().ListItems("unassigned").Count(item => item.Int("lastSalePrice") == price)
                        + second.Session.Game.Store().ListItems("unassigned").Count(item => item.Int("lastSalePrice") == price));
        Assert.Equal(MarketState.Closed, seller.Session.Game.Store().MarketListing(tradeId)!.State);
    }

    [Fact]
    public void BuyingAnAiPreviewKeepsTheTradesMarketIdAndDuplicateFlow()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        var store = rt.Game.Store();
        var owned = store.ListItems("club").First(ClubInventoryService.IsPlayer);
        var resourceId = owned.Int("resourceId");
        const long previewId = 800000001;
        const long price = 1000;
        var preview = rt.Fut.Items.BuildPlayer(resourceId, previewId, "market");
        var tradeId = store.InsertMarketListing(MarketListingKind.Ai, "FUTUser1000", null, resourceId, preview,
            preview.Int("rating"), 150, price, rt.Game.NowSeconds, rt.Game.NowSeconds + 3600);
        var credits = store.Credits();

        rt.Fut.Market.BuyNow(tradeId, price);

        var bought = store.ListItems("unassigned").Single(item => item.Int("lastSalePrice") == price && item.Int("resourceId") == resourceId);
        Assert.Equal(ClubRepository.MarketItemIdOf(tradeId), bought.Int("id"));
        Assert.Equal(credits - price, store.Credits());
        Assert.True(rt.Fut.Inventory.WouldDuplicateOwnedPlayer(bought.Int("id")));
        AssertNamesBoughtCopy(Assert.Single(rt.Fut.Market.Status(new[] { tradeId })["auctionInfo"].Objects()), bought, price);
    }

    [Fact]
    public void AtomicFifaPointUpdatesDoNotLoseConcurrentPoints()
    {
        using var rt = TestRuntime.Create();
        using var other = new TestClub(rt.Game);
        const int changesPerConnection = 50;

        Parallel.Invoke(
            () => { for (var i = 0; i < changesPerConnection; i++) rt.Game.Store().AddFifaPoints(1); },
            () => { for (var i = 0; i < changesPerConnection; i++) other.AddFifaPoints(1); });

        Assert.Equal(changesPerConnection * 2, rt.Game.Store().FifaPoints());
    }

    [Fact]
    public void ListingValidationAndInactiveTransferListRowsArePreserved()
    {
        var clock = new MutableTime(1_700_000_000);
        using var rt = TestRuntime.Create(time: clock, marketTarget: 0);
        var store = rt.Game.Store();
        var listedItem = ListablePlayer(store);
        var first = rt.Fut.Market.List(listedItem.Int("id"), 150, 1000, 3600);
        var repeated = rt.Fut.Market.List(listedItem.Int("id"), 200, 2000, 7200);
        Assert.Equal(first["tradeId"].L(), repeated["tradeId"].L());

        var untradeable = ListablePlayer(store, listedItem.Int("id"));
        untradeable["untradeable"] = true;
        store.SaveItem(untradeable);
        Assert.Equal("this card is untradeable", Assert.Throws<MarketException>(() =>
            rt.Fut.Market.List(untradeable.Int("id"), 150, 1000, 3600)).Message);

        var squadItem = store.ListItems().First(item => ClubInventoryService.IsPlayer(item)
            && store.ListSquads().Any(squad => SquadDocuments.ItemIds(squad).Contains(item.Int("id"))));
        Assert.Equal("remove this card from your squad first", Assert.Throws<MarketException>(() =>
            rt.Fut.Market.List(squadItem.Int("id"), 150, 1000, 3600)).Message);
        Assert.Equal("player item was not found", Assert.Throws<MarketException>(() =>
            rt.Fut.Market.List(long.MaxValue, 150, 1000, 3600)).Message);
        var nonPlayer = store.ListItems().First(item => !ClubInventoryService.IsPlayer(item));
        Assert.Equal("player item was not found", Assert.Throws<MarketException>(() =>
            rt.Fut.Market.List(nonPlayer.Int("id"), 150, 1000, 3600)).Message);

        var inactive = ListablePlayer(store, listedItem.Int("id"), untradeable.Int("id"));
        Assert.True(rt.Fut.Inventory.UpdatePile(inactive.Int("id"), "trade"));
        var pile = rt.Fut.Market.TradePile();
        var inactiveRow = Assert.Single(pile["auctionInfo"].Objects(), row => row["itemData"]!["id"].L() == inactive.Int("id"));
        Assert.Equal(0, inactiveRow["tradeId"].L());
        Assert.Equal("inactive", inactiveRow["tradeState"].S());
        Assert.DoesNotContain(rt.CallObject("GET", $"{G}/club?count=500")["itemData"].Objects(),
            row => row["id"].L() == inactive.Int("id"));

        clock.AdvanceSeconds(3601);
        rt.Fut.Market.ExpireDue();
        var relisted = rt.Fut.Market.List(listedItem.Int("id"), 150, 1000, 3600)["tradeId"].L();
        Assert.NotEqual(first["tradeId"].L(), relisted);
        Assert.Equal(relisted, Assert.Single(store.OwnMarketListings()).TradeId);
    }

    [Fact]
    public async Task ACardInASavedSquadListsAndLeavesThatSquad()
    {
        // Live 10-05: FIFA offers listing for a card in a saved (not active) squad; the server refused it with a 200 and the
        // Transfer List kept a card that was never listed.
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        var store = rt.Game.Store();
        var card = ListablePlayer(store);
        await rt.Game.RunAsync("saved-squad", async work =>
        {
            var saved = (await work.ListSquadsAsync())[0].DeepClone().AsObject();
            var slot = saved.Arr("players")!.OfType<JsonObject>().First();
            slot["itemId"] = card.Int("id");
            slot["itemData"] = card.DeepClone();
            saved["id"] = 2;
            saved["squadId"] = 2;
            await work.SaveSquadAsync(2, "Saved", false, saved);
            return 0;
        }, CancellationToken.None);
        Assert.Contains(store.ListSquads(), squad => !squad.Bool("active") && SquadDocuments.ItemIds(squad).Contains(card.Int("id")));

        var trade = rt.Fut.Market.List(card.Int("id"), 150, 1000, 3600)["tradeId"].L();

        Assert.Equal(MarketState.Active, store.MarketListing(trade)!.State);
        Assert.DoesNotContain(store.ListSquads(), squad => SquadDocuments.ItemIds(squad).Contains(card.Int("id")));
    }

    [Fact]
    public async Task AnExpiredCardThatLeftTheTransferListLeavesNoGhostRow()
    {
        // Live 10-05: an expired card sent to the club or quick sold kept its expired row, which showed on the Transfer List
        // and could not be re-listed, quick sold or moved because the card no longer was there.
        var clock = new MutableTime(1_700_000_000);
        using var rt = TestRuntime.Create(time: clock, marketTarget: 0);
        var store = rt.Game.Store();
        var kept = ListablePlayer(store);
        var moved = ListablePlayer(store, kept.Int("id"));
        var sold = ListablePlayer(store, kept.Int("id"), moved.Int("id"));
        var keptTrade = rt.Fut.Market.List(kept.Int("id"), 150, 1000, 3600)["tradeId"].L();
        rt.Fut.Market.List(moved.Int("id"), 150, 1000, 3600);
        rt.Fut.Market.List(sold.Int("id"), 150, 1000, 3600);
        clock.AdvanceSeconds(3601);
        rt.Fut.Market.ExpireDue();

        Assert.True(rt.Fut.Inventory.UpdatePile(moved.Int("id"), "club"));
        Assert.Equal(new[] { sold.Int("id") }, (await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[] { sold.Int("id") })).Deleted);

        var rows = rt.Fut.Market.TradePile()["auctionInfo"].Objects().ToList();
        Assert.Equal(keptTrade, Assert.Single(rows)["tradeId"].L());
        Assert.Equal(keptTrade, Assert.Single(store.OwnMarketListings()).TradeId);
        Assert.Equal(1, rt.Fut.Market.Counters().TradePileCount);
        var relisted = rt.CallObject("PUT", $"{G}/auctionhouse/relist")["tradeIdList"].Objects().Select(trade => trade["id"].L()).ToList();
        Assert.Single(relisted);
    }

    [Fact]
    public void SearchRoutesApplyEveryPlayerAndPriceFilter()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var buyer = players.Acquire(PlayerIdentity.Of("222222"));
        var store = seller.Session.Game.Store();
        var candidates = ListablePlayers(store).ToList();
        var target = candidates.First(item => item.Str("preferredPosition") is "LM" or "LW");
        var other = candidates.First(item => item.Int("id") != target.Int("id") && item.Int("resourceId") != target.Int("resourceId"));
        var targetTrade = seller.Session.Fut.Market.List(target.Int("id"), 500, 2000, 3600)["tradeId"].L();
        seller.Session.Fut.Market.List(other.Int("id"), 1000, 3000, 3600);
        var listing = store.MarketListing(targetTrade)!;

        JsonObject Search(string query) => buyer.Session.Fut.Market.Search(QueryString.Parse(query));
        void FindsTarget(string query) => Assert.Contains(Search(query)["auctionInfo"].Objects(), row => row["tradeId"].L() == targetTrade);

        Assert.Empty(seller.Session.Fut.Market.Search(QueryString.Parse(string.Empty))["auctionInfo"].A());
        var routed = Call(buyer.Session, "GET", $"{G}/transfermarket?num=1");
        Assert.Equal("no-cache, no-store, must-revalidate", Header(routed.Response, "Cache-Control"));
        Assert.Equal("no-cache", Header(routed.Response, "Pragma"));
        Assert.Equal("0", Header(routed.Response, "Expires"));
        Assert.Equal(2, routed.Body["total"].L());
        Assert.Single(routed.Body["auctionInfo"].A());

        FindsTarget("type=player");
        Assert.Empty(Search("type=manager")["auctionInfo"].A());
        FindsTarget($"definitionId={target.Int("resourceId")}");
        FindsTarget($"maskedDefId={target.Int("assetId")}");
        FindsTarget($"nation={target.Int("nation")}");
        FindsTarget($"league={target.Int("leagueId")}");
        FindsTarget($"club={target.Int("teamid")}");
        FindsTarget("pos=LM-LW");
        var zone = rt.Game.Rules.Club.PositionZones.First(entry => entry.Value.Contains(target.Str("preferredPosition"))).Key;
        FindsTarget($"zone={zone}");
        var level = target.Int("rating") switch { <= 64 => "bronze", <= 74 => "silver", _ => "gold" };
        FindsTarget($"lev={level}");
        FindsTarget($"rarityIds={target.Int("rareflag")}");
        FindsTarget($"minb={listing.BuyNow}&maxb={listing.BuyNow}");
        FindsTarget($"micr={listing.StartingBid}&macr={listing.StartingBid}");
        Assert.Equal(2, Search("start=0&num=1")["total"].L());
        Assert.Single(Search("start=1&num=1")["auctionInfo"].A());

        // A listing must match its catalog metadata (anything else is refused for a converter audit), so the
        // special card comes from a definition that is special in the catalog rather than from editing an
        // ordinary card's rarity.
        var specialResource = seller.Session.Game.Players.Entries.First(entry => entry.Value.IsSpecial && entry.Value.Rareflag > 1).Key;
        var special = seller.Session.Fut.Items.BuildPlayer(specialResource, store.NextItemId(), "club");
        special["untradeable"] = false;
        store.SaveItem(special);
        var specialTrade = seller.Session.Fut.Market.List(special.Int("id"), 10000, 20000, 3600)["tradeId"].L();
        Assert.Contains(Search("rare=SP")["auctionInfo"].Objects(), row => row["tradeId"].L() == specialTrade);
    }

    [Fact]
    public void BidsUseEscrowRefundOutbidsAndRejectInvalidSpending()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var first = players.Acquire(PlayerIdentity.Of("222222"));
        using var second = players.Acquire(PlayerIdentity.Of("333333"));
        using var poor = players.Acquire(PlayerIdentity.Of("444444"));
        var sellerStore = seller.Session.Game.Store();
        var cards = ListablePlayers(sellerStore).Take(3).ToList();
        var tradeId = seller.Session.Fut.Market.List(cards[0].Int("id"), 500, 5000, 3600)["tradeId"].L();
        var listing = sellerStore.MarketListing(tradeId)!;
        var firstCredits = first.Session.Game.Store().Credits();
        var secondCredits = second.Session.Game.Store().Credits();

        Assert.Equal("you cannot bid on your own listing", Assert.Throws<MarketException>(() =>
            seller.Session.Fut.Market.PlaceBid(tradeId, listing.StartingBid)).Message);
        Assert.Equal("bid is below the minimum", Assert.Throws<MarketException>(() =>
            first.Session.Fut.Market.PlaceBid(tradeId, listing.StartingBid - 1)).Message);
        Assert.Equal(firstCredits, first.Session.Game.Store().Credits());

        first.Session.Fut.Market.PlaceBid(tradeId, listing.StartingBid);
        Assert.Equal(firstCredits - listing.StartingBid, first.Session.Game.Store().Credits());
        var raised = listing.StartingBid + rt.Game.Rules.Market.BidIncrement(listing.StartingBid);
        first.Session.Fut.Market.PlaceBid(tradeId, raised);
        Assert.Equal(firstCredits - raised, first.Session.Game.Store().Credits());
        var outbid = raised + rt.Game.Rules.Market.BidIncrement(raised);
        second.Session.Fut.Market.PlaceBid(tradeId, outbid);
        Assert.Equal(firstCredits, first.Session.Game.Store().Credits());
        Assert.Equal(secondCredits - outbid, second.Session.Game.Store().Credits());

        first.Session.Fut.Market.BuyNow(tradeId, listing.BuyNow);
        Assert.Equal(firstCredits - listing.BuyNow, first.Session.Game.Store().Credits());
        Assert.Equal(secondCredits, second.Session.Game.Store().Credits());
        Assert.Equal(MarketState.Closed, sellerStore.MarketListing(tradeId)!.State);

        var ownEscrowTrade = seller.Session.Fut.Market.List(cards[1].Int("id"), 500, 5000, 3600)["tradeId"].L();
        var ownEscrow = sellerStore.MarketListing(ownEscrowTrade)!;
        var beforeOwnEscrow = first.Session.Game.Store().Credits();
        first.Session.Fut.Market.PlaceBid(ownEscrowTrade, ownEscrow.StartingBid);
        first.Session.Fut.Market.BuyNow(ownEscrowTrade, ownEscrow.BuyNow);
        Assert.Equal(beforeOwnEscrow - ownEscrow.BuyNow, first.Session.Game.Store().Credits());

        var offerTrade = seller.Session.Fut.Market.List(cards[2].Int("id"), 500, 5000, 3600)["tradeId"].L();
        var offer = sellerStore.MarketListing(offerTrade)!;
        second.Session.Fut.Market.Offer(offerTrade, offer.BuyNow);
        Assert.Equal(MarketState.Closed, sellerStore.MarketListing(offerTrade)!.State);

        var fourth = ListablePlayer(sellerStore, cards.Select(card => card.Int("id")).ToArray());
        var refusedTrade = seller.Session.Fut.Market.List(fourth.Int("id"), 500, 5000, 3600)["tradeId"].L();
        var refused = sellerStore.MarketListing(refusedTrade)!;
        poor.Session.Game.Store().AddCredits(-poor.Session.Game.Store().Credits());
        Assert.Equal("not enough coins", Assert.Throws<MarketException>(() =>
            poor.Session.Fut.Market.BuyNow(refusedTrade, refused.BuyNow)).Message);
        Assert.Equal(MarketState.Active, sellerStore.MarketListing(refusedTrade)!.State);
        Assert.NotNull(sellerStore.GetItem(fourth.Int("id")));
    }

    [Fact]
    public void ALostBuyNowRaceAnswersFifasTradeClosedErrorNotAnEmptyWin()
    {
        // 10-08 live: two players hit Buy Now on one card. The loser got 200 with no auctionInfo, which
        // FIFA reads as won and opens the assign screen with nothing in it. FIFA's offer handler maps
        // 461 + "Permission Denied" to TRADE_CLOSED and 470 to NOT_ENOUGH_CREDITS.
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var winner = players.Acquire(PlayerIdentity.Of("222222"));
        using var loser = players.Acquire(PlayerIdentity.Of("333333"));
        var sellerStore = seller.Session.Game.Store();
        var cards = ListablePlayers(sellerStore).Take(2).ToList();
        var tradeId = seller.Session.Fut.Market.List(cards[0].Int("id"), 500, 5000, 3600)["tradeId"].L();
        var loserCredits = loser.Session.Game.Store().Credits();

        var won = Call(winner.Session, "POST", $"{G}/trade/{tradeId}/offer", new JsonObject { ["bid"] = 5000 });
        Assert.Equal(200, won.Status);
        Assert.Equal(tradeId, won.Body["auctionInfo"]![0]!["tradeId"].L());

        var lost = Call(loser.Session, "POST", $"{G}/trade/{tradeId}/offer", new JsonObject { ["bid"] = 5000 });
        Assert.Equal(461, lost.Status);
        Assert.Equal("Permission Denied", lost.Body["reason"].S());
        Assert.Null(lost.Body["auctionInfo"]);
        Assert.Equal(loserCredits, loser.Session.Game.Store().Credits());

        var missing = Call(loser.Session, "POST", $"{G}/trade/{tradeId + 1000}/offer", new JsonObject { ["bid"] = 5000 });
        Assert.Equal(478, missing.Status);

        var poorTrade = seller.Session.Fut.Market.List(cards[1].Int("id"), 500, 5000, 3600)["tradeId"].L();
        loser.Session.Game.Store().AddCredits(-loser.Session.Game.Store().Credits());
        var poor = Call(loser.Session, "POST", $"{G}/trade/{poorTrade}/offer", new JsonObject { ["bid"] = 5000 });
        Assert.Equal(470, poor.Status);
        Assert.Equal(MarketState.Active, sellerStore.MarketListing(poorTrade)!.State);
    }

    [Fact]
    public void AiSeedingHonoursDisabledAndSmallTargets()
    {
        using (var disabled = TestRuntime.Create(marketTarget: 0))
        {
            disabled.Fut.Market.SeedAiListings();
            Assert.Equal(0, disabled.Game.Store().CountActiveMarketListings(MarketListingKind.Ai));
        }
        using (var enabled = TestRuntime.Create(marketTarget: 3))
        {
            enabled.Fut.Market.SeedAiListings();
            Assert.Equal(3, enabled.Game.Store().CountActiveMarketListings(MarketListingKind.Ai));
            foreach (var listing in enabled.Game.Store().ActiveMarketListings())
            {
                var (low, high) = enabled.Game.Cards.MarketPriceBounds(listing.ResourceId);
                Assert.InRange(listing.StartingBid, low, high);
                Assert.InRange(listing.BuyNow, listing.StartingBid, high);
            }
        }
        using (var capped = TestRuntime.Create(marketTarget: 251))
        {
            capped.Fut.Market.SeedAiListings();
            Assert.Equal(capped.Game.Rules.Market.AiListings.MaxPerTopUp,
                capped.Game.Store().CountActiveMarketListings(MarketListingKind.Ai));
        }
    }

    [Fact]
    public void ExpirySettlesBidsReturnsUnsoldCardsAndRefundsImpossibleSales()
    {
        var clock = new MutableTime(1_700_000_000);
        using var rt = TestRuntime.Create(time: clock, marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var buyer = players.Acquire(PlayerIdentity.Of("222222"));
        var sellerStore = seller.Session.Game.Store();
        var buyerStore = buyer.Session.Game.Store();
        var cards = ListablePlayers(sellerStore).Take(3).ToList();

        var soldTrade = seller.Session.Fut.Market.List(cards[0].Int("id"), 500, 5000, 60)["tradeId"].L();
        var sold = sellerStore.MarketListing(soldTrade)!;
        buyer.Session.Fut.Market.PlaceBid(soldTrade, sold.StartingBid);
        clock.AdvanceSeconds(61);
        buyer.Session.Fut.Market.ExpireDue();
        Assert.Equal(MarketState.Closed, sellerStore.MarketListing(soldTrade)!.State);
        Assert.Null(sellerStore.GetItem(cards[0].Int("id")));
        Assert.Contains(buyerStore.ListItems("unassigned"), item => item.Int("resourceId") == cards[0].Int("resourceId")
            && item.Int("lastSalePrice") == sold.StartingBid);

        var unsoldTrade = seller.Session.Fut.Market.List(cards[1].Int("id"), 500, 5000, 60)["tradeId"].L();
        clock.AdvanceSeconds(61);
        seller.Session.Fut.Market.ExpireDue();
        Assert.Equal(MarketState.Expired, sellerStore.MarketListing(unsoldTrade)!.State);
        Assert.Equal("trade", sellerStore.GetItem(cards[1].Int("id"))!.Text("pile"));
        Assert.Equal("free", sellerStore.GetItem(cards[1].Int("id"))!.Text("itemState"));
        seller.Session.Fut.Market.ClearTrade(unsoldTrade);
        Assert.Equal("club", sellerStore.GetItem(cards[1].Int("id"))!.Text("pile"));

        // A card with a bid can neither be withdrawn nor deleted: the schema refuses an active listing whose card is
        // gone, so the "seller card is no longer available" refund in expiry is defence in depth, not a reachable
        // state. The listing then settles normally.
        var guardedTrade = seller.Session.Fut.Market.List(cards[2].Int("id"), 500, 5000, 60)["tradeId"].L();
        var guarded = sellerStore.MarketListing(guardedTrade)!;
        var buyerCreditsBeforeBid = buyerStore.Credits();
        var sellerCreditsBeforeSale = sellerStore.Credits();
        buyer.Session.Fut.Market.PlaceBid(guardedTrade, guarded.StartingBid);
        Assert.Throws<MarketException>(() => seller.Session.Fut.Market.ClearTrade(guardedTrade));
        var refused = Assert.Throws<Npgsql.PostgresException>(() => sellerStore.DeleteItem(cards[2].Int("id")));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, refused.SqlState);
        Assert.NotNull(sellerStore.GetItem(cards[2].Int("id")));
        var unassignedBefore = buyerStore.ListItems("unassigned").Count;
        clock.AdvanceSeconds(61);
        buyer.Session.Fut.Market.ExpireDue();
        Assert.Equal(MarketState.Closed, buyerStore.MarketListing(guardedTrade)!.State);
        Assert.Equal(buyerCreditsBeforeBid - guarded.StartingBid, buyerStore.Credits());
        Assert.True(sellerStore.Credits() > sellerCreditsBeforeSale);
        Assert.Equal(unassignedBefore + 1, buyerStore.ListItems("unassigned").Count);
        Assert.Null(sellerStore.GetItem(cards[2].Int("id")));

        seller.Session.Fut.Market.ClearSold();
        Assert.DoesNotContain(sellerStore.OwnMarketListings(), listing => listing.TradeId == soldTrade);
    }

    [Fact]
    public void WatchlistKeepsOrderAndReportsWinningOutbidAndHubCounts()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var watcher = players.Acquire(PlayerIdentity.Of("222222"));
        using var rival = players.Acquire(PlayerIdentity.Of("333333"));
        var cards = ListablePlayers(seller.Session.Game.Store()).Take(2).ToList();
        var firstTrade = seller.Session.Fut.Market.List(cards[0].Int("id"), 500, 5000, 3600)["tradeId"].L();
        var secondTrade = seller.Session.Fut.Market.List(cards[1].Int("id"), 500, 5000, 3600)["tradeId"].L();
        watcher.Session.Fut.Market.Watch(secondTrade);
        watcher.Session.Fut.Market.Watch(999999999);
        watcher.Session.Fut.Market.Watch(firstTrade);
        Assert.Equal(new[] { secondTrade, firstTrade }, watcher.Session.Game.Store().MarketWatchIds());

        var firstListing = seller.Session.Game.Store().MarketListing(firstTrade)!;
        var secondListing = seller.Session.Game.Store().MarketListing(secondTrade)!;
        watcher.Session.Fut.Market.PlaceBid(firstTrade, firstListing.StartingBid);
        watcher.Session.Fut.Market.PlaceBid(secondTrade, secondListing.StartingBid);
        rival.Session.Fut.Market.PlaceBid(secondTrade,
            secondListing.StartingBid + rt.Game.Rules.Market.BidIncrement(secondListing.StartingBid));

        var watchlist = watcher.Session.Fut.Market.Watchlist();
        Assert.Equal(new[] { secondTrade, firstTrade }, watchlist["auctionInfo"].Objects().Select(row => row["tradeId"].L()));
        Assert.Equal(1, watchlist["winning"].L());
        Assert.Equal(1, watchlist["outbid"].L());
        var hub = watcher.Session.Fut.Club.Hub();
        Assert.Equal((2L, 1L, 1L), (hub["watchlist"]!["count"].L(), hub["watchlist"]!["winning"].L(), hub["watchlist"]!["outbid"].L()));

        watcher.Session.Fut.Market.Unwatch(secondTrade);
        Assert.Equal(new[] { firstTrade }, watcher.Session.Game.Store().MarketWatchIds());
    }

    [Fact]
    public void ResetAndDeleteCancelListingsAndRefundBidders()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var deletedSeller = players.Acquire(PlayerIdentity.Of("222222"));
        using var bidder = players.Acquire(PlayerIdentity.Of("333333"));
        var bidderStore = bidder.Session.Game.Store();
        var bidderCredits = bidderStore.Credits();

        var resetCards = ListablePlayers(seller.Session.Game.Store()).Take(2).ToList();
        var resetTrades = resetCards.Select(card => seller.Session.Fut.Market.List(card.Int("id"), 500, 5000, 3600)["tradeId"].L()).ToList();
        foreach (var resetTrade in resetTrades)
        {
            bidder.Session.Fut.Market.PlaceBid(resetTrade, seller.Session.Game.Store().MarketListing(resetTrade)!.StartingBid);
        }
        seller.Session.Game.Store().ClearClub(rt.Game.Config.InitialCredits);
        Assert.Equal(bidderCredits, bidderStore.Credits());
        Assert.All(resetTrades, resetTrade => Assert.Equal(MarketState.Cancelled, bidderStore.MarketListing(resetTrade)!.State));

        var deleteCard = ListablePlayer(deletedSeller.Session.Game.Store());
        var deleteTrade = deletedSeller.Session.Fut.Market.List(deleteCard.Int("id"), 500, 5000, 3600)["tradeId"].L();
        var deleteBid = deletedSeller.Session.Game.Store().MarketListing(deleteTrade)!.StartingBid;
        bidder.Session.Fut.Market.PlaceBid(deleteTrade, deleteBid);
        Assert.Equal(bidderCredits - deleteBid, bidderStore.Credits());
        Assert.True(TestClub.DeleteClub(rt.ConnectionString, deletedSeller.Session.Player.PersonaId));
        Assert.Equal(bidderCredits, bidderStore.Credits());
        Assert.Null(bidderStore.MarketListing(deleteTrade));
    }

    private static IEnumerable<JsonObject> ListablePlayers(TestClub store)
    {
        var squadItems = store.ListSquads().SelectMany(SquadDocuments.ItemIds).ToHashSet();
        return store.ListItems("club").Where(candidate =>
            ClubInventoryService.IsPlayer(candidate) && !candidate.Bool("untradeable") && !squadItems.Contains(candidate.Int("id")));
    }

    private static JsonObject ListablePlayer(TestClub store, params long[] excluded) =>
        ListablePlayers(store).First(item => !excluded.Contains(item.Int("id")));

    private static (int Status, JsonNode Body, FutResponse Response) Call(PlayerSession session, string method, string path, JsonNode? payload = null)
    {
        var response = session.Router.Handle(new FutRequest(method, path, Array.Empty<KeyValuePair<string, string>>(),
            payload is null ? Array.Empty<byte>() : FutJson.SerializeToUtf8(payload)));
        return (response.Status, FutJson.Parse(System.Text.Encoding.UTF8.GetString(response.Body))!, response);
    }

    [Fact]
    public void ReListAllPutsEveryExpiredCardBackAtItsOwnPricesAndDuration()
    {
        // Python server 5fd85ed: PUT auctionhouse/relist had no route, so nothing was re-listed.
        var clock = new MutableTime(1_700_000_000);
        using var rt = TestRuntime.Create(time: clock, marketTarget: 0);
        var store = rt.Game.Store();
        var first = ListablePlayer(store);
        var second = ListablePlayer(store, first.Int("id"));
        var price = (Start: rt.Fut.Market.List(first.Int("id"), 200, 1500, 3600)["auctionInfo"]![0]!["startingBid"].L(),
            BuyNow: rt.Fut.Market.List(second.Int("id"), 300, 2000, 10800)["auctionInfo"]![0]!["buyNowPrice"].L());
        clock.AdvanceSeconds(10801);
        rt.Fut.Market.ExpireDue();
        Assert.All(store.OwnMarketListings(), listing => Assert.Equal(MarketState.Expired, listing.State));

        var trades = rt.CallObject("PUT", $"{G}/auctionhouse/relist")["tradeIdList"].Objects().Select(trade => trade["id"].L()).ToList();

        // Each card goes back up at its own prices and duration, and its expired entry leaves the Transfer List.
        Assert.Equal(2, trades.Count);
        var live = store.OwnMarketListings();
        Assert.Equal(trades.Order(), live.Select(listing => listing.TradeId).Order());
        var byItem = live.ToDictionary(listing => listing.SellerItemId!.Value);
        Assert.Equal((price.Start, 3600L), (byItem[first.Int("id")].StartingBid, byItem[first.Int("id")].EndTime - byItem[first.Int("id")].CreatedAt));
        Assert.Equal((price.BuyNow, 10800L), (byItem[second.Int("id")].BuyNow, byItem[second.Int("id")].EndTime - byItem[second.Int("id")].CreatedAt));
        Assert.All(live, listing => Assert.Equal(MarketState.Active, listing.State));
        // Nothing left to re-list.
        Assert.Empty(rt.CallObject("PUT", $"{G}/auctionhouse/relist")["tradeIdList"].A());
    }

    [Fact]
    public void SnapToTheFifaPriceGridRoundsUpAndDownAcrossBandEdges()
    {
        var rules = GameRules.Load(TestRuntime.DataRoot).Market;
        foreach (var (price, up) in new (long, long)[]
                 { (999, 1000), (1000, 1000), (1001, 1100), (1267, 1300), (9951, 10_000), (9980, 10_000), (10_001, 10_250), (49_999, 50_000),
                   (50_001, 50_500), (99_999, 100_000), (100_001, 101_000), (0, 50), (-5, 50) })
        {
            Assert.Equal(up, rules.SnapUp(price));
            Assert.True(rules.IsOnGrid(up), $"{up} is off the grid");
        }
        foreach (var (price, down) in new (long, long)[] { (999, 950), (1267, 1200), (9999, 9900), (10_249, 10_000), (100_999, 100_000), (0, 50) })
        {
            Assert.Equal(down, rules.SnapDown(price));
        }
        Assert.False(rules.IsOnGrid(1267));
        Assert.False(rules.IsOnGrid(0));
        Assert.True(rules.IsOnGrid(1200));
        Assert.True(rules.IsOnGrid(1500));
        Assert.False(rules.IsOnGrid(1250));
    }

    [Fact]
    public void EveryAiListingSitsOnThePriceGridWithinBounds()
    {
        using var rt = TestRuntime.Create(marketTarget: 3000);
        var rules = rt.Game.Rules.Market;
        for (var pass = 0; pass < 20 && rt.Game.Store().CountActiveMarketListings(MarketListingKind.Ai) < 3000; pass++)
        {
            rt.Fut.Market.SeedAiListings();
        }
        var listings = rt.Game.Store().ActiveMarketListings();
        Assert.True(listings.Count >= 2000, $"only {listings.Count} AI listings seeded");
        foreach (var listing in listings)
        {
            var (low, high) = rt.Game.Cards.MarketPriceBounds(listing.ResourceId);
            Assert.True(rules.IsOnGrid(listing.StartingBid), $"start {listing.StartingBid} is off the grid");
            Assert.True(rules.IsOnGrid(listing.BuyNow), $"buy now {listing.BuyNow} is off the grid");
            Assert.InRange(listing.StartingBid, low, high);
            Assert.InRange(listing.BuyNow, listing.StartingBid, high);
        }
    }

    [Fact]
    public void PlayerListingsAndBidsMustLandOnThePriceGrid()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var buyer = players.Acquire(PlayerIdentity.Of("222222"));
        var store = seller.Session.Game.Store();
        var cards = ListablePlayers(store).Take(3).ToList();

        Assert.Equal("price is not a valid listing price", Assert.Throws<MarketException>(() =>
            seller.Session.Fut.Market.List(cards[0].Int("id"), 1267, 2000, 3600)).Message);
        Assert.Equal("price is not a valid listing price", Assert.Throws<MarketException>(() =>
            seller.Session.Fut.Market.List(cards[0].Int("id"), 500, 2001, 3600)).Message);
        Assert.Empty(store.OwnMarketListings());

        var trade = seller.Session.Fut.Market.List(cards[0].Int("id"), 1200, 2500, 3600)["tradeId"].L();
        var listing = store.MarketListing(trade)!;
        Assert.Equal((1200L, 2500L), (listing.StartingBid, listing.BuyNow));
        buyer.Session.Fut.Market.PlaceBid(trade, 1200);
        Assert.Equal("bid is not a valid bid amount", Assert.Throws<MarketException>(() =>
            buyer.Session.Fut.Market.PlaceBid(trade, 1337)).Message);
        buyer.Session.Fut.Market.PlaceBid(trade, 1300);
    }

    [Fact]
    public void SearchListsTheSoonestEndingFirstAcrossPages()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var buyer = players.Acquire(PlayerIdentity.Of("222222"));
        var store = seller.Session.Game.Store();
        // The highest rated card gets the longest duration, so retail order (soonest first) is the reverse of the old rating order.
        var cards = ListablePlayers(store).GroupBy(item => item.Int("rating")).Select(group => group.First())
            .OrderByDescending(item => item.Int("rating")).Take(4).ToList();
        Assert.Equal(4, cards.Count);
        var durations = new long[] { 28_800, 14_400, 7200, 3600 };
        var trades = cards.Select((card, index) => seller.Session.Fut.Market.List(card.Int("id"), 150, 1000, durations[index])["tradeId"].L()).ToList();
        var soonestFirst = Enumerable.Reverse(trades).ToList();

        JsonObject Page(int start) => buyer.Session.Fut.Market.Search(QueryString.Parse($"start={start}&num=2"));
        var first = Page(0);
        var second = Page(2);
        Assert.Equal(4, first["total"].L());
        Assert.Equal(soonestFirst, first["auctionInfo"].Objects().Concat(second["auctionInfo"].Objects()).Select(row => row["tradeId"].L()).ToList());
    }

    private sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTime(long unixSeconds) => _now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        public override DateTimeOffset GetUtcNow() => _now;

        public void AdvanceSeconds(long seconds) => _now = _now.AddSeconds(seconds);
    }

    private static string? Header(Aurora21.UltimateTeam.Core.Web.FutResponse response, string name) =>
        response.Headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
