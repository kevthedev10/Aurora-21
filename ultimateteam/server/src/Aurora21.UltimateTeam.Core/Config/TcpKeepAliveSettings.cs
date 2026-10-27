using System.Text.Json.Nodes;
using System.Globalization;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>Opt-in hosted socket probes. Zero idle seconds leaves socket defaults untouched.</summary>
public sealed record TcpKeepAliveSettings(int IdleSeconds, int IntervalSeconds, int RetryCount)
{
    public bool Enabled => IdleSeconds > 0;

    public static TcpKeepAliveSettings From(JsonObject? values, JsonObject defaults, string? idleSecondsOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(idleSecondsOverride))
        {
            if (!int.TryParse(idleSecondsOverride, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deploymentIdle)
                || deploymentIdle is < 0 or > 3600)
                throw new RuntimeSetupException("ULTIMATETEAM_TCP_KEEPALIVE_IDLE_SECONDS must be a whole number between 0 and 3600.");
            values = values is null ? new JsonObject() : (JsonObject)values.DeepClone();
            values["idle_seconds"] = deploymentIdle;
        }
        var idle = LimitSettings.Read(values, defaults, "tcp_keepalive", "idle_seconds");
        var interval = LimitSettings.Read(values, defaults, "tcp_keepalive", "interval_seconds");
        var retries = LimitSettings.Read(values, defaults, "tcp_keepalive", "retry_count");
        if (idle is < 0 or > 3600)
            throw new RuntimeSetupException("config.json tcp_keepalive.idle_seconds must be between 0 and 3600 (0 disables the experiment).");
        if (interval is < 1 or > 300)
            throw new RuntimeSetupException("config.json tcp_keepalive.interval_seconds must be between 1 and 300.");
        if (retries is < 1 or > 10)
            throw new RuntimeSetupException("config.json tcp_keepalive.retry_count must be between 1 and 10.");
        return new TcpKeepAliveSettings((int)idle, (int)interval, (int)retries);
    }
}
