using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Tests.Api;

namespace Aurora21.UltimateTeam.Tests.Performance;

/// <summary>Test-only, no-listener async measurements against one throwaway database. Never shipped.</summary>
public static class PerformanceScenario
{
    private const string G = "/ut/game/fifa21";
    public const int DefaultPeers = 10;

    /// <summary>
    /// The most identities this harness will seed. Each one is a distinct club whose inventory is
    /// seeded and held open, so the practical limit is host memory and seeding time rather than the
    /// address space; 127.0.x.y would allow far more.
    /// </summary>
    public const int MaximumPeers = 2000;

    /// <summary>
    /// A distinct loopback identity per peer. The first scheme was 127.0.0.{index + 2}, which ran out
    /// of last-octet values at 253 peers; spreading across the third octet keeps every address valid.
    /// 127.0.0.1 is skipped because the harness uses it as the server address.
    /// </summary>
    internal static string PeerAddress(int index)
    {
        var offset = index + 2;
        return string.Create(CultureInfo.InvariantCulture, $"127.0.{offset / 256}.{offset % 256}");
    }
    private const int Listings = 10_000;
    private sealed record ScenarioFailure(string Operation, Exception Error, double ElapsedMilliseconds, bool DeadlineExceeded);

    // Seed one real typed snapshot, then copy its rows in the owned disposable fixture.
    // Dynamic columns come only from these fixed installed tables; this is not runtime SQL.
    private static async ValueTask SeedRelationalMarketAsync(IUnitOfWork work, long resource, JsonObject preview)
    {
        await work.LockMarketAsync();
        var seed = await work.InsertMarketListingAsync("ai", "Synthetic performance fixture", null,
            resource, preview, preview.Int("rating"), 150, 1000, work.NowSeconds, work.NowSeconds + 86400);
        async ValueTask<string[]> Columns(string table) => (await work.QueryAsync("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema='ultimateteam' AND table_name=@table AND column_name NOT IN ('id','market_listing_id')
            ORDER BY ordinal_position
            """, r => r.GetString(0), ("table", table))).ToArray();
        var listingColumns = await Columns("market_listings");
        var names = string.Join(",", listingColumns);
        var trades = (await work.QueryAsync($"""
            INSERT INTO ultimateteam.market_listings({names})
            SELECT {string.Join(",", listingColumns.Select(c => "s." + c))}
            FROM ultimateteam.market_listings s CROSS JOIN generate_series(1,@count)
            WHERE s.market_listing_id=@seed RETURNING market_listing_id
            """, r => r.GetInt64(0), ("count", Listings - 1), ("seed", seed))).ToArray();
        foreach (var table in new[] { "market_item_snapshots", "market_player_snapshots" })
        {
            var columns = await Columns(table);
            await work.ExecuteAsync($"""
                INSERT INTO ultimateteam.{table}(market_listing_id,{string.Join(",", columns)})
                SELECT t.market_listing_id,{string.Join(",", columns.Select(c => "s." + c))}
                FROM unnest(@trades) AS t(market_listing_id) CROSS JOIN ultimateteam.{table} s WHERE s.market_listing_id=@seed
                """, ("trades", trades), ("seed", seed));
        }
        foreach (var table in new[] { "market_listings", "market_item_snapshots", "market_player_snapshots" })
            await work.ExecuteAsync($"ANALYZE ultimateteam.{table}");
    }

    public static async Task<JsonObject> RunAsync(TimeSpan duration, int samples, string revision,
        CancellationToken cancellationToken = default, Action<string>? progress = null, Action<JsonObject>? checkpoint = null,
        int peers = DefaultPeers, int? pool = null, bool stageTimings = false, bool sampleWaits = false, bool functionBudgets = false,
        int? inventory = null, bool failFast = false, double? targetRequestsPerPlayerSecond = null, int? autoPrepare = null,
        string? clusterConfig = null)
    {
        // Peers run in host/guest pairs, so the count must be even: an odd peer has no opponent.
        // The ceiling is the loopback address space, not an arbitrary limit. PeerAddress spreads
        // identities across 127.0.x.y, which leaves room well past any count this harness can seed.
        if (peers < 2 || peers > MaximumPeers || peers % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(peers), $"Use an even peer count from 2 to {MaximumPeers}");
        }
        // Each pair settles both players with a concurrent replay: four outstanding requests per pair.
        var maximumRequests = peers * 2;
        if (pool is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(pool.Value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(pool.Value, 500);
            // Headroom for the default connection_reserve of 5 plus the harness's own admin connections.
            PostgresTestCluster.MaxConnections = Math.Max(30, pool.Value + 10);
        }
        if (clusterConfig is not null)
        {
            // A deployment profile: the database.cluster_* keys of a config.json fragment, with unset keys
            // taken from config.example.json exactly as the managed cluster reads them.
            var profile = JsonNode.Parse(File.ReadAllText(clusterConfig))!.AsObject();
            var defaults = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "config.example.json")))!.AsObject();
            PostgresTestCluster.Cluster = ClusterSettings.From(profile["database"] as JsonObject,
                defaults["database"] as JsonObject ?? throw new InvalidDataException("config.example.json has no database section"));
        }
        if (inventory is not null)
        {
            // Club size is the dominant cost variable: hub and squad rendering scale with inventory,
            // so a run that only ever seeds the default would hide that slope. The ceiling matches
            // config.example.json's own bound for initial_club_player_count.
            ArgumentOutOfRangeException.ThrowIfLessThan(inventory.Value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(inventory.Value, 5000);
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(samples, 1000);
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromHours(1)) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        if (targetRequestsPerPlayerSecond is { } target && (!double.IsFinite(target) || target < 0.1 || target > 100))
            throw new ArgumentOutOfRangeException(nameof(targetRequestsPerPlayerSecond), "Use a finite target from 0.1 to 100 requests per player per second");
        // A completed pair round makes 28 measured API calls across two players.
        var pairRoundInterval = targetRequestsPerPlayerSecond is { } rate
            ? TimeSpan.FromSeconds(14 / rate) : (TimeSpan?)null;
        long commands = 0;
        var stages = new ConcurrentDictionary<string, LatencyHistogram>();
        var queries = new ConcurrentDictionary<string, LatencyHistogram>();
        var queryRows = new ConcurrentDictionary<string, long>();
        var endpoints = new ConcurrentDictionary<string, LatencyHistogram>();
        var measuring = new AsyncLocal<bool> { Value = true };
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (stageTimings && instrument.Meter.Name == DatabaseTimings.MeterName)
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        string Key(ReadOnlySpan<KeyValuePair<string, object?>> tags, bool query)
        {
            string endpoint = "background", name = "", outcome = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "endpoint") endpoint = tag.Value?.ToString() ?? "background";
                if (tag.Key == "query") name = tag.Value?.ToString() ?? "";
                if (tag.Key is "success" or "status") outcome = tag.Value?.ToString() ?? "";
            }
            return query ? endpoint + " | " + name + " | " + outcome : endpoint + " | " + outcome;
        }
        // Each table is attributed to a fixed route name, never an item/persona id or raw URL.
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (!measuring.Value) return;
            if (instrument.Name == "ultimateteam.database.query") queries.GetOrAdd(Key(tags, true), _ => new LatencyHistogram()).Add(value);
            else if (instrument.Name == "ultimateteam.api.endpoint") endpoints.GetOrAdd(Key(tags, false), _ => new LatencyHistogram()).Add(value);
            else if (instrument.Name == "ultimateteam.database.stage")
                foreach (var tag in tags) if (tag.Key == "stage" && tag.Value is string stage)
                    stages.GetOrAdd(stage, _ => new LatencyHistogram()).Add(value);
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!measuring.Value) return;
            if (instrument.Name == "ultimateteam.database.query.rows") queryRows.AddOrUpdate(Key(tags, true), value, (_, count) => count + value);
        });
        listener.Start();
        JsonObject StageReport() => new(stages.ToArray().OrderBy(pair => pair.Key)
            .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value.Report())));
        JsonObject QueryReport() => new(queries.ToArray().OrderBy(pair => pair.Key).Select(pair =>
        {
            var row = pair.Value.Report(); row["rows"] = queryRows.GetValueOrDefault(pair.Key);
            return KeyValuePair.Create<string, JsonNode?>(pair.Key, row);
        }));
        JsonObject EndpointReport() => new(endpoints.ToArray().OrderBy(pair => pair.Key).Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value.Report())));
        using var logs = new HealthLogs();
        using var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        // The synthetic pack is configuration, and migration 041 made the database authoritative for
        // that on the relational backend. Seeding it before the runtime loads keeps the measured pack
        // the one this scenario defines; writing pack_settings.json would be silently ignored and the
        // run would instead measure an unknown id falling back to a default configuration.
        string? seededConnection = null;
            seededConnection = PostgresTestCluster.CreateDatabase();
            await using var seedSource = NpgsqlDataSource.Create(seededConnection);
            await RelationalSchemaBootstrap.InstallFreshAsync(seedSource);
            await using var seedCommand = seedSource.CreateCommand(
                "INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,"
                + "silver_weight,bronze_weight,rare_slots,manager_chance,chem_style_chance) "
                // Store purchases require a positive coin price; one coin keeps this synthetic workload affordable.
                + "VALUES(90,'store',true,'Synthetic performance x12',1,12,100,0,0,0,0,0)");
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        using var runtime = TestRuntime.Create(loggers: loggers, marketTarget: 0, connectionString: seededConnection,
            prepareRoot: pool is null && inventory is null && autoPrepare is null ? null : root =>
            {
                // Throwaway preference root only; unset keys keep config.example.json defaults.
                Directory.CreateDirectory(Path.Combine(root, "user-config"));
                var preferences = new JsonObject();
                if (pool is not null)
                {
                    preferences["database"] = new JsonObject { ["max_pool_size"] = pool.Value };
                }
                if (autoPrepare is not null)
                {
                    var database = preferences["database"] as JsonObject ?? new JsonObject();
                    database["max_auto_prepare"] = autoPrepare.Value;
                    preferences["database"] = database;
                }
                if (inventory is not null)
                {
                    preferences["initial_club_player_count"] = inventory.Value;
                }
                File.WriteAllText(Path.Combine(root, "user-config", "config.json"), preferences.ToJsonString());
            },
            commandObserver: _ => Interlocked.Increment(ref commands));
        Require(runtime.Game.Packs.GetPackConfig(90).Int("size") == 12, "Synthetic performance pack 90 is not configured");
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = peers + 1 }, shared: true);
        var registry = new MatchmakingRegistry(null, logs, runtime.Game.Time);
        players.MatchRoster = new MatchmakingRoster(registry);
        var leases = new List<PlayerLease>();
        var pipes = new List<Pipe>();
        try
        {
            for (var index = 0; index < peers; index++)
            {
                leases.Add(await players.AcquireAsync(PlayerIdentity.FromPeerAddress(PeerAddress(index)), cancellationToken));
                if ((index + 1) % 20 == 0 || index + 1 == peers) progress?.Invoke($"Synthetic clubs ready: {index + 1}/{peers}");
            }
            var sessions = leases.Select(lease => lease.Session).ToArray();
            Require(sessions.Select(s => s.Player.PersonaId).Distinct().Count() == peers, "Personas not distinct");
            Require(sessions.Select(s => s.Game.ClubId).Distinct().Count() == peers, "Clubs not distinct");
            foreach (var session in sessions)
            {
                await session.Game.RunAsync("performance-fixture", async uow => { _ = await uow.AddCreditsToAsync(uow.ClubId, 100_000_000); }, cancellationToken);
            }
            var resource = runtime.Game.Players.Ids.First(id => runtime.Game.Players.Find(id)!.Rating is >= 75 and <= 80);
            var preview = runtime.Fut.Items.BuildPlayer(resource, runtime.Game.Rules.Market.AiListings.PreviewItemIdBase + 1, "market");
            await runtime.Game.RunAsync("performance-seed", async uow =>
            {
                await SeedRelationalMarketAsync(uow, resource, preview);
            }, cancellationToken);
            var ids = await runtime.Game.ReadAsync(async uow =>
                (await uow.ActiveMarketListingsAsync()).Take(50).Select(l => l.TradeId).ToArray(), cancellationToken);
            Require(ids.Length == 50, "Market fixture incomplete");
            foreach (var session in sessions)
            {
                await session.Game.RunAsync("performance-watch-seed", async uow =>
                {
                    foreach (var id in ids) { _ = await uow.WatchMarketListingAsync(id); }
                }, cancellationToken);
            }
            var report = new JsonObject
            {
                ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["sourceRevision"] = revision,
                ["failFast"] = failFast,
                ["storageBackend"] = "relational",
                ["framework"] = RuntimeInformation.FrameworkDescription,
                ["serverGarbageCollection"] = System.Runtime.GCSettings.IsServerGC,
                ["garbageCollectionLatencyMode"] = System.Runtime.GCSettings.LatencyMode.ToString(),
                ["operatingSystem"] = RuntimeInformation.OSDescription,
                ["logicalProcessors"] = Environment.ProcessorCount,
                ["availableMemoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                ["coreAssemblySha256"] = Hash(typeof(PlayerRegistry).Assembly.Location),
                ["serverAssemblySha256"] = Hash(typeof(BlazeResponses).Assembly.Location),
                ["harnessAssemblySha256"] = Hash(typeof(PerformanceScenario).Assembly.Location),
                ["postgresql"] = await runtime.Game.ReadAsync(uow => uow.ScalarAsync<string>("SELECT version()"), cancellationToken),
                ["databaseBytesBefore"] = await runtime.Game.ReadAsync(uow => uow.ScalarAsync<long>("SELECT pg_database_size(current_database())"), cancellationToken),
                ["peers"] = peers,
                ["openClubs"] = players.OpenCount,
                ["poolMaximum"] = runtime.Game.Database.Settings.MaxPoolSize,
                ["maxAutoPrepare"] = runtime.Game.Database.Settings.MaxAutoPrepare,
                ["clusterConfig"] = clusterConfig,
                // What the cluster actually runs with, whichever file or override set it.
                ["postgresSettings"] = JsonNode.Parse(await runtime.Game.ReadAsync(uow => uow.ScalarAsync<string>(
                    "SELECT json_object_agg(name, current_setting(name) ORDER BY name)::text FROM pg_settings WHERE name IN "
                    + "('max_connections', 'shared_buffers', 'work_mem', 'effective_cache_size', 'max_wal_size', 'max_locks_per_transaction')"),
                    cancellationToken)),
                // Club size is the dominant cost variable, so a report is not comparable without it.
                ["clubPlayerCount"] = runtime.Game.Config.InitialClubPlayerCount,
                ["listings"] = Listings,
                ["watchesPerPeer"] = ids.Length,
                ["packSize"] = 12,
                ["fixtureResourceId"] = resource,
                ["fixtureProvenance"] = "Bundled catalog; synthetic peer identities/market. Not native onboarding or a client capture; pack RNG is not seeded.",
                ["pacing"] = pairRoundInterval is { } interval
                    ? $"{peers / 2} bounded pair workers staggered across {interval.TotalSeconds:F3} seconds; 28 measured requests per pair round (14 per player on average), with each pair starting rounds at least {interval.TotalSeconds:F3} seconds apart."
                    : $"{peers / 2} bounded pair workers; two awaited player routines, serialized pair queueing then concurrent lifecycle and duplicate settlement, then 1 second delay.",
                ["targetRequestsPerPlayerSecond"] = targetRequestsPerPlayerSecond,
                ["transport"] = "In-process async FUT router and matchmaking lifecycle; no Kestrel/TLS/LSX/UDP/native client.",
                ["stageTimingsEnabled"] = stageTimings,
                ["functionBudgetsEnabled"] = functionBudgets,
                ["stageTimingBoundary"] = "Overlapping aggregate wall times; query includes network/mapping/JSON, not database CPU. Queries are attributed to fixed router handler names, with named critical reads and stable SQL-template hashes elsewhere. Rows means returned/affected rows (scalar: 0 or 1), not rows scanned.",
                ["allocationMethod"] = "Serial whole-process GC.GetTotalAllocatedBytes(true) deltas including request/response validation; excludes pack discard. No maintenance job in serial phase.",
            };
            var serial = new JsonObject();
            report["serial"] = serial;
            var squad = await CallAsync(sessions[0], "GET", $"{G}/squad/active", null, cancellationToken);
            foreach (var operation in new[] { "hub", "club-search", "search", "pack-open", "squad-save" })
            {
                report["phase"] = "serial-" + operation + "-running";
                checkpoint?.Invoke(report);
                progress?.Invoke("Serial starting: " + operation);
                stages.Clear(); queries.Clear(); queryRows.Clear(); endpoints.Clear();
                var histogram = new LatencyHistogram();
                long allocated = 0, sql = 0;
                for (var sample = -5; sample < samples; sample++)
                {
                    if (sample >= 0) { PerformanceTrace.Log.Start(operation); }
                    var beforeBytes = GC.GetTotalAllocatedBytes(true);
                    var beforeSql = Interlocked.Read(ref commands);
                    var timer = Stopwatch.StartNew();
                    var response = await OperationAsync(sessions[0], operation, squad, cancellationToken);
                    timer.Stop();
                    if (sample >= 0)
                    {
                        allocated += GC.GetTotalAllocatedBytes(true) - beforeBytes;
                        sql += Interlocked.Read(ref commands) - beforeSql;
                        histogram.Add(timer.Elapsed.TotalMilliseconds);
                    }
                    if (sample >= 0) { PerformanceTrace.Log.Stop(operation); }
                    if (operation == "search") { Require(response["auctionInfo"]!.AsArray().Count == 16, "Search page incomplete"); }
                    if (operation == "pack-open") { await DiscardAsync(sessions[0], response, cancellationToken); }
                }
                var result = histogram.Report();
                result["allocatedBytesPerSample"] = allocated / samples;
                result["sqlCommandsPerSample"] = (double)sql / samples;
                serial[operation] = result;
                if (stageTimings) { result["databaseStagesIncludingWarmupAndCleanup"] = StageReport(); result["queriesIncludingWarmupAndCleanup"] = QueryReport(); result["endpointsIncludingWarmupAndCleanup"] = EndpointReport(); }
                report["phase"] = "serial-" + operation + "-complete";
                checkpoint?.Invoke(report);
                progress?.Invoke($"Serial {operation}: p95={result["p95Ms"]} ms, bytes/sample={result["allocatedBytesPerSample"]}");
                if (failFast)
                {
                    Require(logs.Errors == 0, "Server errors observed during serial phase");
                    CheckLatency("serial", operation, result);
                }
            }
            report["serial"] = serial;
            if (functionBudgets)
            {
                report["functionBudgets"] = await FunctionBudgetScenario.RunAsync(sessions[0].Game, sessions[0].Fut,
                    samples, cancellationToken, progress, () => Interlocked.Read(ref commands));
            }
            report["phase"] = "serial-complete";
            checkpoint?.Invoke(report);
            var measurements = new ConcurrentDictionary<string, LatencyHistogram>();
            var memory = new JsonArray();
            report["memorySamples"] = memory;
            var clients = sessions.Select((session, index) =>
            {
                var pipe = new Pipe();
                pipes.Add(pipe);
                return new BlazeClient(session, new BlazeResponses(session.Fut), pipe.Writer, PeerAddress(index), "127.0.0.1");
            }).ToArray();
            var retainedBefore = GC.GetTotalMemory(true);
            stages.Clear(); queries.Clear(); queryRows.Clear(); endpoints.Clear();
            using var measuredProcess = Process.GetCurrentProcess();
            var cpuBefore = measuredProcess.TotalProcessorTime;
            var collectionsBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var pauseBefore = GC.GetTotalPauseDuration();
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timerSoak = Stopwatch.StartNew();
            PerformanceTrace.Log.Start("soak");
            long rounds = 0;
            long activeRequests = 0, peakRequests = 0;
            ScenarioFailure? firstFailure = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waitSampler = sampleWaits ? new DatabaseWaitSampler(runtime.ConnectionString) : null;
            var waitSampling = waitSampler?.RunAsync(stop.Token) ?? Task.CompletedTask;
            var job = duration == TimeSpan.Zero ? Task.CompletedTask : new MarketMaintenanceJob(runtime.Fut).RunAsync(stop.Token);
            async Task<JsonObject> Measured(PlayerSession session, string name, string method, string path, JsonNode? payload = null)
            {
                var active = Interlocked.Increment(ref activeRequests);
                long previous;
                do { previous = Interlocked.Read(ref peakRequests); }
                while (active > previous && Interlocked.CompareExchange(ref peakRequests, active, previous) != previous);
                using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                requestStop.CancelAfter(TimeSpan.FromSeconds(15));
                var timer = Stopwatch.StartNew();
                try
                {
                    var response = await CallAsync(session, method, path, payload, requestStop.Token);
                    if (failFast) Require(logs.Errors == 0, "Server errors observed during soak");
                    return response;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (!stop.IsCancellationRequested)
                        Interlocked.CompareExchange(ref firstFailure, new ScenarioFailure(name, error, timer.Elapsed.TotalMilliseconds, requestStop.IsCancellationRequested), null);
                    throw;
                }
                finally
                {
                    measurements.GetOrAdd(name, _ => new LatencyHistogram()).Add(timer.Elapsed.TotalMilliseconds);
                    Interlocked.Decrement(ref activeRequests);
                }
            }
            async Task PlayerRoutine(PlayerSession session)
            {
                _ = await Measured(session, "hub", "GET", $"{G}/hub");
                var clubPage = await Measured(session, "club-search", "GET", $"{G}/club?type=player&count=16&start=0");
                Require(clubPage["itemData"]!.AsArray().Count == 16, "Club search page incomplete");
                var search = await Measured(session, "search", "GET", $"{G}/transfermarket?num=16&start=0");
                Require(search["auctionInfo"]!.AsArray().Count == 16, "Search page incomplete");
                var watch = await Measured(session, "watchlist", "GET", $"{G}/watchlist");
                Require(watch["auctionInfo"]!.AsArray().Count == 50, "Watchlist incomplete");
                var status = await Measured(session, "status-25", "GET", $"{G}/trade/status/lite?tradeIds={string.Join(',', ids.Take(25))}");
                Require(status["auctionInfo"]!.AsArray().Count == 25, "Bulk status incomplete");
                var current = await Measured(session, "squad-get", "GET", $"{G}/squad/active");
                _ = await Measured(session, "squad-save", "PUT", $"{G}/squad/1", current);
                var pack = await Measured(session, "pack-open", "POST", $"{G}/purchased/items", PackPayload());
                Require(pack["itemData"]!.AsArray().Count == 12, "Pack incomplete");
                var discarded = await Measured(session, "pack-discard", "POST", "/ut/delete/game/fifa21/item", DiscardPayload(pack));
                Require(discarded["items"]!.AsArray().Count == 12, "Pack not discarded");
            }
            async Task Settle(BlazeClient client, long gameId)
            {
                var session = client.Session;
                var before = await session.Game.ReadAsync(uow => uow.CreditsAsync(), cancellationToken);
                // The pairing the settle follows wrote this player's match clock; back-date it as a played match, so
                // the reward gate (which trusts only the server clock) pays the win instead of refusing a 0 s match.
                await session.Game.RunAsync("perf-match-clock", uow => uow.ExecuteAsync(
                    "-- query: perf.match-clock.played\nUPDATE ultimateteam.match_clock SET created_at = @now - 600, kickoff_at = @now - 600 WHERE club_id = @club",
                    ("now", uow.NowSeconds)), cancellationToken);
                var payload = new JsonObject
                {
                    ["matchId"] = gameId,
                    ["endReason"] = "WIN",
                    // The claimed game clock is recorded, never gating: the server's match clock above decides.
                    ["secondsPlayed"] = 5461,
                    ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
                    ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
                };
                var responses = await Task.WhenAll(Measured(session, "match-end", "POST", $"{G}/match/end", payload),
                    Measured(session, "match-end-replay", "POST", $"{G}/match/end", payload));
                Require(responses[0].JsonEquals(responses[1]), "Duplicate match response changed");
                var after = await session.Game.ReadAsync(uow => uow.CreditsAsync(), cancellationToken);
                // The win award plus the coin rows for this payload (two goals), credited once; a pair that has used its
                // rewarded Rivals games for the day (the reward gate's pair cap) is credited nothing, also once.
                var win = session.Game.Rules.Seasons.MatchCompletionRewards["rivals"].Coins;
                var paid = responses[0]["matchCoins"]!.GetValue<long>();
                Require((paid == win || paid == 0) && after == before + paid, "Match credited more/less than once");
            }
            async Task Sale(PlayerSession seller, PlayerSession buyer)
            {
                // Explicit synthetic gift, not a recovered starter item or real club modification.
                var giftId = await seller.Game.RunAsync("performance-gift", async uow =>
                {
                    var id = await uow.NextItemIdAsync();
                    _ = await uow.SaveItemAsync(seller.Fut.Items.BuildPlayer(resource, id, "club"));
                    return id;
                }, stop.Token);
                var listed = await Measured(seller, "market-list", "POST", $"{G}/auctionhouse", new JsonObject
                {
                    ["itemData"] = new JsonObject { ["id"] = giftId },
                    ["startingBid"] = 150,
                    ["buyNowPrice"] = 1000,
                    ["duration"] = 3600,
                });
                var tradeId = listed.Int("tradeId");
                Require(tradeId > 0, "Listing rejected with a success status");
                var hover = await Measured(buyer, "market-hover", "GET", $"{G}/trade/status/lite?tradeIds={tradeId}");
                var row = hover["auctionInfo"]!.AsArray().Single()!.AsObject();
                Require(row.Int("sellerId") >= runtime.Game.Rules.Market.Listings.SellerPublicIdBase, "Seller public ID missing");
                Require(row.Int("expires") > 0 && row.Int("expires") <= 3600, "Selling time missing");
                var price = row.Int("buyNowPrice");
                var sellerBefore = await seller.Game.ReadAsync(uow => uow.CreditsAsync(), stop.Token);
                var buyerBefore = await buyer.Game.ReadAsync(uow => uow.CreditsAsync(), stop.Token);
                _ = await Measured(buyer, "market-buy", "POST", $"{G}/trade/{tradeId}/buy", new JsonObject { ["bid"] = price });
                var sellerAfter = await seller.Game.ReadAsync(uow => uow.CreditsAsync(), stop.Token);
                var buyerAfter = await buyer.Game.ReadAsync(uow => uow.CreditsAsync(), stop.Token);
                // The seller receives the price less the transfer tax (rules/market.json sellerTaxPercent); the tax is deleted.
                Require(sellerAfter == sellerBefore + runtime.Game.Rules.Market.SellerProceeds(price) && buyerAfter == buyerBefore - price, "Sale balances incorrect");
                var bought = await buyer.Game.ReadAsync(async uow => (await uow.ListItemsAsync("unassigned"))
                    .Single(item => item.Int("resourceId") == resource && item.Int("lastSalePrice") == price), stop.Token);
                var cleared = await Measured(seller, "market-remove", "DELETE", $"/ut/delete/game/fifa21/trade/{tradeId}");
                Require(cleared["auctionInfo"]!.AsArray().Count == 0, "Sold listing not cleared");
                _ = await Measured(buyer, "market-unwatch", "DELETE", $"/ut/delete/game/fifa21/watchlist?tradeId={tradeId}");
                var removed = await Measured(buyer, "market-discard", "POST", "/ut/delete/game/fifa21/item",
                    new JsonObject { ["itemId"] = bought.Int("id") });
                Require(removed["items"]!.AsArray().Count == 1, "Bought synthetic card not discarded");
            }
            var pairing = new object();
            async Task PairWorker(int pair)
            {
                var host = clients[pair * 2];
                var guest = clients[pair * 2 + 1];
                try
                {
                    if (duration > TimeSpan.Zero && pairRoundInterval is { } staggerInterval)
                        await Task.Delay(TimeSpan.FromTicks(staggerInterval.Ticks * pair / (peers / 2)), stop.Token);
                    do
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        var roundStarted = timerSoak.Elapsed;
                        await Task.WhenAll(PlayerRoutine(host.Session), PlayerRoutine(guest.Session));
                        await Sale(host.Session, guest.Session);
                        var gameId = PairAndAdvance(registry, host, guest, pairing);
                        await Task.WhenAll(Settle(host, gameId), Settle(guest, gameId));
                        Require(registry.EndGame(host.PlayerId, gameId), "Game not removed");
                        Require(registry.GameIdFor(host.PlayerId) is null && registry.GameIdFor(guest.PlayerId) is null, "Roster leaked");
                        Interlocked.Increment(ref rounds);
                        if (duration > TimeSpan.Zero)
                        {
                            if (pairRoundInterval is { } interval)
                            {
                                // Task.Delay follows a coarse system timer that can wake a few milliseconds before the
                                // Stopwatch says the interval has passed; wait out any remainder so rounds are never
                                // closer together than the pacing interval.
                                TimeSpan remaining;
                                while ((remaining = interval - (timerSoak.Elapsed - roundStarted)) > TimeSpan.Zero)
                                {
                                    await Task.Delay(remaining, stop.Token);
                                }
                            }
                            else
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                            }
                        }
                    } while (timerSoak.Elapsed < duration);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (!stop.IsCancellationRequested)
                        Interlocked.CompareExchange(ref firstFailure, new ScenarioFailure("pair-lifecycle", error, timerSoak.Elapsed.TotalMilliseconds, false), null);
                    await stop.CancelAsync();
                    throw;
                }
            }
            async Task SampleMemory()
            {
                try { await SampleMemoryLoop(); }
                catch (PerformanceCheckException error)
                {
                    // Record the initiating gate before cancellation makes workers fail.
                    Interlocked.CompareExchange(ref firstFailure, new ScenarioFailure("soak-checkpoint", error, timerSoak.Elapsed.TotalMilliseconds, false), null);
                    // Fail the run now, with the reason in the checkpoint, instead of after the full duration.
                    report["phase"] = "soak-failed";
                    report["failureReason"] = error.Message;
                    checkpoint?.Invoke(report);
                    await stop.CancelAsync();
                    throw;
                }
            }
            async Task SampleMemoryLoop()
            {
                do
                {
                    using var process = Process.GetCurrentProcess();
                    var database = await runtime.Game.ReadAsync(async uow => (
                        Sessions: await uow.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND backend_type = 'client backend'"),
                        Deadlocks: await uow.ScalarAsync<long>("SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()")), cancellationToken);
                    memory.Add(new JsonObject
                    {
                        ["elapsedSeconds"] = timerSoak.Elapsed.TotalSeconds,
                        ["managedBytes"] = GC.GetTotalMemory(false),
                        ["workingSetBytes"] = process.WorkingSet64,
                        ["privateBytes"] = process.PrivateMemorySize64,
                        ["databaseSessions"] = database.Sessions,
                        ["deadlocks"] = database.Deadlocks,
                        ["openClubs"] = players.OpenCount,
                        ["pairRounds"] = Interlocked.Read(ref rounds),
                        ["activeRequests"] = Interlocked.Read(ref activeRequests),
                    });
                    Require(database.Deadlocks == 0, $"Database deadlock observed ({database.Deadlocks})");
                    Require(database.Sessions <= runtime.Game.Database.Settings.MaxPoolSize,
                        $"Pool session bound exceeded ({database.Sessions} client sessions)");
                    progress?.Invoke($"Soak {timerSoak.Elapsed.TotalMinutes:F1} min: rounds={Interlocked.Read(ref rounds)}, managed={GC.GetTotalMemory(false) / 1_048_576} MiB, sessions={database.Sessions}");
                    report["phase"] = "soak-running";
                    report["partialMeasuredDurationSeconds"] = timerSoak.Elapsed.TotalSeconds;
                    report["partialPairRounds"] = Interlocked.Read(ref rounds);
                    report["partialSoak"] = new JsonObject(measurements.ToArray().OrderBy(entry => entry.Key)
                        .Select(entry => KeyValuePair.Create<string, JsonNode?>(entry.Key, entry.Value.Report())));
                    if (stageTimings) { report["partialDatabaseStages"] = StageReport(); report["partialQueries"] = QueryReport(); report["partialEndpoints"] = EndpointReport(); }
                    if (waitSampler is not null) report["postgresWaitSampling"] = waitSampler.Report();
                    checkpoint?.Invoke(report);
                    if (failFast)
                    {
                        Require(logs.Errors == 0, "Server errors observed during soak");
                        // Evaluate sustained observations after a minute, not the first cold request.
                        if (timerSoak.Elapsed >= TimeSpan.FromMinutes(1))
                            foreach (var (operation, measurement) in report["partialSoak"]!.AsObject())
                                if (measurement!["samples"]!.GetValue<long>() >= 50)
                                    CheckLatency("soak", operation, measurement.AsObject());
                    }
                    try { await Task.Delay(TimeSpan.FromMinutes(1), stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                } while (!stop.IsCancellationRequested);
            }
            var sampler = SampleMemory();
            Exception? concurrentError = null;
            try { await Task.WhenAll(Enumerable.Range(0, peers / 2).Select(PairWorker)); }
            catch (Exception error) when (error is not OutOfMemoryException) { concurrentError = error; }
            try
            {
                await stop.CancelAsync();
                await Task.WhenAll(job, sampler, waitSampling);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { concurrentError ??= error; }
            finally { registry.StopAccepting(); }
            if (concurrentError is not null)
            {
                var failure = firstFailure ?? new ScenarioFailure("worker-or-background", concurrentError, timerSoak.Elapsed.TotalMilliseconds, false);
                report["phase"] = "soak-failed";
                report["failureDiagnostic"] = new JsonObject
                {
                    ["operation"] = failure.Operation,
                    ["exceptionType"] = failure.Error.GetType().Name,
                    ["operationElapsedMs"] = failure.ElapsedMilliseconds,
                    ["requestDeadlineExceeded"] = failure.DeadlineExceeded,
                    ["requestDeadlineSeconds"] = 15,
                    ["sqlState"] = (failure.Error as PostgresException)?.SqlState,
                };
                report["partialMeasuredDurationSeconds"] = timerSoak.Elapsed.TotalSeconds;
                report["partialPairRounds"] = rounds;
                report["partialGcPauseSeconds"] = (GC.GetTotalPauseDuration() - pauseBefore).TotalSeconds;
                report["partialAllocatedBytes"] = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
                report["partialErrors"] = logs.Errors;
                report["partialProcessCpuSeconds"] = (measuredProcess.TotalProcessorTime - cpuBefore).TotalSeconds;
                if (stageTimings) { report["partialDatabaseStages"] = StageReport(); report["partialQueries"] = QueryReport(); report["partialEndpoints"] = EndpointReport(); }
                if (waitSampler is not null) { report["postgresWaitSampling"] = waitSampler.Report(); }
                report["partialSoak"] = new JsonObject(measurements.OrderBy(entry => entry.Key)
                    .Select(entry => KeyValuePair.Create<string, JsonNode?>(entry.Key, entry.Value.Report())));
                checkpoint?.Invoke(report);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw();
            }
            timerSoak.Stop();
            PerformanceTrace.Log.Stop("soak");
            var soak = new JsonObject();
            foreach (var (name, histogram) in measurements.OrderBy(entry => entry.Key)) { soak[name] = histogram.Report(); }
            report["soak"] = soak;
            if (waitSampler is not null) { report["postgresWaitSampling"] = waitSampler.Report(); }
            report["processCpuSecondsSoak"] = (measuredProcess.TotalProcessorTime - cpuBefore).TotalSeconds;
            report["processCpuBoundary"] = "In-process application AND producer/validation; excludes PostgreSQL and other host processes. CPU seconds / elapsed seconds gives average logical cores used.";
            report["gcCollectionsSoak"] = new JsonArray(Enumerable.Range(0, 3)
                .Select(generation => (JsonNode?)JsonValue.Create(GC.CollectionCount(generation) - collectionsBefore[generation])).ToArray());
            report["gcPauseSecondsSoak"] = (GC.GetTotalPauseDuration() - pauseBefore).TotalSeconds;
            report["allocatedBytesSoak"] = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            report["allocatedBytesSoakMethod"] = "Whole-process GC.GetTotalAllocatedBytes(true) delta over the soak, including background jobs. Unreliable under server GC (identical runs differed more than twofold; small runs went negative): do not compare runs with it. Measure allocation with collect-function-profile.ps1.";
            if (stageTimings) { report["databaseStagesSoak"] = StageReport(); report["queriesSoak"] = QueryReport(); report["endpointsSoak"] = EndpointReport();
                report["unnamedQueryTexts"] = new JsonObject(DatabaseTimings.UnnamedQueryTexts().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))); }
            report["requestedDurationSeconds"] = duration.TotalSeconds;
            report["measuredDurationSeconds"] = timerSoak.Elapsed.TotalSeconds;
            report["pairRounds"] = rounds;
            report["measuredRequests"] = measurements.Values.Sum(histogram => histogram.Report()["samples"]!.GetValue<long>());
            report["achievedRequestsPerPlayerSecond"] = report["measuredRequests"]!.GetValue<long>() / (peers * timerSoak.Elapsed.TotalSeconds);
            report["peakRequests"] = peakRequests;
            report["maximumProducerRequests"] = maximumRequests;
            report["retainedManagedBytesBefore"] = retainedBefore;
            report["retainedManagedBytesAfter"] = GC.GetTotalMemory(true);
            report["memoryMethod"] = "Unforced minute samples and full-GC quiescent endpoints; fixed-size histograms. Runtime/OS cache growth requires review, not automatic leak claims.";
            report["errors"] = logs.Errors;
            report["databaseBytesAfter"] = await runtime.Game.ReadAsync(uow => uow.ScalarAsync<long>("SELECT pg_database_size(current_database())"), cancellationToken);
            Require(logs.Errors == 0, "Server errors observed");
            Require(peakRequests <= maximumRequests, "Producer request bound exceeded");
            var latencyFailures = new JsonArray();
            // A zero-duration, five-round cold smoke is functional evidence, not a sustained p95 gate.
            foreach (var phase in duration == TimeSpan.Zero ? new[] { serial } : new[] { serial, soak })
            {
                foreach (var (name, measurement) in phase)
                {
                    if (measurement!["p95Ms"]!.GetValue<double>() >= (name == "search" ? 500 : 250))
                    {
                        latencyFailures.Add(name);
                    }
                }
            }
            report["latencyFailures"] = latencyFailures;
            report["phase"] = "complete";
            report["passed"] = latencyFailures.Count == 0;
            return report;
        }
        finally
        {
            foreach (var pipe in pipes) { await pipe.Writer.CompleteAsync(); await pipe.Reader.CompleteAsync(); }
            foreach (var lease in leases) { lease.Dispose(); }
            await players.StopAsync();
            Require(await players.WaitForDrainAsync(TimeSpan.FromSeconds(10)), "Test leases did not drain");
            await runtime.Game.StopAsync();
        }
    }

    internal static void CheckLatency(string phase, string operation, JsonObject measurement)
    {
        var limit = operation == "search" ? 500 : 250;
        Require(measurement["p95Ms"]!.GetValue<double>() < limit,
            $"Latency gate failed: {phase}/{operation} p95={measurement["p95Ms"]} ms (must be <{limit} ms)");
    }

    private static JsonObject PackPayload() => new() { ["packId"] = 90, ["useCredits"] = 1, ["currency"] = "COINS" };
    private static JsonObject DiscardPayload(JsonObject response) => new()
    {
        ["itemId"] = new JsonArray(response["itemData"]!.AsArray().OfType<JsonObject>()
            .Select(item => (JsonNode?)JsonValue.Create(item.Int("id"))).ToArray()),
    };
    private static async Task DiscardAsync(PlayerSession session, JsonObject response, CancellationToken cancellationToken)
    {
        Require(response["itemData"]!.AsArray().Count == 12, "Serial pack incomplete");
        var discarded = await CallAsync(session, "POST", "/ut/delete/game/fifa21/item", DiscardPayload(response), cancellationToken);
        Require(discarded["items"]!.AsArray().Count == 12, "Serial pack cleanup failed");
    }
    private static Task<JsonObject> OperationAsync(PlayerSession session, string operation, JsonObject squad, CancellationToken cancellationToken) => operation switch
    {
        "club-search" => CallAsync(session, "GET", $"{G}/club?type=player&count=16&start=0", null, cancellationToken),
        "hub" => CallAsync(session, "GET", $"{G}/hub", null, cancellationToken),
        "search" => CallAsync(session, "GET", $"{G}/transfermarket?num=16&start=0", null, cancellationToken),
        "pack-open" => CallAsync(session, "POST", $"{G}/purchased/items", PackPayload(), cancellationToken),
        "squad-save" => CallAsync(session, "PUT", $"{G}/squad/1", squad, cancellationToken),
        _ => throw new ArgumentException("Unknown performance operation", nameof(operation)),
    };
    internal static async Task<JsonObject> CallAsync(PlayerSession session, string method, string path, JsonNode? payload, CancellationToken cancellationToken)
    {
        var response = await session.Router.HandleAsync(new FutRequest(method, path,
            new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
            payload is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(payload.ToJsonString())), cancellationToken);
        Require(response.Status == 200, $"{method} {path} returned {response.Status}");
        Require(!response.Headers.Any(h => h.Key.Equals("X-UltimateTeam-Unknown", StringComparison.OrdinalIgnoreCase)), "Unknown-route fallback");
        var parsed = JsonNode.Parse(response.Body) as JsonObject ?? throw new InvalidOperationException("Response not a JSON object");
        Require(!parsed.ContainsKey("reason") && !parsed.ContainsKey("error"), $"{method} {path} rejected");
        return parsed;
    }
    private static long PairAndAdvance(MatchmakingRegistry registry, BlazeClient host, BlazeClient guest, object pairing)
    {
        long gameId;
        // The process-wide Rivals queue pairs whoever is waiting; queue each synthetic pair back to back
        // so concurrent pairs cannot match across each other. The lifecycle below still runs concurrently.
        lock (pairing)
        {
            registry.Start(host.PlayerId, "FUT_Rivals", Array.Empty<byte>(), host);
            registry.Start(guest.PlayerId, "FUT_Rivals", Array.Empty<byte>(), guest);
            gameId = registry.GameIdFor(host.PlayerId) ?? throw new InvalidOperationException("No paired game");
        }
        Require(registry.GameIdFor(guest.PlayerId) == gameId, "Opponents not paired");
        Require(registry.TakeReady(guest.PlayerId, guest) is not null, "Ready not claimed");
        Require(registry.AdvanceGameState(host.PlayerId, gameId, GameState.PreGame).Changed, "Pregame not queued");
        registry.AcknowledgeEndpointInitialization(host.PlayerId, gameId);
        registry.AcknowledgeEndpointInitialization(guest.PlayerId, gameId);
        registry.ClaimConnectionValidation(gameId, host.PlayerId);
        registry.ClaimConnectionValidation(gameId, guest.PlayerId);
        registry.StoreHostSessionUpdatePayload(host.PlayerId, gameId, new byte[] { 1 });
        Require(registry.ClaimSessionUpdateNotification(gameId).Claimed, "Session notification not claimed");
        registry.AcknowledgeGameStep(host.PlayerId, gameId, connected: false);
        registry.AcknowledgeMeshTarget(host.PlayerId, gameId, guest.PersonaId, 4);
        Require(registry.AcknowledgeMeshTarget(guest.PlayerId, gameId, host.PersonaId, 4).CompletedNow, "Mesh not complete");
        Require(registry.ClaimJoinNotifications(gameId).Claimed, "Join not claimed");
        foreach (var id in new[] { host.PlayerId, guest.PlayerId })
        {
            registry.MarkPlayerStateNotificationsDelivered(gameId, id);
            registry.MarkJoinNotificationsDelivered(gameId, id);
        }
        Require(registry.ClaimPregameIfReady(gameId).Claimed, "Pregame not released");
        Require(registry.AdvanceGameState(host.PlayerId, gameId, GameState.InGame).Changed, "Ingame rejected");
        Require(registry.AdvanceGameState(host.PlayerId, gameId, GameState.PostGame).Changed, "Postgame rejected");
        return gameId;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Require(bool condition, string reason) { if (!condition) { throw new PerformanceCheckException(reason); } }

    public sealed class LatencyHistogram
    {
        private readonly long[] _buckets = new long[4802];
        private readonly object _gate = new();
        private long _count;
        private double _maximum;
        private double _total;
        public void Add(double milliseconds)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
            if (!double.IsFinite(milliseconds)) { throw new ArgumentOutOfRangeException(nameof(milliseconds)); }
            lock (_gate)
            {
                var bucket = milliseconds <= 2000 ? (int)Math.Ceiling(milliseconds)
                    : milliseconds <= 20000 ? 2000 + (int)Math.Ceiling((milliseconds - 2000) / 10)
                    : milliseconds <= 120000 ? 3800 + (int)Math.Ceiling((milliseconds - 20000) / 100) : 4801;
                _buckets[bucket]++;
                _count++;
                _total += milliseconds;
                _maximum = Math.Max(_maximum, milliseconds);
            }
        }
        public JsonObject Report()
        {
            lock (_gate)
            {
                return new JsonObject
                {
                    ["samples"] = _count,
                    ["p50Ms"] = Percentile(0.50),
                    ["p95Ms"] = Percentile(0.95),
                    ["p99Ms"] = Percentile(0.99),
                    ["totalMs"] = _total,
                    ["meanMs"] = _count == 0 ? 0 : _total / _count,
                    ["maxMs"] = _maximum,
                    ["percentileMethod"] = "Nearest rank upper-bound buckets: 1ms through 2s, 10ms through 20s, 100ms through 120s; overflow reports observed maximum"
                };
            }
        }
        private double Percentile(double fraction)
        {
            if (_count == 0) { return 0; }
            var rank = (long)Math.Ceiling(_count * fraction);
            long total = 0;
            for (var index = 0; index < _buckets.Length; index++)
            {
                total += _buckets[index];
                if (total >= rank) { return index == 4801 ? _maximum : index <= 2000 ? index : index <= 3800 ? 2000 + (index - 2000) * 10 : 20000 + (index - 3800) * 100; }
            }
            throw new InvalidOperationException("Histogram mismatch");
        }
    }
    private sealed class HealthLogs : ILoggerProvider, ILogger
    {
        private long _errors;
        public long Errors => Interlocked.Read(ref _errors);
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error || exception is PostgresException { SqlState: "40P01" }) { Interlocked.Increment(ref _errors); }
        }
        public void Dispose() { }
    }
}

public sealed class PerformanceCheckException : InvalidOperationException
{
    public PerformanceCheckException(string message) : base(message) { }
}
