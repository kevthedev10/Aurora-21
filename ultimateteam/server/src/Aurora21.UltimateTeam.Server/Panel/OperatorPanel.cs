using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>A process or listener the panel reports on (Dashboard services, Maintenance restarts).</summary>
public sealed record PanelService(string Key, string Label, int Port, Func<CancellationToken, Task<bool>> Up);

/// <summary>What the panel reads besides the database: where logs and backups are, and what the server runs.</summary>
public sealed record OperatorPanelOptions
{
    public string? LogsDirectory { get; init; }

    /// <summary>The database dumps the backup service writes (read-only here).</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>The panel images (card frames, players, badges, flags) served under /panel/assets/; null serves none.</summary>
    public string? AssetsDirectory { get; init; }

    /// <summary>Where a "back up now" request is dropped for the backup service.</summary>
    public string? BackupRequestDirectory { get; init; }

    /// <summary>Shared with the VPS restart worker; null keeps panel restarts unavailable.</summary>
    public string? RestartRequestDirectory { get; init; }

    /// <summary>The address players reach (Dashboard "Advertised address").</summary>
    public string AdvertisedHost { get; init; } = "127.0.0.1";

    public IReadOnlyList<PanelService> Services { get; init; } = [];

    /// <summary>The content areas the panel may edit through changesets (live-content-editing-plan.md, step 3).</summary>
    public IReadOnlyList<IContentArea> ContentAreas { get; init; } = Core.Content.ContentAreas.Default;
    public ContentSchedule? Schedule { get; init; }

    /// <summary>The online game speed matchmaking sends (Access &amp; maintenance); null hides the control.</summary>
    public OnlineGameSpeed? GameSpeed { get; init; }

    /// <summary>How matchmaking announces a departure before IN_GAME (POST maintenance/pregame-departure); null refuses edits.</summary>
    public PregameDeparture? PregameDeparture { get; init; }
}

