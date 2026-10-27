using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>
/// The PostgreSQL server holding the club database. By default this is the player's private
/// managed cluster (<see cref="LocalPostgres"/>), which is stopped again on dispose when this
/// process started it. A deployment or developer points at another server with
/// <see cref="ConnectionVariable"/> (an Npgsql connection string).
/// </summary>
public sealed class ClubDatabase : IDisposable
{
    public const string ConnectionVariable = "ULTIMATETEAM_POSTGRES_CONNECTION";

    private readonly LocalPostgres? _managed;
    private readonly LocalPostgresInstance? _instance;
    private readonly ILogger _log;

    private ClubDatabase(string connectionString, DatabaseSettings settings, LocalPostgres? managed, LocalPostgresInstance? instance, ILogger log)
    {
        ConnectionString = settings.ApplyTo(connectionString);
        Settings = settings;
        DataSource = NpgsqlDataSource.Create(settings.PooledConnectionString(ConnectionString));
        _managed = managed;
        _instance = instance;
        _log = log;
    }

    /// <summary>The server connection string, with the configured per-session timeouts.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// The bounded application-wide pool that <see cref="ClubUnitOfWorkRunner"/> leases connections from. Its
    /// connections only open on first use; open clubs do not retain connections.
    /// </summary>
    public NpgsqlDataSource DataSource { get; }

    public DatabaseSettings Settings { get; }

    public bool Managed => _managed is not null;

    public static ClubDatabase Connect(RuntimePaths paths, bool testMode, string? connectionString, DatabaseSettings settings, ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return new ClubDatabase(connectionString, settings, null, null, log);
        }
        var external = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            if (testMode)
            {
                throw new RuntimeSetupException($"HTTP-only tests use the managed cluster under the isolated runtime root; unset {ConnectionVariable}.");
            }
            log.LogInformation("Using the PostgreSQL server from {Variable}", ConnectionVariable);
            return new ClubDatabase(external, settings, null, null, log);
        }
        var layout = LocalPostgresLayout.From(paths, testMode ? null : settings);
        var managed = new LocalPostgres(layout, log);
        var instance = managed.Start();
        return new ClubDatabase(instance.ConnectionString, settings, managed, instance, log);
    }

    /// <summary>Where the club lives, without credentials.</summary>
    public JsonObject Describe()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        return new JsonObject
        {
            ["engine"] = "postgresql",
            ["backend"] = "relational",
            ["mode"] = Managed ? "managed" : "external",
            ["server"] = $"{builder.Host}:{builder.Port}",
            ["database"] = builder.Database,
            ["dataDirectory"] = _managed is null ? null : Path.GetFullPath(_managed.Layout.DataDirectory),
        };
    }

    public void Dispose()
    {
        // Pooled sessions close before a cluster this process started is stopped.
        DataSource.Dispose();
        if (_managed is null || _instance is not { StartedHere: true })
        {
            return;
        }
        try
        {
            _managed.Stop();
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException)
        {
            _log.LogWarning("Could not stop the local PostgreSQL cluster: {Error}", error.Message);
        }
    }
}
