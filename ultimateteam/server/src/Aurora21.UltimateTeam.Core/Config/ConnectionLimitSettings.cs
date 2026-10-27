using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>Hosted game-connection admission limits. Zero disables the latency limit.</summary>
public sealed record ConnectionLimitSettings(int MaximumPingMilliseconds)
{
    public static ConnectionLimitSettings From(JsonObject? values, JsonObject defaults)
    {
        var maximum = LimitSettings.Read(values, defaults, "connection_limits", "max_ping_ms");
        if (maximum is < 0 or > 60_000)
            throw new RuntimeSetupException("config.json connection_limits.max_ping_ms must be between 0 and 60000 (0 disables the limit).");
        return new ConnectionLimitSettings((int)maximum);
    }
}