/// <summary>
/// The Aurora 21 React operator panel (<c>Resources/panel/</c>) served under <c>/panel/</c>, and its <c>/panel/api/*</c> contract
/// (Aurora16 docs/PANEL-CONTRACTS.md section 6) answered from Aurora 21's PostgreSQL clubs, catalogue and live access.
/// The hub frames this page on its own tunnelled loopback port, as it does Aurora 16's. Access is Aurora 16's
/// private-access model: a direct loopback connection (an SSH tunnel) is the only credential, so forwarded requests,
/// foreign Host headers, cross-site requests and non-JSON writes are refused before any route. Never publish this port.
/// Club changes apply at once in a PostgreSQL transaction (the same per-club runner as FUT), so an edit answers 200
/// with the player's detail; Aurora 16's queued-edit answers (202, 409 pending) never occur here.
/// </summary>
public sealed partial class OperatorPanel(GameContext game, PlayerRegistry players, LaunchIssuer issuer,
    MatchmakingRegistry? matchmaking, OperatorPanelOptions options)
{
    private const int MaxBodyBytes = 256 * 1024;
    private const string ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    private static readonly string[] AllowedHosts = ["127.0.0.1", "localhost", "[::1]"];
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private readonly ILogger _log = game.Logger("UltimateTeam.Panel");

    /// <summary>A refusal the page shows as its message: <c>{error}</c> plus optional fields, with this status.</summary>
    private sealed class PanelError(int status, string message, object? extra = null) : Exception(message)
    {
        public int Status { get; } = status;
        public object? Extra { get; } = extra;
    }

    /// <summary>One API request: the path after <c>/panel/api/</c>, split into parts, and the parsed JSON body.</summary>
    private sealed record ApiRequest(HttpContext Context, string Method, string[] Parts, JsonElement Body)
    {
        public string Query(string key) => Context.Request.Query[key].ToString();
        public CancellationToken Aborted => Context.RequestAborted;
        public string Remote => Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;
        try
        {
            if (Refusal(context) is { } refused)
            {
                _log.LogWarning("PANEL refused {Method} {Path}: {Reason}", method, path, refused);
                await Json(context, 403, new { error = refused });
                return;
            }
            if (method == "GET" && path == "/")
            {
                Redirect(context, "/panel/");
                return;
            }
            if (!Regex.IsMatch(path, "^/panel(/|$)"))
            {
                await Json(context, 404, new { error = "not found" });
                return;
            }
            if (method == "GET" && path == "/panel")
            {
                Redirect(context, path + "/");
                return;
            }
            // The shell and assets are public on this private port: they hold nothing until the API answers.
            if (method == "GET" && path is "/panel/" or "/panel/index.html")
            {
                await StaticAsync(context, "index.html");
                return;
            }
            // Preserve existing React bookmarks; browsers retain their hash route across this redirect.
            if (method == "GET" && path is "/panel/next" or "/panel/next/" or "/panel/next/index.html")
            {
                Redirect(context, "/panel/" + context.Request.QueryString);
                return;
            }
            // Heads uploaded in the card editor come from PostgreSQL.
            if (method == "GET" && PanelHead().Match(path) is { Success: true } uploaded)
            {
                await HeadImageAsync(context, long.Parse(uploaded.Groups[1].Value, CultureInfo.InvariantCulture));
                return;
            }
            // SBC badge images for the SBC tab come from the bundled art folder.
            if (method == "GET" && PanelSbcBadge().Match(path) is { Success: true } badge)
            {
                await SbcBadgeAsync(context, badge.Groups[1].Value);
                return;
            }
            // Downloaded images come from the assets folder, not the embedded page files.
            if (method == "GET" && PanelImage().Match(path) is { Success: true } image)
            {
                await ImageAsync(context, image.Groups[1].Value, image.Groups[2].Value);
                return;
            }
            var asset = Regex.Match(path, @"^/panel/([A-Za-z0-9_/-]+\.(js|css|woff2|png|svg))$");
            if (method == "GET" && asset.Success)
            {
                await StaticAsync(context, asset.Groups[1].Value);
                return;
            }
            if (!path.StartsWith("/panel/api/", StringComparison.Ordinal))
            {
                await Json(context, 404, new { error = "not found" });
                return;
            }
            var api = path["/panel/api/".Length..].TrimEnd('/');
            // Password-free private panel (Aurora 16 auth.js): the SSH tunnel authenticates, sign-in only confirms it.
            if (method == "POST" && api == "login")
            {
                _log.LogInformation("PANEL sign-in from {Remote} (loopback)", context.Connection.RemoteIpAddress);
                await Json(context, 200, new { ok = true, mode = "loopback" });
                return;
            }
            if (method == "POST" && api == "logout")
            {
                context.Response.Headers.SetCookie = "aurora21_panel=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0";
                await Json(context, 200, new { ok = true, mode = "loopback" });
                return;
            }
            if (api == "session")
            {
                await Json(context, 200, new { ok = true, mode = "loopback" });
                return;
            }
            // A head upload carries its picture; every other request is small.
            var body = await BodyAsync(context, (api == "heads" || api.StartsWith("heads/", StringComparison.Ordinal)) ? MaxHeadUploadBytes : MaxBodyBytes);
            var request = new ApiRequest(context, method, api.Split('/', StringSplitOptions.RemoveEmptyEntries), body);
            // One content revision per panel request; a reload answers with the revision it published.
            using var content = game.Live.Pin();
            var reply = await RouteAsync(request);
            if (reply is null) throw new PanelError(404, "not found");
            await Json(context, reply.Value.Status, reply.Value.Body);
        }
        catch (PanelError error)
        {
            await Json(context, error.Status, Merge(new JsonObject { ["error"] = error.Message }, error.Extra));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error)
        {
            _log.LogError(error, "PANEL {Method} {Path} failed", method, path);
            if (!context.Response.HasStarted) await Json(context, 500, new { error = "The panel hit an error; see the server log." });
        }
    }

    /// <summary>Dispatches one API route by its first part; null when no module owns it (404).</summary>
    private async Task<(int Status, object Body)?> RouteAsync(ApiRequest request) => request.Parts.FirstOrDefault() switch
    {
        "dashboard" when request.Parts.Length == 1 && request.Method == "GET" => (200, await DashboardAsync(request)),
        "players" => await PlayersAsync(request),
        "cards" => await CardsAsync(request),
        "packs" => await PacksAsync(request),
        "promos" or "promo-events" or "icon-rotations" => await PromosAsync(request),
        "modes" => await ModesAsync(request),
        "maintenance" => await MaintenanceAsync(request),
        "assets" when request.Parts.Length == 1 && request.Method == "GET" => (200, AssetsReport()),
        "heads" => await HeadsAsync(request),
        "content" => await ContentAsync(request),
        "sbc" => await SbcAsync(request),
        "objectives" => await ObjectivesAsync(request),
        "logs" => await LogsAsync(request),
        "matchmaking" when request.Parts.Length == 1 && request.Method == "GET" => (200, Matchmaking()),
        "leavers" => await LeaversAsync(request),
        "market-log" => await MarketLogAsync(request),
        _ => null,
    };

    /// <summary>
    /// Aurora 16's private-access refusal: a direct loopback connection without forwarding headers, a loopback Host
    /// (DNS rebinding), same-origin fetches (the hub may frame a GET document), and JSON bodies on writes.
    /// </summary>
    private static string? Refusal(HttpContext context)
    {
        var request = context.Request;
        var remote = context.Connection.RemoteIpAddress;
        var local = context.Connection.LocalIpAddress;
        if (remote is null || local is null || !IPAddress.IsLoopback(remote) || !IPAddress.IsLoopback(local) ||
            request.Headers.Keys.Any(key => key.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                                            key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
            return "Private loopback connection required.";
        var host = request.Host.Host.ToLowerInvariant();
        if (request.Host.Host.Contains(':')) host = "[" + host.Trim('[', ']') + "]";
        if (!AllowedHosts.Contains(host)) return "Unexpected Host header.";
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none") &&
            !(site == "same-site" && request.Method is "GET" or "HEAD" && request.Headers["Sec-Fetch-Dest"].ToString() is "document" or "iframe"))
            return "Cross-site request refused.";
        if (request.Headers.TryGetValue("Origin", out var origin))
        {
            if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var originUri)) return "Invalid Origin header.";
            if (originUri.Scheme != "http" || !originUri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase))
                return "Cross-origin request refused.";
        }
        if (request.Method is not ("GET" or "HEAD") &&
            !Regex.IsMatch(request.ContentType ?? string.Empty, @"^application/json(\s*;|$)", RegexOptions.IgnoreCase))
            return "Send Content-Type: application/json.";
        return null;
    }

    private static async Task<JsonElement> BodyAsync(HttpContext context, int limit)
    {
        if (context.Request.Method is "GET" or "HEAD") return default;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > limit) throw new PanelError(413, "Request body too large.");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return default;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }

    private static async Task StaticAsync(HttpContext context, string file)
    {
        var resource = "panel/" + file;
        await using var stream = typeof(OperatorPanel).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            await Json(context, 404, new { error = "not found" });
            return;
        }
        context.Response.StatusCode = 200;
        context.Response.ContentType = Path.GetExtension(file) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".woff2" => "font/woff2",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        context.Response.ContentLength = stream.Length;
        context.Response.Headers.CacheControl = file.EndsWith(".woff2", StringComparison.Ordinal) ? "public, max-age=31536000" : "no-cache";
        Security(context.Response);
        if (file.EndsWith(".html", StringComparison.Ordinal)) context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = location;
        Security(context.Response);
    }

    private static void Security(HttpResponse response)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    private static async Task Json(HttpContext context, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.CacheControl = "no-store";
        Security(context.Response);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static JsonObject Merge(JsonObject target, object? extra)
    {
        if (extra is null) return target;
        foreach (var (key, value) in JsonSerializer.SerializeToNode(extra, extra.GetType())!.AsObject())
            target[key] = value?.DeepClone();
        return target;
    }

    // ------------------------------------------------------------ shared helpers

    /// <summary>A timestamp as the pages parse it: ISO 8601 in UTC.</summary>
    private static string? Iso(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Iso(DateTime? value) => value is null ? null : Iso(new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)));

    private static JsonElement? Field(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value : null;

    private static string? TextField(JsonElement body, string name) =>
        Field(body, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>A whole number from JSON (a number, or a string of digits as Aurora 16 accepts), else null.</summary>
    private static long? WholeField(JsonElement body, string name)
    {
        if (Field(body, name) is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString()?.Trim(), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new PanelError(400, $"{name} must be a whole number.");
    }

    private static bool? FlagField(JsonElement body, string name) => Field(body, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } value when value.GetString()?.Trim() is "1" or "true" or "on" => true,
        { ValueKind: JsonValueKind.String } value when value.GetString()?.Trim() is "0" or "false" or "off" or "" => false,
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt32(out var n) && n is 0 or 1 => n == 1,
        _ => throw new PanelError(400, $"{name} must be on or off."),
    };

    private static int QueryInt(ApiRequest request, string key, int fallback, int minimum, int maximum)
    {
        var raw = request.Query(key);
        if (raw.Length == 0) return fallback;
        if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw new PanelError(400, $"{key} must be a whole number.");
        return Math.Clamp(value, minimum, maximum);
    }

    /// <summary>Discord ids from a comma- or space-separated list; each must be a verified id's shape.</summary>
    private static IReadOnlyList<string> DiscordList(string? text, string field)
    {
        var ids = (text ?? string.Empty).Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length > 5000) throw new PanelError(400, $"{field}: at most 5000 Discord ids.");
        if (ids.FirstOrDefault(id => !PlayerAccess.IsDiscordId(id)) is { } bad)
            throw new PanelError(400, $"{field}: \"{bad}\" is not a Discord user id (5 to 32 digits). Aurora 21 identifies players by their verified Discord account, never by address or name.");
        return ids;
    }

}
