using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Where a private PostgreSQL cluster lives.</summary>
public sealed record LocalPostgresLayout(string BinDirectory, string DataDirectory, string PasswordPath, string LogPath, int Port = 0, string? Password = null, ClusterSettings? Cluster = null)
{
    public static LocalPostgresLayout From(RuntimePaths paths, DatabaseSettings? settings = null) =>
        new(paths.PostgresBinDirectory, paths.PostgresDataDirectory, paths.PostgresPasswordPath,
            Path.Combine(paths.LogsDirectory, "postgres.log"), settings?.ManagedPort ?? 0, settings?.ManagedPassword, settings?.Cluster);
}

/// <summary>A running cluster and whether this process is responsible for stopping it.</summary>
public sealed record LocalPostgresInstance(string ConnectionString, int Port, bool StartedHere);

/// <summary>
/// The player's private PostgreSQL cluster, run from the portable binaries shipped next to the
/// server. It listens on the configured loopback port only, uses password (SCRAM) authentication, and keeps its files under the runtime root next to the logs. A cluster left
/// running by a process that was killed is reused rather than started twice.
/// </summary>
public sealed class LocalPostgres
{
    public const string DatabaseName = "ultimateteam";
    public const string UserName = "ultimateteam";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(120);

    /// <summary>A start can lose its free port to another program between choosing and binding it.</summary>
    private const int StartAttempts = 3;

    private readonly LocalPostgresLayout _layout;
    private readonly ILogger _log;

    public LocalPostgres(LocalPostgresLayout layout, ILogger log)
    {
        _layout = layout;
        _log = log;
    }

    public LocalPostgresLayout Layout => _layout;

    public bool BinariesInstalled => File.Exists(Tool("pg_ctl")) && File.Exists(Tool("initdb")) && File.Exists(Tool("postgres"));

    public bool Initialized => File.Exists(Path.Combine(_layout.DataDirectory, "PG_VERSION"));

    /// <summary>Initializes the cluster when needed, starts it (or attaches to it) and ensures the club database exists.</summary>
    public LocalPostgresInstance Start()
    {
        if (!BinariesInstalled)
        {
            throw new RuntimeSetupException(
                $"PostgreSQL binaries were not found in {_layout.BinDirectory}. Run Build-FIFA21-Launcher.ps1, or 'Aurora21-UltimateTeam-Server postgres install'.");
        }
        using var gate = AcquireLock();
        if (!Initialized)
        {
            Initialize();
        }
        WriteTuning();
        var password = File.ReadAllText(_layout.PasswordPath).Trim();

        LocalPostgresInstance Ready(int port, bool startedHere)
        {
            password = SynchronizePassword(port, password);
            EnsureDatabase(port, password);
            return new LocalPostgresInstance(ConnectionString(port, password, DatabaseName), port, startedHere);
        }

        if (RunningPort() is { } existingPort)
        {
            if (_layout.Port != 0 && existingPort != _layout.Port)
                throw new RuntimeSetupException($"The managed cluster is running on port {existingPort}, but database.managed_port is {_layout.Port}. Stop it with Aurora21-UltimateTeam-Server postgres stop, then start it again.");
            _log.LogInformation("Attached to the running local PostgreSQL cluster on 127.0.0.1:{Port}", existingPort);
            return Ready(existingPort, startedHere: false);
        }

        var failure = string.Empty;
        for (var attempt = 0; attempt < (_layout.Port == 0 ? StartAttempts : 1); attempt++)
        {
            var port = _layout.Port == 0 ? FreeLoopbackPort() : _layout.Port;
            Directory.CreateDirectory(Path.GetDirectoryName(_layout.LogPath)!);
            string[] startArguments = { "start", "-D", _layout.DataDirectory, "-l", _layout.LogPath, "-w", "-t", "60", "-s", "-o", $"-p {port}" };
            // The postmaster outlives this process. On Windows it must not inherit this process's
            // handles (see IsolatedProcess); on Unix pg_ctl already detaches it from our pipes.
            var result = OperatingSystem.IsWindows()
                ? IsolatedProcess.Run(Tool("pg_ctl"), startArguments, Path.Combine(Path.GetDirectoryName(_layout.LogPath)!, "pg_ctl.log"), ToolTimeout)
                : RunTool("pg_ctl", waitForOutput: false, startArguments);
            if (result.ExitCode == 0)
            {
                _log.LogInformation("Started the local PostgreSQL cluster on 127.0.0.1:{Port}", port);
                return Ready(port, startedHere: true);
            }
            // Another process may have started the same cluster in the meantime.
            if (RunningPort() is { } racedPort)
            {
                return Ready(racedPort, startedHere: false);
            }
            failure = $"pg_ctl start exited with {result.ExitCode}: {result.Output.Trim()} (see {_layout.LogPath})";
        }
        throw new RuntimeSetupException(failure);
    }

