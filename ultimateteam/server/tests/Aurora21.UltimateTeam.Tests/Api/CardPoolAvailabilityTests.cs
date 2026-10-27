using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Standard cards, scheduled TOTW cards, and fixed TOTW 1–3 reward pools.</summary>
public sealed class CardPoolAvailabilityTests
{
    private const string G = "/ut/game/fifa21";
    private static readonly long[] CompetitionRareflags = { 47, 48, 52, 53 };

    private static TestRuntime Create() => TestRuntime.Create(marketTarget: 0,
        time: new TestRuntime.FixedTime(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

    private static List<JsonObject> OpenOwned(TestRuntime rt, long packId)
    {
        rt.Game.Store().DeletePile("unassigned");
        rt.Game.InUnit(uow => uow.AddUnopenedPackAsync(packId, "test"));
        var response = rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = packId });
        Assert.True(response.Status == 200, $"pack {packId} returned {response.Status}: {response.Body}");
        return rt.Game.Store().ListItems("unassigned");
    }

    private static List<JsonObject> PickChoices(TestRuntime rt, JsonObject token) =>
        rt.CallObject("POST", $"{G}/item/nontargeted?itemId={token.Int("id")}")["choiceData"]!.AsArray().OfType<JsonObject>().ToList();

    [Fact]
    public void TheNamedRewardPoolContainsTeamsOfTheWeekOneToThree()
    {
        using var rt = Create();
        var pools = rt.Fut.Pools;
        var totw = rt.Game.Rules.Totw;
        var expected = new[] { 1L, 2, 3 }.SelectMany(week => totw.Week(week)!.Starters.Concat(totw.Week(week)!.Subs)).Select(card => card.ResourceId).ToHashSet();
        Assert.Equal(69, expected.Count);
        Assert.Equal(expected.Order(), pools.TotwOneToThreeCards.Order());
        Assert.Equal(expected.Order(), rt.Fut.Pools.Named("totw_1_3")!.Order());

        foreach (var (id, meta) in rt.Game.Players.Entries)
        {
            if (CompetitionRareflags.Contains(meta.Rareflag) || rt.Game.Cards.IsGrantOnly(id) || rt.Game.Cards.IsAquaPro(id)
                || (meta.IsSpecial && !expected.Contains(id)))
            {
                Assert.False(pools.IsBase(id), $"{id} ({meta.Text("name")} {meta.Rating}, rareflag {meta.Rareflag}) is in the base pool");
            }
        }
    }

    [Fact]
    public void GroupedRewardPacksHandOutAvailableCards()
    {
        using var rt = Create();
        var pools = rt.Fut.Pools;
        var icon = rt.Game.IconCardAt();
        void AllLegal(long packId, IEnumerable<JsonObject> items) =>
            Assert.All(items, item => Assert.True(pools.IsBase(item.Int("resourceId")) || pools.TotwOneToThreeCards.Contains(item.Int("resourceId")) || icon(item.Int("resourceId")),
                $"pack {packId} gave {item.Int("resourceId")} rated {item.Int("rating")}"));

        foreach (var packId in new long[] { 304, 305, 319, 308, 400, 402, 403, 404, 405, 543 })
        {
            for (var roll = 0; roll < 25; roll++)
            {
                AllLegal(packId, rt.Fut.Packs.RollPlayerPack(packId).Select(id => new JsonObject { ["resourceId"] = id, ["rating"] = rt.Game.Players.Find(id)!.Rating }));
            }
        }

        // 540: 3 fixed TOTW cards and 9 gold; 541: 11 fixed TOTW cards and 1 gold; 542: one fixed TOTW card.
        foreach (var (packId, size, informs) in new[] { (540L, 12, 3), (541L, 12, 11), (542L, 1, 1) })
        {
            var items = OpenOwned(rt, packId);
            Assert.Equal(size, items.Count);
            AllLegal(packId, items);
            Assert.Equal(informs, items.Count(item => pools.TotwOneToThreeCards.Contains(item.Int("resourceId"))));
            Assert.All(items.Where(item => !pools.TotwOneToThreeCards.Contains(item.Int("resourceId"))), item => Assert.True(item.Int("rating") >= 75, $"pack {packId}"));
        }
        // A small rare-gold reward pack can use its configured special chance when the TOTW schedule is active.
        var rareGold = OpenOwned(rt, 543);
        Assert.Equal(6, rareGold.Count);
        AllLegal(543, rareGold);
        Assert.All(rareGold.Where(item => pools.IsBase(item.Int("resourceId"))), item => Assert.Equal(1, item.Int("rareflag")));
    }

