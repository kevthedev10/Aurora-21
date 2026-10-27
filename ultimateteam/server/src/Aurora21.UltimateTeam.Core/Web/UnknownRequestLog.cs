using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Helpers;

namespace Aurora21.UltimateTeam.Core.Web;

/// <summary>
/// Requests no route handled, kept out of the main log for review after a live test. Every occurrence is one JSON
/// line in <see cref="FileName"/>; <see cref="SummaryFileName"/> lists each endpoint with its count, first and last
/// time and an example path. Numeric path segments are grouped as <c>{id}</c>. Query values are never written, only
/// their names, and body fields whose names look like credentials are replaced. The client still receives the
/// router's empty success response.
/// </summary>
public sealed partial class UnknownRequestLog : IDisposable
{
    public const string FileName = "unknown-requests.jsonl";
    public const string SummaryFileName = "unknown-requests-summary.json";
    public const string Redacted = "[redacted]";
    private const int BodyPreviewBytes = 2048;
    private const long MaxFileBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);

    private readonly BackgroundLineWriter _lines;
    private readonly string _summaryPath;
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly Timer _summaryTimer;
    private readonly object _summaryGate = new();
    private int _dirty;

    private sealed class Endpoint
    {
        public required string Method { get; init; }
        public required string Pattern { get; init; }
        public required string ExamplePath { get; init; }
        public required string FirstSeen { get; init; }
        public string LastSeen { get; set; } = string.Empty;
        public long Count { get; set; }
    }

    public UnknownRequestLog(string directory)
    {
        _lines = new BackgroundLineWriter(Path.Combine(directory, FileName), MaxFileBytes);
        _summaryPath = Path.Combine(directory, SummaryFileName);
        _summaryTimer = new Timer(_ => { if (Interlocked.Exchange(ref _dirty, 0) == 1) WriteSummary(); }, null, SummaryInterval, SummaryInterval);
    }

    public string FilePath => _lines.FilePath;

    public string SummaryPath => _summaryPath;

    /// <summary>Records one unhandled request; true the first time its method and path pattern are seen.</summary>
    public bool Record(FutRequest request)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var pattern = PathPattern(request.Path);
        var created = new Endpoint { Method = request.Method, Pattern = pattern, ExamplePath = request.Path, FirstSeen = now };
        var endpoint = _endpoints.GetOrAdd(request.Method + " " + pattern, created);
        lock (endpoint)
        {
            endpoint.Count++;
            endpoint.LastSeen = now;
        }
        Interlocked.Exchange(ref _dirty, 1);
        _lines.WriteLine(new JsonObject
        {
            ["time"] = now,
            ["method"] = request.Method,
            ["path"] = request.Path,
            ["endpoint"] = pattern,
            ["queryKeys"] = new JsonArray(request.Query.Keys.Distinct(StringComparer.Ordinal).Select(key => (JsonNode?)key).ToArray()),
            ["contentType"] = request.ContentType,
            ["bodyBytes"] = request.Body.Length,
            ["body"] = RedactedBody(request),
        }.ToJsonString());
        var isNew = ReferenceEquals(endpoint, created);
        if (isNew)
        {
            // Make a new endpoint visible in the summary soon, without writing files on the request thread.
            _ = Task.Run(WriteSummary);
        }
        return isNew;
    }

    /// <summary>The summary as written to <see cref="SummaryFileName"/>, most frequent endpoint first.</summary>
    public JsonObject Summary()
    {
        var rows = _endpoints.Values.Select(endpoint =>
        {
            lock (endpoint)
            {
                return (endpoint.Count, Node: new JsonObject
                {
                    ["method"] = endpoint.Method,
                    ["endpoint"] = endpoint.Pattern,
                    ["count"] = endpoint.Count,
                    ["firstSeen"] = endpoint.FirstSeen,
                    ["lastSeen"] = endpoint.LastSeen,
                    ["examplePath"] = endpoint.ExamplePath,
                });
            }
        }).OrderByDescending(row => row.Count).ThenBy(row => row.Node["endpoint"]!.GetValue<string>(), StringComparer.Ordinal);
        return new JsonObject
        {
            ["written"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["endpoints"] = new JsonArray(rows.Select(row => (JsonNode?)row.Node).ToArray()),
        };
    }

    public void Dispose()
    {
        _summaryTimer.Dispose();
        WriteSummary();
        _lines.Dispose();
    }

    private void WriteSummary()
    {
        try
        {
            lock (_summaryGate)
            {
                var temporary = _summaryPath + ".tmp";
                File.WriteAllText(temporary, Summary().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _summaryPath, overwrite: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostics only; the next interval or shutdown writes it again.
        }
    }

    /// <summary>The path with each all-digit segment replaced by <c>{id}</c>, so one endpoint groups its ids.</summary>
    internal static string PathPattern(string path) => NumericSegment().Replace(path, "{id}");

    /// <summary>A preview of the body with credential-like fields replaced; null for an empty body.</summary>
    internal static string? RedactedBody(FutRequest request)
    {
        if (request.Body.Length == 0)
        {
            return null;
        }
        var preview = request.BodyPreview(BodyPreviewBytes);
        if (request.Body.Length <= BodyPreviewBytes)
        {
            try
            {
                if (JsonNode.Parse(preview) is { } json)
                {
                    RedactJson(json);
                    return json.ToJsonString();
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
        // Not JSON, or cut short: redact name/value pairs in form and JSON-like text.
        preview = SensitiveJsonText().Replace(preview, match => match.Groups["prefix"].Value + "\"" + Redacted + "\"");
        return SensitiveFormText().Replace(preview, match => match.Groups["prefix"].Value + Redacted);
    }

    private static void RedactJson(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    if (SensitiveName().IsMatch(key))
                    {
                        obj[key] = Redacted;
                    }
                    else if (obj[key] is { } child)
                    {
                        RedactJson(child);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        RedactJson(child);
                    }
                }
                break;
        }
    }

    [GeneratedRegex(@"(?<=/)\d+(?=/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericSegment();

    [GeneratedRegex(@"token|auth|sid|pass|secret|session|cookie|key|credential", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"(?<prefix>""[^""]*(?:token|auth|sid|pass|secret|session|cookie|key|credential)[^""]*""\s*:\s*)""[^""]*""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveJsonText();

    [GeneratedRegex(@"(?<prefix>(?:^|[&?\s])[^=&\s]*(?:token|auth|sid|pass|secret|session|cookie|key|credential)[^=&\s]*=)[^&\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveFormText();
}
