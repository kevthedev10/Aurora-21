using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>Bounded, allowlisted evidence of received scoring events, independent of rewards and live objectives.</summary>
internal static class MatchScoringEvidence
{
    internal const int MaxPlayers = 23;
    internal const int MaxEvents = 128;
    internal const int MaxAttributes = 64;

    internal static long[] ItemIds(JsonObject body) => (body.Arr("items") ?? []).Take(MaxPlayers).OfType<JsonObject>()
        .Select(row => row.Int("id")).Where(id => id > 0).Distinct().ToArray();

    internal static JsonObject Summarize(JsonObject body, IReadOnlyDictionary<long, JsonObject> owned)
    {
        var items = body.Arr("items") ?? [];
        var input = body.Arr("events") ?? [];
        var events = new JsonArray();
        foreach (var (node, index) in input.Take(MaxEvents).Select((node, index) => (node, index)))
        {
            var attributes = (node as JsonObject)?.Arr("attributes") ?? [];
            var attrs = attributes.Take(MaxAttributes).OfType<JsonObject>().ToArray();
            var name = attrs.Where(attr => Is(attr.Get("key"), "name")).Select(attr => attr.Get("value")).ToArray();
            var players = attrs.Where(attr => Is(attr.Get("key"), "player_id")).ToArray();
            var flags = attrs.Where(attr => Is(attr.Get("key"), "flag")).ToArray();
            events.Add(new JsonObject
            {
                ["index"] = index,
                ["name"] = name.Length == 1 && Is(name[0], "goal") ? "goal"
                    : name.Length == 1 && Is(name[0], "assist") ? "assist" : "unknown",
                ["nameFields"] = name.Length,
                ["playerIdFields"] = players.Length,
                ["playerId"] = players.Length == 1 ? Number(players[0].Get("value")) : null,
                ["flags"] = new JsonArray(flags.Select(attr => (JsonNode?)JsonValue.Create(Number(attr.Get("value")))).ToArray()),
                ["attributeCount"] = attributes.Count,
                ["attributesTruncated"] = attributes.Count > MaxAttributes,
            });
        }
        var rows = new JsonArray();
        foreach (var (node, index) in items.Take(MaxPlayers).Select((node,index)=>(node,index)))
        {
            if (node is not JsonObject row) continue;
            var id = Number(row.Get("id"));
            var meta = id is { } itemId ? owned.GetValueOrDefault(itemId) : null;
            rows.Add(new JsonObject
            {
                ["slot"] = index, ["itemId"] = id, ["owned"] = meta is not null,
                ["resourceId"] = Number(meta?.Get("resourceId")), ["assetId"] = Number(meta?.Get("assetId")),
                ["leagueId"] = Number(meta?.Get("leagueId")), ["nation"] = Number(meta?.Get("nation")),
                ["goals"] = Number(row.Get("goals")), ["assists"] = Number(row.Get("assists")),
            });
        }
        return new JsonObject
        {
            ["source"] = "received-match-end", ["eventsPresent"] = body.Get("events") is not null,
            ["eventCount"] = input.Count, ["eventsTruncated"] = input.Count > MaxEvents, ["events"] = events,
            ["reportedGoals"] = Number(body.Obj("myMatchStats")?.Get("goals")),
            ["itemCount"] = items.Count, ["itemsTruncated"] = items.Count > MaxPlayers, ["players"] = rows,
        };
    }

    // Never serialize arbitrary fields or strings: tokens, headers, names, persona/club IDs and unknown attributes stay out.
    private static bool Is(JsonNode? node, string expected) => node is JsonValue value
        && value.TryGetValue<string>(out var text) && text == expected;

    private static long? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        var text = value.GetValueKind() == System.Text.Json.JsonValueKind.Number ? value.ToJsonString()
            : value.TryGetValue<string>(out var stringValue) ? stringValue : null;
        return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number)
            ? number : null;
    }
}
