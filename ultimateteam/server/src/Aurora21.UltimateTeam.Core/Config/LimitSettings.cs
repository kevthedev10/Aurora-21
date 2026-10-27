using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>Strict operational limits; absent keys use bundled defaults without rewriting preferences.</summary>
internal static class LimitSettings
{
    public static JsonObject? Section(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node))
        {
            return null;
        }
        return node as JsonObject ?? throw new RuntimeSetupException($"config.json {key} must be an object.");
    }

    public static long Read(JsonObject? values, JsonObject defaults, string section, string key)
    {
        var node = values is not null && values.TryGetPropertyValue(key, out var supplied) ? supplied : defaults.Get(key);
        return node is not null && JsonValues.IsStrictInt(node, out var value)
            ? value
            : throw new RuntimeSetupException($"config.json {section}.{key} must be an integer.");
    }
}
