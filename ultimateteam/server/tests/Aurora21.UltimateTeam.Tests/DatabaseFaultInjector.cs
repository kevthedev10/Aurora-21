namespace Aurora21.UltimateTeam.Tests;

/// <summary>
/// Test-only fault injection for application SQL (every statement a unit of work runs
/// and pooled units of work). When armed it throws just before the Nth writing or row-locking statement, or
/// cancels a token there and lets the statement observe the cancellation, so a test can fail a durable command at
/// every position and prove that nothing from the failed attempt was committed.
/// </summary>
public sealed class DatabaseFaultInjector
{
    private static readonly string[] WritePrefixes = { "INSERT", "UPDATE", "DELETE", "WITH" };

    private readonly object _gate = new();
    private readonly Func<Exception> _fault;
    private int _failAt;
    private int _writes;
    private int _remainingFaults;
    private CancellationTokenSource? _cancel;

    /// <param name="fault">The exception to throw; an <see cref="InjectedDatabaseFault"/> by default.</param>
    public DatabaseFaultInjector(Func<Exception>? fault = null) =>
        _fault = fault ?? (() => new InjectedDatabaseFault());

    /// <summary>True once the armed fault has been thrown.</summary>
    public bool Triggered { get; private set; }

    /// <summary>How many faults have been thrown since the last arming.</summary>
    public int FaultsThrown { get; private set; }

    /// <summary>
    /// Arms a fault before the <paramref name="ordinal"/>th (1-based) write or lock from now. With
    /// <paramref name="times"/> above one, the count restarts after each fault, so a retried transaction
    /// fails at the same position again.
    /// </summary>
    public void FailOnWrite(int ordinal, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        lock (_gate)
        {
            _failAt = ordinal;
            _writes = 0;
            _remainingFaults = times;
            _cancel = null;
            FaultsThrown = 0;
            Triggered = false;
        }
    }

    /// <summary>
    /// Arms a cancellation instead of an exception: just before the <paramref name="ordinal"/>th write or lock,
    /// <paramref name="cancellation"/> is cancelled and the statement runs, so the database call itself sees it.
    /// </summary>
    public void CancelOnWrite(int ordinal, CancellationTokenSource cancellation)
    {
        FailOnWrite(ordinal);
        lock (_gate)
        {
            _cancel = cancellation;
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _failAt = 0;
        }
    }

    public void Observe(string sql)
    {
        lock (_gate)
        {
            if (_failAt == 0 || !IsWriteOrLock(sql) || ++_writes != _failAt)
            {
                return;
            }
            _writes = 0;
            if (--_remainingFaults == 0)
            {
                _failAt = 0;
            }
            Triggered = true;
            if (_cancel is { } cancel)
            {
                cancel.Cancel();
                return;
            }
            FaultsThrown++;
        }
        throw _fault();
    }

    private static bool IsWriteOrLock(string sql)
    {
        var text = sql.TrimStart();
        return WritePrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               || text.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class InjectedDatabaseFault : Exception
{
    public InjectedDatabaseFault() : base("injected database fault")
    {
    }
}
