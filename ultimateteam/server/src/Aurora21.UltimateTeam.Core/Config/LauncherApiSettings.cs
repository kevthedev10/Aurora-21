using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// What the hosted launcher API (<c>/client/*</c>) says and how often it issues codes: the server's name on the
/// launcher page, the lines the launcher shows when a player cannot play, and the per-address launch limit. The
/// maintenance line set in the operator panel replaces <see cref="MaintenanceMessage"/> while maintenance is on.
/// </summary>
public sealed record LauncherApiSettings(string ServerName, int LaunchesPerAddress, int LaunchWindowSeconds,
    string MaintenanceMessage, string FullMessage, string BlockedMessage, string NoRoleMessage, string RolesUnavailableMessage,
    string NotConfiguredMessage)
{
    public static LauncherApiSettings From(JsonObject? values, JsonObject defaults)
    {
        var launches = LimitSettings.Read(values, defaults, "launcher_api", "launches_per_address");
        var window = LimitSettings.Read(values, defaults, "launcher_api", "launch_window_seconds");
        if (launches is < 1 or > 10_000)
        {
            throw new RuntimeSetupException("config.json launcher_api.launches_per_address must be between 1 and 10000.");
        }
        if (window is < 1 or > 86_400)
        {
            throw new RuntimeSetupException("config.json launcher_api.launch_window_seconds must be between 1 and 86400.");
        }
        var messages = LimitSettings.Section(values ?? new JsonObject(), "messages");
        var defaultMessages = LimitSettings.Section(defaults, "messages")
            ?? throw new InvalidDataException("The bundled config.example.json launcher_api has no messages");
        return new LauncherApiSettings(Text(values, defaults, "server_name", "launcher_api.server_name"), (int)launches, (int)window,
            Text(messages, defaultMessages, "maintenance", "launcher_api.messages.maintenance"),
            Text(messages, defaultMessages, "full", "launcher_api.messages.full"),
            Text(messages, defaultMessages, "blocked", "launcher_api.messages.blocked"),
            Text(messages, defaultMessages, "no_role", "launcher_api.messages.no_role"),
            Text(messages, defaultMessages, "roles_unavailable", "launcher_api.messages.roles_unavailable"),
            Text(messages, defaultMessages, "not_configured", "launcher_api.messages.not_configured"));
    }

    private static string Text(JsonObject? values, JsonObject defaults, string key, string name)
    {
        var node = values is not null && values.TryGetPropertyValue(key, out var supplied) ? supplied : defaults[key];
        return node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length is > 0 and <= 300
            && !text.Any(char.IsControl)
            ? text.Trim()
            : throw new RuntimeSetupException($"config.json {name} must be one line of 1 to 300 characters.");
    }
}
