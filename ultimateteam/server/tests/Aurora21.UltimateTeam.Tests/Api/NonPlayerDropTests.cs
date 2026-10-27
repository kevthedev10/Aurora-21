using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>The one non-player drop table (rules/pack_odds.json nonPlayerDrops, migration 150).</summary>
public sealed class NonPlayerDropTests
{
    private static NonPlayerDrop Drop(string pool, long resource, long rareflag) =>
        new(pool, "consumable", resource, 90, rareflag, "gold", $"{pool} {resource}");

    [Fact]
    public void RareItemsDropAtRareWeightAndAZeroWeightPoolNeverDrops()
    {
        var rules = new NonPlayerDropRules
        {
            RareWeight = 0.5,
            PoolWeights = new() { [NonPlayerDropTable.ChemistryStyle] = 1, [NonPlayerDropTable.PositionModifier] = 0 },
        };
        var table = new NonPlayerDropTable(rules, [Drop(NonPlayerDropTable.ChemistryStyle, 1, 0), Drop(NonPlayerDropTable.ChemistryStyle, 2, 1),
            Drop(NonPlayerDropTable.PositionModifier, 3, 0)]);
        var random = GameRandom.Seeded("non-player-drop-weights");
        const int draws = 30_000;
        var rare = 0;
        for (var draw = 0; draw < draws; draw++)
        {
            var item = table.Draw(random, NonPlayerDropTable.Consumables)!;
            Assert.Equal(NonPlayerDropTable.ChemistryStyle, item.Pool);
            if (item.Rareflag != 0) rare++;
        }
        // Common 1, rare 0.5: a third of the draws are rare.
        Assert.InRange(rare / (double)draws, 1 / 3.0 - 0.015, 1 / 3.0 + 0.015);
        Assert.Null(table.Draw(random, [NonPlayerDropTable.PositionModifier]));
        Assert.False(table.CanDraw([NonPlayerDropTable.PositionModifier]));

        var distinct = table.DrawDistinct(random, [NonPlayerDropTable.ChemistryStyle], 5);
        Assert.Equal(new long[] { 1, 2 }, distinct.Select(item => item.Resource).Order());

        rules.RareWeight = 0;
        Assert.All(Enumerable.Range(0, 200), _ => Assert.Equal(0, table.Draw(random, [NonPlayerDropTable.ChemistryStyle])!.Rareflag));
    }

    [Fact]
    public void APackBlendsItsPoolsBeforeRepeatingOne()
    {
        var rules = new NonPlayerDropRules
        {
            RareWeight = 0.5,
            PoolWeights = new() { [NonPlayerDropTable.ChemistryStyle] = 1, [NonPlayerDropTable.PositionModifier] = 1, [NonPlayerDropTable.ManagerLeague] = 1 },
        };
        var table = new NonPlayerDropTable(rules, [Drop(NonPlayerDropTable.ChemistryStyle, 1, 0), Drop(NonPlayerDropTable.ChemistryStyle, 2, 0),
            Drop(NonPlayerDropTable.PositionModifier, 3, 1), Drop(NonPlayerDropTable.ManagerLeague, 4, 1)]);
        var random = GameRandom.Seeded("non-player-drop-blend");
        for (var pack = 0; pack < 500; pack++)
        {
            var two = table.DrawBlended(random, NonPlayerDropTable.Consumables, 2);
            Assert.Equal(2, two.Select(item => item.Pool).Distinct().Count());
            var five = table.DrawBlended(random, NonPlayerDropTable.Consumables, 5);
            Assert.Equal(5, five.Count);
            Assert.Equal(3, five.Take(3).Select(item => item.Pool).Distinct().Count());
            Assert.Equal(2, five.Skip(3).Select(item => item.Pool).Distinct().Count());
        }
        // A pool that cannot drop is skipped, not waited for.
        rules.PoolWeights[NonPlayerDropTable.ManagerLeague] = 0;
        var styles = table.DrawBlended(random, NonPlayerDropTable.Consumables, 4);
        Assert.Equal(2, styles.Count(item => item.Pool == NonPlayerDropTable.ChemistryStyle));
        Assert.Empty(table.DrawBlended(random, [NonPlayerDropTable.Kit], 3));
    }

