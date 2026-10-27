using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class CardRarityGroupTests
{
    [Fact]
    public void ExplicitGroupsReachNewItemsAndNativeProjection()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        Assert.NotNull(rt.Game.Players.NativeRarityGroups);
        var nonRare = new HashSet<long> { 0, 47, 52, 76, 77, 89, 92, 93, 94 };
        foreach (var code in rt.Game.Rules.Cards.RarityGroupsByRareflag.Keys)
        {
            var expected = nonRare.Contains(code) ? Array.Empty<long>() : new long[] { 4 };
            Assert.Equal(expected, rt.Game.Cards.RarityGroups(code).Select(node => JsonValues.Int(node)));
            var entry = rt.Game.Players.Entries.First(e => e.Value.Rareflag == code);
            var item = rt.Fut.Items.BuildPlayer(entry.Key, 123, "club");
            Assert.Equal(expected, (item.Arr("groups") ?? new JsonArray()).Select(node => JsonValues.Int(node)));
            // A historical/client supplied group must never override current catalog membership.
            item["groups"] = new JsonArray(4);
            var native = rt.Fut.Native.PlayerItem(item);
            Assert.Equal(expected, (native.Arr("groups") ?? new JsonArray()).Select(node => JsonValues.Int(node)));
        }
        Assert.Empty(rt.Game.Cards.RarityGroups(999999));
    }

    [Fact]
    public async Task IconSwapCannotSatisfyRareRequirementEvenWithForgedClientGroup()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        // An open, listed challenge: a brick wall would refuse eleven players in its locked slots.
        var challenge = rt.Game.Sbc.Sets.Single(set => set.Key == "FND_HYBRID_NATIONS").Challenges[0];
        challenge.Source["requirements"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["type"] = "PLAYERS", ["attributes"] = new JsonObject { ["min"] = 11, ["max"] = 11 } },
                new JsonObject { ["type"] = "RARE", ["attributes"] = new JsonObject { ["min"] = 1 } })
        };
        var footballers = new HashSet<long>();
        var definitions = rt.Game.Players.Entries.Where(e => e.Value.Rareflag == 0 && footballers.Add(e.Value.Int("assetId"))).Take(10).ToList();
        var icon = rt.Game.Players.Entries.First(e => e.Value.Rareflag == 89 && !footballers.Contains(e.Value.Int("assetId")));
        definitions.Add(icon);
        var items = definitions.Select(e => rt.Fut.Items.MakePlayer(e.Key, "club")).ToList();
        JsonObject Squad() => new() { ["players"] = new JsonArray(items.Select(item => (JsonNode)new JsonObject
        {
            ["itemData"] = new JsonObject { ["id"] = item.Int("id"), ["rareflag"] = 1, ["groups"] = new JsonArray(4) }
        }).ToArray()) };
        await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad());
        var before = await rt.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None);
        var failed = await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id));
        Assert.Equal(200, failed.Status);
        Assert.Equal("INVALID", failed.Body!["status"]!.S());
        Assert.Equal(before, await rt.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None));
        foreach (var item in items) Assert.NotNull(await rt.Game.ReadAsync(w => w.GetItemAsync(item.Int("id")), CancellationToken.None));
        Assert.DoesNotContain(challenge.Key, await rt.Game.ReadAsync(w => w.SbcCompletedAsync(), CancellationToken.None));

        var rare = rt.Game.Players.Entries.First(e => e.Value.Rareflag == 1 && !definitions.Any(d => d.Value.Int("assetId") == e.Value.Int("assetId")));
        items[0] = rt.Fut.Items.MakePlayer(rare.Key, "club");
        await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad());
        Assert.Equal(200, (await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id))).Status);
        foreach (var item in items) Assert.Null(await rt.Game.ReadAsync(w => w.GetItemAsync(item.Int("id")), CancellationToken.None));
        Assert.Contains(challenge.Key, await rt.Game.ReadAsync(w => w.SbcCompletedAsync(), CancellationToken.None));
    }
}
