using System.Text.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Access;
using Microsoft.AspNetCore.Http;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>
/// <c>POST /api/v1/launch/code</c>: the minimal launch route, behind the operator's HTTPS reverse proxy. The Aurora
/// launcher uses <see cref="ClientApiEndpoint"/>'s <c>POST /client/launch</c>, which issues codes the same way
/// (<see cref="LaunchIssuer"/>). An issued launch also records the player's Windows maths group from the request's
/// X-Aurora21-Windows and X-Aurora21-Math headers (<see cref="MathGroups.NoteLaunch"/>).
/// </summary>
public sealed class LaunchCodeEndpoint(LaunchIssuer issuer)
{
    public const string WindowsHeader = "X-Aurora21-Windows";
    public const string MathHeader = "X-Aurora21-Math";

    private const int MaxRequestBytes = 4096;
    private const string Route = "/api/v1/launch/code";

    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (context.Request.Path != Route)
        {
            await ReplyAsync(context, 404, new { error = "not_found" });
            return;
        }
        if (context.Request.Method != "POST")
        {
            await ReplyAsync(context, 405, new { error = "method_not_allowed" });
            return;
        }
        if (context.Connection.RemoteIpAddress is not { } peer || !System.Net.IPAddress.IsLoopback(peer) ||
            context.Request.ContentType is not { } contentType ||
            !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
            context.Request.ContentLength is > MaxRequestBytes)
        {
            await ReplyAsync(context, 400, new { error = "invalid_request" });
            return;
        }
        try
        {
            var body = await ReadBodyAsync(context.Request.Body, MaxRequestBytes, context.RequestAborted);
            if (body is null)
            {
                await ReplyAsync(context, 413, new { error = "request_too_large" });
                return;
            }
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("launchAssertion", out var field) ||
                field.ValueKind != JsonValueKind.String)
            {
                await ReplyAsync(context, 400, new { error = "invalid_request" });
                return;
            }
            var outcome = await issuer.IssueAsync(field.GetString(), null, null,
                context.Request.Headers[WindowsHeader].FirstOrDefault(), context.Request.Headers[MathHeader].FirstOrDefault(),
                context.RequestAborted);
            if (!outcome.Issued)
            {
                await ReplyAsync(context, outcome.Status, new { error = outcome.Error });
                return;
            }
            await ReplyAsync(context, 200, new { code = outcome.Code, expiresInSeconds = issuer.CodeLifetimeSeconds });
        }
        catch (JsonException)
        {
            await ReplyAsync(context, 400, new { error = "invalid_request" });
        }
    }

    /// <summary>The whole body, or null when it exceeds <paramref name="limit"/> bytes.</summary>
    internal static async Task<byte[]?> ReadBodyAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task ReplyAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
