using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Pack odds: base Gold Rare slots follow the saved Gold Rare group weights (migration 113 tuned them), TOTW and Icons
/// follow each pack's odds table (migration 119), and the per-pack figures follow from both.
/// </summary>
public sealed class PackOddsTests
{
    private const int Rolls = 5_000;

    // Inside the initial TOTW 1-3 and icon windows, which open 2026-09-30 19:00 Amsterdam (17:00 UTC, migrations 100-101).
    private static TestRuntime Create() => TestRuntime.Create(marketTarget: 0,
        time: new TestRuntime.FixedTime(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

    /// <summary>
    /// Asserts an observed count is within 4 standard errors (plus a hair for tiny rates) of the expected rate: about twenty
    /// rates are checked per run, and 3 would fail one run in twenty by chance.
    /// </summary>
    private static void Near(double expected, long hits, long trials, string what)
    {
        var observed = (double)hits / trials;
        var margin = 4 * Math.Sqrt(expected * (1 - expected) / trials) + 0.5 / trials;
        Assert.True(Math.Abs(observed - expected) <= margin,
            $"{what}: expected {expected:P3}, observed {observed:P3} ({hits}/{trials}), margin {margin:P3}");
    }

    [Fact]
    public void RareGoldSlotsFollowTheSeededGroupWeights()
    {
        // Pack 400: twelve gold rare slots, each an in-form or an Icon with its odds table's chance (pack odds v3,
        // migration 119), otherwise a base Gold Rare selected through the initially rating-based groups.
        using var rt = Create();
        var odds = rt.Game.Rules.PackOdds;
        var pools = rt.Fut.Pools;
        var icon = rt.Game.IconCardAt();
        var table = rt.Game.Packs.GetPackConfig(400).Obj("category_chances")!;
        var informChance = table.Float("totw");
        var iconChance = table.Float("icon");
        Assert.Equal((0.01, 0.001), (informChance, iconChance));
        var baseBands = new long[odds.GoldRareBands.Count];
        (long Min, long Max, double Weight)[] totwGroups = { (75L, 79L, 15.0), (80L, 81L, 27.0), (82L, 83L, 30.0),
            (84L, 85L, 15.0), (86L, 87L, 9.0), (88L, 89L, 3.0), (90L, 99L, 1.0) };
        var informBands = new long[totwGroups.Length];
        long slots = 0, informs = 0, icons = 0;
        for (var roll = 0; roll < Rolls; roll++)
        {
            foreach (var id in rt.Fut.Packs.RollPlayerPack(400))
            {
                var meta = rt.Game.Players.Find(id)!;
                slots++;
                if (pools.TotwOneToThreeCards.Contains(id))
                {
                    informs++;
                    var group = Array.FindIndex(totwGroups, band => meta.Rating >= band.Min && meta.Rating <= band.Max);
                    Assert.True(group >= 0, $"TOTW card {id} has no seeded rating group");
                    informBands[group]++;
                    continue;
                }
                if (icon(id))
                {
                    icons++;
                    continue;
                }
                Assert.True(pools.IsBase(id), $"{id} is neither a base card nor a scheduled TOTW card");
                baseBands[PackOddsRules.BandOf(odds.GoldRareBands, meta.Rating)]++;
            }
        }
        Assert.Equal(Rolls * 12L, slots);
        Near(informChance, informs, slots, "in-form per rare slot");
        Near(iconChance, icons, slots, "Icon per rare slot");
        // The saved Gold Rare groups decide base rares, not the original band weights kept in the rules file.
        var catalog = rt.Game.Live.Current.Catalog;
        var goldRare = catalog.Promos.Single(promo => promo.Code == "gold_rare").PromoId;
        var distribution = rt.Game.Live.Snapshot.PromoCardSelector.Distribution("gold_rare",
            catalog.PromoCards.Where(member => member.PromoId == goldRare)
                .Select(member => catalog.DefinitionsById[member.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet());
        var weights = odds.GoldRareBands.Select(band => distribution
            .Where(card => rt.Game.Players.Find(card.Key)!.Rating >= band.Min && rt.Game.Players.Find(card.Key)!.Rating <= band.Max)
            .Sum(card => card.Value)).ToArray();
        var baseTotal = weights.Sum();
        for (var band = 0; band < baseBands.Length; band++)
        {
            Near(weights[band] / baseTotal, baseBands[band], slots - informs - icons, $"base band {odds.GoldRareBands[band].Min}-{odds.GoldRareBands[band].Max}");
        }
        // Every band has in-forms in TOTW 1-3, so the in-form table applies untruncated.
        var informTotal = totwGroups.Sum(band => band.Weight);
        for (var band = 0; band < informBands.Length; band++)
        {
            Assert.Contains(pools.TotwOneToThreeCards, id => rt.Game.Players.Find(id)!.Rating >= totwGroups[band].Min
                && rt.Game.Players.Find(id)!.Rating <= totwGroups[band].Max);
            Near(totwGroups[band].Weight / informTotal, informBands[band], informs,
                $"TOTW group rating_{totwGroups[band].Min}_{totwGroups[band].Max}");
        }
    }

    [Fact]
    public void PublishedPackFiguresHold()
    {
        // The 82+/84+ targets follow from rare-slot odds: each pack's odds table (TOTW and Icon, migration 119) and the saved
        // Gold Rare group weights (the share of base rares rated 82+ and 84+); TOTW 1-3 groups are 58% 82+ and 28% 84+, and
        // every Icon is 84+. Common slots include every Gold Common, including high-rated commons; those are a separate outcome.
        using var rt = Create();
        var pools = rt.Fut.Pools;
        var catalog = rt.Game.Live.Current.Catalog;
        var goldRare = catalog.Promos.Single(promo => promo.Code == "gold_rare").PromoId;
        var distribution = rt.Game.Live.Snapshot.PromoCardSelector.Distribution("gold_rare",
            catalog.PromoCards.Where(member => member.PromoId == goldRare)
                .Select(member => catalog.DefinitionsById[member.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet());
        double BaseShare(long rating) => distribution.Where(card => rt.Game.Players.Find(card.Key)!.Rating >= rating).Sum(card => card.Value);
        (long PackId, double At82, double At84, double Inform) Expected(long packId)
        {
            var config = rt.Game.Packs.GetPackConfig(packId);
            var table = config.Obj("category_chances")!;
            double totw = table.Float("totw"), icon = table.Float("icon"), slots = config.Int("rare_slots");
            double AtLeastOne(double perSlot) => 1 - Math.Pow(1 - perSlot, slots);
            return (packId, AtLeastOne(totw * 0.58 + icon + (1 - totw - icon) * BaseShare(82)),
                AtLeastOne(totw * 0.28 + icon + (1 - totw - icon) * BaseShare(84)), AtLeastOne(totw));
        }
        // Gold (one rare slot), Premium Gold Players (three) and Rare Gold (twelve).
        foreach (var (packId, at82, at84, inform) in new[] { Expected(304), Expected(305), Expected(400) })
        {
            long with82 = 0, with84 = 0, withInform = 0;
            for (var roll = 0; roll < Rolls; roll++)
            {
                var cards = rt.Fut.Packs.RollPlayerPack(packId)
                    .Select(id => (Id: id, Meta: rt.Game.Players.Find(id)!))
                    .Where(card => card.Meta.Rareflag == 1 || card.Meta.IsSpecial)
                    .Select(card => (card.Id, card.Meta.Rating)).ToList();
                with82 += cards.Any(card => card.Rating >= 82) ? 1 : 0;
                with84 += cards.Any(card => card.Rating >= 84) ? 1 : 0;
                withInform += cards.Any(card => pools.TotwOneToThreeCards.Contains(card.Id)) ? 1 : 0;
            }
            Near(at82, with82, Rolls, $"pack {packId} 82+ rare/special");
            Near(at84, with84, Rolls, $"pack {packId} 84+ rare/special");
            Near(inform, withInform, Rolls, $"pack {packId} TOTW");
        }
    }

    [Fact]
    public void TheUltimatePackHasThreeRaresRatedEightyFourOrMore()
    {
        using var rt = Create();
        for (var roll = 0; roll < 200; roll++)
        {
            var ratings = rt.Fut.Packs.RollPlayerPack(405).Select(id => rt.Game.Players.Find(id)!.Rating).ToList();
            Assert.Equal(30, ratings.Count);
            Assert.True(ratings.Count(rating => rating >= 84) >= 3, $"roll {roll}: {string.Join(",", ratings)}");
        }
    }
}
