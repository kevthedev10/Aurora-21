using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Opt-in stage timings. SQL timings include network waits and client materialization, not just database CPU.</summary>
public static class DatabaseTimings
{
    public const string MeterName = "Aurora21.UltimateTeam.Database";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("ultimateteam.database.stage", "ms");

    private static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>("ultimateteam.database.query", "ms");
    private static readonly Counter<long> QueryRows = Meter.CreateCounter<long>("ultimateteam.database.query.rows", "rows");
    private static readonly Histogram<double> EndpointDuration = Meter.CreateHistogram<double>("ultimateteam.api.endpoint", "ms");
    private static readonly AsyncLocal<string?> CurrentEndpoint = new();
    private static readonly ConcurrentDictionary<string, string> QueryNames = new(StringComparer.Ordinal);
    internal static bool Enabled => Duration.Enabled || QueryDuration.Enabled;

    public sealed class EndpointScope : IDisposable
    {
        private readonly string? _previous = CurrentEndpoint.Value;
        private readonly long _started = EndpointDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        private readonly string _method;
        public bool Enabled => EndpointDuration.Enabled || QueryDuration.Enabled;
        public EndpointScope(string method) { _method = method; if (Enabled) CurrentEndpoint.Value = method + " unknown"; }
        public void Route(string route) { if (Enabled) CurrentEndpoint.Value = _method + " " + route; }
        public int Status { get; set; } = 500;
        public void Dispose()
        {
            if (_started != 0) EndpointDuration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                new("endpoint", CurrentEndpoint.Value), new("status", Status));
            if (Enabled) CurrentEndpoint.Value = _previous;
        }
    }

    internal sealed class QueryMeasurement : IDisposable
    {
        private readonly long _started;
        private readonly string _endpoint = CurrentEndpoint.Value ?? "background";
        private readonly string _name;
        public long Rows { get; set; }
        public bool Succeeded { get; set; }
        public QueryMeasurement(string sql)
        {
            _started = QueryDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
            _name = _started == 0 ? "" : QueryNames.GetOrAdd(sql, text =>
            {
                var first = text.TrimStart().Split('\n')[0].Trim();
                if (first.StartsWith("-- query: ", StringComparison.Ordinal)) return first[10..].Trim();
                // Parameter values are never included. Unannotated commands remain individually identifiable.
                return "sql-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
            });
        }
        public void Dispose()
        {
            if (_started == 0) return;
            var tags = new TagList { { "endpoint", _endpoint }, { "query", _name }, { "success", Succeeded } };
            QueryDuration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, tags);
            QueryRows.Add(Rows, tags);
        }
    }
    internal static QueryMeasurement? MeasureQuery(string sql) => QueryDuration.Enabled ? new(sql) : null;

    /// <summary>Command text behind each hash-derived name seen so far, so reports can identify unannotated SQL.</summary>
    internal static IReadOnlyDictionary<string, string> UnnamedQueryTexts() =>
        QueryNames.Where(pair => pair.Value.StartsWith("sql-", StringComparison.Ordinal))
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

    internal static Measurement Measure(string stage) => new(stage);

    internal readonly struct Measurement : IDisposable
    {
        private readonly string _stage;
        private readonly long _start;

        public Measurement(string stage)
        {
            _stage = stage;
            _start = Duration.Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public void Dispose()
        {
            if (_start != 0)
            {
                Duration.Record(Stopwatch.GetElapsedTime(_start).TotalMilliseconds,
                    new KeyValuePair<string, object?>("stage", _stage));
            }
        }
    }
}
