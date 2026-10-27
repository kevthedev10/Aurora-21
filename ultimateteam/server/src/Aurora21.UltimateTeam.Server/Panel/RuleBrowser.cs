using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Presentation only: dedicated editors still read these paths directly, and legacy required values stay in storage.
/// Prefixes hide entire objects and arrays, including their flattened children.
/// </summary>
internal static class RuleBrowser
{
    private static readonly Dictionary<string, string[][]> Hidden = new()
    {
        // Entire files already have dedicated editors.
        ["sbc"] = [[]], ["objectives"] = [[]], ["champions_rewards"] = [[]],
        ["draft"] = [[]],
        ["tradeability"] = [["sources", "store"], ["sourcePacks", "store"]],
        ["seasons"] = [
            ["weekendLeague", "topX"], ["weekendLeague", "maxMatches"],
            ["matchCompletionRewards", "rivals"], ["matchCompletionRewards", "weekendLeague"],
            ["matchCompletionRewards", "squadBattles"], ["matchCompletionRewards", "draftOnline"], ["matchCompletionRewards", "draftSinglePlayer"],
        ],
        ["rivals_rewards"] = [["weeklyScore"], ["rankShares"], ["poolBelowClubs"], ["divisions"], ["promotion"]],
        ["sqbt_rewards"] = [["mode"], ["minPointsToQualify"], ["rankedTiers"], ["tiers"], ["leaderboardBands"]],
        ["squad_battles"] = [
            ["competition", "boardSize"],
            // The competition calendar and maxGames replaced the old always-open event window.
            ["matchesPerEvent"], ["eventStartedDaysAgo"], ["eventEndsInDays"], ["_eventComment"],
        ],
        ["cards"] = [["rareRarityGroup"], ["legacyPositionModifierIds"], ["aquaProAttributeValue"]],
        // Reference inventories, not runtime controls.
        ["club"] = [["vanity", "unassignedTifos"], ["_unassignedTifosComment"]],
        ["pack_odds"] = [["goldRareBands"], ["_bandsComment"]],
        ["sqbt_totw"] = [["note"]],
    };

    public static bool Visible(string file, JsonArray path)
    {
        if (path.Count == 0) return false;
        if (Text(path[0]) == "version") return false; // Document metadata, not gameplay settings.
        if (file == "sqbt_totw" && path.Count >= 3 && Text(path[0]) == "weeks"
            && Text(path[2]) is "released" or "xiSource") return false; // Source provenance.
        return !Hidden.TryGetValue(file, out var prefixes) || !prefixes.Any(prefix =>
            prefix.Length <= path.Count && prefix.Select((part, index) => Text(path[index]) == part).All(match => match));
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
