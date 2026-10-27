using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Server.Relay;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace Aurora21.UltimateTeam.Server.Hosting;

/// <summary>
/// Opt-in OTLP metrics for the hosted stack's Prometheus (ultimateteam/deploy/README.md, Monitoring). Off unless
/// <c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c> is set, so the local launcher's server never exports anything. The standard
/// OTEL_* variables set the interval, service name and resource attributes; no metric carries a player id or credential.
/// </summary>
public static class MetricsExport
{
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";

    /// <summary>
    /// Exported meters: endpoint and database-stage timings, the gameplay relay, the .NET runtime, Kestrel connections and
    /// the Npgsql pool. The per-query instruments are dropped: endpoint by SQL statement is too many series for a small Prometheus.
    /// </summary>
    public static readonly string[] Meters =
        [DatabaseTimings.MeterName, RelayMetrics.MeterName, "System.Runtime", "Microsoft.AspNetCore.Server.Kestrel", "Npgsql"];

    public static readonly string[] DroppedInstruments = ["ultimateteam.database.query", "ultimateteam.database.query.rows"];

    /// <summary>The only attribute kept on Npgsql's <c>db.client.*</c> instruments.</summary>
    public const string NpgsqlStateTag = "db.client.connection.state";

    /// <summary>Starts exporting to <paramref name="endpoint"/>, or returns null when it is empty.</summary>
    public static MeterProvider? Start(string? endpoint, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new UsageException($"{EndpointVariable} must be an absolute http(s) URL");
        }
        var builder = Sdk.CreateMeterProviderBuilder().AddMeter(Meters);
        foreach (var instrument in DroppedInstruments) builder.AddView(instrument, MetricStreamConfiguration.Drop);
        // Npgsql names its pool by the connection string (host, user, options); keep only the idle/used state.
        builder.AddView("db.client.*", new MetricStreamConfiguration { TagKeys = [NpgsqlStateTag] });
        // Prometheus's OTLP receiver accepts HTTP/protobuf only; the endpoint is the full signal URL.
        var provider = builder.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = uri;
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        }).Build();
        log.LogInformation("METRICS exporting OTLP to {Endpoint}", uri.GetLeftPart(UriPartial.Path));
        return provider;
    }
}
