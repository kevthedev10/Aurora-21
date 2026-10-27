using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// The card editor's reads (live-content-editing-plan.md, step 5): a card's editable fields with the values the catalogue
/// allows, and the next free resource id for a new card based on it. Edits themselves are staged through the generic
/// content changeset route (area <c>player_card</c>) and published under Changes.
/// </summary>
public sealed partial class OperatorPanel
{
    private async Task<(int, object)?> CardEditorAsync(ApiRequest request)
    {
        if (request.Parts is ["cards", "editor", var text] && request.Method == "GET")
        {
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var resource))
                throw new PanelError(400, "A card is identified by its resource id.");
            return (200, await CardEditorViewAsync(resource, request.Aborted));
        }
        if (request.Parts is ["cards", "images", var assetText] && request.Method == "GET")
        {
            if (!long.TryParse(assetText, NumberStyles.None, CultureInfo.InvariantCulture, out var asset))
                throw new PanelError(400, "A player is identified by their asset id.");
            return (200, await PlayerImagesAsync(asset, request.Aborted));
        }
        if (request.Parts is ["cards", "new"] && request.Method == "POST")
        {
            if (!request.Body.TryGetProperty("baseResourceId", out var baseNode) || !baseNode.TryGetInt64(out var baseResource) || baseResource < 1)
                throw new PanelError(400, "baseResourceId must be the resource id of an existing card.");
            return (200, new { resourceId = await NextResourceAsync(baseResource, request.Aborted) });
        }
        return null;
    }

    private async Task<object> CardEditorViewAsync(long resource, CancellationToken token) => await game.UnitOfWork.RunReadAsync(0, async work =>
    {
        var area = new PlayerCardArea();
        var live = await area.ReadAsync(work, new JsonObject { ["resourceId"] = resource })
            ?? throw new PanelError(404, "No player card has that resource id.");
        var type = await work.ScalarAsync<long>("""
            SELECT p.card_type_id FROM ultimateteam.player_card_definitions p JOIN ultimateteam.item_definitions d ON d.definition_id=p.definition_id
            WHERE d.family_id=1 AND d.game_year=2021 AND d.native_resource_id=@resource
            """, ("resource", resource));
        async Task<string[]> Used(string column) => (await work.QueryAsync(
            $"SELECT DISTINCT {column} FROM ultimateteam.player_card_definitions WHERE {column} IS NOT NULL ORDER BY 1", row => row.GetString(0))).ToArray();
        var cardTypes = await work.QueryAsync("""
            SELECT t.card_type_id, t.name, t.card_level, r.native_rareflag FROM ultimateteam.card_types t
            JOIN ultimateteam.native_rarities r USING(native_rarity_id) ORDER BY r.native_rareflag, t.card_level, t.card_type_id
            """, row => (object)new { id = row.GetInt64(0), name = row.GetString(1), level = row.IsDBNull(2) ? null : row.GetString(2), rareflag = row.GetInt64(3) });
        var card = game.Players.Find(resource);
        return (object)new
        {
            resourceId = resource,
            assetId = card?.Int("assetId"),
            name = card?.Text("name"),
            cardTypeId = type,
            grantOnly = game.Cards.IsGrantOnly(resource),
            art = CardArt(resource),
            card = live["card"],
            options = new
            {
                positions = await Used("position"), feet = await Used("preferred_foot"),
                workRates = (await Used("work_rate_attack")).Union(await Used("work_rate_defense")).Order(StringComparer.Ordinal).ToArray(),
                cardTypes,
                traits = await work.QueryAsync("SELECT trait_id, name FROM ultimateteam.traits WHERE is_visible ORDER BY name",
                    // IDs and names come from migration 005's sourced native trait catalogue.
                    row => (object)new { id = row.GetInt64(0), name = row.GetString(1), appliesTo = row.GetInt64(0) switch
                    {
                        15 or 23 or 28 or 29 or 30 or 32 => "goalkeeper",
                        3 or 4 or 7 or 24 or 25 => "both",
                        _ => "outfield",
                    } }),
            },
            revisionSize = PlayerCatalog.ResourceRevisionSize,
        };
    }, token);

    /// <summary>The player's asset id in the lowest revision namespace no card, published or staged, uses yet.</summary>
    private async Task<long> NextResourceAsync(long baseResource, CancellationToken token)
    {
        var baseCard = game.Players.Find(baseResource) ?? throw new PanelError(404, "The base card does not exist.");
        var asset = baseCard.Int("assetId");
        if (asset <= 0) throw new PanelError(409, "The base card has no player asset id.");
        var staged = (await _changesets.OpenAsync(token))?.Changes
            .Where(change => change.Area == PlayerCardArea.AreaName && change.Operation == ContentOperation.Insert)
            .Select(change => change.Key["resourceId"]!.GetValue<long>()).ToHashSet() ?? [];
        var taken = await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync(
            "SELECT native_resource_id FROM ultimateteam.item_definitions WHERE family_id=1 AND native_resource_id % @size = @asset",
            row => row.GetInt64(0), ("size", PlayerCatalog.ResourceRevisionSize), ("asset", asset % PlayerCatalog.ResourceRevisionSize)), token);
        var used = taken.ToHashSet();
        var resource = asset + PlayerCatalog.ResourceRevisionSize;
        while (used.Contains(resource) || staged.Contains(resource)) resource += PlayerCatalog.ResourceRevisionSize;
        return resource;
    }
}
