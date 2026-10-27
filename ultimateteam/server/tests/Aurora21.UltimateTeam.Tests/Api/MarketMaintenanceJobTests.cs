using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class MarketMaintenanceJobTests
{
    private const long Now = 1_700_000_000;

    [Fact]
    public async Task RunOnceExpiresDueRowsAndTopsUpAiListings()
    {
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 3);
        var expiredTrade = InsertAi(runtime, Now - 1);
        var job = new MarketMaintenanceJob(runtime.Fut);

        var result = await job.RunOnceAsync();

        Assert.True(result.Acquired);
        Assert.Equal(1, result.Expired);
        Assert.Equal(3, result.Seeded);
        Assert.Equal(MarketState.Expired, runtime.Game.Store().MarketListing(expiredTrade)!.State);
        Assert.Equal(3, runtime.Game.Store().CountActiveMarketListings(MarketListingKind.Ai));
    }

    [Fact]
    public async Task RunOnceSkipsWhenAnotherConnectionOwnsTheMarketLock()
    {
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 0);
        await using var connection = new NpgsqlConnection(runtime.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            command.Parameters.AddWithValue("key", RelationalMarketRepository.MarketLockKey);
            await command.ExecuteNonQueryAsync();
        }

        var result = await new MarketMaintenanceJob(runtime.Fut).RunOnceAsync();

        Assert.False(result.Acquired);
        Assert.Equal(0, result.Expired);
        Assert.Equal(0, result.Seeded);
    }

    [Fact]
    public async Task RunLoopStopsWhileWaitingForItsNextInterval()
    {
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 0);
        var job = new MarketMaintenanceJob(runtime.Fut, TimeSpan.FromHours(1));
        using var stopping = new CancellationTokenSource();

        var running = job.RunAsync(stopping.Token);
        await Task.Yield();
        stopping.Cancel();

        await running.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ReadPathsDoNotPerformUnrelatedGlobalExpiry()
    {
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 0);
        var selected = InsertAi(runtime, Now - 1);
        var unrelated = InsertAi(runtime, Now - 1);

        var search = runtime.Fut.Market.Search(QueryString.Parse(string.Empty));
        Assert.Empty(search["auctionInfo"]!.AsArray());
        Assert.Equal(MarketState.Active, runtime.Game.Store().MarketListing(selected)!.State);
        Assert.Equal(MarketState.Active, runtime.Game.Store().MarketListing(unrelated)!.State);

        var status = runtime.Fut.Market.Status(new[] { selected });
        Assert.Equal(MarketState.Expired, runtime.Game.Store().MarketListing(selected)!.State);
        Assert.Equal(MarketState.Active, runtime.Game.Store().MarketListing(unrelated)!.State);
        Assert.Equal("expired", status["auctionInfo"]![0]!["tradeState"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancellationAfterExpiryStartsRollsBackTheEntireMaintenancePass()
    {
        var faults = new DatabaseFaultInjector();
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 3, faultInjector: faults);
        var expired = InsertAi(runtime, Now - 1);
        var before = runtime.Game.Store().CountActiveMarketListings(MarketListingKind.Ai);
        using var stopping = new CancellationTokenSource();
        faults.CancelOnWrite(2, stopping);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MarketMaintenanceJob(runtime.Fut).RunOnceAsync(stopping.Token));
        Assert.True(faults.Triggered);
        faults.Disarm();
        Assert.Equal(MarketState.Active, runtime.Game.Store().MarketListing(expired)!.State);
        Assert.Equal(before, runtime.Game.Store().CountActiveMarketListings(MarketListingKind.Ai));
    }

    private static long InsertAi(TestRuntime runtime, long endTime)
    {
        var resourceId = runtime.Game.Players.Ids.First();
        var item = runtime.Fut.Items.BuildPlayer(resourceId, 800_000_000_000 + endTime, "market");
        return runtime.Game.Store().InsertMarketListing(
            MarketListingKind.Ai,
            "Maintenance test",
            null,
            resourceId,
            item,
            item["rating"]!.GetValue<long>(),
            150,
            1_000,
            Now - 3_600,
            endTime);
    }
}
