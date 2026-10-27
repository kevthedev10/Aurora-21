using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The HTTP endpoint writes <c>X-UT-SID</c> from the account loaded with the club instead of reading the database
/// on every response. It must stay the value the UT auth route derives from the stored SID.
/// </summary>
public sealed class SessionHeaderTests
{
    [Fact]
    public async Task TheCachedUtSidMatchesTheStoredSidForEachClub()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, time: new TestRuntime.FixedTime(1_700_000_000));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var other = players.Acquire(PlayerIdentity.Of("222222"));
        foreach (var game in new[] { rt.Game, other.Session.Game })
        {
            var stored = await game.ReadAsync(ClubPresentationService.UtSidAsync, CancellationToken.None);
            Assert.Equal(stored, game.UtSid);
            Assert.StartsWith("ut.", game.UtSid, StringComparison.Ordinal);
        }
        Assert.NotEqual(rt.Game.UtSid, other.Session.Game.UtSid);
    }
}
