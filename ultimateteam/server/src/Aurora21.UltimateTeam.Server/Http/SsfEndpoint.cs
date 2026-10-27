using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>Port 10853: the social framework (SSF) session handshake; every other route is absent.</summary>
public sealed class SsfEndpoint
{
    private const string ServerName = "UltimateTeam21SSF/0.1";
    private const string SessionHeader = "X-SF-SID";

    /// <summary>The session id issued when the client does not bring one.</summary>
    private const string LocalSessionId = "ultimateteam21-ssf";

    private readonly GameContext _game;
    private readonly ILogger _log;

    public SsfEndpoint(GameContext game, ILogger log)
    {
        _game = game;
        _log = log;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if (method is not ("POST" or "GET"))
        {
            await HttpExchange.UnsupportedMethodAsync(context, ServerName);
            return;
        }
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        if (method == "POST")
        {
            await HttpExchange.ReadBodyAsync(context.Request);
            if (path == "/sf/auth")
            {
                var session = context.Request.Headers[SessionHeader].ToString();
                var now = _game.NowSeconds;
                _log.LogInformation("SSF auth accepted (session header present={Present})", session.Length > 0);
                await HttpExchange.WriteTextAsync(context, 200, $"{{\"serverTime\":{now},\"lastOnlineTime\":{now}}}", ServerName,
                    ("Content-Type", "application/json"), ("Connection", "close"), (SessionHeader, session.Length > 0 ? session : LocalSessionId));
                return;
            }
        }
        await HttpExchange.WriteTextAsync(context, 404, "{\"error\":\"route_not_found\"}", ServerName, ("Content-Type", "application/json"), ("Connection", "close"));
    }
}
