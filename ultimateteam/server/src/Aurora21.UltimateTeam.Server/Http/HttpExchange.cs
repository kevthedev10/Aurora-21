using System.Text;
using Microsoft.AspNetCore.Http;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>Request and response plumbing shared by the plain HTTP endpoints.</summary>
public static class HttpExchange
{
    public const long MaxBodyBytes = 16 * 1024 * 1024;

    public static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0 && !request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return Array.Empty<byte>();
        }
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        if (buffer.Length > MaxBodyBytes)
        {
            throw new InvalidDataException("request body too large");
        }
        return buffer.ToArray();
    }

    public static async Task WriteAsync(HttpContext context, int status, byte[] body, string serverName, params (string Name, string Value)[] headers)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.Headers["Server"] = serverName;
        foreach (var (name, value) in headers)
        {
            response.Headers.Append(name, value);
        }
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body);
    }

    public static Task WriteTextAsync(HttpContext context, int status, string body, string serverName, params (string Name, string Value)[] headers) =>
        WriteAsync(context, status, Encoding.UTF8.GetBytes(body), serverName, headers);

    /// <summary>501 for a method the endpoint does not implement; the connection is closed.</summary>
    public static Task UnsupportedMethodAsync(HttpContext context, string serverName) =>
        WriteTextAsync(context, 501, "Unsupported method", serverName, ("Content-Type", "text/plain"), ("Connection", "close"));

    /// <summary>The first <paramref name="limit"/> bytes of a body as text, for logs.</summary>
    public static string Preview(byte[] body, int limit) => Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(limit, body.Length)));
}
