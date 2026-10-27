using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A 1-of-N pick never grants a tradeable card from an untradeable grant (PLAN 2 item 7, 3.1): the chosen player inherits
/// the restriction and reward source of the grant its token came with, and rules/tradeability.json can make every pick
/// untradeable. A token is itself always untradeable (items/player-pick-token).
/// </summary>
public sealed class PickTradeabilityTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Grants pack 513 (82+ pick, 1 of 4) from <paramref name="source"/>, opens it from My Packs and returns the token.</summary>
    private static JsonObject OpenPickPack(TestRuntime rt, string source)
    {
        rt.Game.Store().DeletePile("unassigned");
        rt.Game.InUnit(uow => uow.AddRewardPackAsync(rt.Game.Rules.Tradeability, 513, source));
        var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 513 });
        Assert.Equal("PURCHASECOMPLETE", opened["state"].S());
        var token = rt.Game.Store().ListItems("unassigned").Single(item => item.Int("minRating") == 82);
        Assert.True(token.Bool("untradeable"));
        return token;
    }

    private static string? RewardSource(TestRuntime rt, long itemId) => rt.Game.InUnit(uow => uow.ScalarAsync<string?>(
        "SELECT reward_source FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@id", ("id", itemId)));

    [Fact]
    public void AnUntradeablePickGrantsAnUntradeablePlayer()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        // The Welcome picks come from objectives; every pick pack is untradeable (rules/tradeability.json packs).
        Assert.True(rt.Game.Rules.Tradeability.Untradeable(RewardSources.Objective, 513));
        var token = OpenPickPack(rt, RewardSources.Objective);
        Assert.Equal(RewardSources.Objective, RewardSource(rt, token.Int("id")));

        // The choices already show as untradeable, with the value quick sell would pay, and no server-only key.
        var choices = rt.Fut.Picks.Redeem(token.Int("id")).OfType<JsonObject>().Select(rt.Fut.Native.PackItem).ToList();
        Assert.Equal(4, choices.Count);
        Assert.All(choices, choice => Assert.False(choice.ContainsKey(RewardSources.ItemKey)));
        Assert.All(choices, choice => Assert.True(choice.Bool("untradeable")));
        Assert.All(choices, choice => Assert.Equal(Math.Min(rt.Game.Cards.DiscardValue(choice.Int("resourceId")), rt.Game.Rules.Cards.QuickSell.RewardUntradeableCap), choice.Int("discardValue")));

        var selected = rt.Fut.Picks.Select(token.Int("id"), JsonValue.Create(1));

        var granted = rt.Game.Store().GetItem(selected.Int("id"))!;
        Assert.True(selected.Bool("untradeable"));
        Assert.True(granted.Bool("untradeable"));
        Assert.Equal(RewardSources.Objective, RewardSource(rt, granted.Int("id")));
        Assert.Equal(Math.Min(rt.Game.Cards.DiscardValue(granted.Int("resourceId")), rt.Game.Rules.Cards.QuickSell.RewardUntradeableCap), granted.Int("discardValue"));
        Assert.False(rt.Game.Store().ItemExists(token.Int("id")));
        // An untradeable pick cannot reach the transfer market.
        granted["pile"] = "club";
        rt.Game.Store().SaveItem(granted);
        var listing = rt.CallObject("POST", $"{G}/auctionhouse", new JsonObject { ["itemId"] = granted.Int("id"), ["startingBid"] = 150, ["buyNowPrice"] = 10_000 });
        Assert.Equal("this card is untradeable", listing["reason"].S());
        Assert.DoesNotContain(rt.Game.Store().ActiveMarketListings(), active => active.SellerItemId == granted.Int("id"));
    }

    [Fact]
    public void TheGrantAndThePickSourceDecideTheRestriction()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var tradeability = rt.Game.Rules.Tradeability;
        // Rules edits, no code: the pick pack tradeable from SBCs, and picks not untradeable in themselves.
        tradeability.Packs[513] = false;
        tradeability.Sources[RewardSources.Pick] = false;
        var token = OpenPickPack(rt, RewardSources.Sbc);
        var free = rt.Game.Store().GetItem(rt.Fut.Picks.Select(token.Int("id"), JsonValue.Create(0)).Int("id"))!;
        Assert.False(free.Bool("untradeable"));
        Assert.Equal(RewardSources.Sbc, RewardSource(rt, free.Int("id")));
        Assert.Equal(rt.Game.Cards.DiscardValue(free.Int("resourceId")), free.Int("discardValue"));

        // The pick source untradeable again: the same tradeable grant now yields an untradeable player.
        tradeability.Sources[RewardSources.Pick] = true;
        var second = OpenPickPack(rt, RewardSources.Sbc);
        var restricted = rt.Game.Store().GetItem(rt.Fut.Picks.Select(second.Int("id"), JsonValue.Create(0)).Int("id"))!;
        Assert.True(restricted.Bool("untradeable"));

        // An untradeable grant (Rivals option C) restricts the player even when picks alone would not. The pack override is
        // removed too: a pack's own override beats its source's value.
        tradeability.Sources[RewardSources.Pick] = false;
        tradeability.Packs.Remove(513);
        var third = OpenPickPack(rt, RewardSources.RivalsOptionC);
        var optionC = rt.Game.Store().GetItem(rt.Fut.Picks.Select(third.Int("id"), JsonValue.Create(0)).Int("id"))!;
        Assert.True(optionC.Bool("untradeable"));
        Assert.Equal(RewardSources.RivalsOptionC, RewardSource(rt, optionC.Int("id")));
    }

    [Fact]
    public void ATokenWithoutASourceGrantsAPickRewardThatNeverSellsAboveTheCap()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        rt.Game.Store().DeletePile("unassigned");
        // A token made directly (no pack grant), as an older runtime left it.
        var token = rt.Game.InUnit(uow => rt.Fut.Items.MakePlayerPickAsync(uow, 86, 2));
        var chosen = rt.Fut.Picks.Select(token.Int("id"), JsonValue.Create(0));
        var stored = rt.Game.Store().GetItem(chosen.Int("id"))!;
        Assert.True(stored.Bool("untradeable"));
        Assert.Equal(RewardSources.Pick, RewardSource(rt, stored.Int("id")));
        Assert.Equal(Math.Min(rt.Game.Cards.DiscardValue(stored.Int("resourceId")), rt.Game.Rules.Cards.QuickSell.RewardUntradeableCap), stored.Int("discardValue"));
    }
}
