using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>PUT /item applies a batch of moves in order: each move sees the ones before it.</summary>
public sealed class ItemMoveTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public void ABatchSeesItsEarlierMovesAndSkipsUnknownItems()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var owned = store.ListItems("club").Concat(store.ListItems("squad")).Select(item => item.Int("resourceId")).ToHashSet();
        var resource = rt.Game.Players.Ids.First(id => !owned.Contains(id));
        long Grant()
        {
            var item = rt.Fut.Items.BuildPlayer(resource, store.NextItemId(), "unassigned");
            store.SaveItem(item);
            return item.Int("id");
        }
        var first = Grant();
        var second = Grant();
        const long Unknown = 999_999_999_999;

        var response = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(
                new JsonObject { ["id"] = first, ["pile"] = "club" },
                new JsonObject { ["id"] = second, ["pile"] = "club" },
                new JsonObject { ["id"] = Unknown, ["pile"] = "club" }),
        });

        var results = response["itemData"]!.AsArray().OfType<JsonObject>().ToDictionary(result => result.Int("id"));
        // The first card joins the club, so the second of the same player is a duplicate and stays Unassigned.
        Assert.Equal(("club", true), (results[first].Text("pile"), results[first].Bool("success")));
        Assert.Equal(("unassigned", true), (results[second].Text("pile"), results[second].Bool("success")));
        Assert.False(results[Unknown].Bool("success"));
        Assert.Equal(new[] { second }, response["duplicateItemIdList"]!.AsArray().OfType<JsonObject>().Select(entry => entry.Int("itemId")));
        Assert.Equal("club", store.GetItem(first)!.Text("pile"));
        Assert.Equal("unassigned", store.GetItem(second)!.Text("pile"));
        Assert.True(store.GetItem(second)!.Bool("duplicate"));

        // The Unassigned screen lists it as a duplicate of the club's card.
        var unassigned = rt.CallObject("GET", $"{G}/purchased/items");
        var marked = Assert.Single(unassigned["duplicateItemIdList"]!.AsArray().OfType<JsonObject>());
        Assert.Equal((second, first), (marked.Int("itemId"), marked.Int("duplicateItemId")));
    }

    [Fact]
    public void SendingToPurchasedKeepsTheCardInUnassigned()
    {
        // Transfer Targets sends won items to "purchased", FIFA's wire name for Unassigned (live 10-04: stored as-is, the
        // cards left every screen).
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var card = store.ListItems("club").First(item => item.Int("resourceId") > 0);
        var id = card.Int("id");

        var response = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = id, ["pile"] = "purchased", ["swap"] = 0, ["tradeId"] = 0 }),
        });

        var result = Assert.Single(response["itemData"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(("purchased", true), (result.Text("pile"), result.Bool("success")));
        Assert.Equal("unassigned", store.GetItem(id)!.Text("pile"));
        Assert.Contains(rt.CallObject("GET", $"{G}/purchased/items")["itemData"]!.AsArray().OfType<JsonObject>(), item => item.Int("id") == id);
    }
}
