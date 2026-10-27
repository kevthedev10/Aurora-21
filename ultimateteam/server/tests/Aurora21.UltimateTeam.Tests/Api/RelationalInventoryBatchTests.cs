using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalInventoryBatchTests
{
    private static TestRuntime Create(DatabaseCommandCounter? counter = null, Action<string>? observer = null, int packSize = 12)
    {
        var connection = PostgresTestCluster.CreateInstalledDatabase();
        using (var source = NpgsqlDataSource.Create(connection))
        {
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
            using var command = source.CreateCommand("""
                INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,manager_chance,chem_style_chance,allowed_rareflags)
                VALUES(90,'store',true,'Synthetic batch regression',0,@size,100,0,0,0,0,0,ARRAY[0]::bigint[])
                """);
            command.Parameters.AddWithValue("size", packSize);
            command.ExecuteNonQuery();
        }
        return TestRuntime.Create(connectionString: connection, relationalSlice: true, commandCounter: counter,
            commandObserver: observer, time: new TestRuntime.FixedTime(1700000000));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(50)]
    public async Task DuplicatePackPersistsCorrectReferencesWithBoundedQueries(int size)
    {
        var counter = new DatabaseCommandCounter();
        using var rt = Create(counter, packSize: size);
        var resources = rt.Game.Players.Entries.Where(entry => entry.Value.Rating >= 75 && entry.Value.Rareflag == 0)
            .Select(entry => entry.Key).ToArray();
        var owned = resources.Select((resource, index) => rt.Fut.Items.BuildPlayer(resource, 10000 + index, "club")).ToArray();
        await rt.Game.RunAsync("own-pack-pool", w => w.SaveItemsAsync(owned), CancellationToken.None);
        var reference = owned.ToDictionary(item => item.IntKeep("resourceId"), item => item.IntKeep("id"));
        var mark = counter.Mark();
        var pack = await rt.Game.RunAsync("batch-pack", w => rt.Fut.Packs.CreatePackAsync(w, 90), CancellationToken.None);
        var commands = counter.Since(mark);
        Assert.InRange(commands.Count, 1, 55);
        Assert.Single(commands, sql => sql.StartsWith("INSERT INTO ultimateteam.items", StringComparison.Ordinal));
        var items = pack.Arr("itemData")!.OfType<JsonObject>().ToArray();
        var duplicates = pack.Arr("duplicateItemIdList") ?? pack.Arr("duplicates");
        Assert.Equal(size, items.Length);
        Assert.All(items, item => { Assert.True(item.Bool("duplicate")); Assert.True(item.Bool("isDuplicate")); Assert.Equal("unassigned", item.Text("pile")); });
        Assert.NotNull(duplicates);
        Assert.Equal(size, duplicates!.Count);
        var byItem = items.ToDictionary(item => item.IntKeep("id"));
        foreach (var duplicate in duplicates.OfType<JsonObject>())
            Assert.Equal(reference[byItem[duplicate.IntKeep("itemId")].IntKeep("resourceId")], duplicate.IntKeep("duplicateItemId"));
        var persisted = await rt.Game.ReadAsync(w => w.GetItemsAsync(byItem.Keys.ToArray()), CancellationToken.None);
        foreach (var item in persisted) Assert.True(JsonNode.DeepEquals(byItem[item.IntKeep("id")], item));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(50)]
    public async Task DiscardQueryBudgetDoesNotGrowPerCardAndPreservesOrderAndPayout(int size)
    {
        var counter = new DatabaseCommandCounter();
        using var rt = Create(counter);
        var resource = rt.Game.Players.Entries.First(entry => entry.Value.Rareflag == 0).Key;
        var items = Enumerable.Range(0, size).Select(index => rt.Fut.Items.BuildPlayer(resource, 100 + index, "unassigned")).ToArray();
        await rt.Game.RunAsync("own-batch", w => w.SaveItemsAsync(items), CancellationToken.None);
        var balance = await rt.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None);
        var ids = items.Select(item => item.IntKeep("id")).Reverse().ToArray();
        var mark = counter.Mark();
        var result = await rt.Fut.Inventory.DiscardItemsAsync(ids.Append(ids[0]).Append(999999).Append(0)
            .Select(id => (JsonNode?)JsonValue.Create(id)));
        var commands = counter.Since(mark);
        Assert.InRange(commands.Count, 1, 25);
        Assert.Single(commands, sql => sql.Contains("query: inventory.headers", StringComparison.Ordinal));
        Assert.Single(commands, sql => sql.Contains("query: inventory.discard-guards", StringComparison.Ordinal));
        Assert.Single(commands, sql => sql.Contains("query: inventory.delete-batch", StringComparison.Ordinal));
        Assert.Equal(ids, result.Deleted);
        Assert.Equal(balance + size * rt.Game.Cards.DiscardValue(resource), result.Credits);
        Assert.Empty(await rt.Game.ReadAsync(w => w.ListItemsAsync(), CancellationToken.None));
        var replay = await rt.Fut.Inventory.DiscardItemsAsync(ids.Select(id => (JsonNode?)JsonValue.Create(id)));
        Assert.Empty(replay.Deleted);
        Assert.Equal(result.Credits, replay.Credits);
    }

    [Fact]
    public async Task MixedDiscardReleasesSquadCardsAndProtectsSlotListingAndForeignItems()
    {
        using var rt = Create();
        var resources = rt.Game.Players.Entries.Where(entry => entry.Value.Rareflag == 0 && entry.Value.Rating is >= 75 and <= 78)
            .GroupBy(entry => entry.Value.Int("assetId")).Select(group => group.First().Key).Take(5).ToArray();
        var items = resources.Select((resource, index) => rt.Fut.Items.BuildPlayer(resource, 100 + index, "club")).ToArray();
        items[1]["pile"] = "squad";
        items[2]["untradeable"] = false;
        items[3]["discardValue"] = 9999999;
        await rt.Game.RunAsync("own-mixed", w => w.SaveItemsAsync(items), CancellationToken.None);
        await rt.Fut.Inventory.SaveSquadAsync(new JsonObject
        {
            ["id"] = 1, ["name"] = "Synthetic binding", ["formation"] = "f442",
            ["players"] = new JsonArray(new JsonObject { ["index"] = 0, ["itemId"] = 100 }), ["kicktakers"] = new JsonArray()
        });
        var kit = rt.Game.Kits.Kits.First();
        var kitItem = ItemFactory.KitItem(200, rt.Game.NowSeconds, kit.IntKeep("resourceId"), "free", "club",
            kit.Int("teamid"), ItemFactory.KitType(kit), 2 + kit.Int("kitType"), kit.Text("name"));
        await rt.Game.RunAsync("own-kit", w => w.SaveItemAsync(kitItem), CancellationToken.None);
        await rt.Fut.Inventory.EquipAsync(200, "activeHomeKit");
        Assert.Equal(200, rt.CallRaw("POST", "/ut/game/fifa21/auctionhouse", new JsonObject
            { ["itemId"] = 102, ["startingBid"] = 150, ["buyNowPrice"] = 1000 }).Status);
        var other = await rt.Game.RunAsync("other-club", w => w.EnsureRelationalClubAsync(new ClubIdentity(987001, 987002), 0), CancellationToken.None);
        await rt.Game.RunAsync("foreign-item", w => w.SaveItemOfAsync(other.Account.ClubId, 300, rt.Fut.Items.BuildPlayer(resources[0], 300, "club")), CancellationToken.None);
        var balance = await rt.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None);
        var result = await rt.Fut.Inventory.DiscardItemsAsync(new long[] { 104, 100, 101, 102, 200, 300, 103, 104, 999999 }
            .Select(id => (JsonNode?)JsonValue.Create(id)));
        Assert.Equal(new long[] { 104, 100, 103 }, result.Deleted);
        Assert.Equal(balance + rt.Game.Cards.DiscardValue(resources[0]) + rt.Game.Cards.DiscardValue(resources[3])
            + rt.Game.Cards.DiscardValue(resources[4]), result.Credits);
        Assert.False((await rt.Game.ReadAsync(w => w.GetSquadAsync(1), CancellationToken.None))!.Arr("players")!.OfType<JsonObject>().Single().ContainsKey("itemId"));
        Assert.Equal(new long[] { 101, 102, 200 }, (await rt.Game.ReadAsync(w => w.ListItemsAsync(), CancellationToken.None)).Select(item => item.IntKeep("id")));
        Assert.NotNull(await rt.Game.ReadAsync(w => w.ItemOfAsync(other.Account.ClubId, 300), CancellationToken.None));
    }

    [Fact]
    public async Task CreditFailureRollsBackTheEntireDiscardBatch()
    {
        var fail = false;
        using var rt = Create(observer: sql =>
        {
            if (fail && sql.StartsWith("UPDATE ultimateteam.clubs SET credits", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic payout failure");
        });
        var resources = rt.Game.Players.Ids.Take(12).ToArray();
        await rt.Game.RunAsync("own-rollback", w => w.SaveItemsAsync(resources.Select((resource, index) => rt.Fut.Items.BuildPlayer(resource, 100 + index, "unassigned"))), CancellationToken.None);
        var before = await rt.Game.ReadAsync(w => w.ListItemsAsync(), CancellationToken.None);
        var balance = await rt.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None);
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Fut.Inventory.DiscardItemsAsync(
            Enumerable.Range(100, 12).Select(id => (JsonNode?)JsonValue.Create(id))).AsTask());
        fail = false;
        Assert.Equal(balance, await rt.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None));
        var after = await rt.Game.ReadAsync(w => w.ListItemsAsync(), CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(new JsonArray(before.Select(item => (JsonNode?)item).ToArray()), new JsonArray(after.Select(item => (JsonNode?)item).ToArray())));
    }
}
