using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>Phase 4: the async router, player registry and connection-free club sessions.</summary>
public sealed class AsyncHostingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private const string CreateClub = "-- query: club.create";

    [Fact]
    public async Task OpenClubsOutnumberPoolConnectionsAndKeepTheirWritesIsolated()
    {
        const int poolSize = 2;
        const int playerCount = 8;
        using var rt = TestRuntime.Create(prepareRoot: root =>
        {
            var settings = Path.Combine(root, "user-config");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "config.json"), """{"database": {"max_pool_size": 2}}""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = playerCount + 1 }, shared: true);
        var leases = new List<PlayerLease>();
        try
        {
            for (var i = 0; i < playerCount; i++)
            {
                leases.Add(await players.AcquireAsync(PlayerIdentity.Of((700000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .WaitAsync(Patience));
            }
            Assert.Equal(poolSize, rt.Game.Database.Settings.MaxPoolSize);
            Assert.Equal(playerCount + 1, players.OpenCount);
            Assert.Equal(playerCount, leases.Select(lease => lease.Session.Game.ClubId).Distinct().Count());
            Assert.All(leases, lease => Assert.Equal(1, lease.Session.Handlers));

            var observed = await Task.WhenAll(leases.Select((lease, index) =>
                lease.Session.Game.RunAsync("test-many-open-clubs", async uow =>
                {
                    await uow.LockClubAsync();
                    var before = await uow.CreditsAsync();
                    await uow.AddCreditsToAsync(uow.ClubId, index + 1);
                    var sessions = await uow.ScalarAsync<long>("""
                        SELECT count(*) FROM pg_stat_activity
                        WHERE datname = current_database() AND backend_type = 'client backend'
                        """);
                    await uow.ExecuteAsync("SELECT pg_sleep(0.05)");
                    return (Credits: before + index + 1, Sessions: sessions);
                }, CancellationToken.None))).WaitAsync(Patience);

            Assert.All(observed, result => Assert.InRange(result.Sessions, 1, poolSize));
            for (var i = 0; i < leases.Count; i++)
            {
                var credits = await leases[i].Session.Game.ReadAsync(uow => uow.CreditsAsync(), CancellationToken.None);
                Assert.Equal(observed[i].Credits, credits);
            }
            Assert.Equal(playerCount + 1, players.OpenCount);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentRequestsForAClosedClubShareOneOpen()
    {
        var counter = new DatabaseCommandCounter();
        using var rt = TestRuntime.Create(commandCounter: counter);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        var mark = counter.Mark();

        var leases = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => players.AcquireAsync(PlayerIdentity.Of("222222")))).WaitAsync(Patience);
        try
        {
            Assert.Single(leases.Select(lease => lease.Session).Distinct());
            Assert.Equal(12, leases[0].Session.Handlers);
            Assert.Single(counter.Since(mark), sql => sql.StartsWith(CreateClub, StringComparison.Ordinal));
            Assert.Equal(2, players.OpenCount);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task ACancelledWaiterDoesNotCancelTheSharedOpen()
    {
        var counter = new DatabaseCommandCounter();
        using var rt = TestRuntime.Create(commandCounter: counter);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var mark = counter.Mark();

        var abandoned = players.AcquireAsync(PlayerIdentity.Of("333333"), cancelled.Token);
        var waiting = players.AcquireAsync(PlayerIdentity.Of("333333"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Patience));
        using var lease = await waiting.WaitAsync(Patience);
        Assert.True(players.IsOpen("333333"));
        Assert.Equal(1, lease.Session.Handlers);
        Assert.Single(counter.Since(mark), sql => sql.StartsWith(CreateClub, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOpenWhoseOnlyWaiterCancelledIsStillOwnedByTheRegistry()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => players.AcquireAsync(PlayerIdentity.Of("444444"), cancelled.Token));

        var deadline = DateTime.UtcNow + Patience;
        while (!players.IsOpen("444444"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the abandoned open never completed");
            await Task.Delay(20);
        }
        using var lease = await players.AcquireAsync(PlayerIdentity.Of("444444"));
        Assert.Equal(1, lease.Session.Handlers);
        Assert.False(await players.WaitForDrainAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task AFailedOpenIsNotCachedAndTheCapacityErrorReachesEveryWaiter()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 2 }, shared: true);
        var held = await players.AcquireAsync(PlayerIdentity.Of("555555"));

        var waiters = Enumerable.Range(0, 3).Select(_ => players.AcquireAsync(PlayerIdentity.Of("666666"))).ToList();
        foreach (var waiter in waiters)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => waiter.WaitAsync(Patience));
            Assert.Contains("capacity", error.Message);
        }
        Assert.False(players.IsOpen("666666"));

        held.Dispose();
        using var recovered = await players.AcquireAsync(PlayerIdentity.Of("666666")).WaitAsync(Patience);
        Assert.True(players.IsOpen("666666"));
        Assert.False(players.IsOpen("555555"));
    }

    [Fact]
    public async Task TheRouterPassesCancellationToDatabaseRoutesAndStillAnswersStaticOnes()
    {
        using var rt = TestRuntime.Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rt.Router.HandleAsync(
            new FutRequest("GET", "/fut/playerheads/g4/single/p990000123.dds", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()),
            cancelled.Token));

        // Step 7 made the club routes async, so a cancelled request reaches their statements too.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rt.Router.HandleAsync(
            new FutRequest("GET", "/ut/game/fifa21/user/credits", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()),
            cancelled.Token));

        // A route that needs no database still answers.
        var settings = await rt.Router.HandleAsync(
            new FutRequest("GET", "/ut/game/fifa21/settings", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()),
            cancelled.Token);
        Assert.Equal(200, settings.Status);
    }
}
