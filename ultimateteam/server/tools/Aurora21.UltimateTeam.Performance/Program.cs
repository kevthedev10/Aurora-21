using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Tests.Performance;

var duration = TimeSpan.Zero;
var samples = 50;
var peers = PerformanceScenario.DefaultPeers;
int? pool = null;
int? inventory = null;
int? autoPrepare = null;
double? targetRequestsPerPlayerSecond = null;
var stageTimings = false;
var sampleWaits = false;
var functionBudgets = false;
var failFast = false;
var projectionBenchmark = false;
var revision = "unrecorded-worktree";
string? clusterConfig = null;
string? output = null;
var ownsOutput = false;
try
{
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (++index >= args.Length) { throw new ArgumentException($"Missing value for {option}"); }
        switch (option)
        {
            case "--seconds": duration = TimeSpan.FromSeconds(int.Parse(args[index], CultureInfo.InvariantCulture)); break;
            case "--samples": samples = int.Parse(args[index], CultureInfo.InvariantCulture); break;
            case "--peers": peers = int.Parse(args[index], CultureInfo.InvariantCulture); break;
            case "--pool": pool = int.Parse(args[index], CultureInfo.InvariantCulture); break;
            case "--inventory": inventory = int.Parse(args[index], CultureInfo.InvariantCulture); break;
            case "--auto-prepare": autoPrepare = int.Parse(args[index], CultureInfo.InvariantCulture); break;
            case "--cluster-config": clusterConfig = Path.GetFullPath(args[index]); break;
            case "--target-rps-per-player": targetRequestsPerPlayerSecond = double.Parse(args[index], CultureInfo.InvariantCulture); break;
            case "--timings": stageTimings = bool.Parse(args[index]); break;
            case "--waits": sampleWaits = bool.Parse(args[index]); break;
            case "--functions": functionBudgets = bool.Parse(args[index]); break;
            case "--fail-fast": failFast = bool.Parse(args[index]); break;
            case "--projection-benchmark": projectionBenchmark = bool.Parse(args[index]); break;
            case "--revision": revision = args[index]; break;
            case "--output": output = Path.GetFullPath(args[index]); break;
            default: throw new ArgumentException($"Unknown option {option}");
        }
    }
    if (output is null) { throw new ArgumentException("--output is required; use a fresh ignored outputs/performance/ directory"); }
    if (Directory.Exists(output)) { throw new ArgumentException("Report directory already exists; preserve previous measurements"); }
    Directory.CreateDirectory(output);
    ownsOutput = true;
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    // Customized JsonValue nodes require the default metadata resolver.
    var jsonOptions = new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true };
    void SaveCheckpoint(JsonObject checkpoint)
    {
        var target = Path.Combine(output, "checkpoint.json");
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, checkpoint.ToJsonString(jsonOptions));
        File.Move(temporary, target, overwrite: true);
        if (checkpoint["phase"]?.GetValue<string>() == "soak-failed")
        {
            Console.Error.WriteLine("Failure context: " + checkpoint["failureDiagnostic"]?.ToJsonString());
            if (checkpoint["failureReason"] is { } reason) Console.Error.WriteLine("Invariant: " + reason.GetValue<string>());
            Console.Error.WriteLine("Partial results preserved: " + target);
        }
    }
    var report = projectionBenchmark
        ? await ProjectionBenchmarkScenario.RunAsync(peers, inventory ?? throw new ArgumentException(
            "--inventory is required for --projection-benchmark"), samples, revision, Console.WriteLine, stop.Token)
        : await PerformanceScenario.RunAsync(duration, samples, revision, stop.Token, Console.WriteLine,
            SaveCheckpoint, peers, pool, stageTimings, sampleWaits, functionBudgets, inventory, failFast,
            targetRequestsPerPlayerSecond, autoPrepare, clusterConfig);
    await File.WriteAllTextAsync(Path.Combine(output, "run.json"), report.ToJsonString(jsonOptions));
    Console.WriteLine($"Performance report: {output}");
    if (!report["passed"]!.GetValue<bool>()) Console.WriteLine("Latency gates failed: " + report["latencyFailures"]?.ToJsonString());
    return report["passed"]!.GetValue<bool>() ? 0 : 1;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    // No SQL payloads, tokens, connection strings or raw exception message in shareable evidence.
    Console.Error.WriteLine($"Performance run failed: {error.GetType().Name}");
    var frames = new JsonArray(new System.Diagnostics.StackTrace(error, true).GetFrames()
        .Take(12).Select(frame => (JsonNode?)JsonValue.Create(
            $"{frame.GetMethod()?.DeclaringType?.FullName}.{frame.GetMethod()?.Name}:{frame.GetFileLineNumber()}"))
        .ToArray());
    Console.Error.WriteLine(frames.ToJsonString());
    if (error is PerformanceCheckException)
    {
        Console.Error.WriteLine(error.Message);
    }
    if (ownsOutput && output is not null)
    {
        var failure = new JsonObject
        {
            ["passed"] = false,
            ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sourceRevision"] = revision,
            ["exceptionType"] = error.GetType().Name,
            ["stackFrames"] = frames
        };
        if (error is PerformanceCheckException)
        {
            // Harness-authored invariant text only.
            failure["reason"] = error.Message;
        }
        await File.WriteAllTextAsync(Path.Combine(output, "failure.json"), failure.ToJsonString());
    }
    return 1;
}
