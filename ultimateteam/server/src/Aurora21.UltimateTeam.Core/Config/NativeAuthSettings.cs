using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// The session identity mode's lifetimes (ultimateteam/native-hosting-investigation.md gate 3): how long an issued FUT auth
/// code stays valid, and how long an <c>X-UT-SID</c> session lives without a request.
/// </summary>
public sealed record NativeAuthSettings(int CodeLifetimeSeconds, int SessionIdleSeconds)
{
    public static NativeAuthSettings From(JsonObject? values, JsonObject defaults)
    {
        var code = LimitSettings.Read(values, defaults, "native_auth", "code_lifetime_seconds");
        var idle = LimitSettings.Read(values, defaults, "native_auth", "session_idle_seconds");
        if (code is < 30 or > 86_400)
        {
            throw new RuntimeSetupException("config.json native_auth.code_lifetime_seconds must be between 30 and 86400.");
        }
        if (idle is < 60 or > 604_800)
        {
            throw new RuntimeSetupException("config.json native_auth.session_idle_seconds must be between 60 and 604800.");
        }
        return new NativeAuthSettings((int)code, (int)idle);
    }
}
