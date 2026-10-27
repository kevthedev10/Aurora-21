using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using QueryString = Aurora21.UltimateTeam.Core.Web.QueryString;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>Port 17502 over TCP: the QoS probe, firewall and NAT-type documents pointing at the local UDP responder.</summary>
public sealed class QosEndpoint
{
    private const string ServerName = "UltimateTeam21QoS/0.1";

    /// <summary>127.0.0.1 read as a little-endian uint32.</summary>
    private const uint LoopbackLittleEndian = 16777343;

    /// <summary>A qtyp=2 request asks for the bandwidth probe; anything else is a latency ping.</summary>
    private const string BandwidthQueryType = "2";

    private readonly ILogger _log;

    public QosEndpoint(ILogger log) => _log = log;

    public async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Method != "GET")
        {
            await HttpExchange.UnsupportedMethodAsync(context, ServerName);
            return;
        }
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        var document = path switch
        {
            "/qos/qos" => Probe(QueryString.Parse(context.Request.QueryString.Value?.TrimStart('?') ?? string.Empty).First("qtyp") == BandwidthQueryType),
            "/qos/firewall" => Document("firewall"),
            "/qos/firetype" => Document("firetype"),
            _ => null,
        };
        var status = document is null ? 404 : 200;
        _log.LogInformation("QOS17502 {Peer} - \"GET {Path}\" {Status}", context.Connection.RemoteIpAddress, path, status);
        await HttpExchange.WriteTextAsync(context, status, document ?? "not found", ServerName,
            ("Content-Type", document is null ? "text/plain" : "application/xml; charset=utf-8"),
            ("Cache-Control", "no-store"),
            ("Connection", "close"));
    }

    private static string Probe(bool bandwidth) => bandwidth
        ? Document("qos", ("probes", 10), ("probeSize", 1200), ("requestId", 1234), ("secret", 5678))
        : Document("qos", ("probes", 0), ("probeSize", 0), ("requestId", 1), ("secret", 0));

    private static string Document(string name, params (string Name, object Value)[] values) =>
        RuleText.Format(EmbeddedResources.ReadText(typeof(QosEndpoint).Assembly, $"qos/{name}.xml"),
            values.Concat(new (string, object)[] { ("port", Ports.Qos), ("ip", LoopbackLittleEndian) }).ToArray());
}
