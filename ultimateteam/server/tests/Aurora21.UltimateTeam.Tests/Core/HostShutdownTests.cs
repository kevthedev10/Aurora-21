using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class HostShutdownTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ShutdownCancelsQueuedOpensAndRollsBackOwnedSqlBeforeDrainingHeldLease()
    {
        using var rt = TestRuntime.Create(prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "user-config"));
            File.WriteAllText(Path.Combine(root, "user-config", "config.json"), """{"database":{"max_pool_size":1}}""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { ConcurrentOpens = 1 }, shared: true);
        var lease = await players.AcquireAsync(PlayerIdentity.SinglePlayer(rt.Game.Config));
        var before = await rt.Game.ReadAsync(uow => uow.CreditsAsync(), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sql = rt.Game.RunAsync("shutdown-test", async uow =>
        {
            await uow.AddCreditsToAsync(uow.ClubId, 123);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, uow.CancellationToken);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(Patience);
        var opening = players.AcquireAsync(PlayerIdentity.Of("900001"));
        var queued = players.AcquireAsync(PlayerIdentity.Of("900002"));
        Assert.False(opening.IsCompleted);
        Assert.False(queued.IsCompleted);
        var listeners = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var shutdown = new HostShutdown(Patience, NullLogger.Instance,
            () => { players.RequestStop(); rt.Game.UnitOfWork.RequestStop(); rt.Game.Content.RequestStop(); },
            _ => { listeners.SetResult(); return Task.CompletedTask; },
            () => Task.WhenAll(players.StopAsync(), rt.Game.UnitOfWork.StopAsync(), rt.Game.Content.StopAsync()),
            token => players.WaitForDrainAsync(Patience, cancellationToken: token),
            () => Interlocked.Increment(ref released));
        var stopping = shutdown.StopAsync();
        Assert.Same(stopping, shutdown.StopAsync());
        await listeners.Task.WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => players.AcquireAsync(PlayerIdentity.Of("900003")));
        Assert.Null(players.TryAcquireExisting(rt.Game.Player.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Patience));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sql.WaitAsync(Patience));
        await rt.Game.UnitOfWork.StopAsync().WaitAsync(Patience);
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, released);
        // Pool remains available after rollback and before logical state/pool disposal.
        await using (var connection = await rt.Game.Database.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("SELECT credits FROM ultimateteam.clubs WHERE id=@club", connection))
        {
            command.Parameters.AddWithValue("club", rt.Game.ClubId);
            Assert.Equal(before, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        lease.Dispose();
        Assert.True(await stopping.WaitAsync(Patience));
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task ListenerAndWorkCancellationShareOneDeadlineAndTimeoutIsNotClean()
    {
        var admitted = false;
        var workStarted = false;
        var released = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new HostShutdown(TimeSpan.FromMilliseconds(100), NullLogger.Instance,
            () => admitted = true,
            async token => { Assert.True(admitted); Assert.True(workStarted); await Task.Delay(Timeout.Infinite, token); },
            () => { workStarted = true; return blocked.Task; },
            _ => throw new InvalidOperationException("Must not claim drain after timeout"),
            () => released++);
        var clock = Stopwatch.StartNew();
        var stopped = shutdown.StopAsync();
        Assert.False(await stopped.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.InRange(clock.ElapsedMilliseconds, 50, 2000);
        Assert.Equal(1, released);
        Assert.Same(stopped, shutdown.StopAsync());
        blocked.SetResult();
    }

    [Fact]
    public async Task FailureStillReleasesRelayAndReportsIncomplete()
    {
        var released = false;
        var shutdown = new HostShutdown(Patience, NullLogger.Instance, () => { },
            _ => Task.FromException(new IOException("synthetic listener failure")), () => Task.CompletedTask,
            _ => Task.FromResult(true), () => released = true);
        Assert.False(await shutdown.StopAsync());
        Assert.True(released);
    }

    [Fact]
    public async Task ForcedDeadlineDoesNotDisposePoolUnderAnUnfinishedTransaction()
    {
        using var rt = TestRuntime.Create();
        var before = await rt.Game.ReadAsync(uow => uow.CreditsAsync(), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = rt.Game.RunAsync("delayed-cancellation-cleanup", async uow =>
        {
            await uow.AddCreditsToAsync(uow.ClubId, 321);
            entered.SetResult();
            // A deliberately uncooperative callback; the runner still must not commit or release early.
            await release.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(Patience);
        var shutdown = new HostShutdown(TimeSpan.FromMilliseconds(100), NullLogger.Instance,
            rt.Game.UnitOfWork.RequestStop, _ => Task.CompletedTask, rt.Game.UnitOfWork.StopAsync,
            _ => Task.FromResult(true), () => { });
        try
        {
            Assert.False(await shutdown.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            rt.Game.Dispose();
            var disposed = rt.Game.StopAsync();
            Assert.Same(disposed, rt.Game.StopAsync());
            Assert.False(disposed.IsCompleted);
            await using (var connection = await rt.Game.Database.DataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("SELECT 1", connection))
            {
                Assert.Equal(1, await command.ExecuteScalarAsync());
            }
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
            await disposed.WaitAsync(Patience);
            await using var reporting = new NpgsqlConnection(rt.ConnectionString);
            await reporting.OpenAsync();
            await using var balance = new NpgsqlCommand("SELECT credits FROM ultimateteam.clubs WHERE id=@club", reporting);
            balance.Parameters.AddWithValue("club", rt.Game.ClubId);
            Assert.Equal(before, Convert.ToInt64(await balance.ExecuteScalarAsync()));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await using var _ = await rt.Game.Database.DataSource.OpenConnectionAsync();
            });
        }
        finally
        {
            release.TrySetResult();
            try { await running; } catch (OperationCanceledException) { }
            await rt.Game.StopAsync();
        }
    }

    [Fact]
    public async Task OwnerCancellationDoesNotBlockAdmissionLockAndWaitsForCallbacksToFinish()
    {
        using var rt = TestRuntime.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var sql = rt.Game.RunAsync("blocked-cancellation-callback", async uow =>
        {
            using var registration = uow.CancellationToken.Register(() =>
            {
                callbackEntered.SetResult();
                release.Wait();
            });
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, uow.CancellationToken);
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Patience);
            await Task.Run(rt.Game.UnitOfWork.RequestStop).WaitAsync(TimeSpan.FromSeconds(2));
            await callbackEntered.Task.WaitAsync(Patience);
            var stopped = rt.Game.UnitOfWork.StopAsync();
            Assert.False(stopped.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rt.Game.ReadAsync(uow => uow.CreditsAsync(), CancellationToken.None));
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sql.WaitAsync(Patience));
            await stopped.WaitAsync(Patience);
        }
        finally
        {
            release.Set();
            try { await sql; } catch (OperationCanceledException) { }
            await rt.Game.UnitOfWork.StopAsync();
        }
    }
}
