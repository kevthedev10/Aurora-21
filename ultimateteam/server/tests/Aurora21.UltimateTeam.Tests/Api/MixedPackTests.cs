using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Store;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class MixedPackTests
{


    public static IEnumerable<object[]> RequestedPacks()
    {
        yield return [70, "Bronze Pack", 400, 0, "bronze", 12, 1, new[] { 5, 11, 0, 0, 2, 6, 0, 3 }];
        yield return [71, "Premium Bronze Pack", 750, 0, "bronze", 12, 3, new[] { 5, 12, 0, 0, 3, 6, 0, 2 }];
        yield return [72, "Silver Pack", 2500, 50, "silver", 12, 1, new[] { 4, 12, 0, 0, 0, 3, 0, 3 }];
        yield return [73, "Premium Silver Pack", 3750, 75, "silver", 12, 3, new[] { 6, 12, 0, 0, 0, 3, 0, 1 }];
        yield return [74, "Gold Pack", 5000, 100, "gold", 12, 1, new[] { 3, 6, 5, 9, 0, 3, 0, 2 }];
        yield return [75, "Premium Gold Pack", 7500, 150, "gold", 12, 3, new[] { 8, 9, 2, 3, 0, 1, 0, 1 }]; // migration 156
        yield return [76, "Jumbo Premium Bronze Pack", 1500, 75, "bronze", 24, 7, new[] { 11, 20, 0, 0, 3, 11, 1, 7 }];
        yield return [77, "Jumbo Premium Silver Pack", 7500, 150, "silver", 24, 7, new[] { 11, 24, 0, 0, 1, 6, 1, 4 }];
        yield return [78, "Jumbo Premium Gold Pack", 15000, 300, "gold", 24, 7, new[] { 10, 15, 5, 8, 2, 4, 0, 2 }]; // migration 156
    }

    private static JsonObject Config(string tier, int size, int rare, int[] ranges)
    {
        var contents = new JsonObject { ["tier"] = tier };
        foreach (var (category, index) in MixedPackContents.Categories.Select((category, index) => (category, index)))
            contents[category] = new JsonObject { ["min"] = ranges[index * 2], ["max"] = ranges[index * 2 + 1] };
        return new JsonObject { ["size"] = size, ["rare_slots"] = rare, ["mixed_contents"] = contents,
            ["category_chances"] = new JsonObject { ["icon"] = 0 } };
    }

    [Theory]
    [MemberData(nameof(RequestedPacks))]
    public void EveryCompositionHonoursTheRequestedRangesAndWholePackRarity(int id, string name, int coins, int points, string tier, int size, int rare, int[] ranges)
    {
        var config = MixedPackContents.Read(Config(tier, size, rare, ranges))!;
        var pools = Enumerable.Repeat(new MixedPackContents.Capacity(size, size), 4).ToArray();
        var random = GameRandom.Seeded("mixed-compositions", id, name, coins, points);
        for (var draw = 0; draw < 100; draw++)
        {
            var counts = config.Draw(pools, random);
            Assert.Equal(size, counts.Sum(count => count.Total));
            Assert.Equal(rare, counts.Sum(count => count.Rare));
            for (var category = 0; category < 4; category++)
            {
                Assert.InRange(counts[category].Total, ranges[category * 2], ranges[category * 2 + 1]);
                Assert.InRange(counts[category].Rare, 0, counts[category].Total);
            }
        }
    }

    [Theory]
    [MemberData(nameof(RequestedPacks))]
    public void APacksRaresAreItsPlayers(int id, string name, int coins, int points, string tier, int size, int rare, int[] ranges)
    {
        var config = MixedPackContents.Read(Config(tier, size, rare, ranges))!;
        var pools = Enumerable.Repeat(new MixedPackContents.Capacity(size, size), 4).ToArray();
        var random = GameRandom.Seeded("mixed-rare-players", id, name, coins, points);
        for (var draw = 0; draw < 200; draw++)
        {
            var counts = config.Draw(pools, random);
            Assert.Equal(rare, counts[0].Rare);
            Assert.True(counts[0].Total >= rare);
            Assert.All(counts.Skip(1), count => Assert.Equal(0, count.Rare));
        }
        // Other items never stand in for missing rare players.
        var noRarePlayers = new[] { new MixedPackContents.Capacity(size, 0) }.Concat(pools.Skip(1)).ToArray();
        Assert.Contains("rare players", Assert.Throws<InvalidOperationException>(() => config.Draw(noRarePlayers, random)).Message);
        // More rare slots than the pack has room for players is refused when it is read.
        if (ranges[1] < size) Assert.Throws<InvalidOperationException>(() => MixedPackContents.Read(Config(tier, size, ranges[1] + 1, ranges)));
    }

    [Fact]
    public void MissingPoolsAndImpossibleRarityAreRejectedInsteadOfPaddingItems()
    {
        var config = MixedPackContents.Read(Config("bronze", 4, 1, [1, 1, 1, 1, 1, 1, 1, 1]))!;
        var missing = new[] { new MixedPackContents.Capacity(4, 4), new(0, 0), new(4, 4), new(4, 4) };
        Assert.Contains("consumables", Assert.Throws<InvalidOperationException>(() => config.Draw(missing, GameRandom.Shared)).Message);
        Assert.Throws<InvalidOperationException>(() => config.Draw(Enumerable.Repeat(new MixedPackContents.Capacity(0, 4), 4).ToArray(), GameRandom.Shared));
        Assert.Throws<InvalidOperationException>(() => MixedPackContents.Read(Config("gold", 4, 1, [2, 2, 2, 2, 2, 2, 0, 0])));
        var fractional = Config("gold", 4, 1, [1, 1, 1, 1, 1, 1, 1, 1]);
        fractional.Obj("mixed_contents")!.Obj("players")!["min"] = 0.5;
        Assert.Throws<InvalidOperationException>(() => MixedPackContents.Read(fractional));
    }

    [Fact]
    public async Task AMixedPackKitHasTheCategoryOfEveryOtherKitCard()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        // Only kits fill the cosmetic slots.
        var weights = rt.Game.Live.Snapshot.Rules.PackOdds.NonPlayerDrops.PoolWeights;
        foreach (var pool in NonPlayerDropTable.Cosmetics) weights[pool] = pool == NonPlayerDropTable.Kit ? 1 : 0;
        var opened = await rt.Game.RunAsync("mixed-kits",
            work => rt.Fut.Packs.CreatePackAsync(work, 74, Config("gold", 4, 0, [0, 0, 0, 0, 4, 4, 0, 0])), CancellationToken.None);
        var saved = (await rt.Game.RunAsync("mixed-kit-items", async work => await work.ListItemsAsync(), CancellationToken.None))
            .ToDictionary(item => item.Int("id"));
        var kits = opened.Arr("items")!.Objects().ToArray();
        Assert.Equal(4, kits.Length);
        foreach (var kit in kits)
        {
            Assert.Equal("kit", kit.Text("itemType"));
            // FIFA draws a blank "KIT" card for the kit card subtype (9); the club's kit list has always sent 2 + kitType.
            Assert.Equal(2 + kit.Int("kitType"), kit.Int("category"));
            Assert.Equal(saved[kit.Int("id")].Int("category"), kit.Int("category"));
        }
    }

    [Fact]
    public void ClubItemsFromAStorePackAreTradeable()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        // Only kits fill the cosmetic slots.
        var weights = rt.Game.Live.Snapshot.Rules.PackOdds.NonPlayerDrops.PoolWeights;
        foreach (var pool in NonPlayerDropTable.Cosmetics) weights[pool] = pool == NonPlayerDropTable.Kit ? 1 : 0;
        var kits = new List<JsonObject>();
        for (var pack = 0; pack < 20 && kits.Count == 0; pack++)
        {
            rt.Game.Store().DeletePile("unassigned");
            rt.Game.Store().AddCredits(7500);
            var opened = rt.CallObject("POST", "/ut/game/fifa21/purchased/items", new JsonObject { ["packId"] = 75, ["currency"] = "COINS" });
            Assert.All((opened.Arr("itemData") ?? new JsonArray()).Objects().Where(item => item.Text("itemType") == "kit"), item => Assert.False(item.Bool("untradeable")));
            kits.AddRange(rt.Game.Store().ListItems("unassigned").Where(item => item.Text("itemType") == "kit"));
        }
        Assert.NotEmpty(kits);
        Assert.All(kits, kit => Assert.False(kit.Bool("untradeable")));
    }

    [Fact]
    public void TradeableClubItemsCanBecomeUntradeableRewardsAndPersistTheirCounts()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        foreach (var pool in NonPlayerDropTable.Cosmetics)
        {
            var drop = rt.Game.Live.Snapshot.Drops.Pool(pool).First();
            var item = rt.Fut.Items.BuildDrop(drop, rt.Game.Store().NextItemId(), "unassigned");
            ItemFactory.MakeTradeable(item);
            rt.Game.Store().SaveItem(item);
            var tradeable = rt.Game.Store().GetItem(item.Int("id"))!;
            Assert.False(tradeable.Bool("untradeable"));
            rt.Fut.Items.MarkReward(tradeable, RewardSources.Objective, untradeable: true);
            rt.Game.Store().SaveItem(tradeable);
            var reward = rt.Game.Store().GetItem(item.Int("id"))!;
            Assert.True(reward.Bool("untradeable"));
            if (reward.ContainsKey("untradeableCount")) Assert.Equal(1, reward.Int("untradeableCount"));
            // Reward provenance is stored on the header, rather than exposed in the native item document.
            var source = rt.Game.InUnit(w => w.ScalarAsync<string>(
                "SELECT reward_source FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@item",
                ("item", item.Int("id"))));
            Assert.Equal(RewardSources.Objective, source);
        }
    }

    [Fact]
    public async Task MigrationKeepsOwnedLegacyPacksAndRewardsWhileMixedPreviewAndOpeningShareThePlanner()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var packs = rt.Game.Packs.Load().Obj("packs")!;
        // Every pack is in the list since migration 119; the store sells the nine mixed packs and the My Stadium Pack (123).
        Assert.Equal(10, packs.Count(entry => rt.Game.Packs.IsForSale(long.Parse(entry.Key, System.Globalization.CultureInfo.InvariantCulture))));
        foreach (var row in RequestedPacks())
        {
            var config = packs.Obj(row[0].ToString()!)!;
            Assert.Equal((string)row[1], config.Text("name"));
            Assert.Equal((int)row[2], config.Int("price"));
            // Coin listings never expose tokens; migration 148 moved those prices into separate offers.
            Assert.Equal(0, config.Int("points_price"));
            var tokenOffers = rt.Game.Packs.TokenOffers().Select(entry => entry.Value!.AsObject())
                .Where(offer => offer.Int("base_pack_id") == (int)row[0]).ToArray();
            if ((int)row[3] == 0) Assert.Empty(tokenOffers);
            else
            {
                var tokenOffer = Assert.Single(tokenOffers);
                Assert.Equal((int)row[3], tokenOffer.Int("points_price"));
                Assert.Equal(0, tokenOffer.Int("price"));
            }
            Assert.Equal((int)row[5], config.Int("size"));
            Assert.Equal((int)row[6], config.Int("rare_slots"));
            Assert.True(JsonNode.DeepEquals(Config((string)row[4], (int)row[5], (int)row[6], (int[])row[7])["mixed_contents"], config["mixed_contents"]));
            Assert.True(rt.Game.Packs.IsForSale((int)row[0]));
            Assert.Equal(rt.Game.Rules.Packs.StoreGroupArtIds[(string)row[4]], rt.Game.Rules.Packs.StoreArtId((int)row[0], config));
        }
        Assert.False(rt.Game.Packs.IsForSale(3));
        Assert.Empty(rt.Game.Packs.GetPackConfig(3)); // Testing definitions are removed from a fresh catalogue.
        Assert.NotEmpty(rt.Game.Packs.GetPackConfig(319)); // Competition reward definition stays (303 merged into 319, migration 134).
        foreach (var id in new[] { "70", "71", "72", "73", "74", "75", "76", "77", "78" })
            for (var draw = 0; draw < 10; draw++)
            {
                var config = packs.Obj(id)!;
                var previewPack = rt.Fut.Packs.PreviewPlayerPack(config);
                Assert.Equal(config.Int("size"), previewPack.Count);
                Assert.Equal(config.Int("rare_slots"), previewPack.Objects().Count(item => item.Bool("rareSlot")));
            }
        // Gold consumable slots hold chemistry styles, position modifiers and league cards alike.
        var goldKinds = new HashSet<string>();
        for (var draw = 0; draw < 60; draw++)
            foreach (var item in rt.Fut.Packs.PreviewPlayerPack(packs.Obj("74")!).Objects().Where(item => item.Text("category") == "consumables"))
                goldKinds.Add(rt.Game.Cards.ChemistryStyles.Any(style => style.ResourceId == item.Int("resourceId")) ? "chemistry_style"
                    : rt.Game.Cards.PositionModifiers.Any(modifier => modifier.ResourceId == item.Int("resourceId")) ? "position_modifier"
                    : "manager_league");
        Assert.Equal(new[] { "chemistry_style", "manager_league", "position_modifier" }, goldKinds.Order());
        var rareStyles = new HashSet<string> { "Hawk", "Engine", "Anchor", "Hunter", "Catalyst", "Shadow" };
        foreach (var style in rt.Game.Cards.ChemistryStyles)
        {
            var item = rt.Fut.Items.BuildConsumable(style.ResourceId, 1);
            Assert.Equal(rareStyles.Contains(style.Name) ? 1 : 0, item.Int("rareflag"));
            Assert.Equal(style.PlayStyle, item.Int("cardsubtypeid"));
            Assert.Equal(rt.Game.Rules.Cards.ChemistryStyles.CardAssetId, item.Int("cardassetid"));
        }

        var valid = Config("gold", 3, 1, [1, 1, 1, 1, 1, 1, 0, 0]);
        var preview = rt.Fut.Packs.PreviewPlayerPack(valid);
        Assert.Equal(3, preview.Count);
        Assert.Equal(new[] { "consumables", "players", "stadium" }, preview.Objects().Select(item => item.Text("category")).Order());
        Assert.True(preview.Objects().Single(item => item.Bool("rareSlot")).Text("category") == "players");
        var opened = await rt.Game.RunAsync("mixed-test", work => rt.Fut.Packs.CreatePackAsync(work, 74, valid), CancellationToken.None);
        var saved = await rt.Game.RunAsync("mixed-items", async work => await work.ListItemsAsync(), CancellationToken.None);
        var unassigned = saved.Where(item => item.Text("pile") == "unassigned").ToArray();
        Assert.Equal(3, unassigned.Length);
        Assert.Contains(unassigned, item => item.Int("rareflag") > 0);
        Assert.All(unassigned, item => Assert.True(item.Int("rating") >= 75));
        Assert.NotEmpty(opened);
    }
}
