using System.Text.Json.Nodes;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Services.Auth;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>Ports 8090 and 8081: the FUT, POW and local-auth API served by <see cref="FutRouter"/>.</summary>
public sealed class FutEndpoint
{
    private const string ServerName = "UltimateTeam/0.1";

    /// <summary>How many FUT requests after each <c>/ut/auth</c> get a follow-up evidence line.</summary>
    private const int FollowUpRequests = 30;

    /// <summary>Connections remembered as already seen; the set is cleared past this size.</summary>
    private const int SeenConnectionLimit = 4096;

    private readonly PlayerRegistry _players;
    private readonly ILogger _log;

    // Hosted connector investigation (native-hosting-investigation.md gate 1): per player, the FUT requests still to
    // describe after its last /ut/auth, and the connections already seen.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _followUps = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenConnections = new(StringComparer.Ordinal);

    public FutEndpoint(PlayerRegistry players)
    {
        _players = players;
        _log = players.Local.Game.Logger("UltimateTeam.HttpServices");
    }

    public async Task HandleAsync(HttpContext context)
    {
        var elapsed = Stopwatch.StartNew();
        var request = context.Request;
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? request.Path + request.QueryString;
        var body = await HttpExchange.ReadBodyAsync(request);
        var headers = request.Headers.Select(header => KeyValuePair.Create(header.Key, header.Value.ToString()));
        if (_players.NativeSessions is { } sessions)
        {
            await HandleVerifiedAsync(context, sessions, rawTarget, headers, body, elapsed);
            return;
        }

        using var lease = await _players.AcquireAsync(PlayerFor(context.Connection.RemoteIpAddress, _players), context.RequestAborted);
        var session = lease.Session;
        NoteFollowUp(context, session, rawTarget, headers);
        if (await RouteAsync(context, session.Router, rawTarget, headers, body) is not { } response)
        {
            return;
        }
        await WriteAsync(context, response, rawTarget, session.Game.UtSid, elapsed);
    }

    /// <summary>
    /// The session identity mode (native-hosting-investigation.md gate 3): the player comes only from a verified
    /// credential, before any club is opened.
    /// <list type="bullet">
    /// <item><c>/ut/auth</c>: its <c>identification.authCode</c> must be a code issued for a player; a verified request
    /// starts that player's UT session, whose random <c>X-UT-SID</c> the reply carries.</item>
    /// <item><c>v2/user/accountinfo</c> before <c>/ut/auth</c>: its <c>nucleus-access-code</c> header, the same code.</item>
    /// <item>Every other request with <c>X-UT-SID</c>: that live session's player. Logout ends the session.</item>
    /// <item>A request with none of these reaches only the local auth endpoints and shared <c>/fut/</c> content.</item>
    /// </list>
    /// Anything else is refused with 401 before a club lease. The persona id in a request body never selects a club.
    /// </summary>
    private async Task HandleVerifiedAsync(HttpContext context, NativeSessions sessions, string rawTarget,
        IEnumerable<KeyValuePair<string, string>> headers, byte[] body, Stopwatch elapsed)
    {
        var method = context.Request.Method;
        var path = "/" + rawTarget.Split('?', 2)[0].TrimStart('/');
        var lowerPath = path.ToLowerInvariant();
        string? Header(string name) => context.Request.Headers.TryGetValue(name, out var value) && value.Count > 0 ? value.ToString() : null;
        var utSid = Header("X-UT-SID");
        var utAuth = lowerPath == "/ut/auth" && method is "POST" or "PUT";
        PlayerIdentity? player;
        string reason;
        if (utAuth)
        {
            player = sessions.PlayerForCode((FutJson.SafeParse(body) as JsonObject)?.Obj("identification")?.Str("authCode"));
            reason = "invalid auth code";
        }
        else if (utSid is not null)
        {
            player = sessions.PlayerForSession(utSid);
            reason = "expired session";
        }
        else if (lowerPath.EndsWith("/v2/user/accountinfo", StringComparison.Ordinal) && Header("nucleus-access-code") is { } code)
        {
            player = sessions.PlayerForCode(code);
            reason = "invalid auth code";
        }
        else
        {
            // No credential: only the endpoints that never touch a club.
            var anonymous = await RouteAsync(context, _players.Local.Router, rawTarget, headers, body, anonymous: true);
            if (anonymous is not null || context.RequestAborted.IsCancellationRequested)
            {
                if (anonymous is not null)
                {
                    await WriteAsync(context, anonymous, rawTarget, null, elapsed);
                }
                return;
            }
            player = null;
            reason = "no session";
        }
        if (player is null)
        {
            _log.LogWarning("FUT SESSION REFUSED {Peer} {Method} {Path}: {Reason}", context.Connection.RemoteIpAddress, method, lowerPath, reason);
            await WriteAsync(context, Refused(reason), rawTarget, null, elapsed);
            return;
        }

        using var lease = await _players.AcquireAsync(player, context.RequestAborted);
        var session = lease.Session;
        NoteFollowUp(context, session, rawTarget, headers);
        if (utAuth)
        {
            utSid = sessions.StartSession(player);
            session.Game.UtAuthSid = utSid;
            if ((FutJson.SafeParse(body) as JsonObject)?.Get("nucleusPersonaId") is { } claimed && JsonValues.TryInt(claimed, out var persona) && persona != player.PersonaId)
            {
                // Logged only: the verified code, not this number, chose the club.
                _log.LogWarning("FUT SESSION /ut/auth persona mismatch player={Player}: the body names another persona", player.Id);
            }
            _log.LogInformation("FUT SESSION STARTED player={Player}", player.Id);
        }
        FutResponse? response;
        try
        {
            response = await RouteAsync(context, session.Router, rawTarget, headers, body);
        }
        finally
        {
            if (utAuth)
            {
                session.Game.UtAuthSid = null;
            }
        }
        if (response is null)
        {
            return;
        }
        if (utSid is not null && (lowerPath == "/ut/delete/auth" || (lowerPath == "/ut/auth" && method == "DELETE")))
        {
            sessions.EndSession(utSid);
            _log.LogInformation("FUT SESSION ENDED player={Player} (logout)", player.Id);
        }
        await WriteAsync(context, response, rawTarget, utSid, elapsed);
    }

