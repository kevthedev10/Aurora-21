using Aurora21.UltimateTeam.Core.Helpers;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class GameRandomTests
{
    [Fact]
    public void SeededStreamsAreDeterministicAndIndependent()
    {
        static List<double> Draws(GameRandom random) => Enumerable.Range(0, 8).Select(_ => random.NextDouble()).ToList();

        Assert.Equal(Draws(GameRandom.Seeded("draft-players", 7, 0, 3)), Draws(GameRandom.Seeded("draft-players", 7, 0, 3)));
        Assert.NotEqual(Draws(GameRandom.Seeded("draft-players", 7, 0, 3)), Draws(GameRandom.Seeded("draft-players", 7, 1, 3)));
        Assert.NotEqual(Draws(GameRandom.Seeded("draft-players", 7)), Draws(GameRandom.Seeded("draft-managers", 7)));
    }

    [Fact]
    public void SampleAndShuffleKeepEveryElementOnce()
    {
        var random = GameRandom.Seeded("test", 1);
        var population = Enumerable.Range(0, 1000).ToList();

        var sample = random.Sample(population, 30);
        Assert.Equal(30, sample.Distinct().Count());
        Assert.All(sample, value => Assert.InRange(value, 0, 999));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Sample(population, 1001));

        var shuffled = population.ToList();
        random.Shuffle(shuffled);
        Assert.Equal(population, shuffled.Order());
    }

    [Fact]
    public void WeightedChoiceFollowsTheWeights()
    {
        var random = GameRandom.Seeded("test", 2);
        var items = new[] { "never", "rare", "common" };
        var weights = new[] { 0.0, 1.0, 9.0 };
        var counts = Enumerable.Range(0, 10_000).Select(_ => random.WeightedChoice(items, weights)).GroupBy(item => item).ToDictionary(g => g.Key, g => g.Count());

        Assert.False(counts.ContainsKey("never"));
        Assert.InRange(counts["common"], 8_700, 9_300);
        Assert.Throws<ArgumentException>(() => random.WeightedChoice(items, new[] { 0.0, 0.0, 0.0 }));
    }
}
