using Aurora21.UltimateTeam.Tests.Api;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Performance;

public sealed class PerformanceScenarioTests
{
    [Fact]
    public async Task RelationalBaselineCompletesFixtureChecks()
    {
        var report = await PerformanceScenario.RunAsync(TimeSpan.Zero, 1, "synthetic-backend-check",
            peers: 2, stageTimings: true);
        Assert.Equal("relational", report["storageBackend"]!.GetValue<string>());
        Assert.Equal(10_000, report["listings"]!.GetValue<int>());
        Assert.True(report["passed"]!.GetValue<bool>());
        var search = report["serial"]!["club-search"]!.AsObject();
        Assert.NotEmpty(search["endpointsIncludingWarmupAndCleanup"]!.AsObject());
        Assert.NotEmpty(search["queriesIncludingWarmupAndCleanup"]!.AsObject());
        Assert.NotNull(report["queriesSoak"]);
        Assert.Equal(2L, report["soak"]!["club-search"]!["samples"]!.GetValue<long>());
        Assert.Contains(search["queriesIncludingWarmupAndCleanup"]!.AsObject().Select(pair => pair.Key),
            key => key.Contains("GET ClubRoutes.SearchAsync | club.search.page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PacedPairReportsActualRequestRate()
    {
        var report = await PerformanceScenario.RunAsync(TimeSpan.FromSeconds(1), 1, "synthetic-paced-pair",
            peers: 2, targetRequestsPerPlayerSecond: 2);
        Assert.Equal(2, report["targetRequestsPerPlayerSecond"]!.GetValue<double>());
        Assert.Equal(1, report["pairRounds"]!.GetValue<long>());
        Assert.Equal(28, report["measuredRequests"]!.GetValue<long>());
        Assert.True(report["measuredDurationSeconds"]!.GetValue<double>() >= 7);
        Assert.True(report["achievedRequestsPerPlayerSecond"]!.GetValue<double>() <= 2);
    }

    [Fact]
    public async Task CompletedSerialCheckpointSurvivesLaterFailure()
    {
        System.Text.Json.Nodes.JsonObject? saved = null;
        var error = await Assert.ThrowsAsync<PerformanceCheckException>(() =>
            PerformanceScenario.RunAsync(TimeSpan.Zero, 1, "synthetic-checkpoint-failure",
                peers: 2, stageTimings: true, checkpoint: report =>
                {
                    saved = System.Text.Json.Nodes.JsonNode.Parse(report.ToJsonString(
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default)))!.AsObject();
                    if (report["phase"]!.GetValue<string>() == "serial-complete")
                        throw new PerformanceCheckException("Synthetic failure after serial checkpoint");
                }));
        Assert.Equal("Synthetic failure after serial checkpoint", error.Message);
        Assert.NotNull(saved);
        Assert.Equal("serial-complete", saved!["phase"]!.GetValue<string>());
        Assert.Equal(5, saved["serial"]!.AsObject().Count);
        Assert.NotEmpty(saved["serial"]!["club-search"]!["queriesIncludingWarmupAndCleanup"]!.AsObject());
        Assert.NotEmpty(saved["serial"]!["hub"]!["endpointsIncludingWarmupAndCleanup"]!.AsObject());
    }

    [Fact]
    public async Task DirectFunctionBudgetsPreserveClubDocumentsAndBalance()
    {
        long commands = 0;
        using var runtime = TestRuntime.Create(marketTarget: 0, commandObserver: _ => Interlocked.Increment(ref commands));
        async Task<string> State() => await runtime.Game.ReadAsync(async uow =>
            FutJson.Serialize(new System.Text.Json.Nodes.JsonObject
            {
                ["credits"] = await uow.CreditsAsync(),
                ["items"] = new System.Text.Json.Nodes.JsonArray((await uow.ListItemsAsync()).ToArray()),
                ["squads"] = new System.Text.Json.Nodes.JsonArray((await uow.ListSquadsAsync()).ToArray()),
            }), CancellationToken.None);
        var before = await State();
        var report = await FunctionBudgetScenario.RunAsync(runtime.Game, runtime.Fut, 1,
            CancellationToken.None, null, () => Interlocked.Read(ref commands));
        Assert.Equal(before, await State());
        var rows = report["budgets"]!.AsObject();
        Assert.Equal(14, rows.Count);
        foreach (var (_, row) in rows)
        {
            Assert.Equal(1, row!["samples"]!.GetValue<long>());
            Assert.True(row["allocatedBytesPerCall"]!.GetValue<long>() >= 0);
        }
        // The relational aggregate currently uses batched reads across its item tables; a family the club does not own is
        // not read. New clubs own no consumables since step 2 of live-content-editing-plan.md, so that read is skipped.
        Assert.Equal(6, rows["ClubSnapshot.LoadAsync"]!["sqlCommandsPerCall"]!.GetValue<double>());
        Assert.Equal(0, rows["ClubSnapshot.CountPile.cold"]!["sqlCommandsPerCall"]!.GetValue<double>());
    }

    [Fact]
    public async Task FailFastStopsAtFailedSerialCheckpointBeforeOtherOperations()
    {
        var phases = new List<string>();
        var error = await Assert.ThrowsAsync<PerformanceCheckException>(() => PerformanceScenario.RunAsync(
            TimeSpan.FromSeconds(1), 1, "synthetic-fail-fast", peers: 2, failFast: true, checkpoint: report =>
            {
                var phase = report["phase"]!.GetValue<string>();
                phases.Add(phase);
                if (phase == "serial-hub-complete") report["serial"]!["hub"]!["p95Ms"] = 250.0;
            }));
        Assert.Contains("serial/hub", error.Message);
        Assert.Contains("serial-hub-complete", phases);
        Assert.DoesNotContain(phases, phase => phase.Contains("search") || phase.StartsWith("soak"));
    }

    [Fact]
    public async Task FailedSoakCheckpointPreservesGateInsteadOfWorkerCancellation()
    {
        System.Text.Json.Nodes.JsonObject? saved = null;
        const string reason = "Synthetic soak latency gate failure";
        var error = await Assert.ThrowsAsync<PerformanceCheckException>(() => PerformanceScenario.RunAsync(
            TimeSpan.FromSeconds(1), 1, "synthetic-soak-failure", peers: 2, checkpoint: report =>
            {
                saved = System.Text.Json.Nodes.JsonNode.Parse(report.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default)))!.AsObject();
                if (report["phase"]!.GetValue<string>() == "soak-running")
                    throw new PerformanceCheckException(reason);
            }));
        Assert.Equal(reason, error.Message);
        Assert.Equal("soak-failed", saved!["phase"]!.GetValue<string>());
        Assert.Equal(reason, saved["failureReason"]!.GetValue<string>());
        Assert.Equal("soak-checkpoint", saved["failureDiagnostic"]!["operation"]!.GetValue<string>());
        Assert.Equal(nameof(PerformanceCheckException), saved["failureDiagnostic"]!["exceptionType"]!.GetValue<string>());
        Assert.False(saved["failureDiagnostic"]!["requestDeadlineExceeded"]!.GetValue<bool>());
    }

