using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Services.Club;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The squad-save listing guard and the pack duplicate index read a bounded set instead of one row per
/// referenced card or the whole club. These pin the behaviour those batched reads must preserve.
/// </summary>
public sealed class BatchedOwnershipReadTests
{
    private const string G = "/ut/game/fifa21";
    private const long Now = 1700000000;

    private static TestRuntime Create(long persona = 910777, Action<string>? observer = null)
    {
        return TestRuntime.Create(
            time: new TestRuntime.FixedTime(Now), commandObserver: observer, marketTarget: 0,
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "user-config"));
                File.WriteAllText(Path.Combine(root, "user-config", "config.json"), new JsonObject
                {
                    ["persona_id"] = persona,
                    ["nucleus_id"] = persona + 100000,
                    ["display_name"] = "Batched" + persona,
                    ["database"] = new JsonObject { ["deadlock_retries"] = 0 },
                }.ToJsonString());
            });
    }

    private static async Task<JsonObject> AddPlayer(TestRuntime rt, int index = 0, string pile = "club")
    {
        var resource = rt.Game.Players.Entries.ElementAt(index).Key;
        return await rt.Game.RunAsync("batched-read-player", async w =>
        {
            var item = rt.Fut.Items.BuildPlayer(resource, await w.NextItemIdAsync(), pile);
            item["untradeable"] = false;
            await w.SaveItemAsync(item);
            return item;
        }, CancellationToken.None);
    }

    private static JsonObject Squad(long id, params long[] players) => new()
    {
        ["id"] = id,
        ["squadId"] = id,
        ["name"] = "Batched squad",
        ["squadName"] = "Batched squad",
        ["formation"] = "f442",
        ["players"] = new JsonArray(players.Select((p, i) => (JsonNode?)new JsonObject
        { ["index"] = i, ["itemId"] = p, ["kitNumber"] = i + 1 }).ToArray()),
        ["kicktakers"] = new JsonArray(),
    };

    [Fact]
    public async Task SquadSaveStillRejectsAListedCardAndAsksAboutEveryCardAtOnce()
    {
        var commands = new List<string>();
        using var rt = Create(observer: commands.Add);
        var first = await AddPlayer(rt, 0);
        var listed = await AddPlayer(rt, 1);
        var third = await AddPlayer(rt, 2);
        rt.Call("POST", G + "/auctionhouse", new JsonObject
        { ["itemId"] = listed.IntKeep("id"), ["startingBid"] = 150, ["buyNowPrice"] = 1000, ["duration"] = 3600 });

        commands.Clear();
        var refused = await Assert.ThrowsAsync<ClubRuleException>(() => rt.Fut.Inventory
            .SaveSquadAsync(Squad(1, first.Int("id"), listed.Int("id"), third.Int("id"))).AsTask());
        Assert.Equal(ClubInventoryService.ListedItemReason, refused.Message);
        // One question for the whole squad, not one per referenced card.
        Assert.Equal(1, commands.Count(sql => sql.Contains("seller_item_id = ANY(@items)", StringComparison.Ordinal)));

        // A squad that references no listed card still saves.
        commands.Clear();
        await rt.Fut.Inventory.SaveSquadAsync(Squad(1, first.Int("id"), third.Int("id")));
        Assert.Equal(1, commands.Count(sql => sql.Contains("seller_item_id = ANY(@items)", StringComparison.Ordinal)));
        var saved = await rt.Game.ReadAsync(w => w.GetSquadAsync(1), CancellationToken.None);
        Assert.Equal(new[] { first.Int("id"), third.Int("id") }, SquadDocuments.ItemIds(saved!).ToArray());
    }

    [Fact]
    public async Task OwnedPlayerIndexPrefersTheClubPileCardAndNarrowingKeepsTheSameAnswers()
    {
        using var rt = Create();
        // Same resource twice: the squad-pile card is created first, so it holds the lower id. The
        // club-before-squad order, not the id, decides which card a duplicate points at.
        var squadCard = await AddPlayer(rt, 0, pile: "squad");
        var clubCard = await AddPlayer(rt, 0);
        var other = await AddPlayer(rt, 1);
        var resource = ClubInventoryService.ResourceOrAsset(clubCard);
        Assert.Equal(resource, ClubInventoryService.ResourceOrAsset(squadCard));
        Assert.True(squadCard.Int("id") < clubCard.Int("id"));

        var full = await rt.Game.ReadAsync(w => rt.Fut.Inventory.OwnedPlayerIndexAsync(w), CancellationToken.None);
        Assert.Equal(clubCard.Int("id"), full[resource]);

        var narrowed = await rt.Game.ReadAsync(w => rt.Fut.Inventory.OwnedPlayerIndexAsync(w, new[] { resource }),
            CancellationToken.None);
        Assert.Equal(new[] { resource }, narrowed.Keys.ToArray());
        Assert.Equal(full[resource], narrowed[resource]);

        // Narrowing is a filter, never a different answer: every requested key resolves exactly as it
        // does in the whole-club index. (A legacy runtime also owns its seeded starter club, so the
        // comparison is against the requested slice of the full index rather than all of it.)
        var requested = new[] { resource, ClubInventoryService.ResourceOrAsset(other) };
        var both = await rt.Game.ReadAsync(w => rt.Fut.Inventory.OwnedPlayerIndexAsync(w, requested),
            CancellationToken.None);
        Assert.Equal(requested.OrderBy(id => id), both.Keys.OrderBy(id => id));
        Assert.Equal(requested.ToDictionary(id => id, id => full[id]).OrderBy(pair => pair.Key),
            both.OrderBy(pair => pair.Key));

        // An unowned resource simply yields nothing.
        Assert.Empty(await rt.Game.ReadAsync(w => rt.Fut.Inventory.OwnedPlayerIndexAsync(w, new[] { 1L }),
            CancellationToken.None));
    }
}
