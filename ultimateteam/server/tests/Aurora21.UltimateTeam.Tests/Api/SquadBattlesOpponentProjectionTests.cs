using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class SquadBattlesOpponentProjectionTests
{
    [Fact]
    public async Task SelectedOpponentInputsMatchFullSnapshotWithOwnedCustomisationAndManager()
    {
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000), marketTarget: 0);
        var definition = runtime.Game.Kits.Kits.First();
        var kit = ItemFactory.KitItem(101, runtime.Game.NowSeconds, definition.IntKeep("resourceId"),
            "free", "club", definition.Int("teamid"), ItemFactory.KitType(definition),
            2 + definition.Int("kitType"), definition.Text("name"));
        var manager = runtime.Fut.Items.BuildManager(1_250_425, 501, "club");
        await runtime.Game.RunAsync("opponent-parity-items",
            work => work.SaveItemsAsync(new[] { kit, manager }), CancellationToken.None);
        await runtime.Fut.Inventory.EquipAsync(101, "activeHomeKit");

        var full = await runtime.Game.ReadAsync(async work =>
        {
            var snapshot = await ClubSnapshot.LoadAsync(work);
            return (Actives: await runtime.Fut.Club.ActiveClubItemsAsync(work, snapshot),
                Manager: runtime.Fut.Club.DefaultManager(snapshot));
        }, CancellationToken.None);
        var selected = await runtime.Game.ReadAsync(async work =>
        {
            var view = await ClubPresentationView.LoadActivesAsync(work,
                runtime.Fut.Club.CustomisationStates(), includeManager: true);
            return (Actives: await runtime.Fut.Club.ActiveClubItemsAsync(work, view),
                Manager: runtime.Fut.Club.DefaultManager(view));
        }, CancellationToken.None);

        Assert.Contains(full.Actives, item => item.Int("id") == 101);
        Assert.Equal(full.Actives.Count, selected.Actives.Count);
        for (var index = 0; index < full.Actives.Count; index++)
            Assert.True(JsonNode.DeepEquals(full.Actives[index], selected.Actives[index]));
        Assert.True(JsonNode.DeepEquals(full.Manager, selected.Manager));

        var opponent = runtime.CallObject("GET", "/ut/game/fifa21/sqbt/user/opponentsquad/0");
        Assert.Equal(23, opponent["players"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(full.Manager, opponent["manager"]![0]));
    }
}
