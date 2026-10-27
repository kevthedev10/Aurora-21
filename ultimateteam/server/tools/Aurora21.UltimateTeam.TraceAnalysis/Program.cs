using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length != 2) { Console.Error.WriteLine("Usage: TraceAnalysis <synthetic.nettrace> <fresh-output.json>"); return 2; }
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (File.Exists(output)) { throw new InvalidOperationException("Preserve existing analysis reports"); }
var converted = TraceLog.CreateFromEventPipeDataFile(input);
using var log = new TraceLog(converted);
var markerProcesses = log.Events.Where(e => e.ProviderName == "Aurora21-Performance" && (int)e.ID == 1)
    .Select(e => e.ProcessID).Distinct().ToArray();
if (markerProcesses.Length != 1) { throw new InvalidOperationException("One harness marker process required"); }
var processId = markerProcesses[0];
var phases = new Dictionary<string, Phase>(StringComparer.Ordinal);
string? current = null;
double started = 0;
long starts = 0, stops = 0;
// A collection's generation is only on its start event, so its start time is held until the
// matching stop. Suspension is tracked separately: the stop-the-world window is what stalls
// requests, and it is wider than the collection itself.
var gcStartedAt = new Dictionary<long, (double At, int Generation)>();
double suspendedAt = -1;
foreach (var data in log.Events)
{
    if (data.ProcessID != processId) { continue; }
    if (data.ProviderName == "Aurora21-Performance")
    {
        if ((int)data.ID == 1)
        {
            if (current is not null) { throw new InvalidOperationException("Overlapping phase markers"); }
            current = (string)data.PayloadByName("phase");
            // Keep in step with the serial phases in PerformanceScenario; club-search is the 16-card My Club page.
            if (current is not ("hub" or "club-search" or "search" or "pack-open" or "squad-save" or "soak"))
            { throw new InvalidOperationException("Unknown phase marker"); }
            started = data.TimeStampRelativeMSec;
            starts++;
        }
        else if ((int)data.ID == 2)
        {
            if (current is null || (string)data.PayloadByName("phase") != current)
            { throw new InvalidOperationException("Unpaired phase markers"); }
            var phase = GetPhase(current);
            phase.Calls++;
            phase.DurationMs += data.TimeStampRelativeMSec - started;
            current = null;
            stops++;
        }
        continue;
    }
    var bucket = GetPhase(current ?? "outside-measured-phases");
    if (data is GCAllocationTickTraceData allocation)
    {
        // Allocation ticks are sampled, and the tick interval can straddle a phase boundary.
        var bytes = (double)allocation.AllocationAmount64;
        bucket.AllocationTicks++;
        bucket.SampledAllocationBytes += bytes;
        Add(bucket.Types, allocation.TypeName ?? "unknown", bytes);
        var frames = Frames(data);
        if (frames.Length == 0) { bucket.AllocationTicksWithoutStack++; }
        Add(bucket.AllocationStacks, string.Join(" <- ", frames.Take(16)), bytes);
        foreach (var frame in frames.Distinct()) { Add(bucket.AllocationInclusive, frame, bytes); }
        Add(bucket.NearestCoreAllocation, frames.FirstOrDefault(f => f.StartsWith("Aurora21.UltimateTeam.Core.", StringComparison.Ordinal))
            ?? "no-core-frame-on-physical-stack", bytes);
    }
    else if (data.ProviderName == "Microsoft-DotNETCore-SampleProfiler")
    {
        bucket.ThreadSamples++;
        foreach (var frame in Frames(data).Distinct()) { Add(bucket.ThreadInclusive, frame, 1); }
    }
    else if (data is GCStartTraceData gcStart)
    {
        bucket.Collections++;
        Add(bucket.CollectionsByGeneration, "gen" + gcStart.Depth, 1);
        Add(bucket.CollectionsByReason, gcStart.Reason.ToString(), 1);
        gcStartedAt[gcStart.Count] = (data.TimeStampRelativeMSec, gcStart.Depth);
    }
    else if (data is GCEndTraceData gcEnd)
    {
        if (gcStartedAt.Remove(gcEnd.Count, out var begun))
        {
            var elapsed = data.TimeStampRelativeMSec - begun.At;
            bucket.CollectionMs += elapsed;
            Add(bucket.CollectionMsByGeneration, "gen" + begun.Generation, elapsed);
        }
    }
    else if (data is GCHeapStatsTraceData heap)
    {
        bucket.HeapStatsSamples++;
        bucket.PromotedBytes += heap.TotalPromotedSize0 + heap.TotalPromotedSize1 + heap.TotalPromotedSize2;
        bucket.Gen0Bytes += heap.GenerationSize0;
        bucket.Gen1Bytes += heap.GenerationSize1;
        bucket.Gen2Bytes += heap.GenerationSize2;
        bucket.LargeObjectBytes += heap.GenerationSize3;
    }
    else if (data.EventName == "GC/SuspendEEStart")
    {
        suspendedAt = data.TimeStampRelativeMSec;
    }
    else if (data.EventName == "GC/RestartEEStop" && suspendedAt >= 0)
    {
        // Threads cannot run between these two events; this is the stall a request actually sees.
        var pause = data.TimeStampRelativeMSec - suspendedAt;
        suspendedAt = -1;
        bucket.Pauses++;
        bucket.PauseMs += pause;
        if (pause > bucket.LongestPauseMs) { bucket.LongestPauseMs = pause; }
        Add(bucket.PauseBuckets, pause switch
        {
            < 1 => "under-1ms",
            < 5 => "1-5ms",
            < 10 => "5-10ms",
            < 25 => "10-25ms",
            < 50 => "25-50ms",
            < 100 => "50-100ms",
            _ => "over-100ms",
        }, 1);
    }
}
if (current is not null || starts != stops || stops == 0) { throw new InvalidOperationException("Incomplete phase trace"); }
using var file = File.OpenRead(input);
var result = new
{
    recordedAtUtc = DateTimeOffset.UtcNow,
    traceSha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(),
    traceEventVersion = typeof(TraceLog).Assembly.GetName().Version?.ToString(),
    eventsLost = log.EventsLost,
    starts,
    stops,
    boundary = "Measured serial call intervals exclude warmup and pack cleanup; soak includes producer, validation, and maintenance. All allocations in this process during each interval count. Physical stacks do not reconstruct async callers. AllocationAmount64 ticks estimate bytes; boundary spill/sampling can differ from exact GC totals. Inclusive rows overlap. Thread samples are wall-time samples, not CPU utilization. No SQL, identities or raw payloads exported.",
    phases = phases.ToDictionary(p => p.Key, p => new
    {
        p.Value.Calls,
        p.Value.DurationMs,
        p.Value.AllocationTicks,
        p.Value.SampledAllocationBytes,
        p.Value.AllocationTicksWithoutStack,
        p.Value.ThreadSamples,
        gc = new
        {
            collections = p.Value.Collections,
            collectionMs = p.Value.CollectionMs,
            collectionsByGeneration = p.Value.CollectionsByGeneration,
            collectionsByReason = p.Value.CollectionsByReason,
            collectionMsByGeneration = p.Value.CollectionMsByGeneration,
            // The suspension window, not the collection: this is what stalls in-flight requests.
            pauses = p.Value.Pauses,
            pauseMs = p.Value.PauseMs,
            longestPauseMs = p.Value.LongestPauseMs,
            meanPauseMs = p.Value.Pauses == 0 ? 0 : p.Value.PauseMs / p.Value.Pauses,
            pausesPerSecond = p.Value.DurationMs <= 0 ? 0 : p.Value.Pauses / (p.Value.DurationMs / 1000),
            pauseSharePercent = p.Value.DurationMs <= 0 ? 0 : p.Value.PauseMs / p.Value.DurationMs * 100,
            pauseBuckets = p.Value.PauseBuckets,
            heapStatsSamples = p.Value.HeapStatsSamples,
            promotedBytes = p.Value.PromotedBytes,
            gen0Bytes = p.Value.Gen0Bytes,
            gen1Bytes = p.Value.Gen1Bytes,
            gen2Bytes = p.Value.Gen2Bytes,
            largeObjectBytes = p.Value.LargeObjectBytes,
        },
        types = Top(p.Value.Types),
        nearestCoreAllocation = Top(p.Value.NearestCoreAllocation),
        allocationInclusive = Top(p.Value.AllocationInclusive),
        coreAllocationInclusive = Top(p.Value.AllocationInclusive
            .Where(row => row.Key.StartsWith("Aurora21.UltimateTeam.Core.", StringComparison.Ordinal))
            .ToDictionary(row => row.Key, row => row.Value)),
        allocationStacks = Top(p.Value.AllocationStacks),
        threadInclusive = Top(p.Value.ThreadInclusive)
    })
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result,
    new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }));
