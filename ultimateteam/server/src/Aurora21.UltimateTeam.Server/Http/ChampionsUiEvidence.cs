using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>Only display ranges/counts from the response written to FIFA; never dump a club or its credentials.</summary>
internal static class ChampionsUiEvidence
{
    internal static string? Summarize(string rawTarget, FutResponse response)
    {
        var path = "/" + rawTarget.Split('?', 2)[0].TrimStart('/').ToLowerInvariant();
        if (path is not ("/ut/game/fifa21/champion/user/hub" or "/ut/game/fifa21/champion/leaderboard"
            or "/ut/game/fifa21/hub"))
        {
            return null;
        }

        var evidence = new JsonObject { ["path"] = path };
        try
        {
            var root = JsonNode.Parse(response.Body) as JsonObject;
            if (path.EndsWith("/champion/leaderboard", StringComparison.Ordinal))
            {
                evidence["entryCount"] = (root?["entries"] as JsonArray)?.Count;
            }
            else if (path.EndsWith("/champion/user/hub", StringComparison.Ordinal))
            {
                var leagues = new JsonArray();
                foreach (var key in new[] { "activeChampionLeagues", "previousChampionEvents" })
                {
                    if (root?[key] is not JsonArray events) continue;
                    foreach (var league in events.OfType<JsonObject>())
                    {
                        var summary = Tiers(league, "id");
                        summary["source"] = key;
                        leagues.Add(summary);
                    }
                }
                evidence["leagues"] = leagues;
            }
            else
            {
                evidence["currentChampionEvent"] = root?["championEvent"] is JsonObject champion
                    && champion["currentChampionEvent"] is JsonObject current
                    ? Tiers(current, "championEventId") : null;
            }
        }
        catch (JsonException)
        {
            evidence["parseError"] = true;
        }
        return evidence.ToJsonString();
    }

    private static JsonObject Tiers(JsonObject source, string eventKey)
    {
        var tiers = new JsonArray();
        if (source["prizeTiers"] is JsonArray values)
        {
            foreach (var tier in values.OfType<JsonObject>())
            {
                var type = tier["tierType"] is JsonValue value && value.TryGetValue<string>(out var text)
                    && text is "LB_RANKING_TYPE" or "NUMBER_OF_WINS_TYPE" ? text : "UNKNOWN";
                tiers.Add(new JsonObject
                {
                    ["tierType"] = type,
                    ["tierLevel"] = Number(tier["tierLevel"]),
                    ["tierStart"] = Number(tier["tierStart"]),
                    ["tierEnd"] = Number(tier["tierEnd"]),
                });
            }
        }
        return new JsonObject { ["eventId"] = Number(source[eventKey]), ["prizeTiers"] = tiers };
    }

    private static long? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}