    [Fact]
    public void PicksOfferAvailableCardsAndRedPicksTheFixedRewardCopies()
    {
        using var rt = Create();
        var pools = rt.Fut.Pools;
        foreach (var (packId, minRating) in new[] { (513L, 82L), (514L, 84L), (515L, 86L) })
        {
            for (var roll = 0; roll < 5; roll++)
            {
                var token = Assert.Single(OpenOwned(rt, packId));
                var choices = PickChoices(rt, token);
                Assert.Equal(4, choices.Count);
                Assert.All(choices, choice =>
                {
                    Assert.True(choice.Int("rating") >= minRating, $"pack {packId}");
                    Assert.True(pools.IsBase(choice.Int("resourceId")) || pools.TotwOneToThreeCards.Contains(choice.Int("resourceId"))
                        || rt.Game.IconCardAt()(choice.Int("resourceId")), $"pack {packId} offered {choice.Int("resourceId")}");
                });
                Assert.Equal(4, choices.Select(choice => choice.Int("assetId")).Distinct().Count());
                rt.Call("POST", $"{G}/playerpicks/item/{choices[0].Int("resourceId")}/select");
            }
        }

        // A red copy made from a later, higher Team of the Week is never offered: Lewandowski's TOTW 2 card is 92.
        var informRatingByAsset = pools.TotwOneToThreeCards.Select(id => rt.Game.Players.Find(id)!)
            .ToDictionary(meta => meta.Int("assetId"), meta => meta.Rating);
        var goldInformAssets = pools.TotwOneToThreeCards.Select(id => rt.Game.Players.Find(id)!)
            .Where(meta => rt.Game.Packs.Rules.TierFor(meta.Rating) == "gold").Select(meta => meta.Int("assetId")).ToHashSet();
        Assert.Equal(goldInformAssets.Order(), pools.TotwOneToThreeRedCards
            .Select(id => rt.Game.Players.Find(id)!.Int("assetId")).Order());
        Assert.Contains(informRatingByAsset.Values, rating => rt.Game.Packs.Rules.TierFor(rating) != "gold");
        Assert.All(pools.TotwOneToThreeRedCards, red =>
        {
            var meta = rt.Game.Players.Find(red)!;
            Assert.Equal(18, meta.Rareflag);
            Assert.Equal("gold", rt.Game.Packs.Rules.TierFor(meta.Rating));
            Assert.Equal(informRatingByAsset[meta.Int("assetId")], meta.Rating);
        });
        foreach (var packId in new long[] { 550, 551, 552, 553 })
        {
            var token = Assert.Single(OpenOwned(rt, packId));
            var choices = PickChoices(rt, token);
            Assert.NotEmpty(choices);
            Assert.All(choices, choice =>
            {
                Assert.Contains(choice.Int("resourceId"), pools.TotwOneToThreeRedCards);
                Assert.Equal("gold", rt.Game.Packs.Rules.TierFor(choice.Int("rating")));
            });
        }
    }

    [Fact]
    public void FixedTotwRewardsStillOpenAfterTheGeneralScheduleEnds()
    {
        using var rt = TestRuntime.Create(marketTarget: 0,
            time: new TestRuntime.FixedTime(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));
        var totw = rt.Fut.Pools.TotwOneToThreeCards;
        Assert.Equal(3, OpenOwned(rt, 540).Count(item => totw.Contains(item.Int("resourceId"))));
        Assert.All(OpenOwned(rt, 542), item => Assert.Contains(item.Int("resourceId"), totw));
        var redToken = Assert.Single(OpenOwned(rt, 550));
        Assert.All(PickChoices(rt, redToken), choice =>
            Assert.Contains(choice.Int("resourceId"), rt.Fut.Pools.TotwOneToThreeRedCards));
    }

    [Fact]
    public void FormerRewardPacksKeepTheirSlotsAndHaveOddsTables()
    {
        using var rt = Create();
        var stored = rt.Game.InUnit(uow => uow.StorePacksAsync());
        // Every player pack and pick has its own odds table since migration 119; the fixed TOTW pool packs keep their own rules.
        foreach (var packId in new[] { "304", "400", "405", "513", "543" })
        {
            Assert.NotEmpty(stored.Obj(packId)!.Obj("category_chances")!);
        }
        Assert.Equal(84, stored.Obj("405")!.Arr("rated_rare_slots")![0]!.AsObject().Int("min_rating"));
        // The elite-rating boost is off.
        Assert.Equal(1.0, rt.Game.Packs.Rules.EliteWeightFor(99));
    }
}
