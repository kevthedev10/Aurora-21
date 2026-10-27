using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Swap Duplicate keeps the player's card, and packs wait for an empty Unassigned (Python server 328a5b1).</summary>
public sealed class UnassignedAndSwapTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>The store's Gold Pack (migration 095).</summary>
    private const long GoldPack = 74;

    [Fact]
    public void APackWaitsUntilUnassignedIsEmptyAndChargesNothing()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        rt.Game.Store().AddCredits(1_000_000);
        rt.Call("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = GoldPack, ["currency"] = "COINS" });
        var waiting = rt.Game.Store().ListItems("unassigned").Select(item => item.Int("id")).Order().ToList();
        Assert.NotEmpty(waiting);
        var credits = rt.Game.Store().Credits();

        var (status, body, _) = rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = GoldPack, ["currency"] = "COINS" });

        // CardsDLL maps 471 to PACK_EXISTS_IN_PURCHASED_PILE; nothing is charged and the waiting items stay.
        Assert.Equal((471, "471"), (status, body["code"]!.GetValue<string>()));
        Assert.Equal(credits, rt.Game.Store().Credits());
        Assert.Equal(waiting, rt.Game.Store().ListItems("unassigned").Select(item => item.Int("id")).Order());

        // Once the pile is handled, the next pack opens.
        rt.Game.InUnit(uow => uow.DeletePileAsync("unassigned"));
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = GoldPack, ["currency"] = "COINS" }).Status);
    }

    [Fact]
    public void SwapDuplicatePutsTheNewCopyInTheOldOnesPlace()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        List<JsonObject?> SquadCards() => rt.CallObject("GET", $"{G}/squad/active")["players"]!.AsArray().OfType<JsonObject>()
            .Select(slot => slot["itemData"] as JsonObject).ToList();
        var starters = SquadCards().Take(11).Select(card => card!).ToList();
        var old = starters[3];
        var resource = old.Int("resourceId");
        var fresh = rt.Game.InUnit(uow => rt.Fut.Items.MakePlayerAsync(uow, resource, "unassigned"));

        var answer = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = fresh.Int("id"), ["pile"] = "club", ["swap"] = old.Int("id") }),
        });

        Assert.True(answer["itemData"]![0]!["success"]!.GetValue<bool>());
        var items = store.ListItems().ToDictionary(item => item.Int("id"));
        Assert.Contains(items[fresh.Int("id")].Text("pile"), new[] { "club", "squad" });
        Assert.Equal("unassigned", items[old.Int("id")].Text("pile"));
        Assert.True(items[old.Int("id")].Bool("duplicate"));
        // The squad slot that held the old copy now holds the new one.
        var slots = SquadCards().Select(card => card?.Int("id")).ToList();
        Assert.Contains(fresh.Int("id"), slots);
        Assert.DoesNotContain(old.Int("id"), slots);

        // A swap naming another card changes nothing.
        var other = rt.Game.InUnit(uow => rt.Fut.Items.MakePlayerAsync(uow, starters[4].Int("resourceId"), "unassigned"));
        rt.Call("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = other.Int("id"), ["pile"] = "club", ["swap"] = starters[5].Int("id") }),
        });
        Assert.Contains(store.ListItems().Single(item => item.Int("id") == starters[5].Int("id")).Text("pile"), new[] { "club", "squad" });
    }
}
