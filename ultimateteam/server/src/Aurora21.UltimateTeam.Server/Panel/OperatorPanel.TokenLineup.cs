using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Server.Panel;

public sealed partial class OperatorPanel
{
    private async Task<object> PublishTokenLineupAsync(ApiRequest request)
    {
        var revision = RequiredRevision(request.Body);
        if (!request.Body.TryGetProperty("listing", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 32767)
            throw new PanelError(400, "listing must be an array of {packId, price, enabled, untradeable}.");
        var listing = JsonNode.Parse(entries.GetRawText())!.AsArray();
        // Validate every node before sorting, so invalid nodes cannot silently disappear.
        PanelTokenLineupArea.Entries(listing);
        var currentRevision = await game.UnitOfWork.RunReadAsync(0, PackRevisionAsync, request.Aborted);
        if (currentRevision != revision) throw new PanelError(409, "The pack catalogue changed. Refresh before saving.");
        var sorted = new JsonArray(listing.OfType<JsonObject>().OrderBy(entry => entry["packId"]!.GetValue<long>())
            .Select(entry => entry.DeepClone()).ToArray());
        await RequireAvailabilityWindowAsync(request, PanelTokenLineupArea.Area, new JsonObject { ["scope"] = "public" }, new JsonObject { ["listing"] = sorted.DeepClone() });
        var staged = await (await RequestChangesetsAsync(request)).SetChangeAsync(PanelTokenLineupArea.Area, new JsonObject { ["scope"] = "public" },
            ContentOperation.Update, new JsonObject { ["revision"] = revision, ["listing"] = sorted }, null, request.Aborted);
        return new { listing = sorted, pending = staged?.Status == "published" ? 0 : staged?.Changes.Count ?? 0, applies = staged?.Status == "published" ? "live" : "after-review" };
    }

    private sealed class PanelTokenLineupArea : IContentArea
    {
        public const string Area = "token_store_lineup";
        public string Name => Area;
        public int ApplyOrder => 20;
        public JsonObject NormalizeKey(JsonObject key) => new PanelLineupArea().NormalizeKey(key);
        public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) => new()
        {
            ["revision"] = await PackRevisionAsync(work), ["listing"] = Listing(await work.TokenListingsAsync()),
        };
        public static JsonArray Listing(IEnumerable<TokenStoreRepository.Listing> entries) => new(entries.OrderBy(entry => entry.PackId)
            .Select(entry => (JsonNode?)new JsonObject
            {
                ["packId"] = entry.PackId, ["price"] = entry.Price, ["enabled"] = entry.Enabled, ["untradeable"] = entry.Untradeable ?? true,
            }).ToArray());

        public static List<TokenStoreRepository.Listing> Entries(JsonArray listing)
        {
            var entries = new List<TokenStoreRepository.Listing>();
            var seen = new HashSet<long>();
            foreach (var node in listing)
            {
                if (node is not JsonObject entry || entry.Count != 4 || entry["packId"] is not JsonValue idNode || !idNode.TryGetValue(out long id)
                    || id is < 1 or > 32767 || !seen.Add(id) || entry["price"] is not JsonValue priceNode || !priceNode.TryGetValue(out long price)
                    || price <= 0 || price > MaximumBalance || entry["enabled"] is not JsonValue enabledNode || !enabledNode.TryGetValue(out bool enabled)
                    || !entry.ContainsKey("untradeable") || entry["untradeable"] is { } trade && (trade is not JsonValue tradeNode || !tradeNode.TryGetValue(out bool _)))
                    throw new ContentChangeException(400, "Each token listing needs a unique packId, positive whole token price, enabled boolean and untradeable true, false or null.");
                entries.Add(new(id, 0, price, enabled, entry["untradeable"]?.GetValue<bool>() ?? true));
            }
            return entries;
        }
        public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            if (operation != ContentOperation.Update || after is null || after.Count != 2 || after["revision"] is not JsonValue revision
                || !revision.TryGetValue(out string? _) || after["listing"] is not JsonArray listing)
                throw new ContentChangeException(400, "A token lineup change needs a revision and listing.");
            var ids = Entries(listing).Select(entry => entry.PackId).ToArray();
            if (await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.store_packs WHERE pack_id=ANY(@ids) AND coalesce(admin_only,false))", ("ids", ids)))
                throw new ContentChangeException(400, "A grant-only pack cannot be listed in the token store.");
            // A definition can be inserted in the same changeset; existence is checked after those inserts.
        }
        public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            var entries = Entries(after!["listing"]!.AsArray());
            var ids = entries.Select(entry => entry.PackId).ToArray();
            var known = (await work.QueryAsync("SELECT pack_id FROM ultimateteam.store_packs WHERE kind='store' AND NOT coalesce(admin_only,false)",
                row => row.GetInt64(0))).ToHashSet();
            if (ids.Any(id => !known.Contains(id))) throw new ContentChangeException(400, "The token listing includes a missing or grant-only pack.");
            await work.ExecuteAsync("UPDATE ultimateteam.token_store_listings SET listed=false,enabled=false WHERE NOT(pack_id=ANY(@ids))", ("ids", ids));
            foreach (var entry in entries)
            {
                var existing = await work.ScalarAsync<long>("SELECT coalesce((SELECT offer_id FROM ultimateteam.token_store_listings WHERE pack_id=@id),0)", ("id", entry.PackId));
                var offerId = existing != 0 ? existing : await work.ScalarAsync<long>("""
                    SELECT greatest(coalesce((SELECT max(pack_id) FROM ultimateteam.store_packs),0),
                        coalesce((SELECT max(pack_id) FROM ultimateteam.owned_legacy_pack_definitions),0),
                        coalesce((SELECT max(offer_id) FROM ultimateteam.token_store_listings),0),
                        coalesce((SELECT max(pack_id) FROM ultimateteam.token_store_listings),0),
                        (SELECT last_value FROM ultimateteam.content_store_ids))+1
                    """);
                if (offerId > 32767) throw new ContentChangeException(409, "There is no free FIFA 21 store offer id below 32768.");
                await work.ExecuteAsync("""
                    INSERT INTO ultimateteam.token_store_listings(pack_id,offer_id,price,enabled,untradeable,listed)
                    VALUES(@id,@offer,@price,@enabled,@trade,true)
                    ON CONFLICT(pack_id) DO UPDATE SET price=EXCLUDED.price,enabled=EXCLUDED.enabled,untradeable=EXCLUDED.untradeable,listed=true
                    """, ("id", entry.PackId), ("offer", (int)offerId), ("price", entry.Price), ("enabled", entry.Enabled), ("trade", entry.Untradeable));
            }
            _ = await StorePackCatalog.LoadAsync(work);
        }
    }
}
