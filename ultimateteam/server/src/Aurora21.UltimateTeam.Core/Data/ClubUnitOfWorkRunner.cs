using System.Collections.Concurrent;
using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>
/// The only way to start database work on the pooled data source. Each call leases one connection, opens
/// one transaction, runs the work, and commits; any exception or cancellation rolls the whole transaction
/// back before it reaches the caller, and the connection returns to the pool.
/// </summary>
public sealed class ClubUnitOfWorkRunner
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly int _deadlockRetries;
    private readonly ILogger _log;
    private readonly Action<string>? _observer;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _clubGates = new();
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _accepting = true;
    private int _operations;
    private Task _cancellationTask = Task.CompletedTask;
    private Task? _stopTask;

    /// <summary>Cancels owned units; StopAsync completes only after rollback/commit and lease return.</summary>
    public void RequestStop()
    {
        lock (_lifecycleGate)
        {
            if (!_accepting) return;
            _accepting = false;
            _cancellationTask = _stopping.CancelAsync();
            if (_operations == 0) _drained.TrySetResult();
        }
    }

    public Task StopAsync()
    {
        RequestStop();
        lock (_lifecycleGate) return _stopTask ??= FinishStopAsync();
    }

    private async Task FinishStopAsync()
    {
        try
        {
            await Task.WhenAll(_drained.Task, _cancellationTask).ConfigureAwait(false);
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    /// <summary>Whether the current asynchronous flow already has a unit open; a nested one would deadlock on its own locks.</summary>
    private static readonly AsyncLocal<bool> Active = new();
    private RelationalItemCompatibilityStore? _relationalItems;

    internal void BindRelationalItems(RelationalItemCompatibilityStore store)
    {
        lock (_lifecycleGate)
        {
            if (_operations != 0 || _relationalItems is not null) throw new InvalidOperationException("Bind storage before exposing the runner.");
            _relationalItems = store;
        }
    }

    /// <param name="observer">Test-only SQL observation (command counter, fault injector).</param>
    public ClubUnitOfWorkRunner(NpgsqlDataSource dataSource, TimeProvider time, int deadlockRetries, ILogger log, Action<string>? observer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deadlockRetries);
        _dataSource = dataSource;
        _time = time;
        _deadlockRetries = deadlockRetries;
        _log = log;
        _observer = observer;
    }

    /// <summary>A durable command: one read-committed transaction, committed when <paramref name="work"/> returns.</summary>
    public Task<T> RunAsync<T>(long clubId, string operation, Func<UnitOfWork, ValueTask<T>> work, CancellationToken cancellationToken = default) =>
        RunOnceAsync(clubId, operation, work, serialize: true, cancellationToken);

    public Task RunAsync(long clubId, string operation, Func<UnitOfWork, ValueTask> work, CancellationToken cancellationToken = default) =>
        RunOnceAsync(clubId, operation, async uow =>
        {
            await work(uow);
            return true;
        }, serialize: true, cancellationToken);

    /// <summary>
    /// Work that may meet a deadlock (40P01) or serialization failure (40001), such as a market write: the whole
    /// work is repeated in a fresh transaction up to the configured number of times with a bounded random pause.
    /// The work must rebuild any in-memory state it uses on every attempt.
    /// </summary>
    public async Task<T> RunRetryableAsync<T>(long clubId, string operation, Func<UnitOfWork, ValueTask<T>> work,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(clubId, operation, work, serialize: true, cancellationToken);
            }
            catch (PostgresException error) when (DeadlockRetry.IsRetryable(error) && attempt <= _deadlockRetries)
            {
                _log.LogWarning("DB_RETRY operation={Operation} attempt={Attempt} of {Retries} sqlState={SqlState}",
                    operation, attempt, _deadlockRetries, error.SqlState);
                await Task.Delay(DeadlockRetry.NextPause(), cancellationToken);
            }
            catch (PostgresException error) when (DeadlockRetry.IsRetryable(error))
            {
                _log.LogError("DB_RETRY_EXHAUSTED operation={Operation} attempts={Attempts} sqlState={SqlState}", operation, attempt, error.SqlState);
                throw;
            }
        }
    }

    /// <summary>Reads in a short read-committed transaction, so they also borrow and return a pooled connection.</summary>
    public Task<T> RunReadAsync<T>(long clubId, Func<UnitOfWork, ValueTask<T>> work, CancellationToken cancellationToken = default) =>
        RunOnceAsync(clubId, "read", work, serialize: false, cancellationToken);

    private async Task<T> RunOnceAsync<T>(long clubId, string operation, Func<UnitOfWork, ValueTask<T>> work, bool serialize,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (!_accepting) throw new OperationCanceledException("Database runner is stopping.");
            _operations++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            return await RunTransactionAsync(clubId, operation, work, serialize, linked.Token);
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _operations--;
                if (!_accepting && _operations == 0) _drained.TrySetResult();
            }
        }
    }

    private async Task<T> RunTransactionAsync<T>(long clubId, string operation, Func<UnitOfWork, ValueTask<T>> work, bool serialize,
        CancellationToken cancellationToken)
    {
        if (Active.Value)
        {
            // Inner code receives the open unit; a nested one would wait on its own club gate and row locks.
            throw new InvalidOperationException($"{operation} started a unit of work inside another; pass the IUnitOfWork instead");
        }
        // Serializes one club's durable commands in this process; PostgreSQL row
        // locks still decide correctness across processes and clubs. Reads run concurrently.
        var gate = serialize ? _clubGates.GetOrAdd(clubId, _ => new SemaphoreSlim(1, 1)) : null;
        if (gate is not null)
        {
            using var timing = DatabaseTimings.Measure("club-gate");
            await gate.WaitAsync(cancellationToken);
        }
        Active.Value = true;
        try
        {
            NpgsqlConnection leased;
            using (DatabaseTimings.Measure("pool-acquire"))
            {
                leased = await _dataSource.OpenConnectionAsync(cancellationToken);
            }
            using var leaseTiming = DatabaseTimings.Measure("connection-occupancy");
            await using var connection = leased;
            NpgsqlTransaction opened;
            using (DatabaseTimings.Measure("begin"))
            {
                opened = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            }
            await using var transaction = opened;
            var unit = new UnitOfWork(connection, transaction, clubId, _time, _observer, cancellationToken, _relationalItems);
            try
            {
                T result;
                using (DatabaseTimings.Measure("delegate-including-sql"))
                {
                    result = await work(unit);
                }
                // The commit is the decision point: work cancelled before it rolls back, work that reached it commits.
                cancellationToken.ThrowIfCancellationRequested();
                using (DatabaseTimings.Measure("commit"))
                {
                    await transaction.CommitAsync(CancellationToken.None);
                }
                return result;
            }
            catch (Exception error)
            {
                await RollBackAsync(transaction, operation, error);
                throw;
            }
        }
        finally
        {
            Active.Value = false;
            gate?.Release();
        }
    }

    private async ValueTask RollBackAsync(NpgsqlTransaction transaction, string operation, Exception cause)
    {
        try
        {
            if (transaction.Connection is { State: ConnectionState.Open })
            {
                using var timing = DatabaseTimings.Measure("rollback");
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
        catch (Exception rollback) when (rollback is NpgsqlException or InvalidOperationException)
        {
            // A broken connection has already discarded the transaction; the pool drops the connection.
            _log.LogWarning("DB_ROLLBACK_FAILED operation={Operation} cause={Cause} error={Error}", operation, cause.GetType().Name, rollback.Message);
        }
    }
}
