using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// Club database session and pool limits from the <c>database</c> section of config.json (defaults in
/// config.example.json). The timeouts apply to each server connection as startup options, never as
/// PostgreSQL server-wide settings.
/// </summary>
public sealed record DatabaseSettings(
    int LockTimeoutMilliseconds,
    int StatementTimeoutMilliseconds,
    int DeadlockRetries,
    int MaxPoolSize,
    int ConnectionTimeoutMilliseconds,
    int ConnectionReserve,
    int MaxAutoPrepare,
    int ManagedPort,
    string ManagedPassword,
    ClusterSettings Cluster)
{
    private const int MinimumLockTimeoutMilliseconds = 100;
    private const int MaximumTimeoutMilliseconds = 3_600_000;
    private const int MinimumStatementTimeoutMilliseconds = 1_000;
    private const int MaximumDeadlockRetries = 10;
    private const int MaximumPoolSize = 500;
    private const int MinimumConnectionTimeoutMilliseconds = 1_000;

    /// <summary>Prepared statements kept per pooled connection; 0 leaves every command unprepared.</summary>
    private const int MaximumAutoPrepare = 1_000;

    /// <summary>Npgsql's connection-acquisition timeout is whole seconds, at most 1024.</summary>
    private const int MaximumConnectionTimeoutMilliseconds = 1_024_000;

    private const string PoolApplicationName = "Aurora21-UltimateTeam";

    /// <summary>Reads the section; a missing key uses the bundled default, and an unusable value stops start-up.</summary>
    public static DatabaseSettings From(JsonObject? values, JsonObject defaults)
    {
        long Read(string key) => LimitSettings.Read(values, defaults, "database", key);

        var settings = new DatabaseSettings(
            Bounded(Read("lock_timeout_ms"), "lock_timeout_ms", MinimumLockTimeoutMilliseconds, MaximumTimeoutMilliseconds),
            Bounded(Read("statement_timeout_ms"), "statement_timeout_ms", MinimumStatementTimeoutMilliseconds, MaximumTimeoutMilliseconds),
            Bounded(Read("deadlock_retries"), "deadlock_retries", 0, MaximumDeadlockRetries),
            Bounded(Read("max_pool_size"), "max_pool_size", 1, MaximumPoolSize),
            Bounded(Read("connection_timeout_ms"), "connection_timeout_ms", MinimumConnectionTimeoutMilliseconds, MaximumConnectionTimeoutMilliseconds),
            Bounded(Read("connection_reserve"), "connection_reserve", 0, 10000),
            Bounded(Read("max_auto_prepare"), "max_auto_prepare", 0, MaximumAutoPrepare),
            Bounded(Read("managed_port"), "managed_port", 0, 65535),
            ReadPassword(values, defaults),
            ClusterSettings.From(values, defaults));
        if (settings.StatementTimeoutMilliseconds < settings.LockTimeoutMilliseconds)
        {
            throw new RuntimeSetupException("config.json database.statement_timeout_ms must not be shorter than database.lock_timeout_ms.");
        }
        if (settings.ConnectionTimeoutMilliseconds < settings.LockTimeoutMilliseconds)
        {
            throw new RuntimeSetupException("config.json database.connection_timeout_ms must not be shorter than database.lock_timeout_ms.");
        }
        return settings;
    }

    /// <summary>The connection string with these timeouts added to the session startup options.</summary>
    public string ApplyTo(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var options = string.Create(CultureInfo.InvariantCulture,
            $"-c lock_timeout={LockTimeoutMilliseconds} -c statement_timeout={StatementTimeoutMilliseconds}");
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? options : builder.Options + " " + options;
        return builder.ConnectionString;
    }

    /// <summary>
    /// The pooled connection string for the unit-of-work data source: <paramref name="sessionConnectionString"/>
    /// (already carrying the session timeouts) with pooling on, the configured pool size, acquisition timeout and
    /// automatic statement preparation, and an application name that makes pooled sessions recognisable in <c>pg_stat_activity</c>.
    /// </summary>
    public string PooledConnectionString(string sessionConnectionString) =>
        new NpgsqlConnectionStringBuilder(sessionConnectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = MaxPoolSize,
            Timeout = (ConnectionTimeoutMilliseconds + 999) / 1000,
            ApplicationName = PoolApplicationName,
            MaxAutoPrepare = MaxAutoPrepare,
        }.ConnectionString;

    private static string ReadPassword(JsonObject? values, JsonObject defaults)
    {
        var value = values is not null && values.TryGetPropertyValue("managed_password", out var configured)
            ? configured : defaults["managed_password"];
        if (value is not JsonValue json || !json.TryGetValue<string>(out var password)
            || string.IsNullOrWhiteSpace(password) || password.Any(char.IsControl))
            throw new RuntimeSetupException("config.json database.managed_password must be a nonempty string without control characters.");
        return password;
    }

    private static int Bounded(long value, string key, int minimum, int maximum) =>
        value >= minimum && value <= maximum
            ? (int)value
            : throw new RuntimeSetupException($"config.json database.{key} must be between {minimum} and {maximum}.");
}