Console.WriteLine($"Analysis written; phases={phases.Count}, markers={stops}, lost={log.EventsLost}");
return log.EventsLost == 0 ? 0 : 1;

Phase GetPhase(string name)
{
    if (!phases.TryGetValue(name, out var phase)) { phases[name] = phase = new Phase(); }
    return phase;
}
static void Add(Dictionary<string, double> rows, string key, double value) => rows[key] = rows.GetValueOrDefault(key) + value;
static object[] Top(Dictionary<string, double> rows) => rows.OrderByDescending(p => p.Value).ThenBy(p => p.Key)
    .Take(30).Select(p => (object)new { name = p.Key, weight = p.Value }).ToArray();
static string[] Frames(TraceEvent data)
{
    var frames = new List<string>();
    for (var frame = data.CallStack(); frame is not null && frames.Count < 256; frame = frame.Caller)
    {
        frames.Add(frame.CodeAddress.Method?.FullMethodName ?? "unresolved-managed-or-native-frame");
    }
    return frames.ToArray();
}
sealed class Phase
{
    public long Calls, AllocationTicks, AllocationTicksWithoutStack, ThreadSamples;
    public double DurationMs, SampledAllocationBytes;
    public long Collections, Pauses, HeapStatsSamples;
    public double CollectionMs, PauseMs, LongestPauseMs;
    public double PromotedBytes, Gen0Bytes, Gen1Bytes, Gen2Bytes, LargeObjectBytes;
    public Dictionary<string, double> CollectionsByGeneration { get; } = new();
    public Dictionary<string, double> CollectionsByReason { get; } = new();
    public Dictionary<string, double> CollectionMsByGeneration { get; } = new();
    public Dictionary<string, double> PauseBuckets { get; } = new();
    public Dictionary<string, double> Types { get; } = new();
    public Dictionary<string, double> NearestCoreAllocation { get; } = new();
    public Dictionary<string, double> AllocationInclusive { get; } = new();
    public Dictionary<string, double> AllocationStacks { get; } = new();
    public Dictionary<string, double> ThreadInclusive { get; } = new();
}
