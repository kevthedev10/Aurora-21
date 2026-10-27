namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// SBC rating formula, in whole numbers as the FIFA 21 client computes it: the average is the rating total divided by the
/// player count, rounded down; every player above that average adds his excess over it; the adjusted total divided by the
/// player count, rounded down, is the rating. Live squads pin the whole-number average: refused submits the client had
/// shown as met (2026-10-06: Fiendish and Around the World at exactly 81.0 and 81.09, OTW upgrade at exactly 83.0) and
/// michaelg04's Around the World (shown as 81) all meet their minimum with it, and none with the exact average.
/// A brick wall rates the players placed in its free slots: retail asks "Squad Rating: Min 55" of six players
/// (Multi League &amp; Nation), which an average over eleven slots could never reach.
/// </summary>
public static class SbcSquadRating
{
    public static int CalculateSbcSquadRating(IEnumerable<int> playerRatings)
    {
        ArgumentNullException.ThrowIfNull(playerRatings);
        int[] ratings = playerRatings.ToArray();
        if (ratings.Length is < 1 or > 11)
            throw new ArgumentException("An SBC squad must contain 1 to 11 players.", nameof(playerRatings));

        int sum = ratings.Sum();
        int average = sum / ratings.Length;
        int excess = ratings.Where(rating => rating > average).Sum(rating => rating - average);
        return (sum + excess) / ratings.Length;
    }
}
