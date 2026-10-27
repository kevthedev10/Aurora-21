using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Item lookups, updates, quick sells, position modifiers and card definitions.</summary>
public sealed partial class ItemRoutes : FutRouteGroup
{
    /// <summary>The most items one idList lookup returns.</summary>
    private const int MaxLookupIds = 200;

    /// <summary>Where apply requests without an apply list name their target player.</summary>
    private static readonly string[] ApplyTargetKeys = { "applyTo", "targetItemId", "itemId", "id" };

    public ItemRoutes(FutServices fut) : base(fut)
    {
    }

    /// <summary>
    /// CardsDLL reads a bare top-level array of attribute metadata here. FIFA's details screen asks for a Pro card by its
    /// own id and by its base card's asset id: the base id gets the Pro card's published record only for a club that owns that Pro
    /// card, so every other club's copy of the ordinary base card keeps its real attributes.
    /// </summary>
    public async ValueTask<FutResponse?> AttributeMetadataAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/attributes/metadata", "GET"))
        {
            return null;
        }
        var cards = Context.Cards;
        var ids = r.Query.Ids("defIds").ToList();
        var proCards = ids.Select(cards.ProCardOfBase).OfType<long>().Distinct().ToArray();
        var owned = proCards.Length == 0 ? new HashSet<long>()
            : await Context.ReadAsync(uow => uow.OwnedDefinitionsAsync(proCards), cancellationToken);
        return FutResponse.NoStore(new JsonArray(ids.Select(id => (JsonNode)(cards.ProCardOfBase(id) is { } pro && owned.Contains(pro)
            ? cards.OwnedProMetadata(id)! : cards.Metadata(id))).ToArray()));
    }

    /// <summary>
    /// Position modifiers, chemistry styles and manager league cards apply by definition id. The reply is only the changed
    /// item: extra members such as consume/applyTo crash FIFA 21's fixed-map response parser.
    /// </summary>
    public async ValueTask<FutResponse?> ApplyConsumableAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (ItemResourcePattern().Match(r.Lower) is not { Success: true } match || !r.IsMethod("POST", "PUT"))
        {
            return null;
        }
        var body = r.Payload as JsonObject;
        var targets = ObjectOrList(body?.Get("apply"))
            .Select(row => row is JsonObject rowObject ? rowObject.GetOr("id", rowObject.Get("itemId")) : row)
            .Select(raw => JsonValues.TryInt(raw, out var id) ? (long?)id : null)
            .OfType<long>()
            .ToList();
        if (targets.Count == 0 && body is not null)
        {
            targets.AddRange(ApplyTargetKeys.Select(key => body.Int(key)).Where(id => id > 0).Take(1));
        }
        if (targets.Count == 0)
        {
            return FutResponse.NoStore(ApplyRejected("missing position modifier target"), 400);
        }
        var resourceId = ParseLong(match);
        var target = targets[0];
        try
        {
            var changed = await Fut.Inventory.ApplyConsumableAsync(resourceId, target, cancellationToken);
            Log.LogInformation("Applied consumable resource={Resource} target={Target} position={Position} playStyle={PlayStyle} leagueId={League}",
                resourceId, target, changed.Get("preferredPosition"), changed.Get("playStyle"), changed.Get("leagueId"));
            return FutResponse.NoStore(ItemData(new[] { Fut.Native.PackItem(changed) }));
        }
        catch (ClubRuleException error)
        {
            Log.LogWarning("Consumable rejected resource={Resource} target={Target} reason={Reason}", resourceId, target, error.Message);
            return FutResponse.NoStore(ApplyRejected(error.Message), 400);
        }
    }

    private static JsonObject ApplyRejected(string reason) => new() { ["reason"] = reason, ["itemData"] = new JsonArray(), ["consume"] = false };

    /// <summary>New Items resolves the owned side of duplicates by idList or by instance URL.</summary>
    public async ValueTask<FutResponse?> LookupsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (Is(r, $"{Game}/item", "GET"))
        {
            var ids = r.Query.Ids("idList", MaxLookupIds).ToList();
            var items = (await Context.ReadAsync(uow => uow.GetItemsAsync(ids), cancellationToken)).Select(Fut.Native.PackItem);
            return FutResponse.Json(ItemData(items));
        }
        if (ItemIdPattern().Match(r.Lower) is { Success: true } single && r.Method == "GET")
        {
            return await Context.ReadAsync(uow => uow.GetItemAsync(ParseLong(single)), cancellationToken) is { } item
                ? FutResponse.NoStore(ItemData(new[] { Fut.Native.PackItem(item) }))
                : FutResponse.Reason(404, "item not found");
        }
        return null;
    }

    public async ValueTask<FutResponse?> DiscardsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (ItemIdPattern().Match(r.Lower) is { Success: true } single && r.Method == "DELETE")
        {
            return await DiscardedAsync(new JsonNode?[] { JsonValue.Create(ParseLong(single)) }, cancellationToken);
        }
        if (!Is(r, "/ut/delete/game/fifa21/item", "POST"))
        {
            return null;
        }
        var ids = new List<JsonNode?>();
        if (r.Payload is JsonObject body)
        {
            var itemIds = body.GetOr("itemId", new JsonArray());
            ids.AddRange(itemIds is JsonArray list ? list : new[] { itemIds });
            // Tolerate builds that send itemData instead.
            ids.AddRange(ObjectOrList(body.GetOr("itemData", new JsonArray()))
                .OfType<JsonObject>()
                .Select(entry => JsonValues.HasValue(entry.Get("id")) ? entry.Get("id") : entry.Get("itemId")));
        }
        return await DiscardedAsync(ids, cancellationToken);
    }

    public async ValueTask<FutResponse?> UpdatesAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/item", "PUT", "POST"))
        {
            return null;
        }
        Log.LogInformation("FUT item update method={Method} payload={Payload}", r.Method, FutJson.Serialize(r.Payload));
        return FutResponse.Json(await Fut.Inventory.ApplyItemUpdatesAsync(r.Payload, cancellationToken));
    }

    /// <summary>
    /// GET /defid in any UT namespace: every card variant of <c>defId</c> as concept cards, or, without
    /// a defId, the squad screen's concept player search.
    /// </summary>
    public FutResponse? Definitions(FutRequest r)
    {
        if (!GameSuffix(r).StartsWith("/defid", StringComparison.Ordinal))
        {
            return null;
        }
        if (r.Query.First("defId") is not { Length: > 0 } defIdText)
        {
            return FutResponse.Json(ItemData(Fut.Native.ConceptSearch(r.Query)));
        }
        var defId = long.TryParse(defIdText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return FutResponse.Json(ItemData(Fut.Native.DefinitionVariants(defId)));
    }

    private async ValueTask<FutResponse> DiscardedAsync(IEnumerable<JsonNode?> rawIds, CancellationToken cancellationToken)
    {
        var (credits, deleted) = await Fut.Inventory.DiscardItemsAsync(rawIds, cancellationToken);
        return FutResponse.Json(Template("items/discarded", new TemplateArgs
        {
            ["credits"] = credits,
            ["items"] = new JsonArray(deleted.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()),
        }));
    }

    private static JsonObject ItemData(IEnumerable<JsonObject> items) => new() { ["itemData"] = ClubPresentationService.ToArray(items) };

    /// <summary>Entries sent either as one object or as a list; anything else holds none.</summary>
    private static IEnumerable<JsonNode?> ObjectOrList(JsonNode? value) => value switch
    {
        JsonArray list => list,
        JsonObject one => new JsonNode?[] { one },
        _ => Array.Empty<JsonNode?>(),
    };

    [GeneratedRegex(@"^/ut/game/fifa21/item/(\d+)$")]
    private static partial Regex ItemIdPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/item/resource/(\d+)/?$")]
    private static partial Regex ItemResourcePattern();
}
