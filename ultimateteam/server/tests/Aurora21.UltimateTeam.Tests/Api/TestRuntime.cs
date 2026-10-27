using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// An isolated runtime (temporary state and preference roots) over the real bundled catalogs,
/// driven through the FUT router without opening any listener.
/// </summary>
public sealed class TestRuntime : IDisposable
{
    private static readonly object EnvironmentGate = new();

    private bool _detached;
    private bool _disposed;

    private TestRuntime(string root, string connectionString, GameContext game, FutServices fut)
    {
        Root = root;
        ConnectionString = connectionString;
        Game = game;
        Fut = fut;
        Router = new FutRouter(fut);
    }

    public string Root { get; }

    /// <summary>This runtime's own PostgreSQL database.</summary>
    public string ConnectionString { get; }
    public GameContext Game { get; }
    public FutServices Fut { get; }
    public FutRouter Router { get; }

    public static string DataRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "ultimateteam", "runtime-source", "ultimateteam21");
                if (File.Exists(Path.Combine(candidate, "players.json")))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
            throw new InvalidOperationException("Could not locate ultimateteam/runtime-source/ultimateteam21");
        }
    }

    /// <param name="prepareRoot">Runs against the empty runtime root before start-up.</param>
    /// <param name="connectionString">Reuse an existing database instead of creating one (it is still dropped on dispose).</param>
    /// <param name="seedSql">Test-only SQL applied to a fresh relational schema before catalog loading.</param>
    public static TestRuntime Create(ILoggerFactory? loggers = null, TimeProvider? time = null, Action<string>? prepareRoot = null,
        string? connectionString = null, int? marketTarget = null, DatabaseCommandCounter? commandCounter = null,
        DatabaseFaultInjector? faultInjector = null, Action<string>? commandObserver = null, bool relationalSlice = false,
        bool initializeRelationalClub = false, string? seedSql = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ultimateteam21-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        prepareRoot?.Invoke(root);
        connectionString ??= PostgresTestCluster.CreateInstalledDatabase();
        if (seedSql is not null)
        {
            using var source = NpgsqlDataSource.Create(connectionString);
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
            using var command = source.CreateCommand(seedSql);
            command.ExecuteNonQuery();
        }
        lock (EnvironmentGate)
        {
            var priorMarketTarget = Environment.GetEnvironmentVariable(FeatureSwitches.MarketTargetVariable);
            try
            {
                Environment.SetEnvironmentVariable(RuntimePaths.RuntimeRootVariable, root);
                Environment.SetEnvironmentVariable(RuntimePaths.UserConfigRootVariable, Path.Combine(root, "user-config"));
                if (marketTarget is not null)
                {
                    Environment.SetEnvironmentVariable(FeatureSwitches.MarketTargetVariable, marketTarget.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                var paths = RuntimePaths.Resolve(DataRoot);
                Action<string>? observer = commandCounter is null && faultInjector is null && commandObserver is null
                    ? null
                    : sql =>
                    {
                        commandCounter?.Observe(sql);
                        faultInjector?.Observe(sql);
                        commandObserver?.Invoke(sql);
                    };
                var game = relationalSlice
                    ? GameContext.CreateRelationalSliceAsync(paths, loggers ?? NullLoggerFactory.Instance, time, connectionString, observer).GetAwaiter().GetResult()
                    : GameContext.CreateAsync(paths, loggers ?? NullLoggerFactory.Instance, testMode: true, time, connectionString, observer).GetAwaiter().GetResult();
                var services = !relationalSlice ? FutServices.CreateAsync(game).GetAwaiter().GetResult()
                    : initializeRelationalClub ? FutServices.CreateInitializedRelationalSliceAsync(game).GetAwaiter().GetResult()
                    : FutServices.CreateRelationalSlice(game);
                return new TestRuntime(root, connectionString, game, services);
            }
            finally
            {
                if (marketTarget is not null)
                {
                    Environment.SetEnvironmentVariable(FeatureSwitches.MarketTargetVariable, priorMarketTarget);
                }
            }
        }
    }

    /// <summary>Closes the club without dropping its database, so a test can restart against the same state.</summary>
    public string Detach()
    {
        _detached = true;
        Dispose();
        return ConnectionString;
    }

    public (int Status, JsonNode Body, FutResponse Response) CallRaw(string method, string path, JsonNode? payload = null)
    {
        var body = payload is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(payload.ToJsonString());
        var response = Router.Handle(new FutRequest(method, path, Array.Empty<KeyValuePair<string, string>>(), body));
        var text = Encoding.UTF8.GetString(response.Body);
        return (response.Status, FutJson.Parse(text) ?? JsonValue.Create("null")!, response);
    }

    public JsonNode Call(string method, string path, JsonNode? payload = null)
    {
        var (status, body, _) = CallRaw(method, path, payload);
        Assert.True(status == 200, $"{method} {path} -> {status}: {body.ToJsonString()}");
        return body;
    }

    public JsonObject CallObject(string method, string path, JsonNode? payload = null) => (JsonObject)Call(method, path, payload);

    /// <summary>
    /// Back-dates the live match clock so the next match end passes the real-time minimum, as a played match does: the
    /// reward gate trusts only the server's clock, because the match end's secondsPlayed is a client claim.
    /// </summary>
    public void PlayedRealSeconds(long seconds = 600) =>
        Game.InUnit(uow => uow.ExecuteAsync(
            "-- query: test.match-clock.played\nUPDATE ultimateteam.match_clock SET created_at = @now - @played, kickoff_at = @now - @played WHERE club_id = @club",
            ("now", uow.NowSeconds), ("played", seconds)));

    /// <summary>A clock frozen at one instant.</summary>
    public sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(long unixSeconds) => _now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>A clock the test moves (weekly competitions that close and settle).</summary>
    public sealed class MovableTime : TimeProvider
    {
        public MovableTime(long unixSeconds) => Now = unixSeconds;

        public long Now { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Game.Dispose();
        if (!_detached)
        {
            PostgresTestCluster.DropDatabase(ConnectionString);
        }
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
