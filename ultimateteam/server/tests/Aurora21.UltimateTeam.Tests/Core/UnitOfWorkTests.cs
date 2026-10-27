using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>Phase 4 Step 5: pooled units of work commit, roll back, retry, use savepoints and honour cancellation.</summary>
public sealed class UnitOfWorkTests
{
    private const int MaxPositions = 50;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StageTimingsObserveCommitAndCancelledSqlWithoutSensitiveLabels()
    {
        using var rt = TestRuntime.Create();
        var observed = new ConcurrentDictionary<string, int>();
        var measuring = new AsyncLocal<bool>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == DatabaseTimings.MeterName && instrument.Name == "ultimateteam.database.stage") { owner.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            if (!measuring.Value) { return; }
            Assert.True(double.IsFinite(value) && value >= 0);
            Assert.Equal(1, tags.Length);
            Assert.Equal("stage", tags[0].Key);
            var stage = Assert.IsType<string>(tags[0].Value);
            observed.AddOrUpdate(stage, 1, (_, count) => count + 1);
        });
        listener.Start();
        measuring.Value = true;
        await rt.Game.UnitOfWork.RunAsync(rt.Game.ClubId, "test-timings", async work =>
        {
            await work.LockClubAsync();
            Assert.Equal(1, await work.ScalarAsync<int>("SELECT 1"));
        });
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rt.Game.UnitOfWork.RunAsync(rt.Game.ClubId,
            "test-cancel-timings", async work =>
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                await work.ExecuteAsync("SELECT pg_sleep(30)");
            }, cancellation.Token).WaitAsync(Patience));
        Assert.Equal(2, observed["pool-acquire"]);
        Assert.Equal(2, observed["club-gate"]);
        Assert.Equal(2, observed["connection-occupancy"]);
        Assert.Equal(2, observed["delegate-including-sql"]);
        Assert.Equal(1, observed["commit"]);
        Assert.Equal(1, observed["rollback"]);
        Assert.Equal(1, observed["sql-execute"]);
        Assert.Equal(1, observed["sql-row-lock"]);
        Assert.Equal(1, observed["sql-scalar"]);
    }

    [Fact]
    public async Task CommittedWorkIsVisibleAndRunsOnAPooledSessionWithTheConfiguredTimeouts()
    {
        using var rt = TestRuntime.Create(prepareRoot: root => WriteConfig(root, """{"database": {"statement_timeout_ms": 45000}}"""));
        var store = rt.Game.Store();
        var credits = store.Credits();
        store.SaveItem(new JsonObject { ["id"] = ClubRepository.FirstItemId + 777, ["resourceId"] = 158023, ["pile"] = "club", ["itemType"] = "player" });

        var session = await rt.Game.UnitOfWork.RunAsync(store.ClubId, "test-commit", async work =>
        {
            await ClubRepository.AddCreditsToAsync(work, work.ClubId, 500);
            await ClubRepository.SetFlagAsync(work, "unit-of-work-test");
            Assert.NotNull(await ItemRepository.GetItemAsync(work, ClubRepository.FirstItemId + 777));
            return (Application: await work.ScalarAsync<string>("SELECT current_setting('application_name')"),
                StatementTimeout: await work.ScalarAsync<string>("SHOW statement_timeout"));
        });

        Assert.Equal(credits + 500, store.Credits());
        Assert.True(store.HasFlag("unit-of-work-test"));
        Assert.Equal("Aurora21-UltimateTeam", session.Application);
        Assert.Equal("45s", session.StatementTimeout);
    }

    [Fact]
    public async Task AnExceptionRollsBackEveryWriteOfTheUnit()
    {
        using var rt = TestRuntime.Create();
        var before = Snapshot(rt);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Game.UnitOfWork.RunAsync(rt.Game.Store().ClubId, "test-rollback", async work =>
        {
            await ClubRepository.AddCreditsToAsync(work, work.ClubId, 500);
            await ItemRepository.AddUnopenedPackAsync(work, 3, "test");
            throw new InvalidOperationException("work failed");
        }));

        Assert.Equal("work failed", error.Message);
        Assert.Equal(before, Snapshot(rt));
    }

    [Fact]
    public async Task AFaultBeforeAnyWriteLeavesNothingBehind()
    {
        var faults = new DatabaseFaultInjector();
        using var rt = TestRuntime.Create(faultInjector: faults);
        var clubId = rt.Game.Store().ClubId;

        var failures = await AtEveryWriteAsync(rt, faults,
            position => faults.FailOnWrite(position),
            () => rt.Game.UnitOfWork.RunAsync(clubId, "test-faults", SeveralWritesAsync));

        Assert.True(failures >= 6, $"only {failures} write positions were exercised");
        Assert.True(rt.Game.Store().HasFlag("several-writes"));
    }

    [Fact]
    public async Task CancellationBeforeAnyWriteRollsBackTheUnit()
    {
        var faults = new DatabaseFaultInjector();
        using var rt = TestRuntime.Create(faultInjector: faults);
        var clubId = rt.Game.Store().ClubId;
        CancellationTokenSource? cancellation = null;

        var cancelled = await AtEveryWriteAsync(rt, faults,
            position =>
            {
                cancellation?.Dispose();
                cancellation = new CancellationTokenSource();
                faults.CancelOnWrite(position, cancellation);
            },
            () => rt.Game.UnitOfWork.RunAsync(clubId, "test-cancel", SeveralWritesAsync, cancellation!.Token),
            expectCancellation: true);

        cancellation?.Dispose();
        Assert.True(cancelled >= 6, $"only {cancelled} write positions were exercised");
        Assert.True(rt.Game.Store().HasFlag("several-writes"));
    }

    [Fact]
    public async Task CancellingARunningStatementRollsBackAndThePoolStaysUsable()
    {
        using var rt = TestRuntime.Create();
        var clubId = rt.Game.Store().ClubId;
        var before = Snapshot(rt);
        using var cancellation = new CancellationTokenSource();

        var running = rt.Game.UnitOfWork.RunAsync(clubId, "test-long", async work =>
        {
            await ClubRepository.AddCreditsToAsync(work, work.ClubId, 500);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));
            await work.ExecuteAsync("SELECT pg_sleep(30)");
        }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
        Assert.Equal(before, Snapshot(rt));
        Assert.Equal(1, await rt.Game.UnitOfWork.RunReadAsync(clubId, work => work.ScalarAsync<int>("SELECT 1")));
    }

    [Fact]
    public async Task ADeadlockIsRetriedAndAnExhaustedRetryCommitsNothing()
    {
        var faults = new DatabaseFaultInjector(() => new PostgresException("injected deadlock", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected));
        using var rt = TestRuntime.Create(faultInjector: faults);
        var store = rt.Game.Store();
        var retries = rt.Game.Config.Database.DeadlockRetries;
        Assert.True(retries >= 1);
        var credits = store.Credits();

        faults.FailOnWrite(1);
        await rt.Game.UnitOfWork.RunRetryableAsync(store.ClubId, "test-retry", work => ClubRepository.AddCreditsToAsync(work, work.ClubId, 100));
        Assert.Equal(1, faults.FaultsThrown);
        Assert.Equal(credits + 100, store.Credits());

        var before = Snapshot(rt);
        faults.FailOnWrite(1, times: retries + 1);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            rt.Game.UnitOfWork.RunRetryableAsync(store.ClubId, "test-retry", work => ClubRepository.AddCreditsToAsync(work, work.ClubId, 100)));
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, error.SqlState);
        Assert.Equal(retries + 1, faults.FaultsThrown);
        Assert.Equal(before, Snapshot(rt));
    }

    [Fact]
    public async Task AFailedSavepointStepRollsBackOnlyThatStep()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();
        var credits = store.Credits();

        await rt.Game.UnitOfWork.RunAsync(store.ClubId, "test-savepoint", async work =>
        {
            await ClubRepository.AddCreditsToAsync(work, work.ClubId, 100);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await work.SavepointAsync(async () =>
            {
                await ClubRepository.AddCreditsToAsync(work, work.ClubId, 1_000);
                throw new InvalidOperationException("recoverable step failed");
            }));
            await work.SavepointAsync(async () => await ClubRepository.AddCreditsToAsync(work, work.ClubId, 10));
        });

        Assert.Equal(credits + 110, store.Credits());
    }

    [Fact]
    public async Task ConcurrentUnitsShareABoundedPool()
    {
        using var rt = TestRuntime.Create(prepareRoot: root => WriteConfig(root, """{"database": {"max_pool_size": 2}}"""));
        var clubId = rt.Game.Store().ClubId;

        var observed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => rt.Game.UnitOfWork.RunReadAsync(clubId, async work =>
        {
            var sessions = await work.ScalarAsync<long>("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND application_name = current_setting('application_name')
                """);
            await work.ExecuteAsync("SELECT pg_sleep(0.2)");
            return sessions;
        }))).WaitAsync(Patience);

        Assert.All(observed, sessions => Assert.InRange(sessions, 1, 2));
    }

    /// <summary>Seven writes across four repositories, all of which must commit or roll back together.</summary>
    private static async ValueTask SeveralWritesAsync(IUnitOfWork work)
    {
        await ClubRepository.LockClubAsync(work);
        await ClubRepository.AddCreditsToAsync(work, work.ClubId, 250);
        await ItemRepository.AddUnopenedPackAsync(work, 3, "test");
        var first = await ClubRepository.ReserveItemIdsAsync(work, 2);
        await ItemRepository.SaveItemsAsync(work, new[]
        {
            new JsonObject { ["id"] = first, ["resourceId"] = 158023, ["pile"] = "unassigned", ["itemType"] = "player" },
            new JsonObject { ["id"] = first + 1, ["resourceId"] = 20801, ["pile"] = "unassigned", ["itemType"] = "player" },
        });
        await work.BumpObjectiveStatAsync("packs", 1, 10);
        await ClubRepository.SetFlagAsync(work, "several-writes");
    }

    /// <summary>
    /// Arms a fault at the 1st, 2nd, … write or lock until the unit completes. Every interrupted attempt must fail and
    /// leave the club's committed rows unchanged. Returns the number of interrupted positions.
    /// </summary>
    private static async Task<int> AtEveryWriteAsync(TestRuntime rt, DatabaseFaultInjector faults, Action<int> arm, Func<Task> run,
        bool expectCancellation = false)
    {
        for (var position = 1; position <= MaxPositions; position++)
        {
            var before = Snapshot(rt);
            arm(position);
            Exception? thrown = null;
            try
            {
                await run();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                thrown = error;
            }
            finally
            {
                faults.Disarm();
            }
            if (!faults.Triggered)
            {
                Assert.Null(thrown);
                return position - 1;
            }
            Assert.NotNull(thrown);
            if (expectCancellation)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            }
            Assert.Equal(before, Snapshot(rt));
        }
        throw new InvalidOperationException($"the unit still wrote after {MaxPositions} positions");
    }

    private static string Snapshot(TestRuntime rt) => ClubStateSnapshot.Capture(rt.ConnectionString, rt.Game.Store().ClubId);

    private static void WriteConfig(string root, string json)
    {
        Directory.CreateDirectory(Path.Combine(root, "user-config"));
        File.WriteAllText(Path.Combine(root, "user-config", "config.json"), json);
    }
}
