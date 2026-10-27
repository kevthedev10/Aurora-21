using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Quick sell by tier and rating (PLAN 4.2) and the item-level reward rules: a Match Pack item sells for nothing, a
/// reward-untradeable one for at most the cap (0: every untradeable card sells for nothing, owner 2026-09-29), and the client is shown exactly what quick sell pays.
/// </summary>
public sealed class RewardQuickSellTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Catalogue cards matching <paramref name="match"/>, grant-only cards aside.</summary>
    private static IEnumerable<long> Cards(TestRuntime rt, Func<PlayerMeta, bool> match) => rt.Game.Players.Entries
        .Where(entry => !rt.Game.Cards.IsGrantOnly(entry.Key) && match(entry.Value))
        .Select(entry => entry.Key);

    private static string Tier(TestRuntime rt, PlayerMeta meta) => rt.Game.Packs.Rules.TierFor(meta.Rating, meta.Tier);

    /// <summary>
    /// Retail FIFA 21 discard values (community price guides fifauteam.com/fifa-21-quick-sell-prices-discard and
    /// fifplay.com/fifa-21-quick-sell-prices), as (rating, common, rare): the anchors the multipliers in rules/cards.json reproduce.
    /// </summary>
    private static readonly (long Rating, long Common, long Rare)[] RetailBase =
    {
        (60, 18, 45), (62, 19, 47), (63, 19, 47), (64, 19, 48),
        (65, 98, 228), (66, 99, 231), (67, 101, 235), (70, 105, 245), (74, 111, 259),
        (75, 300, 600), (76, 304, 608), (80, 320, 640), (85, 340, 680), (90, 360, 720), (95, 380, 760), (99, 396, 792),
    };

    /// <summary>Retail in-form values (every special except TOTY and Icons) as (rating, value).</summary>
    private static readonly (long Rating, long Value)[] RetailInForm =
    {
        (60, 1200), (61, 1220), (64, 1280), (65, 4550), (66, 4620), (70, 4900), (72, 5040), (74, 5180), (75, 9150), (76, 9272),
        (80, 9760), (82, 10004), (85, 10370), (90, 10980), (99, 12078),
    };

    /// <summary>
    /// The economy caps over retail (rules/cards.json quickSell maximum, rareMaximum), each one a retail value that would beat the
    /// cheapest market buy-in (EconomyInvariantTests): silver rares at 148, and a special rated 65-74 at 1,280.
    /// </summary>
    private const long SilverRareCap = 148;

    private static long InFormCap(long rating) => rating switch { < 65 => long.MaxValue, < 75 => 1280, _ => long.MaxValue };

    [Fact]
    public void QuickSellFollowsRetailFifa21ByTierRatingAndRarity()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var quickSell = rt.Game.Rules.Cards.QuickSell;

        // The multipliers reproduce the retail rows (tier by rating: bronze below 65, silver 65-74, gold 75 and up).
        foreach (var (rating, common, rare) in RetailBase)
        {
            var tier = rating >= 75 ? "gold" : rating >= 65 ? "silver" : "bronze";
            Assert.Equal(common, quickSell.BaseValue(tier, rating, rare: false));
            // The one economy cap on a base card: retail's silver rares (228-259) would beat the 150 price floor of the market.
            Assert.Equal(tier == "silver" ? Math.Min(rare, SilverRareCap) : rare, quickSell.BaseValue(tier, rating, rare: true));
        }
        foreach (var (rating, value) in RetailInForm)
        {
            Assert.Equal(Math.Min(value, InFormCap(rating)), quickSell.SpecialValue(3, rating));
        }
        Assert.Equal(10_492, quickSell.SpecialValue(3, 86));
        // Every special sells alike (owner, 2026-10-01: all specials discard for around 10k): TOTY and Icons use the same 122 a point.
        Assert.All(new long[] { 5, 11, 12, 18 }, rareflag => Assert.Equal(new[] { 9150L, 10_492L, 12_078L }, new[] { 75L, 86L, 99L }.Select(rating => quickSell.SpecialValue(rareflag, rating))));
        Assert.All(new long[] { 76, 77, 89, 92, 93, 94 }, token => Assert.Equal(0, quickSell.SpecialValue(token, 90)));

        // Every catalogue card sells for its kind's value; nothing is ever below the retail floor of its tier.
        foreach (var (card, meta) in rt.Game.Players.Entries.Where(entry => !rt.Game.Cards.IsGrantOnly(entry.Key)))
        {
            var value = rt.Game.Cards.DiscardValue(card);
            if (meta.IsSpecial && !quickSell.BaseCompetitionRareflags.Contains(meta.Rareflag))
            {
                Assert.Equal(quickSell.SpecialValue(meta.Rareflag, meta.Rating), value);
            }
            else
            {
                Assert.Equal(quickSell.BaseValue(Tier(rt, meta), meta.Rating, rare: meta.Rareflag == 1 || quickSell.BaseCompetitionRareflags.Contains(meta.Rareflag)), value);
            }
        }

        // A gold rare 90 and a TOTW 90 from the catalogue.
        var gold = Cards(rt, meta => meta.Rareflag == 1 && !meta.IsSpecial && meta.Rating == 90).First();
        Assert.Equal(720, rt.Game.Cards.DiscardValue(gold));
        var inForm = Cards(rt, meta => meta.Rareflag == 3 && meta.Rating == 90).First();
        Assert.Equal(10_980, rt.Game.Cards.DiscardValue(inForm));
        Assert.Equal(Math.Min(228, SilverRareCap), quickSell.BaseValue("silver", 65, rare: true));

        // Managers, chemistry styles and position modifiers keep their values (retail's per-card values are not documented).
        Assert.Equal((150, 300), (quickSell.ManagerCommon, quickSell.ManagerRare));
        // A manager never sells for more than a base player of its tier: the flat values made a 400-coin Bronze Pack
        // return 97% of its price in quick sell (pack-odds-2026-10 bronze/silver audit). Gold managers keep the flat values.
        Assert.Equal((150L, 300L), (quickSell.ManagerValue("gold", 75, rare: false), quickSell.ManagerValue("gold", 80, rare: true)));
        Assert.Equal((98L, 148L), (quickSell.ManagerValue("silver", 65, rare: false), quickSell.ManagerValue("silver", 70, rare: true)));
        Assert.Equal((17L, 45L), (quickSell.ManagerValue("bronze", 55, rare: false), quickSell.ManagerValue("bronze", 60, rare: true)));
        Assert.Equal(100, rt.Game.Rules.Cards.ChemistryStyles.DiscardValue);
        Assert.Equal(80, rt.Game.Rules.Cards.PositionModifierCard.DiscardValue);
    }

    [Fact]
    public void QuickSellRisesWithRatingWithinEveryKindAndASpecialNeverSellsBelowABaseCard()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var kinds = rt.Game.Players.Entries.Where(entry => !rt.Game.Cards.IsGrantOnly(entry.Key) && entry.Value.Rating > 0)
            .GroupBy(entry => entry.Value.IsSpecial && !rt.Game.Rules.Cards.QuickSell.BaseCompetitionRareflags.Contains(entry.Value.Rareflag)
                ? $"special {entry.Value.Rareflag}" : $"base {Tier(rt, entry.Value)} {(entry.Value.Rareflag == 1 || rt.Game.Rules.Cards.QuickSell.BaseCompetitionRareflags.Contains(entry.Value.Rareflag) ? "rare" : "common")}");
        var falls = new List<string>();
        foreach (var kind in kinds)
        {
            var byRating = kind.GroupBy(entry => entry.Value.Rating).OrderBy(group => group.Key)
                .Select(group => (Rating: group.Key, Values: group.Select(entry => rt.Game.Cards.DiscardValue(entry.Key)).Distinct().ToList())).ToList();
            Assert.All(byRating, row => Assert.Single(row.Values));
            foreach (var (previous, next) in byRating.Zip(byRating.Skip(1)))
            {
                if (next.Values[0] < previous.Values[0]) falls.Add($"{kind.Key}: {previous.Rating} sells {previous.Values[0]}, {next.Rating} sells {next.Values[0]}");
            }
        }
        Assert.True(falls.Count == 0, string.Join("\n", falls));

        // A special (not a token) is worth at least a gold rare of its rating.
        var quickSell = rt.Game.Rules.Cards.QuickSell;
        foreach (var (card, meta) in rt.Game.Players.Entries.Where(entry => entry.Value.IsSpecial && entry.Value.Rating >= 75 && !quickSell.ZeroValueRareflags.Contains(entry.Value.Rareflag)
                     && !quickSell.BaseCompetitionRareflags.Contains(entry.Value.Rareflag) && !rt.Game.Cards.IsGrantOnly(entry.Key)))
        {
            Assert.True(rt.Game.Cards.DiscardValue(card) >= quickSell.BaseValue("gold", meta.Rating, rare: true), $"{card} rareflag {meta.Rareflag} rating {meta.Rating}");
        }
    }

    [Fact]
    public async Task MatchPackItemsSellForNothingAndRewardUntradeablesForAtMostTheCap()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        store.DeletePile("unassigned");
        var ninety = Cards(rt, meta => meta.Rareflag == 1 && !meta.IsSpecial && meta.Rating >= 90).First();
        var ninetyValue = 8 * rt.Game.Players.Find(ninety)!.Rating; // retail: a gold rare sells for 8 coins a rating point
        var common = Cards(rt, meta => meta.Rareflag == 0 && !meta.IsSpecial && meta.Rating is >= 75 and <= 79).First();
        var style = rt.Game.Cards.ChemistryStyles.First(entry => entry.PlayStyle != rt.Game.Rules.Cards.ChemistryStyles.OutfieldDefault).ResourceId;
        // (label, resource, consumable, source, untradeable, expected quick sell)
        var grants = new (string Label, long Resource, bool Consumable, string? Source, bool Untradeable, long Expected)[]
        {
            ("tradeable 90 rare", ninety, false, null, false, ninetyValue),
            ("tradeable reward 90 rare", ninety, false, RewardSources.Sbc, false, ninetyValue),
            ("untradeable reward 90 rare", ninety, false, RewardSources.RivalsOptionC, true, 0),
            ("untradeable reward common", common, false, RewardSources.Pick, true, 0),
            ("match pack 90 rare", ninety, false, RewardSources.MatchPack, true, 0),
            ("gift 90 rare", ninety, false, RewardSources.Gift, true, 0),
            ("match pack chemistry style", style, true, RewardSources.MatchPack, true, 0),
        };
        var ids = new Dictionary<string, long>();
        await rt.Game.RunAsync("reward-grants", async uow =>
        {
            foreach (var grant in grants)
            {
                var id = await uow.NextItemIdAsync();
                var item = grant.Consumable ? rt.Fut.Items.BuildConsumable(grant.Resource, id, "unassigned") : rt.Fut.Items.BuildPlayer(grant.Resource, id, "unassigned");
                if (grant.Source is { } source)
                {
                    rt.Fut.Items.MarkReward(item, source, grant.Untradeable);
                }
                // The document a grant builds already shows the value quick sell will pay.
                Assert.Equal(grant.Expected, item.Int("discardValue"));
                await uow.SaveItemAsync(item);
                ids[grant.Label] = id;
            }
            return 0;
        }, CancellationToken.None);

        // Stored, rendered and shown to the client alike; the reward source never reaches the client.
        var unassigned = rt.CallObject("GET", $"{G}/purchased/items")["itemData"]!.AsArray().OfType<JsonObject>().ToDictionary(item => item.Int("id"));
        foreach (var grant in grants)
        {
            var id = ids[grant.Label];
            Assert.Equal(grant.Expected, store.GetItem(id)!.Int("discardValue"));
            Assert.Equal(grant.Expected, unassigned[id].Int("discardValue"));
            Assert.False(unassigned[id].ContainsKey(RewardSources.ItemKey), grant.Label);
            Assert.Equal(grant.Source, await rt.Game.ReadAsync(uow => uow.ScalarAsync<string?>(
                "SELECT reward_source FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@id", ("id", id)), CancellationToken.None));
        }
        // A later save of the item (a pile move) keeps its reward source.
        rt.Call("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = ids["match pack 90 rare"], ["pile"] = "club" }) });
        Assert.Equal(0, store.GetItem(ids["match pack 90 rare"])!.Int("discardValue"));

        // Quick sell pays exactly the values shown.
        foreach (var grant in grants)
        {
            var before = store.Credits();
            var sold = await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[] { JsonValue.Create(ids[grant.Label]) });
            Assert.Equal(new[] { ids[grant.Label] }, sold.Deleted);
            Assert.Equal(before + grant.Expected, store.Credits());
        }
    }

    /// <summary>
    /// One value everywhere (retail 4 / 8 / 122 coins a rating point): a Team of the Week in-form and another special opened
    /// into the club show the same discardValue in the club view, the club's value sort, a market listing preview and a
    /// player item rebuilt from a stale copy, and quick sell pays exactly it.
    /// </summary>
    [Fact]
    public async Task AnInFormAndAnotherSpecialShowTheSameQuickSellEverywhereAndQuickSellPaysIt()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var inForm = Cards(rt, meta => meta.Rareflag == 3 && meta.Rating >= 88 && !meta.Bool("untradeable")).First();
        var promo = Cards(rt, meta => meta.Rareflag == 11 && meta.Rating >= 88 && !meta.Bool("untradeable")).First();
        var specials = new[] { inForm, promo };
        var listed = new Dictionary<long, long>();
        var sellable = new Dictionary<long, long>();
        await rt.Game.RunAsync("special-grants", async uow =>
        {
            foreach (var resource in specials)
            {
                foreach (var target in new[] { listed, sellable })
                {
                    var id = await uow.NextItemIdAsync();
                    var item = rt.Fut.Items.BuildPlayer(resource, id, "club");
                    // Whatever a stale document carried, the shown value is the card's.
                    item["discardValue"] = 1;
                    await uow.SaveItemAsync(item);
                    target[resource] = id;
                }
            }
            return 0;
        }, CancellationToken.None);

        var club = rt.CallObject("GET", $"{G}/club?type=player&count=500&sortBy=value")["itemData"]!.AsArray().OfType<JsonObject>().ToList();
        foreach (var resource in specials)
        {
            var expected = rt.Game.Cards.DiscardValue(resource);
            Assert.True(expected >= 8 * rt.Game.Players.Find(resource)!.Rating, "a special sells for more than a gold rare");
            Assert.Equal(expected, club.Single(item => item.Int("id") == sellable[resource]).Int("discardValue"));
            Assert.Equal(expected, rt.Fut.Native.PlayerItem(store.GetItem(listed[resource])!)!.Int("discardValue"));
            Assert.Equal(expected, rt.Fut.Items.BuildPlayer(resource, 1, "club").Int("discardValue"));
        }
        // The club's value sort puts higher values first, by the same rule (its SQL form).
        var values = club.Select(item => item.Int("discardValue")).ToList();
        Assert.Equal(values.OrderByDescending(value => value), values);

        foreach (var resource in specials)
        {
            rt.Fut.Market.List(listed[resource], 2500, 5000, 3600);
        }
        var preview = rt.CallObject("GET", $"{G}/tradepile")["auctionInfo"]!.AsArray().OfType<JsonObject>().Select(auction => auction.Obj("itemData")!).ToList();
        Assert.Equal(specials.Length, preview.Count);
        foreach (var item in preview)
        {
            Assert.Equal(rt.Game.Cards.DiscardValue(item.Int("resourceId")), item.Int("discardValue"));
        }

        foreach (var resource in specials)
        {
            var before = store.Credits();
            var sold = await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[] { JsonValue.Create(sellable[resource]) });
            Assert.Equal(new[] { sellable[resource] }, sold.Deleted);
            Assert.Equal(before + rt.Game.Cards.DiscardValue(resource), store.Credits());
        }
    }

    /// <summary>
    /// A card is never listable below its own quick-sell value (so listing cheap, buying and quick-selling cannot print coins):
    /// every card's minimum listing price is above its tradeable quick-sell value, on the price grid and within its maximum.
    /// </summary>
    [Fact]
    public void NoCardIsListableBelowItsQuickSellValue()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var market = rt.Game.Rules.Market;
        var specials = 0;
        foreach (var (card, meta) in rt.Game.Players.Entries.Where(entry => entry.Value.Rating > 0))
        {
            var value = rt.Game.Cards.DiscardValue(card);
            var (minimum, maximum) = rt.Game.Cards.MarketPriceBounds(card);
            Assert.True(minimum > value, $"{card} rareflag {meta.Rareflag} rating {meta.Rating}: lists from {minimum}, quick sells for {value}");
            Assert.True(market.IsOnGrid(minimum) && market.IsOnGrid(maximum) && minimum <= maximum, $"{card}: bounds {minimum}-{maximum}");
            specials += meta.IsSpecial ? 1 : 0;
        }
        Assert.True(specials > 1000);
        // A 99 special (about 12,000) lists from above it; the ordinary special floor of 2,500 cannot undercut it.
        var top = Cards(rt, meta => meta.IsSpecial && meta.Rating == 99 && meta.Rareflag != 10).First();
        Assert.Equal(market.SnapUp(rt.Game.Cards.DiscardValue(top) + 1), rt.Game.Cards.MarketPriceBounds(top).Minimum);
    }

    /// <summary>The swap tokens (Icon Swaps 89, 92, 93, 94; FGS Swaps 76, 77) sell for nothing and are untradeable however they are built or granted.</summary>
    [Fact]
    public async Task EverySwapTokenIsUntradeableAndSellsForNothing()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var quickSell = rt.Game.Rules.Cards.QuickSell;
        var tokens = rt.Game.Players.Entries.Where(entry => rt.Game.Rules.Cards.UntradeableRareflags.Contains(entry.Value.Rareflag)).ToList();
        Assert.Equal(new long[] { 76, 77, 89, 92, 93, 94 }, tokens.Select(entry => entry.Value.Rareflag).Distinct().Order());
        Assert.Equal(tokens.Select(entry => entry.Value.Rareflag).Distinct().Order(), quickSell.ZeroValueRareflags.Order());
        Assert.All(tokens, entry =>
        {
            Assert.True(rt.Game.Cards.IsAlwaysUntradeable(entry.Key));
            Assert.True(rt.Fut.Items.BuildPlayer(entry.Key, 1, "unassigned").Bool("untradeable"));
            Assert.Equal(0, rt.Game.Cards.DiscardValue(entry.Key));
        });
        // The path an operator grant or a pack takes stores it untradeable too.
        var fgs = tokens.First(entry => entry.Value.Rareflag == 76).Key;
        var stored = await rt.Game.RunAsync("fgs-token", async uow => await rt.Fut.Items.MakePlayerAsync(uow, fgs, "unassigned"), CancellationToken.None);
        Assert.True(stored.Bool("untradeable"));
        Assert.True(rt.Game.Store().GetItem(stored.Int("id"))!.Bool("untradeable"));
    }

    [Theory]
    [InlineData(RewardSources.MatchPack, true, true)]
    [InlineData(RewardSources.RivalsOptionC, true, false)]
    [InlineData(RewardSources.Sbc, false, false)]
    public async Task AnOwnedRewardPackOpensItemsWithItsSourceAndRestriction(string source, bool untradeable, bool worthless)
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        store.DeletePile("unassigned");
        await rt.Game.RunAsync("grant-pack", uow => uow.AddRewardPackAsync(rt.Game.Rules.Tradeability, 304, source), CancellationToken.None);

        var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 304 });

        Assert.Equal("PURCHASECOMPLETE", opened["state"].S());
        var shown = opened["itemData"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.NotEmpty(shown);
        foreach (var item in shown)
        {
            var stored = store.GetItem(item.Int("id"))!;
            Assert.Equal(untradeable, stored.Bool("untradeable"));
            Assert.Equal(untradeable, item.Bool("untradeable"));
            Assert.Equal(stored.Int("discardValue"), item.Int("discardValue"));
            Assert.False(item.ContainsKey(RewardSources.ItemKey));
            if (item.Text("itemType") == "player")
            {
                var value = rt.Game.Cards.DiscardValue(item.Int("resourceId"));
                Assert.Equal(worthless ? 0 : untradeable ? Math.Min(value, rt.Game.Rules.Cards.QuickSell.RewardUntradeableCap) : value, item.Int("discardValue"));
            }
        }
        Assert.Equal(shown.Count, await rt.Game.ReadAsync(uow => uow.ScalarAsync<long>(
            "SELECT count(*) FROM ultimateteam.items WHERE club_id=@club AND pile='unassigned' AND reward_source=@source", ("source", source)), CancellationToken.None));
        var credits = store.Credits();
        var sold = await rt.Fut.Inventory.DiscardItemsAsync(shown.Select(item => (JsonNode?)JsonValue.Create(item.Int("id"))));
        Assert.Equal(credits + shown.Sum(item => item.Int("discardValue")), sold.Credits);
        if (worthless)
        {
            Assert.Equal(credits, sold.Credits);
        }
    }
}
