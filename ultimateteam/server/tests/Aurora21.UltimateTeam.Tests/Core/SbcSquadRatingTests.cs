using Aurora21.UltimateTeam.Core.Services.Modes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class SbcSquadRatingTests
{
    [Fact]
    public void Eight84sAndThree83sRoundUpTo84() =>
        Assert.Equal(84, SbcSquadRating.CalculateSbcSquadRating(Enumerable.Repeat(84, 8).Concat(Enumerable.Repeat(83, 3))));

    [Fact]
    public void AnEightyPointEightSquadRatesAs81() =>
        Assert.Equal(81, SbcSquadRating.CalculateSbcSquadRating(new[] { 79, 77, 81, 81, 83, 82, 77, 80, 83, 76, 78 }));

    [Fact]
    public void Nine84sAndTwo83sReach84() =>
        Assert.Equal(84, SbcSquadRating.CalculateSbcSquadRating(Enumerable.Repeat(84, 9).Concat(Enumerable.Repeat(83, 2))));

    [Fact]
    public void ExcessFromAn87AndFive84sLiftsFive82sTo84() =>
        Assert.Equal(84, SbcSquadRating.CalculateSbcSquadRating(new[] { 87 }.Concat(Enumerable.Repeat(84, 5)).Concat(Enumerable.Repeat(82, 5))));

    /// <summary>Live squads FIFA let through as meeting their minimum (2026-10-05/06); the exact average rated each one lower.</summary>
    [Theory]
    [InlineData(81, new[] { 77, 80, 77, 80, 80, 81, 79, 80, 83, 80, 81 })] // Fiendish, club 47
    [InlineData(81, new[] { 81, 84, 80, 76, 77, 80, 81, 80, 80, 79, 80 })] // Around the World, club 252
    [InlineData(83, new[] { 83, 83, 83, 83, 83, 81, 82, 82, 80, 81, 80 })] // OTW or TOTW Upgrade, club 103
    [InlineData(81, new[] { 78, 76, 83, 80, 77, 82, 83, 81, 81, 77, 79 })] // Around the World, michaelg04 (shown as 81)
    public void TheAverageIsAWholeNumberLikeTheClients(int expected, int[] ratings) =>
        Assert.Equal(expected, SbcSquadRating.CalculateSbcSquadRating(ratings));

    [Fact]
    public void ASquadJustShortStaysShort() =>
        // Around the World, club 101: 80 by every reading.
        Assert.Equal(80, SbcSquadRating.CalculateSbcSquadRating(new[] { 83, 80, 77, 81, 79, 76, 80, 80, 80, 79, 75 }));

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void RequiresOneToElevenPlayers(int count) =>
        Assert.Throws<ArgumentException>(() => SbcSquadRating.CalculateSbcSquadRating(Enumerable.Repeat(84, count)));

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(10)]
    public void ABrickWallRatesItsOwnPlayers(int count) =>
        Assert.Equal(84, SbcSquadRating.CalculateSbcSquadRating(Enumerable.Repeat(84, count)));

    [Theory]
    [InlineData(47)]
    [InlineData(84)]
    [InlineData(99)]
    public void EqualRatingsKeepTheirRating(int rating) =>
        Assert.Equal(rating, SbcSquadRating.CalculateSbcSquadRating(Enumerable.Repeat(rating, 11)));

    [Fact]
    public void RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => SbcSquadRating.CalculateSbcSquadRating(null!));
}
