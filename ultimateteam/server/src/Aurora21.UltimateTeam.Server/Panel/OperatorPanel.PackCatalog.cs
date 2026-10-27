using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Store;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>Reviewed edits to FIFA 21's PostgreSQL pack definitions and published store set.</summary>
public sealed partial class OperatorPanel
{
    /// <summary>Serialises pack catalogue edits, deletes and content publishes that republish the live pack rules.</summary>
    private readonly SemaphoreSlim _catalogEdits = new(1, 1);

    private static readonly Dictionary<string, (string Column, string Kind, decimal Max)> EditablePackFields = new(StringComparer.Ordinal)
    {
        ["name"] = ("name", "text", 100), ["bio"] = ("bio", "text", 500),
        ["price"] = ("price", "long", MaximumBalance), ["points_price"] = ("points_price", "long", MaximumBalance),
        ["purchase_limit"] = ("purchase_limit", "int", 1000000),
        ["community_limit"] = ("community_limit", "int", 999999),
        ["size"] = ("size", "int", 100), ["gold_weight"] = ("gold_weight", "int", 100),
        ["silver_weight"] = ("silver_weight", "int", 100), ["bronze_weight"] = ("bronze_weight", "int", 100),
        ["rare_slots"] = ("rare_slots", "int", 100), ["min_rating"] = ("min_rating", "int", 99),
        ["max_rating"] = ("max_rating", "int", 99), ["guaranteed_specials"] = ("guaranteed_specials", "int", 100),
        ["manager_chance"] = ("manager_chance", "chance", 1),
        ["chem_style_chance"] = ("chem_style_chance", "chance", 1), ["store_group"] = ("store_group", "text", 40),
        ["store_section"] = ("store_section", "text", 20),
        ["promo_only"] = ("promo_only", "bool", 0), ["admin_only"] = ("admin_only", "bool", 0),
        ["allow_unweighted_specials"] = ("allow_unweighted_specials", "bool", 0),
        ["enabled"] = ("enabled", "bool", 0),
        ["player_picks"] = ("player_picks", "json-array", 100),
        ["player_pick_count"] = ("player_pick_count", "int", 5),
        ["bonus_player_count"] = ("bonus_player_count", "int", 100),
        ["bonus_min_rating"] = ("bonus_min_rating", "int", 99),
        ["bonus_promo_only"] = ("bonus_promo_only", "bool", 0),
        ["include_general_pool"] = ("include_general_pool", "bool", 0),
        ["promo_codes"] = ("promo_codes", "codes", 200),
        ["category_chances"] = ("category_chances", "json-object", 100),
        ["mixed_contents"] = ("mixed_contents", "json-object", 100),
        // A My Stadium pack's slots (migration 123; VanityRules.PackIssue checks it); {} makes it a player pack again.
        ["vanity_pack"] = ("vanity_pack", "json-object", 100),
    };

