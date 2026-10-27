using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Server.Tools;
using Microsoft.AspNetCore.Http;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>What the launcher API reports about this server, fixed at start-up.</summary>
public sealed record ClientApiOptions(LauncherApiSettings Settings, string? GameAddress, string? CertificateRoot,
    string? InstallDirectory, DnsEndPoint? RedirectorHealth);

/// <summary>
/// The Aurora launcher's API for Aurora 21, <c>/client/*</c> on the loopback launch listener, published by the
/// operator's HTTPS reverse proxy. The same routes and shapes as Aurora 16 (its docs/LAUNCHER.md), so the launcher's
/// FIFA 21 page follows its FIFA 13/16 pages:
/// <list type="bullet">
/// <item><c>GET /client/health[?discord=&amp;name=]</c>: is the server up and open to this caller.</item>
/// <item><c>POST /client/launch</c> <c>{discordId, discordName, launcher, assertion, friends?}</c>: the verified launch; unlike
/// Aurora 16 the assertion is required and the reply carries the one-time game code for the helper. <c>friends</c>, the
/// signed-in account's launcher friends (Discord ids), lets online Friendlies pair mutual friends first
/// (<see cref="LauncherFriends"/>).</item>
/// <item><c>GET|POST /client/presence</c>: which of these Discord accounts are playing.</item>
/// <item><c>GET /client/matches</c>: the online games being played now (mode, phase, players' display names, durations), for a
/// website or Discord bot. Public, so it carries no Discord ids, persona ids, addresses or relay ports.</item>
/// <item><c>GET /client/trust-anchors/manifest.json</c> and its files: this server's public trust anchors.</item>
/// <item><c>GET /client/install/manifest.json</c> and its files: the client pack the Install button downloads.</item>
/// </list>
/// Every reply except install files is JSON with <c>Cache-Control: no-store</c>. Codes and assertions are never logged.
/// </summary>
public sealed class ClientApiEndpoint(LaunchIssuer issuer, MatchmakingRegistry? matchmaking, ClientApiOptions options,
    TimeProvider? time = null, Func<JsonObject>? leaderboardUi = null, Aurora21.UltimateTeam.Core.Sessions.MathGroups? mathGroups = null,
    LauncherFriends? friends = null)
{
    private const int MaxLaunchBytes = 8 * 1024;
    private const int MaxPresenceBytes = 32 * 1024;
    private const int MaxPresenceIds = 500;
    private static readonly TimeSpan RedirectorCheckInterval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ClientInstall _install = new(options.InstallDirectory);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _launches = new(StringComparer.Ordinal);
    private (DateTimeOffset At, bool Up)? _redirector;

    public LaunchIssuer Issuer => issuer;

    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;
        if ((method is "GET" or "HEAD") && path == "/client/health") await HealthAsync(context);
        else if ((method is "GET" or "HEAD") && path == LeaderboardUiManifest.Path)
        {
            if (leaderboardUi is null) await Reply(context, 503, new { error = "leaderboard_ui_unavailable" });
            else
            {
                var manifest = leaderboardUi();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ETag = '"' + manifest["revision"]!.GetValue<string>() + '"';
                context.Response.ContentType = "application/json";
                if (method != "HEAD") await context.Response.WriteAsync(manifest.ToJsonString(), context.RequestAborted);
            }
        }
        else if (method == "POST" && path == "/client/launch") await LaunchAsync(context);
        else if (method == "POST" && path == "/client/launch/math") await MathReportAsync(context);
        else if ((method is "GET" or "POST") && path == "/client/presence") await PresenceAsync(context);
        else if ((method is "GET" or "HEAD") && path == "/client/matches") await MatchesAsync(context);
        else if ((method is "GET" or "HEAD") && path.StartsWith("/client/trust-anchors/", StringComparison.Ordinal))
            await TrustAnchorAsync(context, path["/client/trust-anchors/".Length..]);
        else if ((method is "GET" or "HEAD") && path.StartsWith("/client/install/", StringComparison.Ordinal))
            await _install.ServeAsync(context, path["/client/install/".Length..]);
        else await Reply(context, 404, new { ok = false, error = "not_found" });
    }

    private async Task HealthAsync(HttpContext context)
    {
        var settings = issuer.Access.Settings;
        var messages = options.Settings;
        var online = issuer.Presence?.OnlineCount ?? 0;
        var discord = context.Request.Query["discord"].ToString();
        AccessDecision? decision = null;
        if (PlayerAccess.IsDiscordId(discord))
        {
            // The launcher says who it is signed in as; the health poll may ask Discord for that account's roles (cached).
            decision = (await issuer.DecideAsync(discord, context.RequestAborted)).Decision;
        }
        else if (settings.Maintenance)
        {
            decision = new AccessDecision(AccessRefusal.Maintenance, false);
        }
        else if (settings.MaxPlayers > 0 && online >= settings.MaxPlayers)
        {
            decision = new AccessDecision(AccessRefusal.Full, false);
        }
        var refused = decision is { Allowed: false } || !issuer.Configured;
        var reason = !issuer.Configured ? "not_configured" : decision?.Reason ?? string.Empty;
        var message = reason.Length == 0 && decision is { Bypass: true } ? settings.MaintenanceMessage : Message(reason);
        var install = _install.Manifest();
        await Reply(context, 200, new
        {
            server = messages.ServerName,
            blaze = true,
            redirector = await RedirectorUpAsync(context.RequestAborted),
            ut = true,
            players = online,
            maintenance = new
            {
                enabled = refused || settings.Maintenance,
                bypass = decision?.Bypass ?? false,
                message,
                reason,
            },
            gameAddress = options.GameAddress,
            install = install is null ? null : new { version = install.Version, files = install.Files.Count },
            trustAnchors = TrustAnchorsPublished(),
            at = _time.GetUtcNow(),
        });
    }

    private async Task LaunchAsync(HttpContext context)
    {
        // A web page must not announce a launch for the PC it runs on (Aurora 16): JSON only, never cross-site.
        if (context.Request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
        {
            await Reply(context, 403, new { ok = false, error = "json_launcher_request_required" });
            return;
        }
        var address = CallerAddress(context);
        if (!AllowLaunch(address))
        {
            await Reply(context, 429, new { ok = false, error = "too_many_launches" });
            return;
        }
        var body = await LaunchCodeEndpoint.ReadBodyAsync(context.Request.Body, MaxLaunchBytes, context.RequestAborted);
        if (body is null)
        {
            await Reply(context, 413, new { ok = false, error = "request_too_large" });
            return;
        }
        string? discordId, name, assertion;
        List<string>? friendIds = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            discordId = Text(root, "discordId");
            name = Text(root, "discordName");
            assertion = Text(root, "assertion");
            if (root.TryGetProperty("friends", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                friendIds = list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty).Take(LauncherFriends.MaxFriends).ToList();
            }
        }
        catch (JsonException)
        {
            await Reply(context, 400, new { ok = false, error = "invalid_json" });
            return;
        }
        if (!PlayerAccess.IsDiscordId(discordId))
        {
            await Reply(context, 400, new { ok = false, error = "discordId required" });
            return;
        }
        if (string.IsNullOrEmpty(assertion))
        {
            await Reply(context, 401, new { ok = false, error = "assertion_required" });
            return;
        }
        var outcome = await issuer.IssueAsync(assertion, discordId, name,
            context.Request.Headers[LaunchCodeEndpoint.WindowsHeader].FirstOrDefault(),
            context.Request.Headers[LaunchCodeEndpoint.MathHeader].FirstOrDefault(), context.RequestAborted);
        if (!outcome.Issued)
        {
            var reason = outcome.Error == "launch_not_configured" ? "not_configured" : outcome.Error ?? "";
            await Reply(context, outcome.Status, new { ok = false, error = outcome.Error, message = Message(reason) is { Length: > 0 } text ? text : null });
            return;
        }
        if (friendIds is not null)
        {
            // Only after the verified launch: a launcher names the friends of the account it is signed in as.
            friends?.Record(discordId!, friendIds);
        }
        var player = PlayerIdentity.Of(discordId!);
        await Reply(context, 200, new
        {
            ok = true,
            account = outcome.Player?.DisplayName,
            personaId = player.PersonaId,
            code = outcome.Code,
            expiresInSeconds = issuer.CodeLifetimeSeconds,
            gameAddress = options.GameAddress,
        });
    }

    /// <summary>
    /// <c>POST /client/launch/math</c>: the client pack's maths bridge (Aurora21-MathBridge.exe, run by Start-Aurora21.ps1)
    /// reports the pinned ucrtbase it switched FIFA's maths imports to. The body is <c>{ "code": "&lt;game code&gt;" }</c> and the
    /// <c>X-Aurora21-Math</c> header the bridge contract. The code names the player without being used up, so the report is
    /// accepted until the code expires (native_auth.code_lifetime_seconds), also after FIFA has logged in with it: the helper
    /// reports only once the switch has held for its watch period, usually after the launch is ready. The latest report wins;
    /// the next <c>POST /client/launch</c> with an X-Aurora21-Windows header clears it.
    /// </summary>
    private async Task MathReportAsync(HttpContext context)
    {
        if (context.Request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
        {
            await Reply(context, 403, new { ok = false, error = "json_launcher_request_required" });
            return;
        }
        var math = context.Request.Headers[LaunchCodeEndpoint.MathHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(math))
        {
            await Reply(context, 400, new { ok = false, error = "math_header_required" });
            return;
        }
        // MathGroups ignores a value outside the contract, so answering ok would tell the helper a report landed that never did.
        if (!Aurora21.UltimateTeam.Core.Sessions.MathGroups.IsBridgeReport(math))
        {
            await Reply(context, 400, new { ok = false, error = "invalid_math_header" });
            return;
        }
        var body = await LaunchCodeEndpoint.ReadBodyAsync(context.Request.Body, MaxLaunchBytes, context.RequestAborted);
        if (body is null)
        {
            await Reply(context, 413, new { ok = false, error = "request_too_large" });
            return;
        }
        string? code;
        try
        {
            using var json = JsonDocument.Parse(body);
            code = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("code", out var field)
                && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        }
        catch (JsonException)
        {
            await Reply(context, 400, new { ok = false, error = "invalid_json" });
            return;
        }
        var player = issuer.PlayerForCode(code);
        if (player is null)
        {
            await Reply(context, 401, new { ok = false, error = "unknown_code" });
            return;
        }
        if (mathGroups is null)
        {
            await Reply(context, 503, new { ok = false, error = "math_groups_unavailable" });
            return;
        }
        mathGroups.NoteLaunch(player.Id, null, math, (long)_time.GetUtcNow().ToUnixTimeSeconds());
        await Reply(context, 200, new { ok = true, group = mathGroups.GroupOf(player.Id) });
    }

    private async Task MatchesAsync(HttpContext context)
    {
        if (matchmaking is null)
        {
            await Reply(context, 503, new { ok = false, error = "matchmaking_unavailable" });
            return;
        }
        var games = matchmaking.ActiveMatches();
        var matches = games.Select(game => new
        {
            id = game.GameId,
            mode = PublicMode(game.Mode),
            phase = game.State switch
            {
                GameState.InGame => "in_match",
                GameState.PostGame => "post_match",
                _ => "pre_match",
            },
            players = game.Players.Select(player => new
            {
                name = issuer.Access.Find(player.PlayerId)?.DisplayName ?? "Player",
                left = player.Left,
            }),
            pairedForSeconds = game.PairedForMs / 1000,
            inMatchForSeconds = game.InGameForMs / 1000,
        }).ToList();
        // Read-only and public: a website may poll it from the browser.
        context.Response.Headers.AccessControlAllowOrigin = "*";
        await Reply(context, 200, new
        {
            ok = true,
            count = matches.Count,
            inMatch = matches.Count(match => match.phase == "in_match"),
            matches,
            at = _time.GetUtcNow(),
        });
    }

    /// <summary>The scenario mode as players know it; an unknown one passes through.</summary>
    internal static string PublicMode(string mode) => MatchmakingRegistry.ModeKey(mode) switch
    {
        MatchmakingRegistry.RivalsMode => "rivals",
        "fut_draft" => "draft",
        "fut_champions" => "champions",
        _ when HouseRules.IsHouseRulesMode(mode) => "friendlies",
        _ => mode,
    };

    private async Task PresenceAsync(HttpContext context)
    {
        IEnumerable<string> requested;
        if (context.Request.Method == "GET")
        {
            requested = context.Request.Query["discord"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            var body = await LaunchCodeEndpoint.ReadBodyAsync(context.Request.Body, MaxPresenceBytes, context.RequestAborted);
            if (body is null)
            {
                await Reply(context, 413, new { error = "request_too_large" });
                return;
            }
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("discord", out var list) ||
                    list.ValueKind != JsonValueKind.Array) throw new JsonException();
                requested = list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "").ToArray();
            }
            catch (JsonException)
            {
                await Reply(context, 400, new { error = "invalid_json" });
                return;
            }
        }
        var ids = requested.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length > MaxPresenceIds || ids.Any(id => !PlayerAccess.IsDiscordId(id)))
        {
            await Reply(context, 400, new { error = $"at most {MaxPresenceIds} Discord ids of 5-32 digits" });
            return;
        }
        var presence = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var playerId = PlayerIdentity.Of(id).Id;
            if (issuer.Presence?.IsOnline(playerId) != true) continue;
            presence[id] = new
            {
                online = true,
                searching = matchmaking?.IsSearching(playerId) ?? false,
                inMatch = matchmaking?.IsInMatch(playerId) ?? false,
                name = issuer.Access.Find(id)?.DisplayName,
            };
        }
        await Reply(context, 200, new { presence, at = _time.GetUtcNow() });
    }

    private async Task TrustAnchorAsync(HttpContext context, string name)
    {
        var files = options.CertificateRoot is null ? [] : CertificateGenerator.TrustAnchorFiles(options.CertificateRoot)
            .Where(file => File.Exists(file.Source)).ToArray();
        if (files.Length == 0)
        {
            await Reply(context, 404, new { error = "no_trust_anchors" });
            return;
        }
        if (name == "manifest.json")
        {
            await Reply(context, 200, new
            {
                schema = 1,
                files = files.Select(file => new
                {
                    name = file.Name,
                    size = new FileInfo(file.Source).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Source))).ToLowerInvariant(),
                }),
            });
            return;
        }
        var match = files.FirstOrDefault(file => file.Name == name);
        if (match.Name is null)
        {
            await Reply(context, 404, new { error = "not_found" });
            return;
        }
        var bytes = await File.ReadAllBytesAsync(match.Source, context.RequestAborted);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = name.EndsWith(".pem", StringComparison.Ordinal) ? "application/x-pem-file" : "application/octet-stream";
        context.Response.ContentLength = bytes.Length;
        if (context.Request.Method != "HEAD") await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private bool TrustAnchorsPublished() => options.CertificateRoot is not null &&
        CertificateGenerator.TrustAnchorFiles(options.CertificateRoot).All(file => File.Exists(file.Source));

    /// <summary>
    /// The caller's address for the launch limit. Only the loopback reverse proxy reaches this listener, so the
    /// address it forwards is the player's; a direct loopback call counts as itself.
    /// </summary>
    private static string CallerAddress(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString().Split(',').Select(part => part.Trim()).LastOrDefault(part => part.Length > 0);
        return forwarded is not null && IPAddress.TryParse(forwarded, out var address)
            ? address.ToString()
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private bool AllowLaunch(string address)
    {
        var now = _time.GetUtcNow();
        var window = TimeSpan.FromSeconds(options.Settings.LaunchWindowSeconds);
        var queue = _launches.GetOrAdd(address, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window) queue.Dequeue();
            if (queue.Count >= options.Settings.LaunchesPerAddress) return false;
            queue.Enqueue(now);
        }
        if (_launches.Count > 10_000)
        {
            foreach (var (key, old) in _launches)
            {
                lock (old)
                {
                    if (old.Count == 0 || now - old.Peek() >= window) _launches.TryRemove(key, out _);
                }
            }
        }
        return true;
    }

    private async Task<bool> RedirectorUpAsync(CancellationToken cancellationToken)
    {
        if (options.RedirectorHealth is not { } target) return true;
        var now = _time.GetUtcNow();
        if (_redirector is { } cached && now - cached.At < RedirectorCheckInterval) return cached.Up;
        bool up;
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(target.Host, target.Port, timeout.Token);
            up = true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            up = false;
        }
        _redirector = (now, up);
        return up;
    }

    /// <summary>The line the launcher shows for a reason word; the panel's maintenance line replaces the default.</summary>
    private string Message(string reason) => reason switch
    {
        "maintenance" => issuer.Access.Settings.MaintenanceMessage is { Length: > 0 } custom ? custom : options.Settings.MaintenanceMessage,
        "full" => options.Settings.FullMessage,
        "blocked" => options.Settings.BlockedMessage,
        "no_role" => options.Settings.NoRoleMessage,
        "roles_unavailable" => options.Settings.RolesUnavailableMessage,
        "not_configured" => options.Settings.NotConfiguredMessage,
        _ => string.Empty,
    };

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task Reply(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };
}
