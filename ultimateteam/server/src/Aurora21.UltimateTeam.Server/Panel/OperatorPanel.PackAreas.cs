using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Store;

namespace Aurora21.UltimateTeam.Server.Panel;

public sealed partial class OperatorPanel
{
    /// <summary>
    /// One pack definition (there is one kind of pack since migration 119). A read includes the whole row and catalogue
    /// revision for conflict detection. Hide takes a pack out of the store lineup; the definition stays.
    /// </summary>
    private sealed class PanelPackArea(GameContext? game = null) : IContentArea, IContentBaselineArea
    {
        public const string Area = "store_pack";
        public string Name => Area;
        public int ApplyOrder => 10;

        /// <summary>
        /// Columns migration 119 removed. A pack change staged or published before it still names them; they are ignored, so
        /// such a change can be published or reverted with its other fields.
        /// </summary>
        private static readonly HashSet<string> RemovedFields = new(StringComparer.Ordinal) { "special_chance", "banded_odds", "odds_table" };

        public JsonObject NormalizeKey(JsonObject key)
        {
            if (key["packId"] is not JsonValue value || !value.TryGetValue(out long id) || id is < 1 or > 32767)
                throw new ContentChangeException(400, "packId must be a FIFA 21 store pack id from 1 to 32767.");
            return new JsonObject { ["packId"] = id };
        }

        public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
        {
            var id = Id(key);
            var rows = await work.QueryAsync("""
                SELECT to_jsonb(p)::text FROM ultimateteam.store_packs p WHERE pack_id=@id AND kind='store'
                """, row => JsonNode.Parse(row.GetString(0))!.AsObject(), ("id", id));
            return rows.Count == 0 ? null : new JsonObject
            {
                ["revision"] = await PackRevisionAsync(work), ["pack"] = rows[0],
            };
        }

        public async ValueTask CheckBaselineAsync(IUnitOfWork work, ContentChange change)
        {
            if (change.Operation != ContentOperation.Insert) return;
            var stagedRevision = change.After?["revision"] is JsonValue value && value.TryGetValue(out string? text)
                ? text : null;
            if (stagedRevision != await PackRevisionAsync(work))
                throw new ContentChangeException(409, "The pack catalogue changed since this pack was staged; review it again.");
        }

        public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            var live = await ReadAsync(work, key);
            if (operation == ContentOperation.Hide)
            {
                if (live?["pack"]?["always_present"]?.GetValue<bool>() == true)
                    throw new ContentChangeException(409, "This built-in pack must stay available.");
                return;
            }
            if (after is null || after["revision"] is not JsonValue revision || !revision.TryGetValue(out string? _) ||
                after["pack"] is not JsonObject pack || after.Count != 2)
                throw new ContentChangeException(400, "A pack change needs its catalogue revision and pack fields.");
            if (operation == ContentOperation.Insert && live is not null)
                throw new ContentChangeException(409, "That pack already exists.");
            if (operation == ContentOperation.Update && live is null)
                throw new ContentChangeException(404, "No such pack.");
            var fields = Fields(pack, live?["pack"] as JsonObject);
            // A view-only pack keeps what it draws; how it is named, sold and shown can still change.
            if (game is not null && live is not null && game.Packs.GetPackConfig(Id(key)) is { Count: > 0 } config
                && ViewOnlyReason(Id(key), config) is { } viewOnly
                && fields.Keys.FirstOrDefault(name => !ViewOnlyEditableFields.Contains(name)) is { } locked)
                throw new ContentChangeException(400, $"{locked} cannot change: {viewOnly}");
            if ((pack["purchase_limit"]?.GetValue<long>() ?? 0) > 0 && (pack["community_limit"]?.GetValue<long>() ?? 0) > 0)
                throw new ContentChangeException(400, "Choose either a per-club purchase limit or a community limit, not both.");
            if (operation == ContentOperation.Insert && !fields.ContainsKey("name"))
                throw new ContentChangeException(400, "A new pack needs a name.");
            if (live?["pack"]?["always_present"]?.GetValue<bool>() == true &&
                fields.TryGetValue("admin_only", out var grantOnly) && Equals(grantOnly.Value, true))
                throw new ContentChangeException(409, "A required built-in pack cannot become grant-only.");
            if (game is not null)
                try
                {
                    PlayerPickDraw.Validate(game, pack, await PromoEdits.PromosAsync(work), live is null ? 0 : Id(key));
                    _ = MixedPackContents.Read(pack);
                    if (PackOpeningService.VanityPack(pack) is { } vanity)
                    {
                        if (game.Rules.Club.Vanity.PackIssue(vanity) is { } issue) throw new InvalidOperationException(issue);
                        // A My Stadium pack opens one item per slot, so its size is its slot count.
                        if (pack.Int("size", 12) != vanity.Arr("slots")!.Count)
                            throw new InvalidOperationException("A My Stadium pack's size must equal its number of slots.");
                    }
                }
                catch (InvalidOperationException error) { throw new ContentChangeException(400, error.Message); }
        }

