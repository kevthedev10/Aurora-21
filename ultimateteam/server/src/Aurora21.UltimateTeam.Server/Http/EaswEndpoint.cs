using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>Port 42232: the EASW/OSDK services (roster, routing, CTL) and bundled FIFA content files.</summary>
public sealed class EaswEndpoint
{
    private const string ServerName = "UltimateTeamEASW/0.1";

    /// <summary>Bundled files that still hold this marker in their first bytes are test placeholders, never served.</summary>
    private const string PlaceholderMarker = "ULTIMATETEAM_TEST_PLACEHOLDER";
    private const int PlaceholderScanBytes = 256;

    private static readonly string[] ContentPrefixes = { "/contentfifa/", "/content/fifa/" };

    private static readonly (string Name, string Value)[] SessionHeaders =
    {
        ("EASW-Session", "fut15-local-easw-session"),
        ("EASW-Token", "fut15-local-easw-token"),
    };

    private readonly GameContext _game;
    private readonly ILogger _log;

    public EaswEndpoint(GameContext game, ILogger log)
    {
        _game = game;
        _log = log;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        switch (context.Request.Method)
        {
            case "POST":
                await HttpExchange.ReadBodyAsync(context.Request);
                if (path == "/extension")
                {
                    await WriteAsync(context, 200, Document("result"), "text/xml");
                }
                else
                {
                    await HttpExchange.WriteAsync(context, 200, Document("result"), ServerName, ("Content-Type", "text/xml"), ("Connection", "close"));
                }
                return;
            case "GET":
                await GetAsync(context, path);
                return;
            default:
                await HttpExchange.UnsupportedMethodAsync(context, ServerName);
                return;
        }
    }

    private async Task GetAsync(HttpContext context, string path)
    {
        switch (path)
        {
            case "/roster":
                await WriteAsync(context, 200, Document("roster"), "text/xml");
                return;
            // Advertise only resources that exist: missing network files make the OSDK loop FetchClientConfig -> Logout.
            case "/routing" or "/routing/cfgrouting.xml":
                await WriteAsync(context, 200, Document("routing"), "text/xml; charset=utf-8");
                return;
            case "/ctl":
                await WriteAsync(context, 200, Document("result"), "text/xml");
                return;
        }

        // Missing content gets a real 404 rather than an unrelated success document the OSDK cannot parse.
        const string plain = "text/plain; charset=utf-8";
        var relative = ContentPrefixes.Where(prefix => path.StartsWith(prefix, StringComparison.Ordinal)).Select(prefix => path[prefix.Length..]).FirstOrDefault();
        var resource = relative is { Length: > 0 } && !relative.Contains('\\')
            ? RuntimePaths.ContainedPathIgnoreCase(_game.Paths.ContentRoot, relative)
            : null;
        if (resource is null)
        {
            await WriteAsync(context, 404, "content route not found\n"u8.ToArray(), plain);
            return;
        }
        if (!File.Exists(resource))
        {
            _log.LogWarning("Missing FIFA content resource: {Resource}", relative);
            await WriteAsync(context, 404, "content resource unavailable\n"u8.ToArray(), plain);
            return;
        }
        byte[] body;
        try
        {
            body = await File.ReadAllBytesAsync(resource);
        }
        catch (IOException error)
        {
            _log.LogError(error, "Unable to read FIFA content resource: {Resource}", relative);
            await WriteAsync(context, 500, "content resource read failure\n"u8.ToArray(), plain);
            return;
        }
        if (body.Length == 0 || Encoding.ASCII.GetString(body, 0, Math.Min(PlaceholderScanBytes, body.Length)).Contains(PlaceholderMarker, StringComparison.Ordinal))
        {
            _log.LogWarning("Refusing placeholder FIFA content resource: {Resource}", relative);
            await WriteAsync(context, 404, "content resource unavailable\n"u8.ToArray(), plain);
            return;
        }
        var contentType = Path.GetExtension(resource).ToLowerInvariant() switch
        {
            ".csv" => "text/csv; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            _ => "application/octet-stream",
        };
        await WriteAsync(context, 200, body, contentType);
    }

    private static byte[] Document(string name) =>
        Encoding.UTF8.GetBytes(EmbeddedResources.ReadText(typeof(EaswEndpoint).Assembly, $"easw/{name}.xml"));

    /// <summary>An EASW response: the content type, a closed connection and the EASW session headers.</summary>
    private static Task WriteAsync(HttpContext context, int status, byte[] body, string contentType) =>
        HttpExchange.WriteAsync(context, status, body, ServerName, new[] { ("Content-Type", contentType), ("Connection", "close") }.Concat(SessionHeaders).ToArray());
}
