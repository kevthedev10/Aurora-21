using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A club keeps one copy of a manager or club item, as of a player: a second copy is a duplicate and stays in Unassigned
/// (live 10-05: clubs held up to 12 copies of one manager, since the duplicate rule only looked at players).
/// </summary>
public sealed class DuplicateClubCardTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>A manager the club owns in its club pile, set as the active squad's manager.</summary>
    private static async Task<(long Resource, long Owned)> SquadManagerAsync(TestRuntime rt) =>
        await rt.Game.RunAsync("managers", async work =>
        {
            var resource = rt.Game.Managers.Entries.OrderBy(entry => entry.Value.Int("rating")).First().Key;
            var manager = await rt.Fut.Items.MakeManagerAsync(work, resource, "club");
            var squad = (await work.ListSquadsAsync())[0];
            squad["manager"] = new JsonArray(new JsonObject { ["id"] = manager.Int("id"), ["dream"] = false });
            await work.SaveSquadAsync(squad.Int("id"), squad.Text("squadName"), true, squad);
            return (resource, manager.Int("id"));
        }, CancellationToken.None);

    [Fact]
    public async Task ASecondCopyOfAnOwnedManagerStaysInUnassigned()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var (resource, owned) = await SquadManagerAsync(rt);
        var copy = rt.Game.InUnit(uow => rt.Fut.Items.MakeManagerAsync(uow, resource, "unassigned")).Int("id");

        var response = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = copy, ["pile"] = "club" }),
        });

        var result = Assert.Single(response["itemData"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(("unassigned", true), (result.Text("pile"), result.Bool("success")));
        Assert.Equal(new[] { copy }, response["duplicateItemIdList"]!.AsArray().OfType<JsonObject>().Select(entry => entry.Int("itemId")));
        Assert.Equal("unassigned", store.GetItem(copy)!.Text("pile"));
        Assert.True(store.GetItem(copy)!.Bool("duplicate"));

        // The Unassigned screen lists it as a duplicate of the club's manager.
        var marked = Assert.Single(rt.CallObject("GET", $"{G}/purchased/items")["duplicateItemIdList"]!.AsArray().OfType<JsonObject>());
        Assert.Equal((copy, owned), (marked.Int("itemId"), marked.Int("duplicateItemId")));
    }

    [Fact]
    public void TwoCopiesOfANewManagerInOneBatchKeepOne()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var owned = store.ListItems().Where(item => item.Text("itemType") == "manager").Select(item => item.Int("resourceId")).ToHashSet();
        var resource = rt.Game.Managers.Entries.Select(entry => entry.Key).First(id => !owned.Contains(id));
        var first = rt.Game.InUnit(uow => rt.Fut.Items.MakeManagerAsync(uow, resource, "unassigned")).Int("id");
        var second = rt.Game.InUnit(uow => rt.Fut.Items.MakeManagerAsync(uow, resource, "unassigned")).Int("id");

        // Store All.
        var response = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = first, ["pile"] = "club" }, new JsonObject { ["id"] = second, ["pile"] = "club" }),
        });

        var results = response["itemData"]!.AsArray().OfType<JsonObject>().ToDictionary(entry => entry.Int("id"));
        Assert.Equal("club", results[first].Text("pile"));
        Assert.Equal("unassigned", results[second].Text("pile"));
        Assert.Equal("club", store.GetItem(first)!.Text("pile"));
        Assert.Equal("unassigned", store.GetItem(second)!.Text("pile"));
    }

    [Fact]
    public async Task SwapDuplicatePutsTheNewManagerInTheSquadsManagerSlot()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var (resource, owned) = await SquadManagerAsync(rt);
        var fresh = rt.Game.InUnit(uow => rt.Fut.Items.MakeManagerAsync(uow, resource, "unassigned")).Int("id");

        var answer = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = fresh, ["pile"] = "club", ["swap"] = owned }),
        });

        Assert.True(answer["itemData"]![0]!["success"]!.GetValue<bool>());
        Assert.Equal("club", store.GetItem(fresh)!.Text("pile"));
        Assert.Equal("unassigned", store.GetItem(owned)!.Text("pile"));
        Assert.True(store.GetItem(owned)!.Bool("duplicate"));
        Assert.Equal(fresh, rt.CallObject("GET", $"{G}/squad/active")["manager"]![0]!.AsObject().Int("id"));
    }

    private static JsonObject Kit(TestRuntime rt, long id, string pile)
    {
        var kit = rt.Game.Kits.Kits.First();
        return ItemFactory.KitItem(id, rt.Game.NowSeconds, kit.IntKeep("resourceId"), "free", pile, kit.Int("teamid"), ItemFactory.KitType(kit),
            2 + kit.Int("kitType"), kit.Text("name"));
    }

    [Fact]
    public void ASecondCopyOfAnOwnedKitStaysInUnassigned()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var owned = Kit(rt, store.NextItemId(), "club");
        store.SaveItem(owned);
        var copy = Kit(rt, store.NextItemId(), "unassigned");
        store.SaveItem(copy);

        var response = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = copy.Int("id"), ["pile"] = "club", ["swap"] = 0, ["tradeId"] = 0 }),
        });

        var result = Assert.Single(response["itemData"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(("unassigned", true), (result.Text("pile"), result.Bool("success")));
        Assert.Equal("unassigned", store.GetItem(copy.Int("id"))!.Text("pile"));
        var marked = Assert.Single(rt.CallObject("GET", $"{G}/purchased/items")["duplicateItemIdList"]!.AsArray().OfType<JsonObject>());
        Assert.Equal((copy.Int("id"), owned.Int("id")), (marked.Int("itemId"), marked.Int("duplicateItemId")));
    }

    [Fact]
    public async Task MigrationUnassignsExtraCopiesAndKeepsTheSquadsManager()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var resource = rt.Game.Managers.Entries.OrderBy(entry => entry.Value.Int("rating")).First().Key;
        var (older, squadCopy) = await rt.Game.RunAsync("managers", async work =>
        {
            var first = await rt.Fut.Items.MakeManagerAsync(work, resource, "club");
            var second = await rt.Fut.Items.MakeManagerAsync(work, resource, "club");
            var squad = (await work.ListSquadsAsync())[0];
            squad["manager"] = new JsonArray(new JsonObject { ["id"] = second.Int("id"), ["dream"] = false });
            await work.SaveSquadAsync(squad.Int("id"), squad.Text("squadName"), true, squad);
            return (first.Int("id"), second.Int("id"));
        }, CancellationToken.None);
        var kits = new[] { Kit(rt, store.NextItemId(), "club"), Kit(rt, store.NextItemId() + 1, "club") };
        foreach (var kit in kits) store.SaveItem(kit);

        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=160");
            using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/160-unassign-duplicate-club-cards.sql")!;
            using var reader = new StreamReader(stream);
            await work.ExecuteAsync(await reader.ReadToEndAsync());
            return 0;
        });

        // The squad's copy stays its manager; the other copy and the second kit wait in Unassigned.
        Assert.Equal("club", store.GetItem(squadCopy)!.Text("pile"));
        Assert.Equal("unassigned", store.GetItem(older)!.Text("pile"));
        Assert.Equal(squadCopy, rt.CallObject("GET", $"{G}/squad/active")["manager"]![0]!.AsObject().Int("id"));
        Assert.Equal(new[] { "club", "unassigned" }, kits.Select(kit => store.GetItem(kit.Int("id"))!.Text("pile")));
    }
}
