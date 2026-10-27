using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalMarketTests
{
    private const string G="/ut/game/fifa21";
    private const long Now=1700000000;
    private sealed class Clock : TimeProvider
    {
        private long _seconds=Now;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Advance(long seconds)=>Interlocked.Add(ref _seconds,seconds);
    }
    private static TestRuntime Create(string? connection=null,long persona=910001,Action<string>? observer=null,TimeProvider? time=null,int target=0)
    {
        if(connection is null){connection=PostgresTestCluster.CreateInstalledDatabase();using var source=NpgsqlDataSource.Create(connection);RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();}
        return TestRuntime.Create(connectionString:connection,relationalSlice:true,time:time??new TestRuntime.FixedTime(Now),commandObserver:observer,marketTarget:target,
            prepareRoot:root=>{Directory.CreateDirectory(Path.Combine(root,"user-config"));File.WriteAllText(Path.Combine(root,"user-config","config.json"),new JsonObject{["persona_id"]=persona,["nucleus_id"]=persona+100000,["display_name"]="Market"+persona,["database"]=new JsonObject{["deadlock_retries"]=0}}.ToJsonString());});
    }
    private static Task<T> Read<T>(TestRuntime rt,Func<UnitOfWork,ValueTask<T>> action)=>rt.Game.ReadAsync(action,CancellationToken.None);
    private static Task<long> Scalar(TestRuntime rt,string sql)=>Read(rt,w=>w.ScalarAsync<long>(sql));
    private static async Task<JsonObject> AddPlayer(TestRuntime rt,int index=0,Action<JsonObject>? change=null)
    {
        var resource=rt.Game.Players.Entries.ElementAt(index).Key;
        return await rt.Game.RunAsync("market-test-player",async w=>{var item=rt.Fut.Items.BuildPlayer(resource,await w.NextItemIdAsync(),"club");item["untradeable"]=false;change?.Invoke(item);await w.SaveItemAsync(item);return item;},CancellationToken.None);
    }
    private static JsonObject ListBody(long item,long duration=3600)=>new(){["itemId"]=item,["startingBid"]=150,["buyNowPrice"]=1000,["duration"]=duration};
    private static long List(TestRuntime rt,JsonObject item,long duration=3600)=>rt.CallObject("POST",G+"/auctionhouse",ListBody(item.IntKeep("id"),duration)).IntKeep("tradeId");
    private static Task<MarketListing> Listing(TestRuntime rt,long trade)=>Read(rt,async w=>(await w.MarketListingAsync(trade))!);
    /// <summary>Coins the transfer tax has deleted from every club: each closed sale's price less what its seller received.</summary>
    private static async Task<long> Taxed(TestRuntime rt)=>(await Read(rt,w=>w.QueryAsync("SELECT sold_price FROM ultimateteam.market_listings WHERE state='closed' AND seller_club_id IS NOT NULL",r=>r.GetInt64(0))))
        .Sum(price=>price-rt.Game.Rules.Market.SellerProceeds(price));

    [Fact]
    public async Task WatchAndTransferPagesPreserveGlobalCountsAndCombinedOrder()
    {
        var commands = new List<string>();
        using var rt = Create(observer: commands.Add);
        var listed = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var item = await AddPlayer(rt, i);
            listed.Add(List(rt, item));
        }
        for (var i = 4; i < 7; i++)
            await AddPlayer(rt, i, item => item["pile"] = "trade");
        foreach (var id in listed)
            rt.Call("POST", G + "/watchlist", new JsonObject { ["tradeId"] = id });
        var full = rt.CallObject("GET", G + "/auctionhouse");
        Assert.Equal(7, full.IntKeep("count"));
        var fullRows = full["auctionInfo"]!.AsArray();
        var measuredRows = new List<(string Query, long Rows)>();
        var measuring = new AsyncLocal<bool> { Value = true };
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == DatabaseTimings.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!measuring.Value || instrument.Name != "ultimateteam.database.query.rows") return;
            foreach (var tag in tags) if (tag.Key == "query") measuredRows.Add((tag.Value!.ToString()!, value));
        });
        listener.Start();
        foreach (var (offset, count) in new[] { (0, 2), (3, 3), (4, 2), (6, 5), (100, 2), (0, 0) })
        {
            commands.Clear(); measuredRows.Clear();
            var page = rt.CallObject("GET", G + $"/tradepile?offset={offset}&count={count}");
            Assert.Equal(7, page.IntKeep("count"));
            Assert.Equal(full.IntKeep("selling"), page.IntKeep("selling"));
            Assert.Equal(full.IntKeep("sold"), page.IntKeep("sold"));
            var expected = new JsonArray(fullRows.Skip(offset).Take(count).Select(n => n!.DeepClone()).ToArray());
            Assert.True(JsonNode.DeepEquals(expected, page["auctionInfo"]));
            Assert.Contains(commands, sql => sql.Contains("-- query: market.transfer-list.page", StringComparison.Ordinal));
            Assert.All(measuredRows.Where(r => r.Query is "market.snapshots" or "inventory.headers"), r => Assert.InRange(r.Rows, 0, count));
        }
        var watch = rt.CallObject("GET", G + "/watchlist?count=100");
        commands.Clear(); measuredRows.Clear();
        var watchPage = rt.CallObject("GET", G + "/watchlist?offset=1&count=2");
        Assert.Equal(4, watchPage.IntKeep("count"));
        Assert.True(JsonNode.DeepEquals(new JsonArray(watch["auctionInfo"]!.AsArray().Skip(1).Take(2).Select(n => n!.DeepClone()).ToArray()), watchPage["auctionInfo"]));
        Assert.Equal(watch.IntKeep("winning"), watchPage.IntKeep("winning"));
        Assert.Equal(watch.IntKeep("outbid"), watchPage.IntKeep("outbid"));
        Assert.Contains(commands, sql => sql.Contains("-- query: market.watchlist.page", StringComparison.Ordinal));
        Assert.Contains(measuredRows, r => r.Query == "market.snapshots" && r.Rows == 2);
        Assert.All(measuredRows.Where(r => r.Query == "market.snapshots"), r => Assert.Equal(2, r.Rows));
        var empty = rt.CallObject("GET", G + "/watchlist?offset=100&count=2");
        Assert.Empty(empty["auctionInfo"]!.AsArray());
        Assert.Equal(4, empty.IntKeep("count"));
    }

    [Fact]
    public async Task LoansCannotBeListedAndChemistryStyleUsesNamedForeignKey()
    {
        using var rt=Create();
        var loan=await AddPlayer(rt,change:i=>{i["loans"]=5;i["untradeable"]=false;});
        var error=await Assert.ThrowsAsync<MarketException>(()=>rt.Fut.Market.ListAsync(loan.IntKeep("id"),150,1000,3600).AsTask());
        Assert.Equal("loan cards cannot be listed",error.Message);
        Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM ultimateteam.market_listings"));
        var item=await AddPlayer(rt,change:i=>i["playStyle"]=266); // Hunter from current rules.
        var trade=List(rt,item);
        Assert.Equal(266,(await Listing(rt,trade)).Item.IntKeep("playStyle"));
        Assert.Equal(0,(await Listing(rt,trade)).Item.IntKeep("loans"));
        Assert.Equal(1,await Scalar(rt,"SELECT count(*) FROM ultimateteam.market_player_snapshots p JOIN ultimateteam.consumable_definitions c ON c.definition_id=p.chemistry_style_definition_id WHERE c.consumable_kind='chemistry_style' AND c.native_effect_id=266"));
        Assert.Equal(24,await Scalar(rt,"SELECT count(*) FROM ultimateteam.consumable_definitions WHERE consumable_kind='chemistry_style'"));
    }

    [Fact]
    public async Task RoutesKeepImmutableSnapshotsAndReopen()
    {
        using var rt=Create();
        var item=await AddPlayer(rt,change:i=>{i["contracts"]=19;i["contract"]=19;i["statsList"]=rt.Game.Cards.StatRows(new JsonArray(new JsonObject{["index"]=0,["value"]=7}));i["extraLocalTag"]="keep";});
        var actual=rt.CallObject("POST",G+"/auctionhouse",ListBody(item.IntKeep("id")));
        var trade=actual.IntKeep("tradeId");
        Assert.Equal(trade,await Scalar(rt,"SELECT market_listing_id FROM ultimateteam.market_listings"));
        rt.Call("POST",G+"/watchlist",new JsonObject{["tradeId"]=trade});
        foreach(var path in new[]{"/auctionhouse","/watchlist","/trade/status/lite?tradeIds="+trade+","+trade+",999999","/transfermarket?type=player"})
            Assert.NotNull(rt.Call("GET",G+path));
        var snapshot=(await Listing(rt,trade)).Item;
        await rt.Game.RunAsync("mutate-owned",async w=>{var owned=(await w.GetItemAsync(item.IntKeep("id")))!;owned["contracts"]=25;owned["contract"]=25;await w.SaveItemAsync(owned);},CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(snapshot,(await Listing(rt,trade)).Item));
        Assert.Equal(1,await Scalar(rt,"SELECT count(*) FROM ultimateteam.market_player_snapshots WHERE match_stats->'current'->>0='7' AND jsonb_array_length(match_stats->'lifetime')=5"));
        Assert.Equal(0,await Scalar(rt,"SELECT count(*) FROM pg_tables WHERE schemaname='public'"));
        var saved=rt.Call("GET",G+"/watchlist");var connection=rt.Detach();using var reopened=Create(connection);
        Assert.True(JsonNode.DeepEquals(saved,reopened.Call("GET",G+"/watchlist")));
    }

    [Fact]
    public async Task HubActiveCountIsSharedAndReusedWhileItsClubCountsStayLive()
    {
        using var seller=Create();var other=Create(seller.ConnectionString,910002);
        try
        {
            var cache=new ActiveListingCountCache();
            var first=await Read(seller,w=>cache.CountersAsync(w,60_000));
            Assert.Equal(first,await Read(seller,w=>w.MarketCountersAsync()));
            List(seller,await AddPlayer(seller));
            var reused=await Read(seller,w=>cache.CountersAsync(w,60_000));
            // The whole-market count is reused within its lifetime; this club's own counts are not.
            Assert.Equal(first.ActiveListings,reused.ActiveListings);
            Assert.Equal(first.Selling+1,reused.Selling);
            Assert.Equal(first.ActiveListings+1,(await Read(seller,w=>new ActiveListingCountCache().CountersAsync(w,60_000))).ActiveListings);
            Assert.Equal(first.ActiveListings+1,(await Read(seller,w=>cache.CountersAsync(w,0))).ActiveListings);
            Assert.Equal(await Read(seller,w=>w.MarketCountersAsync()),await Read(seller,w=>new ActiveListingCountCache().CountersAsync(w,60_000)));
            // Every player context of one process shares the root's cache.
            using var player=await seller.Game.ForPlayerAsync(PlayerIdentity.Of("333333"));
            Assert.Same(seller.Game.ActiveListingCount,player.ActiveListingCount);
        }
        finally{other.Detach();}
    }

    [Fact]
    public async Task MarketViewerMatchesTheSeparateCreditAndWatchReads()
    {
        using var seller=Create();var watcher=Create(seller.ConnectionString,910002);
        try
        {
            var first=List(seller,await AddPlayer(seller));var second=List(seller,await AddPlayer(seller,1));
            await watcher.Fut.Market.WatchAsync(second);await watcher.Fut.Market.WatchAsync(first);
            foreach(var rt in new[]{seller,watcher})
            {
                var viewer=await Read(rt,w=>w.MarketViewerAsync());
                Assert.Equal(await Read(rt,w=>w.CreditsAsync()),viewer.Credits);
                Assert.Equal((await Read(rt,w=>w.MarketWatchIdsAsync())).Order(),viewer.Watched.Order());
            }
            Assert.Equal(new[]{first,second}.Order(),(await Read(watcher,w=>w.MarketViewerAsync())).Watched.Order());
            Assert.Empty((await Read(seller,w=>w.MarketViewerAsync())).Watched);
            var missing=await Read(seller,w=>RelationalMarketRepository.MarketViewerAsync(ClubScopedUnitOfWork.For(w,long.MaxValue)));
            Assert.Equal(0,missing.Credits);Assert.Empty(missing.Watched);
        }
        finally{watcher.Detach();}
    }

    private static async Task<JsonObject> AddKit(TestRuntime rt,bool untradeable=false)
    {
        // A kit as a store pack drops it: the drop table's rating and rarity, tradeable unless the grant says otherwise.
        var kit=rt.Game.Live.Snapshot.Drops.Pool(NonPlayerDropTable.Kit).First();
        return await rt.Game.RunAsync("market-test-kit",async w=>{
            var item=ItemFactory.KitItem(await w.NextItemIdAsync(),Now,kit.Resource,"free","club",kit.Team,kit.KitType,2+kit.KitType,kit.Name);
            item["rating"]=kit.Rating;item["rareflag"]=kit.Rareflag;
            if(!untradeable)ItemFactory.MakeTradeable(item);
            await w.SaveItemAsync(item);return item;},CancellationToken.None);
    }

    [Fact]
    public async Task ATradeableKitListsIsFoundAsAClubItemAndSellsToAnotherClub()
    {
        using var seller=Create();var buyer=Create(seller.ConnectionString,910002);
        try
        {
            var kit=await AddKit(seller);
            var trade=List(seller,kit);
            Assert.True(trade>0);
            Assert.Equal("trade",(await Read(seller,w=>w.GetItemAsync(kit.IntKeep("id"))))!.Text("pile"));
            long[] Found(string query)=>buyer.CallObject("GET",G+"/transfermarket?"+query)["auctionInfo"]!.AsArray().Select(a=>a!.AsObject().IntKeep("tradeId")).ToArray();
            Assert.Contains(trade,Found("type=clubInfo&cat=kit"));
            Assert.Contains(trade,Found("type=kit"));
            Assert.Contains(trade,Found("type=clubInfo"));
            Assert.DoesNotContain(trade,Found("type=clubInfo&cat=ball"));
            Assert.DoesNotContain(trade,Found("type=player"));
            // The listing shows the kit's own card: FIFA picks Home/Away/Third art by category, a player card was a blank "KIT".
            var card=buyer.CallObject("GET",G+"/transfermarket?type=clubInfo&cat=kit")["auctionInfo"]!.AsArray()
                .Select(a=>a!.AsObject()).Single(a=>a.IntKeep("tradeId")==trade)["itemData"]!.AsObject();
            Assert.Equal(("kit",kit.IntKeep("resourceId"),2+kit.Int("kitType")),(card.Text("itemType"),card.IntKeep("resourceId"),card.Int("category")));
            Assert.False(card.ContainsKey("pile"));
            Assert.Equal(200,buyer.CallRaw("POST",G+"/trade/"+trade+"/buy").Status);
            var bought=Assert.Single(await Read(buyer,w=>w.ListItemsAsync("unassigned")));
            Assert.Equal(("kit",kit.IntKeep("resourceId"),false),(bought.Text("itemType"),bought.IntKeep("resourceId"),bought.Bool("untradeable")));
            Assert.Equal(2+bought.Int("kitType"),bought.Int("category"));
            Assert.Equal(2,bought.Int("owners"));
            Assert.Null(await Read(seller,w=>w.GetItemAsync(kit.IntKeep("id"))));

            // A kit granted untradeable (onboarding, objectives, the Match Pack) stays off the market.
            var locked=await AddKit(seller,untradeable:true);
            await Assert.ThrowsAsync<MarketException>(()=>seller.Fut.Market.ListAsync(locked.IntKeep("id"),150,1000,3600).AsTask());
        }
        finally{buyer.Detach();}
    }

    [Fact]
    public async Task BidsRefundEscrowAndBuyNowCreatesIndependentBuyerItem()
    {
        using var seller=Create();var buyer=Create(seller.ConnectionString,910002);var bidder=Create(seller.ConnectionString,910003);
        try
        {
            var item=await AddPlayer(seller,change:i=>{i["contract"]=17;i["contracts"]=17;i["assists"]=3;i["lifetimeAssists"]=3;});
            var buyerOriginal=await AddPlayer(buyer,1);
            // Each club issues ids from its own band (migration 045), so the two clubs' first cards no longer share an id.
            Assert.NotEqual(item.IntKeep("id"),buyerOriginal.IntKeep("id"));
            var sellerInternal=await Scalar(seller,"SELECT id FROM ultimateteam.items WHERE club_id=@club");var trade=List(seller,item);var listing=await Listing(seller,trade);
            var sellerCoins=await Read(seller,w=>w.CreditsAsync());var buyerCoins=await Read(buyer,w=>w.CreditsAsync());var bidderCoins=await Read(bidder,w=>w.CreditsAsync());
            await bidder.Fut.Market.PlaceBidAsync(trade,listing.StartingBid);Assert.Equal(bidderCoins-listing.StartingBid,await Read(bidder,w=>w.CreditsAsync()));
            var increased=listing.StartingBid+seller.Game.Rules.Market.BidIncrement(listing.StartingBid);
            Assert.Equal(200,buyer.CallRaw("POST",G+"/trade/"+trade+"/offer",new JsonObject{["bid"]=increased}).Status);Assert.Equal(bidderCoins,await Read(bidder,w=>w.CreditsAsync()));
            var purchase=buyer.CallRaw("POST",G+"/trade/"+trade+"/buy");Assert.Equal(200,purchase.Status);Assert.Equal("buyNow",purchase.Body.AsObject().Arr("auctionInfo")![0]!.AsObject().Str("bidState"));
            Assert.Equal(buyerCoins-listing.BuyNow,await Read(buyer,w=>w.CreditsAsync()));Assert.Equal(sellerCoins+seller.Game.Rules.Market.SellerProceeds(listing.BuyNow),await Read(seller,w=>w.CreditsAsync()));Assert.Equal(950,seller.Game.Rules.Market.SellerProceeds(1000));
            Assert.Null(await Read(seller,w=>w.GetItemAsync(item.IntKeep("id"))));Assert.NotNull(await Read(buyer,w=>w.GetItemAsync(buyerOriginal.IntKeep("id"))));
            var bought=Assert.Single(await Read(buyer,w=>w.ListItemsAsync("unassigned")));Assert.NotEqual(item.IntKeep("id"),bought.IntKeep("id"));Assert.Equal(17,bought.Int("contract"));Assert.Equal(0,bought.Int("assists"));Assert.Equal(3,bought.Int("lifetimeAssists"));Assert.Equal(2,bought.Int("owners"));Assert.Equal(0,bought.Int("loyaltyBonus"));
            Assert.NotEqual(sellerInternal,await Scalar(buyer,"SELECT id FROM ultimateteam.items WHERE club_id=@club AND pile='unassigned'"));
            Assert.Equal(MarketState.Closed,(await Listing(seller,trade)).State);Assert.Equal(item.IntKeep("id"),(await Listing(seller,trade)).Item.IntKeep("id"));
            Assert.Contains(trade,await Read(buyer,w=>w.MarketWatchIdsAsync()));Assert.Equal(1,(await buyer.Fut.Market.CountersAsync()).WatchCount);
            await Assert.ThrowsAsync<MarketException>(()=>buyer.Fut.Market.BuyNowAsync(trade).AsTask());
        }
        finally{bidder.Detach();buyer.Detach();}
    }

    /// <summary>
    /// Migration 164: a card bought before retail loyalty carried the seller's club stats and loyalty bonus. The sale
    /// snapshot's club stats move off the copy (lifetime keeps them), and a bought card is loyal only after ten own games.
    /// </summary>
    [Fact]
    public async Task MigrationMovesASellersClubStatsOffABoughtCard()
    {
        using var seller=Create();var buyer=Create(seller.ConnectionString,910002);
        try
        {
            var item=await AddPlayer(seller,change:i=>{i["statsList"]=seller.Game.Cards.StatRows(new JsonArray(new JsonObject{["index"]=0,["value"]=12},new JsonObject{["index"]=1,["value"]=5}));i["lifetimeStats"]=i["statsList"]!.DeepClone();i["assists"]=4;i["lifetimeAssists"]=4;});
            var trade=List(seller,item);
            Assert.Equal(200,buyer.CallRaw("POST",G+"/trade/"+trade+"/buy").Status);
            var bought=Assert.Single(await Read(buyer,w=>w.ListItemsAsync("unassigned")));
            Assert.Equal(0,bought.Int("loyaltyBonus"));Assert.Equal(0,bought.Int("assists"));Assert.Equal(4,bought.Int("lifetimeAssists"));
            // What a buy stored before this change, plus two games, a goal and an assist for the buyer.
            await buyer.Game.RunAsync("old-buy",w=>w.ExecuteAsync("UPDATE ultimateteam.player_items p SET match_stats='{\"current\":[14,6,0,0,0],\"lifetime\":[14,6,0,0,0]}'::jsonb,assists=5,lifetime_assists=5,loyalty_bonus=1 FROM ultimateteam.items i WHERE i.id=p.item_id AND i.club_id=@club AND i.pile='unassigned'"),CancellationToken.None);
            await buyer.Game.RunAsync("migration-164",async w=>
            {
                await w.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=164");
                using var stream=typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/164-retail-loyalty.sql")!;
                using var reader=new StreamReader(stream);
                return await w.ExecuteAsync(await reader.ReadToEndAsync());
            },CancellationToken.None);
            bought=Assert.Single(await Read(buyer,w=>w.ListItemsAsync("unassigned")));
            long Stat(string list,int index)=>bought[list]!.AsArray().OfType<JsonObject>().Single(row=>row.Int("index")==index).Int("value");
            Assert.Equal(2,Stat("statsList",0));Assert.Equal(1,Stat("statsList",1));Assert.Equal(14,Stat("lifetimeStats",0));Assert.Equal(6,Stat("lifetimeStats",1));
            Assert.Equal(1,bought.Int("assists"));Assert.Equal(5,bought.Int("lifetimeAssists"));Assert.Equal(0,bought.Int("loyaltyBonus"));
        }
        finally{buyer.Detach();}
    }

    [Fact]
    public async Task AWonCopyOfAnOwnedCardIsListedAsADuplicate()
    {
        // 10-07 report: a bought duplicate offered Send to Club; PUT /item kept it in Unassigned and the next pack was refused.
        using var seller=Create();var buyer=Create(seller.ConnectionString,910002);
        try
        {
            var owned=await AddPlayer(buyer);var trade=List(seller,await AddPlayer(seller));var fresh=List(seller,await AddPlayer(seller,1));
            var bought=buyer.CallObject("POST",G+"/trade/"+trade+"/offer",new JsonObject{["bid"]=(await Listing(seller,trade)).BuyNow});
            var copy=Assert.Single(await Read(buyer,w=>w.ListItemsAsync("unassigned"))).IntKeep("id");
            var entry=Assert.Single(bought.Arr("duplicateItemIdList")!)!.AsObject();
            Assert.Equal(copy,entry.Int("itemId"));Assert.Equal(owned.IntKeep("id"),entry.Int("duplicateItemId"));
            Assert.True(JsonNode.DeepEquals(bought["duplicateItemIdList"],buyer.CallObject("GET",G+"/watchlist")["duplicateItemIdList"]));
            // A card the club does not own is new, and a duplicate that has left Unassigned is no longer listed.
            var other=buyer.CallObject("POST",G+"/trade/"+fresh+"/offer",new JsonObject{["bid"]=(await Listing(seller,fresh)).BuyNow});
            Assert.Null(other["reason"]);Assert.Empty(other.Arr("duplicateItemIdList")!);
            buyer.Call("PUT",G+"/item",new JsonObject{["itemData"]=new JsonArray(new JsonObject{["id"]=copy,["pile"]="trade"})});
            Assert.Empty(buyer.CallObject("GET",G+"/watchlist").Arr("duplicateItemIdList")!);
        }
        finally{buyer.Detach();}
    }

    [Fact]
    public async Task TheTransferListNamesItsDuplicatesOfClubCards()
    {
        // 10-07 report: Send to Club from the transfer list treated a duplicate as new, the same 471 path as a bought one.
        using var rt=Create();
        var owned=await AddPlayer(rt);var copy=await AddPlayer(rt,change:i=>i["pile"]="trade");var listed=await AddPlayer(rt);await AddPlayer(rt,1,i=>i["pile"]="trade");
        List(rt,listed);
        var duplicates=rt.CallObject("GET",G+"/tradePile").Arr("duplicateItemIdList")!.OfType<JsonObject>().ToList();
        Assert.Equal(new[]{copy.IntKeep("id"),listed.IntKeep("id")}.Order(),duplicates.Select(entry=>entry.Int("itemId")).Order());
        Assert.All(duplicates,entry=>Assert.Equal(owned.IntKeep("id"),entry.Int("duplicateItemId")));
    }

    [Fact]
    public async Task ExpirySettlesBidOnceReturnsUnsoldAndClearPreservesSnapshots()
    {
        var clock=new Clock();using var seller=Create(time:clock);var buyer=Create(seller.ConnectionString,910002,time:clock);
        try
        {
            var sold=await AddPlayer(seller);var unsold=await AddPlayer(seller,1);var soldTrade=List(seller,sold,60);var unsoldTrade=List(seller,unsold,60);var price=(await Listing(seller,soldTrade)).StartingBid;
            await buyer.Fut.Market.PlaceBidAsync(soldTrade,price);clock.Advance(61);
            Assert.Equal(2,await seller.Fut.Market.ExpireDueAsync());Assert.Equal(0,await seller.Fut.Market.ExpireDueAsync());Assert.Equal(MarketState.Closed,(await Listing(seller,soldTrade)).State);
            Assert.Equal(MarketState.Expired,(await Listing(seller,unsoldTrade)).State);var returned=(await Read(seller,w=>w.GetItemAsync(unsold.IntKeep("id"))))!;Assert.Equal("trade",returned.Str("pile"));Assert.Equal("free",returned.Str("itemState"));
            await seller.Fut.Market.ClearTradeAsync(unsoldTrade);Assert.Equal("club",(await Read(seller,w=>w.GetItemAsync(unsold.IntKeep("id"))))!.Str("pile"));
            await seller.Fut.Market.ClearSoldAsync();Assert.Empty(await Read(seller,w=>w.OwnMarketListingsAsync()));Assert.Equal(2,await Scalar(seller,"SELECT count(*) FROM ultimateteam.market_item_snapshots"));
        }
        finally{buyer.Detach();}
    }

    [Fact]
    public async Task LateBidResetsTheClockToTheExtension()
    {
        var clock=new Clock();using var seller=Create(time:clock);var bidder=Create(seller.ConnectionString,910002,time:clock);var rival=Create(seller.ConnectionString,910003,time:clock);
        try
        {
            var extension=seller.Game.Rules.Market.Listings.BidExtensionSeconds;Assert.Equal(30,extension);
            var trade=List(seller,await AddPlayer(seller),60);var listing=await Listing(seller,trade);
            // With more than the extension left a bid leaves the clock alone.
            await bidder.Fut.Market.PlaceBidAsync(trade,listing.StartingBid);Assert.Equal(listing.EndTime,(await Listing(seller,trade)).EndTime);
            clock.Advance(50);var raised=listing.StartingBid+seller.Game.Rules.Market.BidIncrement(listing.StartingBid);
            await rival.Fut.Market.PlaceBidAsync(trade,raised);var extended=await Listing(seller,trade);Assert.Equal(Now+50+extension,extended.EndTime);
            Assert.Equal(extension,rival.CallObject("GET",G+"/trade/status/lite?tradeIds="+trade).Arr("auctionInfo")![0]!.AsObject().Int("expires"));
            // Past the original end the listing is still live and does not expire until the extension runs out.
            clock.Advance(20);Assert.Equal(0,await seller.Fut.Market.ExpireDueAsync());Assert.Equal(MarketState.Active,(await Listing(seller,trade)).State);
            clock.Advance(11);Assert.Equal(1,await seller.Fut.Market.ExpireDueAsync());var sold=await Listing(seller,trade);Assert.Equal(MarketState.Closed,sold.State);Assert.Equal(raised,sold.SoldPrice);
        }
        finally{rival.Detach();bidder.Detach();}
    }

    [Fact]
    public async Task SearchFiltersPagingAndTypedPositionOverrideMatchOracle()
    {
        using var rt=Create();var resources=rt.Game.Players.Entries.Take(20).Select(p=>p.Key).ToArray();
        await rt.Game.RunAsync("search-fixture",async w=>{foreach(var (resource,index) in resources.Select((r,i)=>(r,i)))
        {var item=rt.Fut.Items.BuildPlayer(resource,800000001+index,"market");if(index==0)item["preferredPosition"]="LW";if(index==1)item["preferredPosition"]=null;if(index==2)item.Remove("preferredPosition");
        await w.InsertMarketListingAsync(MarketListingKind.Ai,"Local AI",null,resource,item,item.Int("rating"),150,1000+index*100,Now,Now+3600);}},CancellationToken.None);
        var all=await Read(rt,w=>w.ActiveMarketListingsAsync());
        var first=all[0].Item;
        foreach(var text in new[]{"","type=manager","pos=LW","pos=LM-LW","pos=-","zone=attack","zone=unknown","lev=gold","lev=silver","lev=bronze","rare=SP","rarityIds=0,1","minb=1500&maxb=2500","micr=150&macr=500","start=3&num=7","start=999&num=3","definitionId="+resources[0],"assetId="+first.Int("assetId"),"nation="+first.Int("nation"),"league="+first.Int("leagueId"),"club="+first.Int("teamid")})
        {
            var query=QueryString.Parse(text);var expected=all.Where(l=>rt.Fut.Market.MatchesInMemory(l,query)).OrderBy(l=>l.EndTime).ThenBy(l=>l.TradeId).ToList();
            var page=await Read(rt,w=>rt.Fut.Market.SearchListingsAsync(w,query));Assert.Equal(expected.Count,page.Total);
            var offset=text.StartsWith("start=3")?3:text.StartsWith("start=999")?999:0;var limit=text.Contains("num=7")?7:text.Contains("num=3")?3:rt.Game.Rules.Market.Listings.SearchPageSize;
            Assert.Equal(expected.Skip(offset).Take(limit).Select(l=>l.TradeId),page.Listings.Select(l=>l.TradeId));
        }
        var response=rt.CallRaw("GET",G+"/transfermarket?type=player");Assert.Equal(200,response.Status);Assert.Equal(20,response.Body.AsObject().Int("total"));
    }

    [Fact]
    public async Task LateFailureRollsBackRefundBalancesBuyerAllocatorAndSellerDeletion()
    {
        string? fail=null;using var seller=Create(observer:sql=>{if(fail is not null&&sql.Contains(fail,StringComparison.Ordinal))throw new InvalidOperationException("synthetic market failure");});var buyer=Create(seller.ConnectionString,910002);var bidder=Create(seller.ConnectionString,910003);
        try
        {
            var item=await AddPlayer(seller);fail="INSERT INTO ultimateteam.market_player_snapshots";await Assert.ThrowsAsync<InvalidOperationException>(()=>seller.Fut.Market.ListAsync(item.IntKeep("id"),150,1000,3600).AsTask());fail=null;
            Assert.Equal("club",(await Read(seller,w=>w.GetItemAsync(item.IntKeep("id"))))!.Str("pile"));Assert.Equal(0,await Scalar(seller,"SELECT count(*) FROM ultimateteam.market_listings"));
            var trade=List(seller,item);var listing=await Listing(seller,trade);await bidder.Fut.Market.PlaceBidAsync(trade,listing.StartingBid);
            var balances=await Read(seller,w=>w.QueryAsync("SELECT credits FROM ultimateteam.clubs ORDER BY id",r=>r.GetInt64(0)));var next=await Scalar(buyer,"SELECT next_item_id FROM ultimateteam.clubs WHERE id=@club");
            // Fail after refund, buyer grant and seller deletion, before the listing closes.
            fail="UPDATE ultimateteam.market_listings SET state = 'closed'";
            var clockConnection=seller.ConnectionString;
            // Reopen the buyer with the fault observer, preserving its database.
            buyer.Detach();buyer=Create(clockConnection,910002,observer:sql=>{if(fail is not null&&sql.Contains(fail,StringComparison.Ordinal))throw new InvalidOperationException("synthetic late sale failure");});
            await Assert.ThrowsAsync<InvalidOperationException>(()=>buyer.Fut.Market.BuyNowAsync(trade).AsTask());fail=null;
            Assert.Equal(balances,await Read(seller,w=>w.QueryAsync("SELECT credits FROM ultimateteam.clubs ORDER BY id",r=>r.GetInt64(0))));Assert.Equal(next,await Scalar(buyer,"SELECT next_item_id FROM ultimateteam.clubs WHERE id=@club"));
            Assert.NotNull(await Read(seller,w=>w.GetItemAsync(item.IntKeep("id"))));Assert.Empty(await Read(buyer,w=>w.ListItemsAsync()));Assert.Equal(MarketState.Active,(await Listing(seller,trade)).State);
        }
        finally{bidder.Detach();buyer.Detach();}
    }

    [Fact]
    public async Task ConcurrentBuyersAndOppositeClubSalesSettleWithoutDeadlock()
    {
        using var seller=Create();var buyer=Create(seller.ConnectionString,910002);var other=Create(seller.ConnectionString,910003);
        try
        {
            var item=await AddPlayer(seller);var trade=List(seller,item);
            async Task<bool> Buy(TestRuntime rt){try{await rt.Fut.Market.BuyNowAsync(trade);return true;}catch(MarketException){return false;}}
            var results=await Task.WhenAll(Task.Run(()=>Buy(buyer)),Task.Run(()=>Buy(other)));Assert.Single(results,r=>r);
            Assert.Equal(1,await Scalar(seller,"SELECT count(*) FROM ultimateteam.items WHERE pile='unassigned'"));
            var a=List(seller,await AddPlayer(seller,1));var b=List(buyer,await AddPlayer(buyer,2));
            await Task.WhenAll(Task.Run(()=>buyer.Fut.Market.BuyNowAsync(a).AsTask()),Task.Run(()=>seller.Fut.Market.BuyNowAsync(b).AsTask()));
            Assert.Equal(MarketState.Closed,(await Listing(seller,a)).State);Assert.Equal(MarketState.Closed,(await Listing(seller,b)).State);
            Assert.Equal(3,await Scalar(seller,"SELECT count(*) FROM ultimateteam.market_listings WHERE state='closed'"));
            Assert.Equal(3*seller.Game.Config.InitialCredits-await Taxed(seller),await Scalar(seller,"SELECT sum(credits) FROM ultimateteam.clubs"));
        }
        finally{other.Detach();buyer.Detach();}
    }

    [Fact]
    public async Task IndependentSaleCompletesWhileAnotherListingIsLocked()
    {
        using var seller = Create();
        var otherSeller = Create(seller.ConnectionString, 910002);
        var buyer = Create(seller.ConnectionString, 910003);
        var otherBuyer = Create(seller.ConnectionString, 910004);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? holder = null;
        Task? waiting = null;
        try
        {
            var a = List(seller, await AddPlayer(seller));
            var b = List(otherSeller, await AddPlayer(otherSeller, 1));
            holder = seller.Game.RunAsync("hold-one-listing", async work =>
            {
                await RelationalMarketRepository.LockMarketSharedAsync(work);
                Assert.NotNull(await work.MarketListingAsync(a, forUpdate: true));
                locked.SetResult();
                await release.Task;
            }, CancellationToken.None);
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(await otherSeller.Game.RunAsync("maintenance-excluded", work => work.TryLockMarketAsync(), CancellationToken.None));
            waiting = buyer.Fut.Market.BuyNowAsync(a).AsTask();
            await otherBuyer.Fut.Market.BuyNowAsync(b).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(waiting.IsCompleted);
            Assert.Equal(MarketState.Closed, (await Listing(otherSeller, b)).State);
            release.SetResult();
            await holder;
            await waiting;
            Assert.Equal(MarketState.Closed, (await Listing(seller, a)).State);
            Assert.Equal(2, await Scalar(seller, "SELECT count(*) FROM ultimateteam.items WHERE pile='unassigned'"));
            Assert.Equal(4 * seller.Game.Config.InitialCredits - await Taxed(seller), await Scalar(seller, "SELECT sum(credits) FROM ultimateteam.clubs"));
        }
        finally
        {
            release.TrySetResult();
            if (holder is not null) await holder;
            if (waiting is not null) await waiting;
            otherBuyer.Detach(); buyer.Detach(); otherSeller.Detach();
        }
    }

    [Fact]
    public async Task StandaloneWatchLocksListingBeforePurchaseAndDeferredClubChecks()
    {
        using var seller = Create();
        var buyer = Create(seller.ConnectionString, 910002);
        using var release = new ManualResetEventSlim();
        var reachedInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = Create(seller.ConnectionString, 910002, observer: sql =>
        {
            if (!sql.Contains("INSERT INTO ultimateteam.market_watch", StringComparison.Ordinal)) return;
            reachedInsert.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Synthetic watch hold expired");
        });
        Task? watch = null; Task? purchase = null;
        try
        {
            var trade = List(seller, await AddPlayer(seller));
            watch = Task.Run(() => watcher.Fut.Market.WatchAsync(trade));
            await reachedInsert.Task.WaitAsync(TimeSpan.FromSeconds(20));
            purchase = buyer.Fut.Market.BuyNowAsync(trade).AsTask();
            var blocked = false;
            for (var poll = 0; poll < 100 && !purchase.IsCompleted; poll++)
            {
                blocked = await Read(seller, work => work.ScalarAsync<bool>("""
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                      AND wait_event_type='Lock' AND query LIKE '%WHERE l.market_listing_id =%FOR UPDATE%')
                    """));
                if (blocked) break;
                await Task.Delay(25);
            }
            Assert.True(blocked, "Purchase must wait on the watch listing lock before acquiring club rows.");
            release.Set();
            await watch;
            await purchase;
            Assert.Equal(MarketState.Closed, (await Listing(seller, trade)).State);
            Assert.Equal(1, await Scalar(seller, "SELECT count(*) FROM ultimateteam.items WHERE pile='unassigned'"));
            Assert.Equal(2 * seller.Game.Config.InitialCredits - await Taxed(seller), await Scalar(seller, "SELECT sum(credits) FROM ultimateteam.clubs"));
        }
        finally
        {
            release.Set();
            if (watch is not null) await watch;
            if (purchase is not null) await purchase;
            watcher.Detach(); buyer.Detach();
        }
    }

    [Fact]
    public async Task DirectSqlEnforcesSnapshotImmutabilityAndActiveSellerOwnership()
    {
        using var rt=Create();var item=await AddPlayer(rt);var trade=List(rt,item);
        foreach(var sql in new[]{"UPDATE ultimateteam.market_item_snapshots SET timestamp=timestamp+1", "UPDATE ultimateteam.market_player_snapshots SET contracts=contracts+1", "UPDATE ultimateteam.market_player_snapshots SET match_stats=jsonb_set(match_stats,'{current,0}','99')", "DELETE FROM ultimateteam.items WHERE club_id=@club"})
        {
            var error=await Assert.ThrowsAsync<PostgresException>(()=>rt.Game.RunAsync("sql-rejected",w=>w.ExecuteAsync(sql),CancellationToken.None));Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
        }
        Assert.NotNull(await Read(rt,w=>w.GetItemAsync(item.IntKeep("id"))));
        Assert.Empty((await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[]{JsonValue.Create(item.IntKeep("id"))})).Deleted);
        var moved=rt.CallObject("PUT",G+"/item",new JsonObject{["itemData"]=new JsonObject{["id"]=item.IntKeep("id"),["pile"]="club"}});Assert.False(moved.Arr("itemData")![0]!.AsObject().Bool("success"));
        Assert.Equal(trade,(await Read(rt,w=>w.LiveMarketListingForItemAsync(item.IntKeep("id"))))!.TradeId);
    }

    [Fact]
    public async Task AiPreviewAndMaintenanceUseSnapshotsAndWithdrawalRefundsOtherClubs()
    {
        using var seller=Create(target:2);var buyer=Create(seller.ConnectionString,910002);
        try
        {
            var maintenance=await seller.Fut.Market.RunMaintenanceOnceAsync();Assert.True(maintenance.Acquired);Assert.Equal(2,maintenance.Seeded);
            var ai=Assert.Single((await Read(seller,w=>w.ActiveMarketListingsAsync())).Take(1));var preview=ai.Item.IntKeep("id");await buyer.Fut.Market.BuyNowAsync(ai.TradeId);
            var bought=Assert.Single(await Read(buyer,w=>w.ListItemsAsync("unassigned")));Assert.NotEqual(preview,bought.IntKeep("id"));
            var item=await AddPlayer(seller);var trade=List(seller,item);var start=(await Listing(seller,trade)).StartingBid;var coins=await Read(buyer,w=>w.CreditsAsync());
            await buyer.Fut.Market.PlaceBidAsync(trade,start);await seller.Game.RunAsync("withdraw",w=>w.WithdrawFromMarketAsync(),CancellationToken.None);
            Assert.Equal(coins,await Read(buyer,w=>w.CreditsAsync()));Assert.Equal(MarketState.Cancelled,(await Listing(seller,trade)).State);
            Assert.Equal(3,await Scalar(seller,"SELECT count(*) FROM ultimateteam.market_item_snapshots"));
        }
        finally{buyer.Detach();}
    }
}
