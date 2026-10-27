namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>Leaderboard sizes and the six permanent Champions position slots.</summary>
public static class LeaderboardSizes
{
    public const int ChampionsMinimum = ChampionsRewardRules.ClientTopBands;
    public const int Maximum = 200;
    public static void Validate(int size, int minimum, string setting)
    {
        if (size < minimum || size > Maximum)
            throw new InvalidDataException($"{setting} must be a whole number from {minimum} to {Maximum}.");
    }
    /// <summary>Rank 1 stands alone; the remaining positions are divided over five slots, rounding boundaries down.</summary>
    public static (long Low, long High)[] ChampionsBands(int size)
    {
        Validate(size, ChampionsMinimum, "weekendLeague.topX");
        return [(1, 1), .. Enumerable.Range(0, ChampionsMinimum - 1).Select(index =>
            (2L + (long)(size - 1) * index / (ChampionsMinimum - 1),
             1L + (long)(size - 1) * (index + 1) / (ChampionsMinimum - 1)))];
    }
    public static string Name(long low, long high) => low == high ? $"Rank {low}" : $"Rank {low}-{high}";
}
