using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Hosting;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>
/// The gosredirector getServerInstance answer pointing FIFA at the local Blaze endpoint. Served
/// over TLS by the redirector command and in cleartext on port 42231 for debugging.
/// </summary>
public sealed class RedirectorEndpoint
{
    public const string ServerName = "UltimateTeam21Redirector/0.1";

    private const int BodyPreviewBytes = 800;

    private readonly ILogger _log;

    public RedirectorEndpoint(ILogger log) => _log = log;

    /// <summary>
    /// The server instance document; <paramref name="indent"/> is one nesting level. With <paramref name="secure"/> the
    /// Blaze endpoint is TLS (<c>&lt;secure&gt;1&lt;/secure&gt;</c>) and is named by the redirector certificate's host,
    /// which the hosts entry maps to 127.0.0.1, so FIFA can match the certificate to it (inferred; the Blaze TLS probe
    /// tests it). Otherwise plaintext Blaze on 127.0.0.1, as before.
    /// </summary>
    public static byte[] ServerInstance(int blazePort, string indent = "  ", bool secure = false)
    {
        var lines = new (int Depth, string Text)[]
        {
            (0, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"),
            (0, "<serverinstanceinfo>"),
            (1, "<address member=\"0\">"),
            (2, "<valu>"),
            (3, $"<hostname>{(secure ? TlsProbeOptions.Hostname : "127.0.0.1")}</hostname>"),
            (3, "<ip>2130706433</ip>"),
            (3, $"<port>{blazePort.ToString(CultureInfo.InvariantCulture)}</port>"),
            (2, "</valu>"),
            (1, "</address>"),
            (1, $"<secure>{(secure ? 1 : 0)}</secure>"),
            (0, "</serverinstanceinfo>"),
        };
        return Encoding.UTF8.GetBytes(string.Join("\n", lines.Select(line => string.Concat(Enumerable.Repeat(indent, line.Depth)) + line.Text)));
    }

    public static Task WriteServerInstanceAsync(HttpContext context, byte[] document) =>
        HttpExchange.WriteAsync(context, 200, document, ServerName, ("Content-Type", "application/xml"), ("Connection", "close"));

    /// <summary>The cleartext debug listener (42231).</summary>
    public async Task HandleCleartextAsync(HttpContext context)
    {
        if (context.Request.Method is not ("GET" or "POST"))
        {
            await HttpExchange.UnsupportedMethodAsync(context, ServerName);
            return;
        }
        var body = await HttpExchange.ReadBodyAsync(context.Request);
        _log.LogInformation("Redirector request {Method} {Path} body={Body}", context.Request.Method, context.Request.Path, HttpExchange.Preview(body, BodyPreviewBytes));
        await WriteServerInstanceAsync(context, ServerInstance(Ports.Blaze, indent: "    "));
    }
}
