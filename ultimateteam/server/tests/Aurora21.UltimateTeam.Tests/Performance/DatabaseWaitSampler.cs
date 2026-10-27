using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Npgsql;

namespace Aurora21.UltimateTeam.Tests.Performance;

/// <summary>Test-only sampled backend states in the harness-owned database; never collects SQL or identities.</summary>
internal sealed class DatabaseWaitSampler
{
    private readonly string _connectionString;
    private readonly string _database;
    private readonly ConcurrentDictionary<string, long> _backendSamples = new();
    private long _samples;
    private long _maximumPendingWork;
    private int _maximumThreads;

    public DatabaseWaitSampler(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString);
        _database = options.Database ?? throw new InvalidOperationException("Test database required");
        if (!_database.StartsWith("t_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Wait sampling requires a harness-owned test database");
        }
        // Observe from the same throwaway cluster's admin database, outside the application's pool.
        options.Database = "postgres";
        options.Pooling = false;
        options.ApplicationName = "Aurora21-Attribution";
        options.CommandTimeout = 5;
        _connectionString = options.ConnectionString;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                await using var command = new NpgsqlCommand("""
                    SELECT CASE
                      WHEN state = 'idle' THEN 'idle'
                      WHEN state = 'idle in transaction' THEN 'idle-in-transaction'
                      WHEN wait_event_type = 'Lock' AND wait_event = 'advisory' THEN 'lock-advisory'
                      WHEN wait_event_type = 'Lock' THEN 'lock-other'
                      WHEN wait_event_type = 'IO' THEN 'io'
                      WHEN wait_event_type = 'LWLock' THEN 'lwlock'
                      WHEN wait_event_type = 'Client' THEN 'client'
                      WHEN wait_event IS NULL AND state = 'active' THEN 'active-no-reported-wait'
                      ELSE 'other' END AS category, count(*)
                    FROM pg_stat_activity
                    WHERE datname = @database AND backend_type = 'client backend'
                    GROUP BY 1
                    """, connection);
                command.Parameters.AddWithValue("database", _database);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var count = reader.GetInt64(1);
                    _backendSamples.AddOrUpdate(reader.GetString(0), count, (_, prior) => prior + count);
                }
                Interlocked.Increment(ref _samples);
                _maximumPendingWork = Math.Max(_maximumPendingWork, ThreadPool.PendingWorkItemCount);
                _maximumThreads = Math.Max(_maximumThreads, ThreadPool.ThreadCount);
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public JsonObject Report() => new()
    {
        ["samples"] = _samples,
        ["periodMs"] = 250,
        ["method"] = "Backend-state counts sampled from a separate nonpooled admin connection to this throwaway cluster. Sampling is not exact wait duration or database CPU; idle-in-transaction can include client service work. No SQL/identity values retained.",
        ["backendSamples"] = new JsonObject(_backendSamples.OrderBy(pair => pair.Key)
            .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
        ["maximumThreadPoolPendingWorkItems"] = _maximumPendingWork,
        ["maximumThreadPoolThreads"] = _maximumThreads,
    };
}
