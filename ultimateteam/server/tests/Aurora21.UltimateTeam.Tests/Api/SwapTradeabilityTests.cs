using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Swap Duplicate leaves each copy its own tradeability and reward source, as in FIFA 21: swapping an untradeable duplicate
/// in sends the tradeable club copy to Unassigned, where it can be listed or quick sold at its full value.
/// </summary>
public sealed class SwapTradeabilityTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>A club copy and an Unassigned copy of one base gold rare, each with its own restriction and source.</summary>
    private static (long Old, long New) Copies(TestRuntime rt, bool oldUntradeable, string? oldSource, bool newUntradeable, string? newSource)
    {
        var store = rt.Game.Store();
        store.DeletePile("unassigned");
        var owned = store.ListItems().Where(ClubInventoryService.IsPlayer).Select(item => item.Int("resourceId")).ToHashSet();
        // A gold rare the club does not own yet (the local club starts with its best 250 cards).
        var resource = rt.Game.Players.Entries.First(entry => entry.Key < PlayerCatalog.ResourceRevisionSize && entry.Value.Rareflag == 1
            && !entry.Value.IsSpecial && entry.Value.Rating is >= 82 and <= 85 && !rt.Game.Cards.IsGrantOnly(entry.Key) && !owned.Contains(entry.Key)).Key;
        return rt.Game.InUnit(async uow =>
        {
            JsonObject Build(long id, string pile, bool untradeable, string? source)
            {
                var item = rt.Fut.Items.BuildPlayer(resource, id, pile);
                if (source is not null)
                {
                    rt.Fut.Items.MarkReward(item, source, untradeable);
                }
                return item;
            }
            var old = Build(await uow.NextItemIdAsync(), "club", oldUntradeable, oldSource);
            var fresh = Build(await uow.NextItemIdAsync(), "unassigned", newUntradeable, newSource);
            await uow.SaveItemsAsync(new[] { old, fresh });
            return (old.Int("id"), fresh.Int("id"));
        });
    }

    private static void Swap(TestRuntime rt, long fresh, long old)
    {
        var answer = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = fresh, ["pile"] = "club", ["swap"] = old }),
        });
        Assert.True(answer["itemData"]![0]!["success"]!.GetValue<bool>());
    }

    private static string? RewardSource(TestRuntime rt, long itemId) => rt.Game.InUnit(uow => uow.ScalarAsync<string?>(
        "SELECT reward_source FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@id", ("id", itemId)));

    [Fact]
    public async Task SwappingAnUntradeableDuplicateInFreesTheTradeableClubCopyToSell()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var (old, fresh) = Copies(rt, oldUntradeable: false, oldSource: null, newUntradeable: true, newSource: RewardSources.MatchPack);

        Swap(rt, fresh, old);

        // The untradeable copy joins the club as it is; the tradeable club copy leaves for Unassigned still tradeable.
        var joined = store.GetItem(fresh)!;
        var leaving = store.GetItem(old)!;
        Assert.Contains(joined.Text("pile"), new[] { "club", "squad" });
        Assert.Equal("unassigned", leaving.Text("pile"));
        Assert.True(joined.Bool("untradeable"));
        Assert.Equal(RewardSources.MatchPack, RewardSource(rt, fresh));
        Assert.False(leaving.Bool("untradeable"));
        Assert.Null(RewardSource(rt, old));
        Assert.Equal(rt.Game.Cards.DiscardValue(leaving.Int("resourceId")), leaving.Int("discardValue"));

        // Quick-selling the copy that left pays its full value.
        var credits = store.Credits();
        Assert.Equal(new[] { old }, (await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[] { JsonValue.Create(old) })).Deleted);
        Assert.Equal(credits + rt.Game.Cards.DiscardValue(leaving.Int("resourceId")), store.Credits());
    }

    [Fact]
    public void TheTradeableClubCopySwappedOutCanBeListed()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var (old, fresh) = Copies(rt, oldUntradeable: false, oldSource: null, newUntradeable: true, newSource: RewardSources.Pick);

        Swap(rt, fresh, old);

        var leaving = store.GetItem(old)!;
        leaving["pile"] = "trade";
        store.SaveItem(leaving);
        var answer = rt.CallObject("POST", $"{G}/auctionhouse",
            new JsonObject { ["itemId"] = old, ["startingBid"] = 150, ["buyNowPrice"] = 20_000 });
        Assert.True(answer["tradeId"].L() > 0, answer.ToJsonString());
    }

    [Fact]
    public void SwappingATradeableCopyInKeepsBothRestrictions()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var (old, fresh) = Copies(rt, oldUntradeable: true, oldSource: RewardSources.Pick, newUntradeable: false, newSource: RewardSources.Sbc);

        Swap(rt, fresh, old);

        var joined = store.GetItem(fresh)!;
        var leaving = store.GetItem(old)!;
        Assert.False(joined.Bool("untradeable"));
        Assert.Equal(RewardSources.Sbc, RewardSource(rt, fresh));
        Assert.True(leaving.Bool("untradeable"));
        Assert.Equal(RewardSources.Pick, RewardSource(rt, old));
        Assert.Equal(Math.Min(rt.Game.Cards.DiscardValue(leaving.Int("resourceId")), rt.Game.Rules.Cards.QuickSell.RewardUntradeableCap), leaving.Int("discardValue"));
    }

    [Fact]
    public void SwappingCopiesAlikeChangesNoRestriction()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var (old, fresh) = Copies(rt, oldUntradeable: true, oldSource: RewardSources.Objective, newUntradeable: true, newSource: RewardSources.Objective);

        Swap(rt, fresh, old);

        Assert.True(store.GetItem(fresh)!.Bool("untradeable"));
        Assert.True(store.GetItem(old)!.Bool("untradeable"));
        Assert.Equal((RewardSources.Objective, RewardSources.Objective), (RewardSource(rt, fresh), RewardSource(rt, old)));
    }
}
