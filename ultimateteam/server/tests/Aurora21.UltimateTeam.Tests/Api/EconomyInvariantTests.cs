using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The phase 1 economy's invariants (PLAN 4.1, 4.2): quick sell never beats the cheapest AI buy-in, the transfer tax
/// deletes 5%, the AI Market Maker lists only base cards under the dated ceiling, fair values rise with rating, and there
/// is no FIFA Points drop.
/// </summary>
public sealed class EconomyInvariantTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>The cheapest price the AI Market Maker can start this card at: its lowest starting-bid factor, snapped up to the price grid, within its bounds.</summary>
    private static long CheapestAiStart(TestRuntime rt, long resourceId)
    {
        var meta = rt.Game.Players.Find(resourceId)!;
        var market = rt.Game.Rules.Market;
        var value = market.FairValueFor(resourceId, meta.Rating, meta.Rareflag, meta.IsSpecial);
        var (low, high) = rt.Game.Cards.MarketPriceBounds(resourceId);
        return Math.Clamp(market.SnapUp((long)(value * market.AiListings.StartingBidFactor.Minimum)), low, high);
    }

    [Fact]
    public void QuickSellIsBelowTheCheapestAiBuyInOfEveryBaseAndTeamOfTheWeekCard()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var cards = rt.Game.Players.Entries
            .Where(entry => entry.Value.Rating > 0 && !rt.Game.Cards.IsGrantOnly(entry.Key)
                && ((entry.Value.Rareflag is 0 or 1 && !entry.Value.IsSpecial) || entry.Value.Rareflag == 3))
            .Select(entry => entry.Key).ToList();
        Assert.True(cards.Count > 15_000);
        Assert.Contains(cards, card => rt.Game.Players.Find(card)!.Rareflag == 3);
        var arbitrage = cards.Where(card => rt.Game.Cards.DiscardValue(card) >= CheapestAiStart(rt, card))
            .Select(card => $"{card} (rating {rt.Game.Players.Find(card)!.Rating}, rareflag {rt.Game.Players.Find(card)!.Rareflag}): quick sell {rt.Game.Cards.DiscardValue(card)} >= AI start {CheapestAiStart(rt, card)}")
            .ToList();
        Assert.True(arbitrage.Count == 0, string.Join("\n", arbitrage.Take(20)));
    }

    /// <summary>
    /// No store pack pays back more through quick sell than it costs (PACK-ODDS-SPEC 4): its commons and rares at the dearest
    /// quick-sell value of their tier (the worst case for any pull), plus each rare slot's published in-form chance at the mean
    /// in-form value, stay below the price.
    /// </summary>
    [Fact]
    public void NoStorePackReturnsMoreThanItsPriceThroughQuickSell()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var bases = rt.Game.Players.Entries.Where(entry => entry.Value.Rating > 0 && !entry.Value.IsSpecial && !rt.Game.Cards.IsGrantOnly(entry.Key)).ToList();
        long Dearest(string tier, long rareflag) => bases.Where(entry => entry.Value.Rareflag == rareflag && rt.Game.Packs.Rules.TierFor(entry.Value.Rating, entry.Value.Tier) == tier)
            .Max(entry => rt.Game.Cards.DiscardValue(entry.Key));
        var inForms = rt.Game.Players.Entries.Where(entry => entry.Value.Rareflag == 3 && entry.Value.Rating >= 75 && !rt.Game.Cards.IsGrantOnly(entry.Key)).ToList();
        var meanInForm = inForms.Average(entry => (double)rt.Game.Cards.DiscardValue(entry.Key));
        // (pack, price, tier, commons, rares, in-form chance per rare slot)
        var packs = new (string Name, long Price, string Tier, int Commons, int Rares, double InForm)[]
        {
            ("Bronze", 400, "bronze", 11, 1, 0), ("Premium Bronze", 750, "bronze", 11, 1, 0.005), ("Silver", 2500, "silver", 11, 1, 0),
            ("Premium Silver", 3750, "silver", 11, 1, 0.005), ("Gold", 5000, "gold", 11, 1, 0.004), ("Premium Gold", 7500, "gold", 9, 3, 0.011),
            ("Rare Gold", 25_000, "gold", 0, 12, 0.011),
        };
        var over = packs.Select(pack => (pack.Name, pack.Price, Back: pack.Commons * Dearest(pack.Tier, 0) + pack.Rares * Dearest(pack.Tier, 1) + pack.Rares * pack.InForm * meanInForm))
            .Where(pack => pack.Back >= pack.Price).Select(pack => $"{pack.Name}: pays back {pack.Back:0} >= price {pack.Price}").ToList();
        Assert.True(over.Count == 0, string.Join("\n", over));
    }

    [Fact]
    public void FairValuesRiseWithRatingAndBoundsFollowThePlan()
    {
        var rules = GameRules.Load(TestRuntime.DataRoot).Market;
        // PLAN 4.1 fair value table for base cards, 75-80 raised to 1,100 above the 360 / 700 gold quick sell (owner, 2026-09-29).
        var plan = new Dictionary<long, long>
        {
            [60] = 150, [74] = 150, [75] = 1100, [76] = 1100, [77] = 1100, [78] = 1100, [79] = 1100, [80] = 1100, [81] = 1300, [82] = 2200,
            [83] = 3500, [84] = 6500, [85] = 11_000, [86] = 18_000, [87] = 28_000, [88] = 42_000, [89] = 65_000, [90] = 100_000,
            [91] = 150_000, [92] = 220_000, [93] = 320_000,
        };
        foreach (var (rating, value) in plan)
        {
            Assert.Equal(value, rules.FairValue.BaseValue(rating));
        }
        for (var rating = 1L; rating < Cards.MaxRating; rating++)
        {
            Assert.True(rules.FairValue.BaseValue(rating + 1) >= rules.FairValue.BaseValue(rating), $"fair value falls after {rating}");
        }
        Assert.Equal((150, 10_000), rules.BoundsFor(74, special: false));
        Assert.Equal((150, 25_000), rules.BoundsFor(81, special: false));
        Assert.Equal((150, 75_000), rules.BoundsFor(85, special: false));
        Assert.Equal((150, 750_000), rules.BoundsFor(89, special: false));
        Assert.Equal((150, 3_000_000), rules.BoundsFor(99, special: false));
        Assert.Equal((2500, 250_000), rules.BoundsFor(80, special: true));
        Assert.Equal((2500, 750_000), rules.BoundsFor(87, special: true));
        Assert.Equal((2500, 2_000_000), rules.BoundsFor(95, special: true));
        Assert.True(rules.PriceBounds.SpecialMaximumCap == 2_000_000);
    }

    [Fact]
    public void TheSellerPaysAFivePercentTransferTax()
    {
        var rules = GameRules.Load(TestRuntime.DataRoot).Market;
        Assert.Equal(5, rules.SellerTaxPercent);
        Assert.Equal(950, rules.SellerProceeds(1000));
        Assert.Equal(142, rules.SellerProceeds(150));
        Assert.Equal(95_000, rules.SellerProceeds(100_000));

        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 3 }, shared: true);
        using var seller = players.Acquire(PlayerIdentity.Of("111111"));
        using var buyer = players.Acquire(PlayerIdentity.Of("222222"));
        var sellerStore = seller.Session.Game.Store();
        var buyerStore = buyer.Session.Game.Store();
        var squadItems = sellerStore.ListSquads().SelectMany(SquadDocuments.ItemIds).ToHashSet();
        var item = sellerStore.ListItems("club").First(candidate => ClubInventoryService.IsPlayer(candidate) && !candidate.Bool("untradeable")
            && !squadItems.Contains(candidate.Int("id")));
        var (sellerCredits, buyerCredits) = (sellerStore.Credits(), buyerStore.Credits());
        var (_, high) = seller.Session.Game.Cards.MarketPriceBounds(item.Int("resourceId"));
        var price = Math.Min(10_000, high);
        var tradeId = seller.Session.Fut.Market.List(item.Int("id"), 150, price, 3600)["tradeId"].L();

        buyer.Session.Fut.Market.BuyNow(tradeId, price);

        Assert.Equal(buyerCredits - price, buyerStore.Credits());
        Assert.Equal(sellerCredits + price - (price * 5 + 99) / 100, sellerStore.Credits());
    }

    [Fact]
    public void TheAiMarketMakerListsOnlyTradeableBaseCardsUnderTheDatedCeiling()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DateTimeOffset.Parse("2026-10-01T12:00:00Z").ToUnixTimeSeconds()), marketTarget: 200);
        var ai = rt.Game.Rules.Market.AiListings;
        Assert.Equal(new long[] { 0, 1 }, ai.Rareflags.Order());
        var testWeek = DateTimeOffset.Parse("2026-10-01T12:00:00Z").ToUnixTimeSeconds();
        var launch = DateTimeOffset.Parse("2026-10-14T00:00:00Z").ToUnixTimeSeconds();
        Assert.Equal(84, ai.MaxRatingAt(testWeek));
        Assert.Equal(83, ai.MaxRatingAt(launch));
        Assert.Equal(84, ai.MaxRatingAt(DateTimeOffset.Parse("2025-01-01T00:00:00Z").ToUnixTimeSeconds()));

        void AssertListable(IEnumerable<long> cards, long ceiling)
        {
            foreach (var card in cards)
            {
                var meta = rt.Game.Players.Find(card)!;
                Assert.True(meta.Rareflag is 0 or 1, $"{card} rareflag {meta.Rareflag}");
                Assert.False(meta.IsSpecial);
                Assert.InRange(meta.Rating, 1, ceiling);
                Assert.False(rt.Game.Cards.IsGrantOnly(card) || rt.Game.Cards.IsAquaPro(card) || meta.Bool("untradeable"));
                Assert.True(card < PlayerCatalog.ResourceRevisionSize);
            }
        }
        var listable = rt.Fut.Market.AiListable(testWeek);
        AssertListable(listable, 84);
        Assert.Contains(listable, card => rt.Game.Players.Find(card)!.Rating == 84);
        var afterLaunch = rt.Fut.Market.AiListable(launch);
        AssertListable(afterLaunch, 83);
        Assert.DoesNotContain(afterLaunch, card => rt.Game.Players.Find(card)!.Rating == 84);

        // The listings the maintenance job actually creates obey the same rules.
        rt.Fut.Market.SeedAiListings();
        var listings = rt.Game.Store().ActiveMarketListings().Where(listing => listing.Kind == MarketListingKind.Ai).ToList();
        Assert.Equal(200, listings.Count);
        AssertListable(listings.Select(listing => listing.ResourceId), 84);
    }

    [Fact]
    public void ThereIsNoFifaPointsDropAndNewClubsStartWithNone()
    {
        Assert.Null(typeof(ClubRules).GetProperty("DailyDrop"));
        Assert.Null(typeof(FutServices).GetProperty("DailyDrop"));
        Assert.True(GameRules.Load(TestRuntime.DataRoot).Club.FifaPointsPurchases);

        using var rt = TestRuntime.Create(marketTarget: 0);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 2 }, shared: true);
        using var fresh = players.Acquire(PlayerIdentity.Of("333333"));
        Assert.Equal(0, fresh.Session.Game.Store().FifaPoints());
        var claim = fresh.Session.Router.Handle(new FutRequest("POST", "/ultimateteam/daily-drop",
            Array.Empty<KeyValuePair<string, string>>(), "{}"u8.ToArray()));
        Assert.Contains(claim.Headers, header => header.Key == "X-UltimateTeam-Unknown");
        Assert.Equal(0, fresh.Session.Game.Store().FifaPoints());
    }
}
