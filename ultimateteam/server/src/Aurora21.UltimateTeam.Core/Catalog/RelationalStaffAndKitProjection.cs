using System.Text.Json.Nodes;
using System.Text.Json;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Catalog;

internal static class RelationalStaffAndKitProjection
{
    internal static JsonObject Manager(ItemDefinitionRecord definition, ManagerDefinitionRecord manager,
        DefinitionExtensionRecord? extension)
    {
        var result = Extension(extension);
        result["name"] = definition.DisplayName;
        result["firstName"] = manager.FirstName;
        result["lastName"] = manager.LastName;
        result["assetId"] = definition.NativeAssetId;
        result["carddbid"] = definition.NativeCardDbId;
        // Existing JSON helpers read numbers backed by JsonElement, long, int or
        // double. Preserve numeric precision without introducing a decimal JsonValue.
        result["rating"] = JsonValue.Create(JsonSerializer.SerializeToElement(manager.Rating));
        result["rareflag"] = manager.NativeRareflag;
        result["leagueId"] = manager.NativeLeagueId;
        result["nation"] = manager.NativeNationId;
        result["teamid"] = 0; // Keep the existing manager response no-team code.
        result["negotiation"] = manager.Negotiation;
        return result;
    }

    internal static JsonObject Kit(ItemDefinitionRecord definition, CustomisationDefinitionRecord kit,
        DefinitionExtensionRecord? extension)
    {
        var result = Extension(extension);
        result["resourceId"] = definition.NativeResourceId?.Value;
        result["name"] = kit.Name;
        result["teamid"] = kit.NativeTeamId;
        result["kitType"] = kit.NativeKitType;
        return result;
    }

    private static JsonObject Extension(DefinitionExtensionRecord? extension) =>
        extension is null ? new JsonObject() : JsonNode.Parse(extension.Payload.Text) as JsonObject ?? new JsonObject();
}
