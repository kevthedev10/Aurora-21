using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class StartupPreflightTests
{
    [Fact]
    public void PanelAssetsPreferTheBundledFolderWhenPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aurora21-panel-assets-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, "state");
        var bundled = Path.Combine(root, "panel-assets");
        var paths = new RuntimePaths(Path.Combine(root, "runtime-source", "ultimateteam21"), state,
            Path.Combine(root, "user-config"), true);
        try
        {
            Assert.Equal(Path.Combine(state, "panel-assets"), paths.PanelAssetsDirectory);
            Directory.CreateDirectory(bundled);
            Assert.Equal(bundled, paths.PanelAssetsDirectory);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectionPrecedesSchemaAndPreservesPersonalSettingsAndExternalDatabase()
    {
        var connectionString = PostgresTestCluster.CreateDatabase();
        string? root = null;
        const string personal = """{"database":{"connection_reserve":10000},"custom":"keep","sid":"synthetic-secret"}""";
        var counter = new DatabaseCommandCounter();
        try
        {
            var error = Assert.Throws<RuntimeSetupException>(() => TestRuntime.Create(connectionString: connectionString, commandCounter: counter,
                prepareRoot: value => { root = value; WriteConfig(value, personal); }));
            Assert.Contains("connection_reserve", error.Message);
            Assert.DoesNotContain("synthetic-secret", error.Message);
            Assert.Equal(personal, File.ReadAllText(Path.Combine(root!, "user-config", "config.json")));
            Assert.Equal(0, counter.Count);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT to_regclass('public.clubs') IS NULL", connection);
            Assert.Equal(true, await command.ExecuteScalarAsync());
        }
        finally
        {
            PostgresTestCluster.DropDatabase(connectionString);
            if (root is not null) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectedRestartDoesNotChangeAnExistingClub()
    {
        using var rt = TestRuntime.Create();
        var before = ClubStateSnapshot.Capture(rt.ConnectionString, rt.Game.ClubId);
        string? root = null;
        try
        {
            Assert.Throws<RuntimeSetupException>(() => TestRuntime.Create(connectionString: rt.ConnectionString,
                prepareRoot: value => { root = value; WriteConfig(value, """{"database":{"connection_reserve":10000}}"""); }));
            Assert.Equal(before, ClubStateSnapshot.Capture(rt.ConnectionString, rt.Game.ClubId));
        }
        finally
        {
            if (root is not null) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LimitsLogOncePerOwningStartupAndNeverForAnotherPlayer()
    {
        using var recording = new RecordingProvider();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(recording));
        using var rt = TestRuntime.Create(loggers: factory);
        Assert.Single(recording.Messages, message => message.StartsWith("DATABASE_LIMITS", StringComparison.Ordinal));
        using var player = await rt.Game.ForPlayerAsync(PlayerIdentity.Of("910001"));
        Assert.Single(recording.Messages, message => message.StartsWith("DATABASE_LIMITS", StringComparison.Ordinal));
        Assert.DoesNotContain(recording.Messages, message => message.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProgrammaticServeRejectsInvalidPlayerOptionsBeforeResolvingPaths()
    {
        await Assert.ThrowsAsync<RuntimeSetupException>(() => ServeCommand.RunAsync(
            new ServeOptions("127.0.0.1", LsxMode.Off, 19000, false) { Players = new PlayerRegistryOptions { MaxOpenClubs = 0 } }, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedManagedStartupStopsOnlyTheClusterItStarted(bool alreadyRunning)
    {
        var root = Path.Combine(Path.GetTempPath(), "aurora21-preflight-" + Guid.NewGuid().ToString("N"));
        var paths = new RuntimePaths(TestRuntime.DataRoot, root, Path.Combine(root, "user-config"), true);
        var cluster = new LocalPostgres(LocalPostgresLayout.From(paths), NullLogger.Instance);
        try
        {
            paths.EnsureDirectories();
            WriteConfig(root, """{"database":{"connection_reserve":10000}}""");
            if (alreadyRunning) _ = cluster.Start();
            var error = await Assert.ThrowsAsync<RuntimeSetupException>(() => GameContext.CreateAsync(paths, NullLoggerFactory.Instance, testMode: true));
            Assert.Contains("connection_reserve", error.Message);
            Assert.Equal(alreadyRunning, cluster.Stop());
        }
        finally
        {
            cluster.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteConfig(string root, string json)
    {
        Directory.CreateDirectory(Path.Combine(root, "user-config"));
        File.WriteAllText(Path.Combine(root, "user-config", "config.json"), json);
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
