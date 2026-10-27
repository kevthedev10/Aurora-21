using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class LegacyMatchPackTests
{
    [Fact]
    public void AlreadyEarnedMatchPackOpensItsStoredContentsAfterRetirement()
    {
        using var rt = TestRuntime.Create();
        var player = rt.Game.Players.Entries.First(entry => entry.Value.Rareflag == 1 && entry.Value.Rating is >= 75 and <= 81 && entry.Key <= 0x00FFFFFF).Key;
        var style = rt.Game.Cards.ChemistryStyles.First(entry => entry.Name != "Basic").ResourceId;
        var kit = rt.Game.Kits.Kits.First().Int("resourceId");
        var stored = new JsonObject { ["tier"] = "standard", ["highlight"] = false,
            ["players"] = new JsonArray(player), ["chemistryStyle"] = style, ["kit"] = kit };
        rt.Game.InUnit(uow => uow.AddRolledPackAsync(544, "match_pack", stored, true));
        rt.Game.Store().DeletePile("unassigned");
        var opened = rt.CallObject("POST", "/ut/game/fifa21/purchased/items", new JsonObject {
            ["packId"] = 544, ["useCredits"] = 0, ["usePreOrder"] = 1, ["currency"] = "PREORDER" });
        Assert.Equal(3, opened["itemList"]!.AsArray().Count);
        Assert.Equal(new[] { player, style, kit }.Order(), rt.Game.Store().ListItems("unassigned").Select(item => item.Int("resourceId")).Order());
        Assert.All(rt.Game.Store().ListItems("unassigned"), item => Assert.True(item.Bool("untradeable")));
        Assert.DoesNotContain(544L, rt.Game.Store().UnopenedPackIds());
    }
}
