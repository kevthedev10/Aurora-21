using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Tests;

/// <summary>
/// One throwaway PostgreSQL cluster per test run, started from the portable binaries in a
/// temporary folder; every test runtime gets its own database in it. It never touches the
/// maintainer's managed cluster or club.
/// </summary>
internal static class PostgresTestCluster
{
    private static readonly Lazy<(LocalPostgres Cluster, int Port, string Password)> Shared = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _maxConnections;
    private static ClusterSettings? _cluster;

    /// <summary>Headroom for cluster housekeeping on a busy host; Npgsql's own default is 30 seconds.</summary>
    private const int TeardownTimeoutSeconds = 180;

    /// <summary>
    /// Raises the throwaway cluster's <c>max_connections</c> above the managed-cluster default of 30 (performance
    /// runs with a larger pool). Must be set before the first database is created in this process.
    /// </summary>
    public static int MaxConnections
    {
        get => _maxConnections;
        set
        {
            if (Shared.IsValueCreated) { throw new InvalidOperationException("The test cluster is already running"); }
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 30);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1000);
            _maxConnections = value;
        }
    }

    /// <summary>
    /// Cluster-wide settings (a deployment profile under measurement) written the way the managed cluster
    /// writes them; null keeps PostgreSQL's own defaults. <see cref="MaxConnections"/>, when set, still wins.
    /// Must be set before the first database is created in this process.
    /// </summary>
    public static ClusterSettings? Cluster
    {
        get => _cluster;
        set
        {
            if (Shared.IsValueCreated) { throw new InvalidOperationException("The test cluster is already running"); }
            _cluster = value;
        }
    }

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public static string CreateDatabase()
    {
        var (_, port, password) = Shared.Value;
        var name = "t_" + Guid.NewGuid().ToString("N");
        using (var connection = new NpgsqlConnection(LocalPostgres.ConnectionString(port, password, "postgres")))
        {
            connection.Open();
            using var command = new NpgsqlCommand($"CREATE DATABASE {name} OWNER {LocalPostgres.UserName}", connection);
            command.CommandTimeout = TeardownTimeoutSeconds;
            command.ExecuteNonQuery();
        }
        return LocalPostgres.ConnectionString(port, password, name);
    }

    /// <summary>
    /// Creates a database with the relational schema and catalog already installed and returns its connection
    /// string. It is a copy of a template installed once per test run by the same
    /// <see cref="RelationalSchemaBootstrap.InstallFreshAsync(NpgsqlDataSource, CancellationToken)"/> a test would
    /// otherwise run, so a runtime that opens it takes the already-installed path, as a restarted server does.
    /// A later install call on it takes the already-installed path (a readiness check).
    /// Tests of the fresh-install path itself use <see cref="CreateDatabase"/>.
    /// </summary>
    public static string CreateInstalledDatabase()
    {
        var (_, port, password) = Shared.Value;
        var template = Template.Value;
        var name = "t_" + Guid.NewGuid().ToString("N");
        using (var connection = new NpgsqlConnection(LocalPostgres.ConnectionString(port, password, "postgres")))
        {
            connection.Open();
            using var command = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE {template} OWNER {LocalPostgres.UserName}", connection);
            command.CommandTimeout = TeardownTimeoutSeconds;
            command.ExecuteNonQuery();
        }
        return LocalPostgres.ConnectionString(port, password, name);
    }

    private static readonly Lazy<string> Template = new(InstallTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string InstallTemplate()
    {
        var connectionString = CreateDatabase();
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        using (var source = NpgsqlDataSource.Create(connectionString))
        {
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        }
        // Copying requires that nobody is connected to the template. Disposing the install's data source closed
        // its connections; refusing later ones means a stray test cannot block every copy or change the template.
        var (_, port, password) = Shared.Value;
        using var connection = new NpgsqlConnection(LocalPostgres.ConnectionString(port, password, "postgres"));
        connection.Open();
        using var command = new NpgsqlCommand($"ALTER DATABASE {name} WITH IS_TEMPLATE true ALLOW_CONNECTIONS false", connection);
        command.ExecuteNonQuery();
        return name;
    }

    public static void DropDatabase(string connectionString)
    {
        var (_, port, password) = Shared.Value;
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        using var connection = new NpgsqlConnection(LocalPostgres.ConnectionString(port, password, "postgres"));
        connection.Open();
        using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
        // FORCE already evicts other sessions, so this waits on the server finishing the drop rather
        // than on a lock. Under a loaded host that outran Npgsql's 30 second default and failed tests
        // in teardown, after their bodies had already passed.
        command.CommandTimeout = TeardownTimeoutSeconds;
        command.ExecuteNonQuery();
    }

    private static (LocalPostgres, int, string) Start()
    {
        RemoveStoppedClusters();
        var root = Path.Combine(Path.GetTempPath(), "ultimateteam21-pg-" + Guid.NewGuid().ToString("N"));
        var layout = new LocalPostgresLayout(
            Path.Combine(BinariesRoot(), RuntimePaths.PostgresPlatform, "bin"),
            Path.Combine(root, "data"),
            Path.Combine(root, "ultimateteam.password"),
            Path.Combine(root, "postgres.log"),
            Cluster: _cluster);
        var cluster = new LocalPostgres(layout, NullLogger.Instance);
        if (!cluster.BinariesInstalled)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binaries are missing in {layout.BinDirectory}. Run 'Aurora21-UltimateTeam-Server postgres install' " +
                $"(or set {RuntimePaths.PostgresRootVariable}) before running the tests.");
        }
        var instance = cluster.Start();
        if (_maxConnections > 0)
        {
            var password = File.ReadAllText(layout.PasswordPath).Trim();
            using (var connection = new NpgsqlConnection(LocalPostgres.ConnectionString(instance.Port, password, "postgres")))
            {
                connection.Open();
                using var command = new NpgsqlCommand($"ALTER SYSTEM SET max_connections = {_maxConnections}", connection);
                command.ExecuteNonQuery();
            }
            // max_connections only changes on restart.
            cluster.Stop();
            instance = cluster.Start();
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                cluster.Stop();
                Directory.Delete(root, recursive: true);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
            {
            }
        };
        return (cluster, instance.Port, File.ReadAllText(layout.PasswordPath).Trim());
    }

    /// <summary>Earlier runs stop their cluster on exit, but the host may end before the folder is deleted.</summary>
    private static void RemoveStoppedClusters()
    {
        foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), "ultimateteam21-pg-*"))
        {
            if (File.Exists(Path.Combine(directory, "data", "postmaster.pid")))
            {
                continue;
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string BinariesRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable(RuntimePaths.PostgresRootVariable);
        if (!string.IsNullOrEmpty(explicitRoot))
        {
            return explicitRoot;
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ultimateteam", "postgres");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "postgres");
    }
}
