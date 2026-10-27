using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>One overall shutdown budget, including listener stop, background work and lease draining.</summary>
public sealed record ShutdownSettings(int DrainTimeoutMilliseconds)
{
    public TimeSpan DrainTimeout => TimeSpan.FromMilliseconds(DrainTimeoutMilliseconds);

    public static ShutdownSettings From(JsonObject? values, JsonObject defaults)
    {
        var timeout = LimitSettings.Read(values, defaults, "shutdown", "drain_timeout_ms");
        if (timeout is < 100 or > 300_000)
        {
            throw new RuntimeSetupException("config.json shutdown.drain_timeout_ms must be between 100 and 300000.");
        }
        return new ShutdownSettings((int)timeout);
    }
}