    private async Task<(int, object)?> PackCatalogRouteAsync(ApiRequest request)
    {
        try
        {
            var parts = request.Parts;
            if (parts[1] == "simulate" && parts.Length == 2 && request.Method == "POST")
                return (200, await SimulatePacksAsync(request.Body, request.Aborted, request));
            if (parts[1] == "catalog")
            {
                if (parts.Length == 2 && request.Method == "GET") return (200, await CatalogAsync(request.Aborted, request));
                if (parts.Length == 2 && request.Method == "POST") return (201, await ChangePackAsync(request, null, false));
                if (parts.Length == 3 && parts[2] == "test" && request.Method == "POST") return (200, await PreviewPackAsync(request));
                if (parts.Length == 4 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var deleteId) && deleteId > 0)
                {
                    if (parts[3] == "delete-check" && request.Method == "GET") return (200, await PackDeleteCheckAsync(deleteId, request.Aborted));
                    if (parts[3] == "delete" && request.Method == "POST") return (200, await StagePackDeleteAsync(request, deleteId));
                }
                if (parts.Length == 3 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    if (request.Method == "PUT") return (200, await ChangePackAsync(request, id, false));
                    // DELETE only hides: a hidden pack stays in the catalogue because a club may already own one. Removing the
                    // definition is POST {id}/delete (OperatorPanel.PackDelete.cs).
                    if (request.Method == "DELETE") return (200, await ChangePackAsync(request, id, true));
                }
            }
            if (parts[1] == "tokens-lineup" && parts.Length == 2 && request.Method == "PUT")
                return (200, await PublishTokenLineupAsync(request));
            if (parts[1] == "lineup" && parts.Length == 2 && request.Method == "PUT")
                return (200, await PublishLineupAsync(request));
            throw new PanelError(405, "Unknown pack catalogue operation or method.");
        }
        catch (ContentChangeException error) { throw new PanelError(error.Status, error.Message); }
    }

    private async Task<object> CatalogAsync(CancellationToken token,ApiRequest? request=null)
    {
        if(request?.Query("dropId") is { Length:>0 } text && long.TryParse(text,out var id))
        {
            var drop=await Schedule.GetDropAsync(id,token);
            return await game.UnitOfWork.RunReadAsync(0,async work=>
            {
                var revision=await PackRevisionAsync(work);
                return await Schedule.DraftReadAsync(work,drop.ChangesetId,projected=>CatalogWithinAsync(projected,token,revision));
            },token);
        }
        return await game.UnitOfWork.RunReadAsync(0,async work=>await CatalogWithinAsync(work,token),token);
    }
    private async Task<object> CatalogWithinAsync(Aurora21.UltimateTeam.Core.Data.IUnitOfWork work,CancellationToken token,string? revisionOverride=null)
    {
        var packs = await work.StorePacksAsync();
        var catalog = await work.LoadCatalogSnapshotAsync();
        var flags = await work.QueryAsync("""
            SELECT pack_id,always_present,admin_only FROM ultimateteam.store_packs WHERE kind='store' AND pack_id <> 544 ORDER BY pack_id
            """, row => (Id: row.GetInt64(0), Locked: row.GetBoolean(1), GrantOnly: row.GetBoolean(2)));
        // A pack is used by rewards while a published rule or a mode reward (migration 126) names it. Tradeability only says
        // how a pack is granted, so it does not count (it still blocks deleting a pack, OperatorPanel.PackDelete.cs).
        var rules = (await RuleRows.DocumentsAsync(work)).Where(pair => pair.Key != "tradeability")
            .Select(pair => (File: pair.Key, Document: JsonNode.Parse(pair.Value))).ToArray();
        var rewardUses = await ModeRewardRepository.PackUsesAsync(work);
        var sbcUses = game.Sbc.PackUses();
        var lineup = flags.Where(row => !row.GrantOnly && packs.Obj(row.Id.ToString(CultureInfo.InvariantCulture))?.Bool("enabled", true) == true)
            .Select(row => row.Id).ToArray();
        var artNames = game.Packs.Rules.StoreGroupArtIds.GroupBy(pair => pair.Value)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).Min(StringComparer.Ordinal)!);
        var listed = (await PanelLineupArea.ListedAsync(work)).ToDictionary(entry => entry.PackId);
        var tokenListings = await work.TokenListingsAsync();
        return (object)new
        {
            tokenListing = PanelTokenLineupArea.Listing(tokenListings),
            revision = revisionOverride ?? await PackRevisionAsync(work),
            // The store listing (migration 132): listed packs, each switched on or off, with a purchase's tradeability.
            listing = PanelLineupArea.Listing(listed.Values),
            // Pack art: the store categories, in art order (bronze, silver, gold, special), then legacy aliases.
            groups = game.Packs.Rules.StoreGroupArtIds.OrderBy(pair => pair.Value).Select(pair => pair.Key)
                .Concat(game.Packs.Rules.StoreGroupAliases.Keys.Order(StringComparer.Ordinal)).ToArray(),
            promos = catalog.Promos.Where(promo => promo.Status == "active" && promo.Code is not ("fut_champions" or "developer_cards"))
                .Select(promo => new { code = promo.Code, name = promo.Name, category = promo.OddsCategory }).OrderBy(promo => promo.name).ToArray(),
            pickLimits = new { minChoices = game.Rules.Cards.PlayerPickToken.MinChoices, maxChoices = game.Rules.Cards.PlayerPickToken.MaxChoices,
                maxTokensPerGroup = game.Rules.Cards.PlayerPickToken.MaxTokensPerGroup },
            // The values a new pack or pick starts its odds table with (rules/pack_odds.json); packs have no fallback odds.
            oddsTemplate = new { icon = game.Rules.PackOdds.IconChance, pickTotw = game.Rules.PackOdds.Picks.InformChance },
            publicPackIds = lineup,
            // What a My Stadium pack slot can draw: each catalogued item type and how many items a store pack may give.
            vanityTypes = game.Rules.Club.Vanity.Catalogue.Keys.Order().Select(subtype => new
            {
                subtype, name = game.Rules.Club.Vanity.SubtypeName(subtype),
                items = game.Rules.Club.Vanity.SellableItems(new HashSet<long> { subtype }).Count(),
            }).Where(type => type.items > 0).ToArray(),
            packs = flags.Select(row =>
            {
                var config = packs.Obj(row.Id.ToString(CultureInfo.InvariantCulture)) ?? new JsonObject();
                var usedBy = new List<string>();
                foreach (var (file, document) in rules)
                    if (document is not null) PackReferences.Find(document, row.Id, file, usedBy);
                usedBy.AddRange(rewardUses.GetValueOrDefault(row.Id) ?? []);
                usedBy.AddRange(sbcUses.GetValueOrDefault(row.Id) ?? []);
                return new
                {
                    id = row.Id, locked = row.Locked, grantOnly = row.GrantOnly, inStore = lineup.Contains(row.Id) || tokenListings.Any(entry => entry.PackId == row.Id && entry.Enabled), usedBy,
                    listed = listed.ContainsKey(row.Id),
                    art = artNames.GetValueOrDefault(game.Packs.Rules.StoreArtId(row.Id, config)),
                    viewOnlyReason = ViewOnlyReason(row.Id, config),
                    description = config.Text("name", $"Pack {row.Id}"), blurb = config.Text("bio", ""),
                    group = config.Str("store_section") == "tokens" ? "Tokens" : config.Text("store_group", "") is { Length: > 0 } group ? group : "Automatic",
                    coins = config.Str("store_section") == "tokens" ? 0 : config.Int("price"),
                    points = config.Int("points_price"), items = config.Int("size", 12), rare = config.Int("rare_slots"),
                    config,
                    unavailableReason = PackOpeningService.MixedAvailability(config, game.Live.Snapshot, game.Time.GetUtcNow()),
                };
            }).ToArray(),
        };
    }

    /// <summary>
    /// Why the panel shows a pack without editing what it draws, or null: its contents come from somewhere the editor does
    /// not control. Such a pack can still be named, priced, put in the lineup, granted and awarded.
    /// </summary>
    private static string? ViewOnlyReason(long id, JsonObject config) =>
        PackOpeningService.UsesSpecialistGenerator(config) && PackOpeningService.VanityPack(config) is null ? "This pack draws club items, consumables, managers, stadiums or swap tokens; the panel cannot edit that generator."
        : config.Text("resource_pool") is { Length: > 0 } ? "This pack draws from a fixed card pool (the TOTW 1–3 reward pool) that the editor cannot change."
        : id == LegacyMatchPackService.PackId ? "Retired Match Pack: only previously earned, stored contents can be opened."
        : id is >= 550 and <= 553 ? "A Aurora Champions red pick: the server allows red cards only from these pack ids, so its contents stay as they are."
        : null;

    /// <summary>The fields a view-only pack may still change: how it is named, sold and shown, never what it draws.</summary>
    private static readonly HashSet<string> ViewOnlyEditableFields = new(StringComparer.Ordinal)
    {
        "name", "bio", "store_group", "store_section", "price", "points_price", "purchase_limit", "community_limit", "admin_only",
    };

    private static ValueTask<string> PackRevisionAsync(IUnitOfWork work) => work.ScalarAsync<string>("""
        SELECT md5(jsonb_build_object(
            'packs',coalesce((SELECT jsonb_agg(to_jsonb(p) ORDER BY pack_id) FROM ultimateteam.store_packs p WHERE kind='store'),'[]'::jsonb),
            'tokens',coalesce((SELECT jsonb_agg(to_jsonb(t) ORDER BY pack_id) FROM ultimateteam.token_store_listings t),'[]'::jsonb))::text)
        """);

    private async Task<object> ChangePackAsync(ApiRequest request, long? packId, bool hide)
    {
        var body = request.Body;
        var revision = RequiredRevision(body);
        var fields = hide ? new Dictionary<string, (string Column, object Value)>(StringComparer.Ordinal)
            : ReadPackFields(body);
        if (fields.Keys.Any(name => name is "price" or "points_price" or "store_section" or "enabled"))
            throw new PanelError(400, "Set price, availability and tradeability in Store Lineup or Token Store Lineup.");
        if (!hide && fields.Count == 0) throw new PanelError(400, "Change at least one pack field.");
        await _catalogEdits.WaitAsync(request.Aborted);
        try
        {
            var changesets=await RequestChangesetsAsync(request);
            var open = request.Query("apply") == "1" ? null : await changesets.OpenAsync(request.Aborted);
            var stagedIds = open?.Changes.Where(change => change.Area == PanelPackArea.Area && change.Operation == ContentOperation.Insert)
                .Select(change => change.Key["packId"]!.GetValue<long>()).ToHashSet() ?? [];
            var selectedChangesetId = request.Query("dropId") is {Length:>0} selectedText && long.TryParse(selectedText,out var selectedId)
                ? (long?)await Schedule.DraftIdAsync(selectedId,request.Aborted) : null;
            var state = await game.UnitOfWork.RunReadAsync(0, async work =>
            {
                if (await PackRevisionAsync(work) != revision) throw new PanelError(409, "The pack catalogue changed. Refresh before saving.");
                var id = packId;
                if (id is null)
                {
                    // The next id after every pack and every owned legacy definition, so a freed or retired id is never
                    // reused and a new pack stays clear of the special-cased red-pick ids (550-553). Its art is its store
                    // category, not its id.
                    await ContentChangesets.LockAsync(work);
                    var highest = await work.ScalarAsync<long>("""
                        SELECT greatest(coalesce((SELECT max(pack_id) FROM ultimateteam.store_packs),0),
                                        coalesce((SELECT max(pack_id) FROM ultimateteam.owned_legacy_pack_definitions),0),
                                        coalesce((SELECT max(offer_id) FROM ultimateteam.token_store_listings),0),
                                        coalesce((SELECT max(pack_id) FROM ultimateteam.token_store_listings),0),
                                        coalesce((SELECT max((target_key->>'packId')::bigint) FROM ultimateteam.content_changes WHERE area='store_pack'),0),
                                        (SELECT last_value FROM ultimateteam.content_store_ids))
                        """);
                    if (highest >= 32767) throw new PanelError(409,"There is no free FIFA 21 store pack id below 32768.");
                    await work.ExecuteAsync("SELECT setval('ultimateteam.content_store_ids',@highest,true)",("highest",highest));
                    id=await work.ScalarAsync<long>("SELECT nextval('ultimateteam.content_store_ids')");
                    while (stagedIds.Contains(id.Value) || await work.ScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM ultimateteam.store_packs WHERE pack_id=@pack)", ("pack", id.Value))) id++;
                    if (id is < 1 or > 32767) throw new PanelError(409, "There is no free FIFA 21 store pack id below 32768.");
                    if (!fields.ContainsKey("name")) throw new PanelError(400, "A new pack needs a name.");
                }
                var key = new JsonObject { ["packId"] = id.Value };
                var before = selectedChangesetId is { } draftId
                    ? await Schedule.DraftBaselineAsync(work,draftId,new PanelPackArea(),key)
                    : await new PanelPackArea().ReadAsync(work,key);
                return (Id: id.Value, Before: before);
            }, request.Aborted);
            var prior = open?.Changes.FirstOrDefault(change => change.Area == PanelPackArea.Area &&
                change.Key["packId"]?.GetValue<long>() == state.Id);
            if (packId is not null && state.Before is null && prior?.Operation != ContentOperation.Insert)
                throw new PanelError(404, "No such pack.");
            var after = hide ? null : prior?.After?.DeepClone().AsObject() ?? state.Before?.DeepClone().AsObject() ?? new JsonObject
            {
                ["revision"] = revision, ["pack"] = new JsonObject(),
            };
            if (after is not null)
                foreach (var (name, field) in fields) ((JsonObject)after["pack"]!)[name] = JsonSerializer.SerializeToNode(field.Value);
            await RequireAvailabilityWindowAsync(request, PanelPackArea.Area, new JsonObject { ["packId"] = state.Id }, after);
            var staged = await (await RequestChangesetsAsync(request)).SetChangeAsync(PanelPackArea.Area, new JsonObject { ["packId"] = state.Id },
                hide ? ContentOperation.Hide : packId is null || prior?.Operation == ContentOperation.Insert
                    ? ContentOperation.Insert : ContentOperation.Update,
                after, null, request.Aborted);
            return new { id = state.Id, pending = staged?.Status == "published" ? 0 : staged?.Changes.Count ?? 0, applies = staged?.Status == "published" ? "live" : "after-review" };
        }
        finally { _catalogEdits.Release(); }
    }

    /// <summary>
    /// PUT packs/lineup: stages the store listing. The body is <c>{revision, listing: [{packId, enabled, untradeable}]}</c>; an
    /// older client's <c>{revision, publicPackIds}</c> lists those packs switched on, keeping any tradeability they had.
    /// </summary>
    private async Task<object> PublishLineupAsync(ApiRequest request)
    {
        var revision = RequiredRevision(request.Body);
        var listing = new JsonArray();
        if (request.Body.TryGetProperty("listing", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() <= 32767)
        {
            listing = JsonNode.Parse(entries.GetRawText())!.AsArray();
        }
        else if (request.Body.TryGetProperty("publicPackIds", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= 32767)
        {
            var ids = new List<long>();
            foreach (var node in list.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Number || !node.TryGetInt64(out var id) || id <= 0 || !ids.AddUnique(id))
                    throw new PanelError(400, "Pack ids must be unique positive integers.");
            }
            var kept = await game.UnitOfWork.RunReadAsync(0, async work => (await PanelLineupArea.ListedAsync(work))
                .ToDictionary(entry => entry.PackId), request.Aborted);
            listing = PanelLineupArea.Listing(ids.Select(id => (id, true, kept.GetValueOrDefault(id).Untradeable, kept.GetValueOrDefault(id).Price)));
        }
        else
        {
            throw new PanelError(400, "listing must be an array of {packId, enabled, untradeable}.");
        }
        var before = await game.UnitOfWork.RunReadAsync(0, async work =>
        {
            if (await PackRevisionAsync(work) != revision) throw new PanelError(409, "The pack catalogue changed. Refresh before saving.");
            return await new PanelLineupArea().ReadAsync(work, new JsonObject { ["scope"] = "public" });
        }, request.Aborted);
        if (listing.Any(node => node is not JsonObject)) throw new PanelError(400, "Each listing must be an object.");
        // Sorted as the listing reads, so staging the published listing again stages nothing.
        var sorted = new JsonArray(listing.OfType<JsonObject>().OrderBy(entry => entry["packId"] is JsonValue id && id.TryGetValue(out long value) ? value : 0)
            .Select(entry => (JsonNode?)entry.DeepClone()).ToArray());
        var after = new JsonObject { ["revision"] = before!["revision"]!.DeepClone(), ["listing"] = sorted };
        await game.UnitOfWork.RunReadAsync(0, async work => { await new PanelLineupArea().ValidateAsync(work, ContentOperation.Update, new JsonObject { ["scope"] = "public" }, after); return true; }, request.Aborted);
        await RequireAvailabilityWindowAsync(request, PanelLineupArea.Area, new JsonObject { ["scope"] = "public" }, after);
        var staged = await (await RequestChangesetsAsync(request)).SetChangeAsync(PanelLineupArea.Area, new JsonObject { ["scope"] = "public" },
            ContentOperation.Update, after, null, request.Aborted);
        return new { listing = sorted, pending = staged?.Status == "published" ? 0 : staged?.Changes.Count ?? 0, applies = staged?.Status == "published" ? "live" : "after-review" };
    }

    private static string RequiredRevision(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("revision", out var node) && node.ValueKind == JsonValueKind.String &&
        node.GetString() is { Length: 32 } revision && revision.All(Uri.IsHexDigit)
            ? revision : throw new PanelError(400, "A catalogue revision is required.");

    private static Dictionary<string, (string Column, object Value)> ReadPackFields(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("pack", out var pack) || pack.ValueKind != JsonValueKind.Object)
            throw new PanelError(400, "A pack object is required.");
        var fields = new Dictionary<string, (string Column, object Value)>(StringComparer.Ordinal);
        foreach (var property in pack.EnumerateObject())
        {
            if (!EditablePackFields.TryGetValue(property.Name, out var rule)) throw new PanelError(400, $"{property.Name} is not an editable FIFA 21 pack field.");
            var value = property.Value;
            object parsed;
            switch (rule.Kind)
            {
                case "json-array":
                case "json-object":
                    if (value.ValueKind != (rule.Kind == "json-array" ? JsonValueKind.Array : JsonValueKind.Object)
                        || value.GetRawText().Length > 30000 || rule.Kind == "json-array" && value.GetArrayLength() > rule.Max)
                        throw new PanelError(400, $"{property.Name} must be a bounded {rule.Kind}.");
                    parsed = JsonNode.Parse(value.GetRawText())!; break;
                case "codes":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > rule.Max
                        || value.EnumerateArray().Any(code => code.ValueKind != JsonValueKind.String || code.GetString() is not { Length: > 0 and <= 100 }))
                        throw new PanelError(400, "promo_codes must be an array of promo codes.");
                    parsed = value.EnumerateArray().Select(code => code.GetString()!).Distinct(StringComparer.Ordinal).ToArray(); break;
                case "text":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } raw || raw.Length > rule.Max ||
                        (property.Name is "name" or "store_group" && string.IsNullOrWhiteSpace(raw)))
                        throw new PanelError(400, $"{property.Name} must be nonempty text of at most {rule.Max} characters.");
                    parsed = raw.Trim(); break;
                case "bool":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new PanelError(400, $"{property.Name} must be true or false.");
                    parsed = value.GetBoolean(); break;
                case "chance":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var chance) || chance < 0 || chance > rule.Max)
                        throw new PanelError(400, $"{property.Name} must be from 0 to 1.");
                    parsed = chance; break;
                default:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0 || number > rule.Max)
                        throw new PanelError(400, $"{property.Name} is out of range.");
                    parsed = rule.Kind == "int" ? (object)checked((int)number) : number; break;
            }
            fields[property.Name] = (rule.Column, parsed);
        }
        return fields;
    }

    private static void ValidatePanelPack(StorePackCatalog catalog, long id)
    {
        var config = catalog.Rules.Pack(id) ?? throw new PanelError(400, "The pack is missing after editing.");
        if (config.Int("purchase_limit") > 0 && config.Int("community_limit") > 0)
            throw new PanelError(400, "Choose either a per-club purchase limit or a community limit, not both.");
        var size = config.Int("size", 12);
        var group = config.Text("store_group", "");
        if (group.Length > 0 && !catalog.Rules.StoreGroups.ContainsKey(group) && !catalog.Rules.StoreGroupAliases.ContainsKey(group))
            throw new PanelError(400, "Choose an existing store category.");
        if (size is < 1 or > 100 || size > catalog.Rules.MaxPackSize || config.Int("rare_slots") > size ||
            config.Int("guaranteed_specials") > config.Int("rare_slots") ||
            (config.Get("min_rating") is not null && config.Get("max_rating") is not null && config.Int("min_rating") > config.Int("max_rating")))
            throw new PanelError(400, "Pack size, rare slots, special guarantees or rating range conflict with the pack rules.");
    }

    private async Task<object> PreviewPackAsync(ApiRequest request)
    {
        var fields = ReadPackFields(request.Body);
        var id = request.Body.TryGetProperty("packId", out var idNode) && idNode.ValueKind == JsonValueKind.Number && idNode.TryGetInt64(out var number)
            ? number : 0;
        var config = id > 0 ? await PreviewConfigAsync(id, request.Aborted, request) : new JsonObject();
        foreach (var (key, field) in fields) config[key] = JsonSerializer.SerializeToNode(field.Value);
        if (!config.ContainsKey("size")) config["size"] = 12;
        try
        {
            var snapshot = await PromoPreviewSnapshotAsync(true, request.Aborted, request);
            PlayerPickDraw.Validate(game, config, snapshot.Catalog.Promos.ToDictionary(p => p.Code, p => (p.OddsCategory, p.Status)), id);
            return new { items = WithArt(players.Local.Fut.Packs.PreviewPlayerPack(config, snapshot: snapshot, sourcePackId: id)) };
        }
        catch (NotSupportedException error) { throw new PanelError(422, error.Message); }
        catch (InvalidOperationException error) { throw new PanelError(422, $"This pack cannot draw from its current player pool: {error.Message}"); }
    }

    private async Task<object> SimulatePacksAsync(JsonElement body, CancellationToken token, ApiRequest? request=null)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("packId", out var idNode) ||
            idNode.ValueKind != JsonValueKind.Number || !idNode.TryGetInt64(out var id) || id <= 0 ||
            !body.TryGetProperty("rolls", out var countNode) || countNode.ValueKind != JsonValueKind.Number ||
            !countNode.TryGetInt32(out var count) || count is < 1 or > 20)
            throw new PanelError(400, "Choose a pack id and 1–20 preview rolls.");
        var config = await PreviewConfigAsync(id, token, request);
        if (config.Count == 0) throw new PanelError(404, "No such pack.");
        var rolls = new List<object>();
        try
        {
            var snapshot = await PromoPreviewSnapshotAsync(true, token, request);
            for (var index = 0; index < count; index++)
                rolls.Add(new { index, items = WithArt(players.Local.Fut.Packs.PreviewPlayerPack(config, snapshot: snapshot, sourcePackId: id)) });
        }
        catch (NotSupportedException error) { throw new PanelError(422, error.Message); }
        catch (InvalidOperationException error) { throw new PanelError(422, $"This pack cannot draw from its current player pool: {error.Message}"); }
        return new { packId = id, rolls, changedClubState = false };
    }

    private async Task<JsonObject> PreviewConfigAsync(long id, CancellationToken token, ApiRequest? request=null)
    {
        var config = request?.Query("dropId") is {Length:>0}
            ? (await PromoPreviewSnapshotAsync(true,token,request)).Rules.Packs.Pack(id)?.DeepClone() as JsonObject ?? new JsonObject()
            : game.Packs.GetPackConfig(id);
        var staged = (await (request is null ? _changesets : await RequestChangesetsAsync(request)).OpenAsync(token))?.Changes.LastOrDefault(change =>
            change.Area == PanelPackArea.Area && change.Key["packId"]?.GetValue<long>() == id);
        if (staged?.After?["pack"] is JsonObject edited)
        {
            if (config.Count == 0) config = new JsonObject
            {
                ["size"] = 12, ["gold_weight"] = 100, ["silver_weight"] = 0, ["bronze_weight"] = 0,
                ["rare_slots"] = 1,
            };
            foreach (var (name, value) in edited)
                if (EditablePackFields.ContainsKey(name)) config[name] = value?.DeepClone();
        }
        return config;
    }
}

internal static class PanelListExtensions
{
    public static bool AddUnique<T>(this List<T> list, T item)
    {
        if (list.Contains(item)) return false;
        list.Add(item);
        return true;
    }
}
