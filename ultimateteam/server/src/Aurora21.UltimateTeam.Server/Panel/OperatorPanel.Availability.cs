using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;

namespace Aurora21.UltimateTeam.Server.Panel;

public sealed partial class OperatorPanel
{
    private static bool? AvailabilityEnabled(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : null;

    // Definition edits remain immediate. New availability must use a per-item window so expiry can turn it off.
    private async Task RequireAvailabilityWindowAsync(ApiRequest request, string area, JsonObject key, JsonObject? after)
    {
        if (after is null) return;
        if (area == "content_window" && AvailabilityEnabled(after["enabled"]) == true)
        {
            await RequireDropWindowAsync(request);
            return;
        }
        var objectiveLive = area == "rules" && key["file"]?.ToString() == "objectives"
            && key["path"] is JsonArray path && path.FirstOrDefault()?.ToString() == "live";
        if (area is not ("store_lineup" or "token_store_lineup" or "sbc_live" or "store_pack") && !objectiveLive) return;
        var before = await game.UnitOfWork.RunReadAsync(0, work => PanelContentAreas(game, options.ContentAreas).First(candidate => candidate.Name == area).ReadAsync(work, key), request.Aborted);
        if (area == "store_pack")
        {
            var old = before?["pack"];
            var next = after["pack"];
            if (AvailabilityEnabled(next?["store_listed"]) == true && AvailabilityEnabled(next?["enabled"]) != false
                && (AvailabilityEnabled(old?["store_listed"]) != true || AvailabilityEnabled(old?["enabled"]) == false))
                throw new PanelError(422, "Set this pack's start and end dates in Store Lineup before putting it on sale.");
            return;
        }
        if (objectiveLive && ((JsonArray)key["path"]!).Count != 1)
            throw new PanelError(422, "Manage objective availability through a dated release.");
        var field = objectiveLive ? "value" : "listing";
        var idField = objectiveLive ? "group" : area == "sbc_live" ? "setKey" : "packId";
        var enabled = (before?[field] as JsonArray)?.OfType<JsonObject>().Where(row => AvailabilityEnabled(row["enabled"]) == true)
            .Select(row => row[idField]?.ToString()).ToHashSet() ?? [];
        if (objectiveLive && before?[field] is null)
            enabled = game.Live.Current.Rules.Objectives.LiveGroupIds.Select(id => (string?)id.ToString()).ToHashSet();
        if ((after[field] as JsonArray)?.OfType<JsonObject>().Any(row => AvailabilityEnabled(row["enabled"]) == true && !enabled.Contains(row[idField]?.ToString())) == true)
            await RequireDropWindowAsync(request);
    }

    private async Task RequireDropWindowAsync(ApiRequest request)
    {
        if (!long.TryParse(request.Query("dropId"), out var id))
            throw new PanelError(422, "Choose a start and end date to release content. Existing live content can still be edited or removed.");
        var drop = await Schedule.GetDropAsync(id, request.Aborted);
        if (drop.EndsAt is null || drop.EndsAt <= drop.StartsAt)
            throw new PanelError(422, "This release needs a start and end date.");
    }
}
