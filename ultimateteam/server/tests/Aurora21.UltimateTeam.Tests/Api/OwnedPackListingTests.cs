using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class OwnedPackListingTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public async Task EveryOwnedCopyGetsATileAndOpeningConsumesOnlyOneWithItsOriginalTradeability()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        await rt.Game.RunAsync("grant-duplicate-packs", async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.unopened_packs WHERE club_id=@club");
            await work.DeletePileAsync("unassigned");
            await work.AddUnopenedPackAsync(100, "sbc", untradeable: false);
            await work.AddUnopenedPackAsync(100, "draft_single", untradeable: true);
            await work.AddUnopenedPackAsync(100, "sbc", untradeable: true);
            await work.AddUnopenedPackAsync(74, "sbc", untradeable: false);
        }, CancellationToken.None);
        var credits = rt.Game.Store().Credits();
        void CheckListing(int copies)
        {
            var mine = rt.CallObject("GET", $"{G}/store/purchasegroup/mypacks");
            Assert.Equal(copies + 1, mine.Int("total"));
            foreach (var key in new[] { "purchase", "packList", "unopenedPacks", "recoveredPacks", "purchasedPacks" })
            {
                var tiles = mine[key]!.AsArray().OfType<JsonObject>().ToArray();
                Assert.Equal(copies + 1, tiles.Length);
                Assert.Equal(copies, tiles.Count(tile => tile.Int("packId") == 100));
                Assert.Single(tiles, tile => tile.Int("packId") == 74);
                Assert.All(tiles, tile => Assert.Equal(1, tile.Int("quantity")));
            }
            var all = rt.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"]!.AsArray()
                .OfType<JsonObject>().Where(tile => tile.Bool("owned")).ToArray();
            Assert.Equal(copies + 1, all.Length);
            Assert.Equal(copies, all.Count(tile => tile.Int("packId") == 100));
            Assert.All(all, tile => Assert.Equal(1, tile.Int("quantity")));
        }
        CheckListing(3);
        foreach (var (source, untradeable, remaining) in new[] { ("sbc", false, 2), ("draft_single", true, 1), ("sbc", true, 0) })
        {
            var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject
            {
                ["packId"] = 100, ["useCredits"] = 0, ["usePreOrder"] = 1, ["currency"] = "PREORDER"
            });
            var items = opened["itemData"]!.AsArray().OfType<JsonObject>().ToArray();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.Equal(untradeable, item.Bool("untradeable")));
            var sources = rt.Game.InUnit(work => work.QueryAsync(
                "SELECT reward_source FROM ultimateteam.items WHERE club_id=@club AND native_item_id=ANY(@ids)",
                row => row.GetString(0), ("ids", items.Select(item => item.IntKeep("id")).ToArray())));
            Assert.Equal(items.Length, sources.Count);
            Assert.All(sources, actual => Assert.Equal(source, actual));
            Assert.Equal(credits, rt.Game.Store().Credits());
            CheckListing(remaining);
            await rt.Game.RunAsync("clear-test-reveal", work => work.DeletePileAsync("unassigned"), CancellationToken.None);
        }
    }
}