    [Fact]
    public void FailFastUsesExistingEndpointLatencyGatesAndNamesTheFailure()
    {
        System.Text.Json.Nodes.JsonObject Measurement(double ms) => new() { ["p95Ms"] = ms };
        PerformanceScenario.CheckLatency("serial", "hub", Measurement(249));
        PerformanceScenario.CheckLatency("soak", "search", Measurement(499));
        var error = Assert.Throws<PerformanceCheckException>(() => PerformanceScenario.CheckLatency("soak", "hub", Measurement(250)));
        Assert.Contains("soak/hub", error.Message);
        Assert.Contains("250", error.Message);
        Assert.Throws<PerformanceCheckException>(() => PerformanceScenario.CheckLatency("serial", "search", Measurement(500)));
    }

    [Fact]
    public void WaitSamplerRefusesANonTestDatabaseBeforeConnecting()
    {
        Assert.Throws<InvalidOperationException>(() => new DatabaseWaitSampler("Host=127.0.0.1;Database=real-club"));
    }

    [Fact]
    public void HistogramUsesNearestRankAndHandlesOverflow()
    {
        var histogram = new PerformanceScenario.LatencyHistogram();
        for (var sample = 1; sample <= 100; sample++) { histogram.Add(sample - 0.25); }
        var report = histogram.Report();
        Assert.Equal(100, report["samples"]!.GetValue<long>());
        Assert.Equal(50, report["p50Ms"]!.GetValue<double>());
        Assert.Equal(95, report["p95Ms"]!.GetValue<double>());
        Assert.Equal(99, report["p99Ms"]!.GetValue<double>());
        Assert.Equal(5025, report["totalMs"]!.GetValue<double>());
        Assert.Equal(50.25, report["meanMs"]!.GetValue<double>());
        var overflow = new PerformanceScenario.LatencyHistogram();
        overflow.Add(2345);
        Assert.Equal(2350, overflow.Report()["p95Ms"]!.GetValue<double>());
        for (var sample = 0; sample < 99; sample++) overflow.Add(4000);
        overflow.Add(15000);
        Assert.Equal(4000, overflow.Report()["p95Ms"]!.GetValue<double>());
        Assert.Equal(15000, overflow.Report()["maxMs"]!.GetValue<double>());
        var capped = new PerformanceScenario.LatencyHistogram(); capped.Add(150001);
        Assert.Equal(150001, capped.Report()["p95Ms"]!.GetValue<double>());
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Add(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Add(-1));
    }

    [Fact]
    public async Task MeasurementRejectsUnknownRouteFallback()
    {
        using var runtime = TestRuntime.Create(marketTarget: 0);
        using var players = new PlayerRegistry(runtime.Fut);
        await Assert.ThrowsAsync<PerformanceCheckException>(() => PerformanceScenario.CallAsync(players.Local,
            "GET", "/not-a-real-performance-route", null, CancellationToken.None));
    }
}
