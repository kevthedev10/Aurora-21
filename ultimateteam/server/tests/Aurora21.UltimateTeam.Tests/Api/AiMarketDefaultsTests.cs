using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Services.Market;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class AiMarketDefaultsTests
{

    [Fact]
    public void AiMarketIsDisabledByDefault()
    {
        using var rt = TestRuntime.Create();
        Assert.Null(rt.Game.Config.Switches.MarketTarget);
        Assert.Equal(0, rt.Game.Rules.Market.AiListings.Target);
        rt.Fut.Market.SeedAiListings();
        Assert.Equal(0, rt.Game.Store().CountActiveMarketListings(MarketListingKind.Ai));
    }
}