    /// <summary>Stops the cluster if it is running. Returns false when it was not running.</summary>
    public bool Stop()
    {
        if (!BinariesInstalled || !Initialized || RunningPort() is null)
        {
            return false;
        }
        var result = RunTool("pg_ctl", waitForOutput: true, "stop", "-D", _layout.DataDirectory, "-m", "fast", "-w", "-t", "60", "-s");
        if (result.ExitCode != 0 && RunningPort() is not null)
        {
            throw new RuntimeSetupException($"pg_ctl stop exited with {result.ExitCode}: {result.Output.Trim()}");
        }
        _log.LogInformation("Stopped the local PostgreSQL cluster");
        return true;
    }

    /// <summary>The port of the cluster running on this data directory, or null.</summary>
    public int? RunningPort()
    {
        if (!Initialized || !File.Exists(Path.Combine(_layout.DataDirectory, "postmaster.pid")))
        {
            return null;
        }
        var status = RunTool("pg_ctl", waitForOutput: true, "status", "-D", _layout.DataDirectory);
        if (status.ExitCode != 0)
        {
            return null;
        }
        try
        {
            using var stream = new FileStream(Path.Combine(_layout.DataDirectory, "postmaster.pid"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            // Line 4 of postmaster.pid is the port the postmaster listens on.
            for (var line = 1; reader.ReadLine() is { } text; line++)
            {
                if (line == 4 && int.TryParse(text.Trim(), out var port))
                {
                    return port;
                }
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    public static string ConnectionString(int port, string password, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = database,
            Username = UserName,
            Password = password,
            Pooling = false,
            Timeout = 15,
        }.ToString();

    private void Initialize()
    {
        var staging = _layout.DataDirectory + ".initializing";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
        if (Directory.Exists(_layout.DataDirectory) && Directory.EnumerateFileSystemEntries(_layout.DataDirectory).Any())
        {
            throw new RuntimeSetupException($"{_layout.DataDirectory} exists but is not a PostgreSQL cluster; refusing to overwrite it.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_layout.PasswordPath)!);
        var password = _layout.Password ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var passwordFile = Path.Combine(Path.GetDirectoryName(_layout.PasswordPath)!, $"initdb-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(passwordFile, password);
            _log.LogInformation("Creating the local PostgreSQL cluster in {Directory}", _layout.DataDirectory);
            var result = RunTool("initdb", waitForOutput: true,
                "-D", staging, "-U", UserName, "--pwfile", passwordFile, "--auth", "scram-sha-256",
                "-E", "UTF8", "--locale", "C", "--no-instructions");
            if (result.ExitCode != 0)
            {
                throw new RuntimeSetupException($"initdb exited with {result.ExitCode}: {result.Output.Trim()}");
            }
        }
        finally
        {
            File.Delete(passwordFile);
        }

        // Settings for a small single-user cluster that is only reachable from this machine.
        File.AppendAllText(Path.Combine(staging, "postgresql.conf"), """

            # Aurora 21 UltimateTeam: private loopback-only cluster.
            listen_addresses = '127.0.0.1'
            unix_socket_directories = ''
            password_encryption = 'scram-sha-256'
            # Capacity settings live in the generated include below so config.json can change them
            # without recreating the cluster. The include must come last so it wins.
            include_if_exists = 'aurora21-tuning.conf'

            """);
        File.WriteAllText(_layout.PasswordPath, password);
        if (Directory.Exists(_layout.DataDirectory))
        {
            Directory.Delete(_layout.DataDirectory);
        }
        Directory.Move(staging, _layout.DataDirectory);
    }

    private string SynchronizePassword(int port, string storedPassword)
    {
        if (_layout.Password is not { } desired || desired == storedPassword) return storedPassword;
        using var connection = new NpgsqlConnection(ConnectionString(port, storedPassword, "postgres"));
        try { connection.Open(); }
        catch (PostgresException error) when (error.SqlState == "28P01")
        {
            // A previous role update may have committed before the password file was saved.
            using var recovered = new NpgsqlConnection(ConnectionString(port, desired, "postgres"));
            recovered.Open();
            SavePassword(desired);
            return desired;
        }
        using var command = new NpgsqlCommand($"ALTER ROLE {UserName} WITH PASSWORD '" + desired.Replace("'", "''") + "'", connection);
        command.ExecuteNonQuery();
        SavePassword(desired);
        return desired;
    }

    private void SavePassword(string password)
    {
        var temporary = _layout.PasswordPath + ".new";
        File.WriteAllText(temporary, password);
        File.Move(temporary, _layout.PasswordPath, overwrite: true);
    }

    private void EnsureDatabase(int port, string password)
    {
        using var connection = new NpgsqlConnection(ConnectionString(port, password, "postgres"));
        connection.Open();
        using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", DatabaseName);
        if (exists.ExecuteScalar() is null)
        {
            using var create = new NpgsqlCommand($"CREATE DATABASE {DatabaseName} OWNER {UserName} ENCODING 'UTF8' TEMPLATE template0", connection);
            create.ExecuteNonQuery();
        }
    }

    /// <summary>Serializes initdb/start across processes sharing one runtime root.</summary>
    private FileStream AcquireLock()
    {
        var root = Path.GetDirectoryName(_layout.PasswordPath)!;
        Directory.CreateDirectory(root);
        var deadline = DateTime.UtcNow + ToolTimeout;
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(root, "cluster.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>A PostgreSQL tool's file name on this platform.</summary>
    public static string ExecutableName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private string Tool(string name) => Path.Combine(_layout.BinDirectory, ExecutableName(name));

    private (int ExitCode, string Output) RunTool(string name, bool waitForOutput, params string[] arguments)
    {
        var start = new ProcessStartInfo(Tool(name))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _layout.BinDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        // Keep the server's own locale settings from leaking into initdb/postgres.
        start.Environment["LC_ALL"] = "C";
        start.Environment.Remove("PGDATA");
        start.Environment.Remove("PGPORT");

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // "pg_ctl start" leaves the postmaster holding inherited pipe handles, so only the
        // timed wait is used: it returns when the tool exits, not when the pipes close.
        if (!process.WaitForExit(ToolTimeout))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            throw new TimeoutException($"{name} did not finish within {ToolTimeout.TotalSeconds:0} seconds");
        }
        if (waitForOutput)
        {
            process.WaitForExit();
        }
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return Ports.Production.Contains(port) ? FreeLoopbackPort() : port;
    }
    /// <summary>
    /// Regenerates the capacity include from config.json and makes sure postgresql.conf includes it.
    /// A cluster created before this existed has no include line, so it is added here rather than
    /// requiring the cluster to be recreated. PostgreSQL reads these at start-up, so a change only
    /// takes effect on a cluster this process starts; an already running one keeps its current values.
    /// </summary>
    private void WriteTuning()
    {
        if (_layout.Cluster is not { } cluster)
        {
            return;
        }
        try
        {
            var configPath = Path.Combine(_layout.DataDirectory, "postgresql.conf");
            if (File.Exists(configPath) && !File.ReadAllText(configPath).Contains(ClusterSettings.FileName, StringComparison.Ordinal))
            {
                File.AppendAllText(configPath, Environment.NewLine + "include_if_exists = '" + ClusterSettings.FileName + "'" + Environment.NewLine);
                _log.LogInformation("Added the Aurora 21 capacity include to an existing cluster configuration");
            }
            var tuningPath = Path.Combine(_layout.DataDirectory, ClusterSettings.FileName);
            var desired = cluster.ToConfigFile();
            if (!File.Exists(tuningPath) || File.ReadAllText(tuningPath) != desired)
            {
                File.WriteAllText(tuningPath, desired);
                _log.LogInformation("Wrote cluster capacity settings: max_connections={Connections}, shared_buffers={Buffers}MB, max_locks_per_transaction={Locks}",
                    cluster.MaxConnections, cluster.SharedBuffersMb, cluster.MaxLocksPerTransaction);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the cluster still starts on whatever it already has.
            _log.LogWarning("Could not write cluster capacity settings ({Error}); the cluster keeps its current values", error.Message);
        }
    }

}