        public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            var id = Id(key);
            if (operation == ContentOperation.Hide)
            {
                await work.ExecuteAsync("UPDATE ultimateteam.store_packs SET enabled=false WHERE pack_id=@id AND kind='store'", ("id", id));
                return;
            }
            var pack = (JsonObject)after!["pack"]!;
            var live = await ReadAsync(work, key);
            var fields = Fields(pack, live?["pack"] as JsonObject);
            // A new pack starts unassigned: out of the store lineup and named by no reward, with the special pack art.
            if (operation == ContentOperation.Insert)
                await work.ExecuteAsync("""
                    INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,store_group)
                    VALUES(@id,'store',false,'New Pack',0,12,100,0,0,1,'special')
                    """, ("id", id));
            // Apply all fields together so switching limit kinds never violates the row constraint mid-edit.
            if (fields.Count > 0)
            {
                var edits = fields.Values.ToArray();
                var assignments = edits.Select((field, index) => $"{field.Column}=@field{index}{(field.Value is JsonNode ? "::jsonb" : "")}");
                var parameters = edits.Select((field, index) => ($"field{index}", (object?)(field.Value is JsonNode json ? json.ToJsonString() : field.Value)))
                    .Append(("id", (object?)id)).ToArray();
                await work.ExecuteAsync($"UPDATE ultimateteam.store_packs SET {string.Join(",", assignments)} WHERE pack_id=@id", parameters);
            }
            try { ValidatePanelPack(await StorePackCatalog.LoadAsync(work), id); }
            catch (PanelError error) { throw new ContentChangeException(error.Status, error.Message); }
        }

        private static Dictionary<string, (string Column, object Value)> Fields(JsonObject after, JsonObject? before)
        {
            if (after.Any(pair => RemovedFields.Contains(pair.Key)) || (before is not null && !JsonNode.DeepEquals(after["kind"], before["kind"])))
            {
                after = after.DeepClone().AsObject();
                foreach (var removed in RemovedFields) after.Remove(removed);
                // A row from before migration 119 may still say "reward"; a pack's kind is never edited.
                if (before is not null && before.ContainsKey("kind")) after["kind"] = before["kind"]?.DeepClone();
            }
            // A column added after the change was staged (vanity_pack, migration 123) is still unset: it is not a removal.
            if (before is not null && before.Any(pair => pair.Value is null && !after.ContainsKey(pair.Key)))
            {
                after = after.DeepClone().AsObject();
                foreach (var (name, _) in before.Where(pair => pair.Value is null && !after.ContainsKey(pair.Key)).ToList()) after[name] = null;
            }
            if (before is not null && (after.Count != before.Count || before.Any(pair => !after.ContainsKey(pair.Key))))
                throw new ContentChangeException(400, "A pack edit cannot remove stored fields.");
            var changed = new JsonObject();
            foreach (var (name, value) in after)
            {
                if (before is not null && JsonNode.DeepEquals(value, before[name])) continue;
                if (!EditablePackFields.ContainsKey(name))
                    throw new ContentChangeException(400, $"{name} is not an editable FIFA 21 pack field.");
                changed[name] = value?.DeepClone();
            }
            using var json = JsonDocument.Parse(new JsonObject { ["pack"] = changed }.ToJsonString());
            try { return ReadPackFields(json.RootElement); }
            catch (PanelError error) { throw new ContentChangeException(error.Status, error.Message); }
        }

        private static long Id(JsonObject key) => key["packId"]!.GetValue<long>();
    }

    /// <summary>
    /// The store listing (migration 132), applied after staged pack definitions. Its value is
    /// <c>{"revision", "listing": [{"packId", "price", "enabled", "untradeable"}]}</c>: the listed packs, each switched on or off, with the
    /// tradeability a purchase opens with (null defaults to tradeable). A pack is sold only when it is
    /// listed and switched on (store_packs.enabled stays that "sold" flag). An older change holds <c>publicPackIds</c>: those
    /// packs listed and switched on, keeping any tradeability they had; History can still revert one.
    /// </summary>
    private sealed class PanelLineupArea : IContentArea
    {
        public const string Area = "store_lineup";
        public string Name => Area;
        public int ApplyOrder => 20;

        public JsonObject NormalizeKey(JsonObject key) => key["scope"] is JsonValue value &&
            value.TryGetValue(out string? scope) && scope == "public"
            ? new JsonObject { ["scope"] = "public" }
            : throw new ContentChangeException(400, "The store lineup key is {scope: public}.");

        public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) => new()
        {
            ["revision"] = await PackRevisionAsync(work),
            ["listing"] = Listing(await ListedAsync(work)),
        };

        /// <summary>The listed packs: id, switched on, purchase tradeability and coin price.</summary>
        public static async ValueTask<List<(long PackId, bool Enabled, bool? Untradeable, long Price)>> ListedAsync(IUnitOfWork work) =>
            await work.QueryAsync("""
                SELECT pack_id, coalesce(enabled,true), store_untradeable, coalesce(price,0) FROM ultimateteam.store_packs
                WHERE kind='store' AND coalesce(store_listed,false) AND NOT coalesce(admin_only,false) ORDER BY pack_id
                """, row => (row.GetInt64(0), row.GetBoolean(1), row.IsDBNull(2) ? (bool?)false : row.GetBoolean(2), row.GetInt64(3)));

        public static JsonArray Listing(IEnumerable<(long PackId, bool Enabled, bool? Untradeable, long Price)> entries) =>
            new(entries.OrderBy(entry => entry.PackId).Select(entry => (JsonNode?)new JsonObject
            {
                ["packId"] = entry.PackId, ["enabled"] = entry.Enabled, ["untradeable"] = entry.Untradeable, ["price"] = entry.Price,
            }).ToArray());

        public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            if (operation != ContentOperation.Update || after is null || after["revision"] is not JsonValue revision ||
                !revision.TryGetValue(out string? _) || after.Count != 2)
                throw new ContentChangeException(400, "A store lineup change needs a revision and the listing.");
            var listing = await EntriesAsync(work, after);
            var rows = await work.QueryAsync("""
                SELECT pack_id,always_present,coalesce(admin_only,false) FROM ultimateteam.store_packs WHERE kind='store'
                """, row => (Id: row.GetInt64(0), Locked: row.GetBoolean(1), GrantOnly: row.GetBoolean(2)));
            var on = listing.Where(entry => entry.Enabled).Select(entry => entry.PackId).ToHashSet();
            if (rows.Any(row => row.Locked && !on.Contains(row.Id)))
                throw new ContentChangeException(400, "A required built-in pack must stay listed and switched on.");
            if (rows.Any(row => row.GrantOnly && listing.Any(entry => entry.PackId == row.Id)))
                throw new ContentChangeException(400, "A grant-only pack cannot be listed in the store.");
            // Unknown ids may be packs being created in this same changeset. Apply checks them after those inserts.
        }

        public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            var listing = await EntriesAsync(work, after!);
            var listed = listing.Select(entry => entry.PackId).ToArray();
            var on = listing.Where(entry => entry.Enabled).Select(entry => entry.PackId).ToArray();
            var known = (await work.QueryAsync("SELECT pack_id FROM ultimateteam.store_packs WHERE kind='store'",
                row => row.GetInt64(0))).ToHashSet();
            if (listed.Any(id => !known.Contains(id)))
                throw new ContentChangeException(400, "The store listing includes a pack that does not exist.");
            if (listing.Any(entry => entry.Enabled && entry.Price <= 0))
                throw new ContentChangeException(400, "Set a coin price above zero before switching a regular store listing on.");
            await work.ExecuteAsync("""
                UPDATE ultimateteam.store_packs SET store_listed = pack_id = ANY(@listed), enabled = pack_id = ANY(@on),
                    store_untradeable = CASE WHEN pack_id = ANY(@untradeable) THEN true WHEN pack_id = ANY(@tradeable) THEN false END
                WHERE kind='store' AND NOT coalesce(admin_only,false)
                """, ("listed", listed), ("on", on),
                ("untradeable", listing.Where(entry => entry.Untradeable == true).Select(entry => entry.PackId).ToArray()),
                ("tradeable", listing.Where(entry => entry.Untradeable == false).Select(entry => entry.PackId).ToArray()));
            foreach (var entry in listing)
                await work.ExecuteAsync("UPDATE ultimateteam.store_packs SET price=@price,points_price=0,store_section=NULL WHERE pack_id=@id",
                    ("price", entry.Price), ("id", entry.PackId));
            _ = await StorePackCatalog.LoadAsync(work);
        }

        /// <summary>The listing a change holds: its listing, or an older change's publicPackIds (listed, switched on, tradeability kept).</summary>
        private static async ValueTask<List<(long PackId, bool Enabled, bool? Untradeable, long Price)>> EntriesAsync(IUnitOfWork work, JsonObject after)
        {
            var entries = new List<(long PackId, bool Enabled, bool? Untradeable, long Price)>();
            var seen = new HashSet<long>();
            var currentPrices = (await work.QueryAsync("SELECT pack_id,coalesce(price,0) FROM ultimateteam.store_packs",
                row => (Id: row.GetInt64(0), Price: row.GetInt64(1)))).ToDictionary(row => row.Id, row => row.Price);
            if (after["listing"] is JsonArray listing)
            {
                foreach (var node in listing)
                {
                    if (node is not JsonObject entry || entry.Count is not (3 or 4) || entry["packId"] is not JsonValue idValue || !idValue.TryGetValue(out long id)
                        || id is < 1 or > 32767 || !seen.Add(id) || entry["enabled"] is not JsonValue enabledValue || !enabledValue.TryGetValue(out bool enabled)
                        || !entry.ContainsKey("untradeable") || (entry["untradeable"] is { } trade && (trade is not JsonValue tradeValue || !tradeValue.TryGetValue(out bool _))))
                        throw new ContentChangeException(400, "Each listed pack is {packId, enabled, untradeable} with a unique FIFA 21 pack id, and untradeable true, false or null.");
                    var price = currentPrices.GetValueOrDefault(id);
                    if (entry.ContainsKey("price") && (entry["price"] is not JsonValue priceValue || !priceValue.TryGetValue(out price) || price <= 0 || price > MaximumBalance))
                        throw new ContentChangeException(400, "Each listing needs a positive whole coin price within the balance limit.");
                    entries.Add((id, enabled, entry["untradeable"]?.GetValue<bool>() ?? false, price));
                }
                return entries;
            }
            if (after["publicPackIds"] is not JsonArray ids)
                throw new ContentChangeException(400, "A store lineup change needs a revision and the listing.");
            var kept = (await ListedAsync(work)).ToDictionary(entry => entry.PackId, entry => entry.Untradeable);
            foreach (var node in ids)
            {
                if (node is not JsonValue value || !value.TryGetValue(out long id) || id is < 1 or > 32767 || !seen.Add(id))
                    throw new ContentChangeException(400, "Store pack ids must be unique positive FIFA 21 ids.");
                entries.Add((id, true, kept.GetValueOrDefault(id) ?? false, currentPrices.GetValueOrDefault(id)));
            }
            return entries;
        }
    }
}
