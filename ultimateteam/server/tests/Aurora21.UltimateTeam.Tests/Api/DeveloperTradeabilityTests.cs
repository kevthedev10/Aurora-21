using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class DeveloperTradeabilityTests
{
    [Fact]
    public async Task DeveloperCardsStayUntradeableOnSaveAndReopen()
    {
        using var first = TestRuntime.Create(marketTarget: 0);
        const long resource = 167996618;
        var id = await first.Game.RunAsync("developer-tradeability", async work =>
        {
            var item = await first.Fut.Items.MakePlayerAsync(work, resource);
            Assert.True(item.Bool("untradeable"));
            item["untradeable"] = false;
            await work.SaveItemAsync(item);
            Assert.True((await work.GetItemAsync(item.IntKeep("id")))!.Bool("untradeable"));
            return item.IntKeep("id");
        }, CancellationToken.None);
        using var reopened = TestRuntime.Create(connectionString: first.Detach(), marketTarget: 0);
        var saved = await reopened.Game.RunAsync("read-reopened-developer", work => work.GetItemAsync(id), CancellationToken.None);
        Assert.True(saved!.Bool("untradeable"));
        Assert.Equal(resource, saved!.IntKeep("resourceId"));
    }
}
