using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Each shared-server player's own name (Python server 62ac9ad) and plain leaderboard names (81c770e).</summary>
public sealed class PlayerNamesTests
{
    [Theory]
    [InlineData("Team", "Team")]
    [InlineData("Tëam ✨ FC!", "TamFC")]
    [InlineData("a_very-long.name_over16", "a_very-long.name")]
    [InlineData("✨✨", "Player654321")]
    [InlineData(null, "Player654321")]
    public void PersonaNamesAreEaStyle(string? raw, string expected) =>
        Assert.Equal(expected, PlayerNames.Persona("987654321", raw));

    [Theory]
    [InlineData("  Bold   𝓜ath  name ", "Bold ath name")]
    [InlineData("Émile's club", "miles club")]
    [InlineData("🎮🎮", null)]
    [InlineData("A name that is far too long for a row", "A name that is far t")]
    public void LeaderboardNamesArePlainAscii(string raw, string? expected) =>
        Assert.Equal(expected, PlayerNames.Readable(raw));
}
