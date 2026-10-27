using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;

namespace Aurora21.UltimateTeam.Core.Web;

/// <summary>An HTTP request as seen by the FUT router (transport independent).</summary>
public sealed class FutRequest
{
    private JsonNode? _payload;
    private QueryValues? _query;

    public FutRequest(string method, string rawTarget, IEnumerable<KeyValuePair<string, string>> headers, byte[] body)
    {
        Method = method.ToUpperInvariant();
        RawTarget = rawTarget;
        Body = body;
        var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            headerMap[key] = value;
        }
        Headers = headerMap;

        // ProtoHttp sends POW/local-auth paths with a double leading slash; urlsplit would
        // otherwise read the first segment as a network location.
        var target = rawTarget;
        if (target.StartsWith("//pow/", StringComparison.Ordinal) || target.StartsWith("//connect/", StringComparison.Ordinal)
            || target.StartsWith("//ultimateteam/", StringComparison.Ordinal))
        {
            target = "/" + target.TrimStart('/');
        }
        var (path, query) = SplitTarget(target);
        Path = "/" + path.TrimStart('/');
        var lower = Path.ToLowerInvariant().TrimEnd('/');
        Lower = lower.Length == 0 ? "/" : lower;
        RawQuery = query;
    }

    public string Method { get; }

    public string RawTarget { get; }

    public string Path { get; }

    /// <summary>Lower-cased path without a trailing slash (the router's matching key).</summary>
    public string Lower { get; }

    public string RawQuery { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public byte[] Body { get; }

    public QueryValues Query => _query ??= QueryString.Parse(RawQuery);

    /// <summary>The body as JSON (an empty object when absent or invalid).</summary>
    public JsonNode Payload => _payload ??= FutJson.SafeParse(Body);

    public JsonObject? PayloadObject => Payload as JsonObject;

    public string ContentType => Headers.TryGetValue("Content-Type", out var value) ? value : string.Empty;

    public bool IsMethod(params string[] methods) => methods.Contains(Method);

    public string BodyPreview(int limit) => Encoding.UTF8.GetString(Body.AsSpan(0, Math.Min(limit, Body.Length)));

    /// <summary>
    /// <c>urllib.parse.urlsplit</c> for origin-form targets: a <c>//authority</c> prefix is
    /// dropped, the fragment removed and the query separated.
    /// </summary>
    private static (string Path, string Query) SplitTarget(string target)
    {
        var withoutFragment = target.Split('#', 2)[0];
        if (withoutFragment.StartsWith("//", StringComparison.Ordinal))
        {
            var rest = withoutFragment[2..];
            var end = rest.IndexOfAny(new[] { '/', '?' });
            withoutFragment = end < 0 ? string.Empty : rest[end..];
        }
        var parts = withoutFragment.Split('?', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }
}

/// <summary>The router's response: status, extra headers and a body (JSON unless a content type is set).</summary>
public sealed class FutResponse
{
    public const string JsonContentType = "application/json; charset=utf-8";

    public FutResponse(int status, byte[] body, IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        Status = status;
        Body = body;
        Headers = headers?.ToList() ?? new List<KeyValuePair<string, string>>();
    }

    public int Status { get; }

    public byte[] Body { get; }

    public List<KeyValuePair<string, string>> Headers { get; }

    public string ContentType =>
        Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? JsonContentType;

    public IEnumerable<KeyValuePair<string, string>> ExtraHeaders =>
        Headers.Where(h => !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

    public static FutResponse Json(JsonNode? body, int status = 200, params (string Name, string Value)[] headers) =>
        new(status, FutJson.SerializeToUtf8(body), headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)));

    public static FutResponse Json(FutResult result) => Json(result.Body, result.Status);

    /// <summary>A JSON response marked <c>Cache-Control: no-store</c>.</summary>
    public static FutResponse NoStore(JsonNode? body, int status = 200) => Json(body, status, ("Cache-Control", "no-store"));

    public static FutResponse NoStore(FutResult result) => NoStore(result.Body, result.Status);

    /// <summary>Game actions: a success is not cached, an error is a plain JSON reason.</summary>
    public static FutResponse Action(FutResult result) => result.Status == 200 ? NoStore(result.Body) : Json(result);

    /// <summary>A response rendered from an embedded template with no placeholders.</summary>
    public static FutResponse Template(string name) => Template(name, null);

    /// <summary>
    /// A response written straight from an embedded template, skipping the node tree. Only for a body
    /// that is sent unchanged; a handler that still edits fields must build the object instead.
    /// </summary>
    public static FutResponse Template(string name, TemplateArgs? args, int status = 200, params (string Name, string Value)[] headers) =>
        new(status, TemplateLibrary.Core.Bytes(name, args), headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)));

    /// <summary>The template form of <see cref="NoStore(JsonNode?, int)"/>.</summary>
    public static FutResponse TemplateNoStore(string name, TemplateArgs? args, int status = 200) =>
        Template(name, args, status, ("Cache-Control", "no-store"));

    public static FutResponse Reason(int status, string reason, params (string Name, string Value)[] headers) =>
        Json(new JsonObject { ["reason"] = reason }, status, headers);

    public static FutResponse Bytes(int status, byte[] body, params (string Name, string Value)[] headers) =>
        new(status, body, headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)));
}
