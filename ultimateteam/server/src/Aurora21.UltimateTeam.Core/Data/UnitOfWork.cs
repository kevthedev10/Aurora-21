using System.Globalization;
using Npgsql;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>
/// One leased pooled connection and its transaction for one root operation. Created and disposed only by
/// <see cref="ClubUnitOfWorkRunner"/>; inner code receives it as <see cref="IUnitOfWork"/> instead of opening its own.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly TimeProvider _time;
    private readonly Action<string>? _observer;
    private readonly RelationalItemCompatibilityStore? _relationalItems;
    private readonly Dictionary<long, bool> _heldClubLocks = new();
    private long _savepointSequence;

    internal UnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction, long clubId, TimeProvider time,
        Action<string>? observer, CancellationToken cancellationToken, RelationalItemCompatibilityStore? relationalItems = null)
    {
        _connection = connection;
        _transaction = transaction;
        ClubId = clubId;
        _time = time;
        _observer = observer;
        CancellationToken = cancellationToken;
        _relationalItems = relationalItems;
    }

    public long ClubId { get; private set; }
    public RelationalItemCompatibilityStore RelationalItems => _relationalItems
        ?? throw new InvalidOperationException("The relational item store has not been bound.");

    public long NowSeconds => _time.GetUtcNow().ToUnixTimeSeconds();

    public long NowMilliseconds => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public bool InTransaction => true;

    public IDictionary<long, bool> HeldClubLocks => _heldClubLocks;

    public CancellationToken CancellationToken { get; }

    /// <summary>Points <c>@club</c> at another club, for work that first resolves the club (for example by persona).</summary>
    public void UseClub(long clubId) => ClubId = clubId;

    public async ValueTask<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        using var timing = DatabaseTimings.Measure(SqlStage(sql, "sql-execute"));
        using var queryTiming = DatabaseTimings.MeasureQuery(sql);
        await using var command = ClubSql.Command(_connection, _transaction, ClubId, _observer, sql, parameters);
        var affected = await command.ExecuteNonQueryAsync(CancellationToken);
        if (queryTiming is not null) { queryTiming.Rows = Math.Max(0, affected); queryTiming.Succeeded = true; }
        return affected;
    }

    public async ValueTask<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var timing = DatabaseTimings.Measure(SqlStage(sql, "sql-query-materialization"));
        using var queryTiming = DatabaseTimings.MeasureQuery(sql);
        await using var command = ClubSql.Command(_connection, _transaction, ClubId, _observer, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(CancellationToken))
        {
            rows.Add(map(reader));
            if (queryTiming is not null) queryTiming.Rows++;
        }
        if (queryTiming is not null) queryTiming.Succeeded = true;
        return rows;
    }

    public async ValueTask<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var timing = DatabaseTimings.Measure(SqlStage(sql, "sql-scalar"));
        using var queryTiming = DatabaseTimings.MeasureQuery(sql);
        await using var command = ClubSql.Command(_connection, _transaction, ClubId, _observer, sql, parameters);
        var result = await command.ExecuteScalarAsync(CancellationToken);
        var value = ClubSql.Convert<T>(result);
        if (queryTiming is not null) { queryTiming.Rows = result is null ? 0 : 1; queryTiming.Succeeded = true; }
        return value;
    }

    public async ValueTask SavepointAsync(Func<ValueTask> step)
    {
        var name = "aurora21_" + (++_savepointSequence).ToString(CultureInfo.InvariantCulture);
        // ROLLBACK TO SAVEPOINT releases the row locks taken inside the step, so the held-lock record
        // has to rewind with it; otherwise a later caller would skip a lock the database no longer
        // holds. RELEASE keeps those locks until the transaction ends, so the success path keeps them.
        var heldBeforeStep = new Dictionary<long, bool>(_heldClubLocks);
        await _transaction.SaveAsync(name, CancellationToken);
        try
        {
            await step();
            await _transaction.ReleaseAsync(name, CancellationToken);
        }
        catch
        {
            await _transaction.RollbackAsync(name, CancellationToken.None);
            _heldClubLocks.Clear();
            foreach (var held in heldBeforeStep) _heldClubLocks[held.Key] = held.Value;
            throw;
        }
    }

    private static string SqlStage(string sql, string fallback) =>
        !DatabaseTimings.Enabled ? fallback :
        sql.Contains("pg_advisory", StringComparison.OrdinalIgnoreCase) ? "sql-advisory-lock" :
        sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase) ? "sql-row-lock" : fallback;
}