    /// <summary>The 401 a request without a verified player gets (the store's coded error shape; retail's is unconfirmed).</summary>
    private static FutResponse Refused(string reason) =>
        FutResponse.Json(TemplateLibrary.Core.Object("bootstrap/session-refused", new TemplateArgs { ["reason"] = reason }), 401);

    /// <summary>Routes one request; null when the client went away (nothing to answer).</summary>
    private async Task<FutResponse?> RouteAsync(HttpContext context, FutRouter router, string rawTarget,
        IEnumerable<KeyValuePair<string, string>> headers, byte[] body, bool anonymous = false)
    {
        var request = new FutRequest(context.Request.Method, rawTarget, headers, body);
        try
        {
            return anonymous
                ? await router.HandleAnonymousAsync(request, context.RequestAborted)
                : await router.HandleAsync(request, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nobody is left to answer.
            _log.LogInformation("FUT {Peer} - \"{Method} {Path}\" abandoned by the client", context.Connection.RemoteIpAddress, context.Request.Method, rawTarget.Split('?')[0]);
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.LogError(error, "FUT handler error for {Method} {Path}", context.Request.Method, rawTarget.Split('?')[0]);
            return FutResponse.Json(new JsonObject { ["error"] = "local server exception" }, 500);
        }
    }

    /// <summary>Logs and writes a response; <c>/ut</c> replies carry <paramref name="utSid"/> as <c>X-UT-SID</c> when there is one.</summary>
    private async Task WriteAsync(HttpContext context, FutResponse response, string rawTarget, string? utSid, Stopwatch elapsed)
    {
        var request = context.Request;
        var lowerPath = rawTarget.ToLowerInvariant();
        if (IsLocalAuthPath(lowerPath))
        {
            // Local auth requests carry secrets in their query strings; log the path only.
            _log.LogInformation("FUT {Peer} - local auth {Method} {Path} handler={ElapsedMs} ms", context.Connection.RemoteIpAddress, request.Method, lowerPath.Split('?')[0], elapsed.Elapsed.TotalMilliseconds);
        }
        else
        {
            _log.LogInformation("FUT {Peer} - \"{Method} {Path}\" {Status} handler={ElapsedMs} ms", context.Connection.RemoteIpAddress, request.Method, rawTarget, response.Status, elapsed.Elapsed.TotalMilliseconds);
        }

        var headersOut = new List<(string, string)> { ("Content-Type", response.ContentType), ("Connection", "keep-alive") };
        if (utSid is not null && (rawTarget.StartsWith("/ut", StringComparison.Ordinal) || rawTarget.StartsWith("//ut", StringComparison.Ordinal)))
        {
            // Single and peer-IP modes: derived from the account loaded with the club, no database transaction per
            // response. Session mode: the request's verified session.
            headersOut.Add(("X-UT-SID", utSid));
        }
        headersOut.AddRange(response.ExtraHeaders.Select(header => (header.Key, header.Value)));
        await HttpExchange.WriteAsync(context, response.Status, response.Body, ServerName, headersOut.ToArray());
        if (ChampionsUiEvidence.Summarize(rawTarget, response) is { } champions)
        {
            _log.LogInformation("CHAMPIONS-UI serverBuildSha256={Build} status={Status} evidence={Evidence}",
                LocalAuthService.ServerBuildSha256, response.Status, champions);
        }
    }

    /// <summary>The player a connection belongs to: the local player, or on a shared server the peer address's player.</summary>
    public static PlayerIdentity PlayerFor(System.Net.IPAddress? peer, PlayerRegistry players) =>
        !players.IsShared
            ? players.Local.Player
            : players.NativeSessions is not null
                // The session mode never derives a player from an address. FUT resolves its own (verified code, X-UT-SID);
                // binding Blaze and LSX to a verified player is gate 4 of native-hosting-investigation.md.
                ? throw new InvalidOperationException("the session identity mode has no verified player binding for Blaze or LSX yet")
            : peer is null
                ? throw new InvalidOperationException("shared player identity requires a peer address")
                : (peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer).AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    ? throw new InvalidOperationException("shared player identity requires an IPv4 peer address")
                    : PlayerIdentity.FromPeerAddress((peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer).ToString());

    /// <summary>
    /// Redacted evidence of what FIFA sends back after <c>/ut/auth</c> (gate 1 of native-hosting-investigation.md): for the
    /// next <see cref="FollowUpRequests"/> FUT requests of that player, and the first request on every new connection,
    /// the connection, every header name, and the credential-like headers described by <see cref="CredentialEvidence"/>
    /// (a value this server issued by name, such as <c>club-ut-sid</c>; anything else as a length and hash prefix). The
    /// path is logged without its query string.
    /// </summary>
    private void NoteFollowUp(HttpContext context, PlayerSession session, string rawTarget, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var path = rawTarget.Split('?', 2)[0];
        var player = session.Player.Id;
        var isUtAuth = path.TrimStart('/').Equals("ut/auth", StringComparison.OrdinalIgnoreCase);
        if (_seenConnections.Count > SeenConnectionLimit)
        {
            _seenConnections.Clear();
        }
        // Kestrel always names the connection; a request without one (an in-memory host) is never "new".
        var newConnection = !string.IsNullOrEmpty(context.Connection.Id) && _seenConnections.TryAdd(context.Connection.Id, 0);
        var remaining = _followUps.GetValueOrDefault(player);
        if (!isUtAuth && remaining <= 0 && !newConnection)
        {
            return;
        }
        if (isUtAuth)
        {
            _followUps[player] = FollowUpRequests;
        }
        else if (remaining > 0)
        {
            _followUps[player] = remaining - 1;
        }
        var list = headers.ToList();
        _log.LogInformation("AUTH EVIDENCE follow-up conn={Connection} new={New} after-ut-auth={Sequence} {Method} {Path} header-names: {Names} credentials: {Credentials}",
            context.Connection.Id, newConnection, isUtAuth ? "auth" : remaining > 0 ? (FollowUpRequests - remaining + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-",
            context.Request.Method, path, string.Join(',', list.Select(header => header.Key.ToLowerInvariant())),
            CredentialEvidence.DescribeHeaders(list, session.Game));
    }

    private static bool IsLocalAuthPath(string lowerTarget)
    {
        var path = "/" + lowerTarget.Split('?', 2)[0].TrimStart('/');
        return LocalAuthService.Paths.Contains(path.TrimEnd('/'));
    }
}