    [Fact]
    public void GoldPackMixesFillPremiumAndJumboPacksWithPlayers()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var packs = rt.Game.Packs.Load().Obj("packs")!;
        foreach (var (id, min, max) in new[] { ("75", 8, 9), ("78", 10, 15) })
        {
            var pack = packs.Obj(id)!;
            for (var draw = 0; draw < 100; draw++)
            {
                var items = rt.Fut.Packs.PreviewPlayerPack(pack).Objects().ToArray();
                Assert.InRange(items.Count(item => item.Text("category") == "players"), min, max);
                var consumables = items.Where(item => item.Text("category") == "consumables").ToArray();
                var styles = consumables.Count(item => rt.Game.Cards.ChemistryStyles.Any(style => style.ResourceId == item.Int("resourceId")));
                // Three consumable pools: at most one style per three consumables, rounded up.
                Assert.True(styles <= (consumables.Length + 2) / 3, $"pack {id}: {styles} styles in {consumables.Length} consumables");
            }
        }
    }

    [Fact]
    public void EveryChemistryStyleButTheDefaultsDropsAndTheRareOnesAtHalfTheRate()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var drops = rt.Game.Live.Snapshot.Drops;
        var styles = rt.Game.Rules.Cards.ChemistryStyles;
        var pool = drops.Pool(NonPlayerDropTable.ChemistryStyle);
        Assert.Equal(rt.Game.Cards.ChemistryStyles.Select(style => style.PlayStyle).Where(style => style is not (250 or 273)).Order(),
            pool.Select(item => rt.Game.Cards.ChemistryStyle(item.Resource)!.PlayStyle).Order());
        Assert.Equal(styles.RareStyles.Order(), pool.Where(item => item.Rareflag != 0).Select(item => rt.Game.Cards.ChemistryStyle(item.Resource)!.PlayStyle).Order());

        var random = GameRandom.Seeded("chemistry-style-drops");
        const int draws = 40_000;
        var counts = pool.ToDictionary(item => item.Resource, _ => 0);
        for (var draw = 0; draw < draws; draw++) counts[drops.Draw(random, [NonPlayerDropTable.ChemistryStyle])!.Resource]++;
        var total = pool.Sum(drops.Weight);
        foreach (var item in pool)
        {
            var expected = drops.Weight(item) / total;
            var margin = 4 * Math.Sqrt(expected * (1 - expected) / draws);
            Assert.True(Math.Abs(counts[item.Resource] / (double)draws - expected) <= margin, $"{item.Name}: {counts[item.Resource]}/{draws}, expected {expected:P2}");
        }
        Assert.True(counts[pool.Single(item => item.Name == "Shadow").Resource] > 0);
        Assert.True(counts[pool.Single(item => item.Name == "Hunter").Resource] > 0);
    }

    [Fact]
    public void GoldPacksDropRareStylesAndEveryCosmeticKind()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var drops = rt.Game.Live.Snapshot.Drops;
        var gold = rt.Game.Packs.Load().Obj("packs")!.Obj("74")!;
        var styles = new HashSet<string>();
        var cosmetics = new HashSet<string>();
        for (var draw = 0; draw < 400; draw++)
            foreach (var item in rt.Fut.Packs.PreviewPlayerPack(gold).Objects())
            {
                if (item.Text("category") == "consumables" && rt.Game.Cards.ChemistryStyles.Any(style => style.ResourceId == item.Int("resourceId")))
                    styles.Add(item.Text("name"));
                if (item.Text("category") == "stadium") cosmetics.Add(item.Text("itemType"));
                // A pack's rare slot is its rare player.
                if (item.Bool("rareSlot")) Assert.Equal("players", item.Text("category"));
            }
        Assert.Contains("Shadow", styles);
        Assert.Contains("Hunter", styles);
        var expected = NonPlayerDropTable.Cosmetics.Where(pool => drops.Pool(pool).Any(item => item.Tier == "gold"))
            .Select(pool => drops.Pool(pool).First().Kind).Order();
        Assert.Equal(expected, cosmetics.Order());
    }
}
