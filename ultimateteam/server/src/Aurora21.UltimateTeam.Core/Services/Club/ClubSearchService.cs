using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Core.Services.Club;

/// <summary>
/// My Club search (GET query or POST body paging document), including the Club Info bucket
/// that My Stadium uses for kits and badges.
/// </summary>
public sealed class ClubSearchService
{
    // -1 is FIFA's "any" and never filters.
    private static readonly (string[] Criteria, string ItemKey)[] IdFilters =
    {
        (new[] { "maskedDefId", "assetId" }, "assetId"),
        (new[] { "leag", "league", "leagueId" }, "leagueId"),
        (new[] { "team", "club", "teamid" }, "teamid"),
        (new[] { "nat", "nationality", "nation" }, "nation"),
    };

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public ClubSearchService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.ClubSearch");
    }

    /// <summary>A club search result: the page template and its values, or an already built document.</summary>
    public readonly record struct SearchBody(string? Template, TemplateArgs? Args, JsonObject? Document);

    public async ValueTask<JsonObject> SearchAsync(IUnitOfWork uow, QueryValues query, JsonNode payload)
    {
        var body = await SearchRelationalAsync(uow, query, payload, stream: false);
        return body.Document ?? TemplateLibrary.Core.Object(body.Template!, body.Args);
    }

    /// <summary>The search result for the route that sends the body unchanged; the page items are written, not copied.</summary>
    public ValueTask<SearchBody> SearchBodyAsync(IUnitOfWork uow, QueryValues query, JsonNode payload) =>
        SearchRelationalAsync(uow, query, payload, stream: true);

    private async ValueTask<SearchBody> SearchRelationalAsync(IUnitOfWork uow, QueryValues query, JsonNode payload, bool stream)
    {
        var club = _fut.Club;
        var criteria = SearchCriteria(query, payload);
        string Text(params string[] names) => JsonValues.Str(criteria.Value(names));
        var type = Normalize(JsonValues.Str(criteria.Value("type", "itemType", "ctype") ?? JsonValue.Create("player"), "player"));
        var isPlayerSearch = type == "player" || _fut.Game.Rules.Club.PositionGroupTypes.ContainsKey(type);
        var view = await ClubPresentationView.LoadActivesAsync(uow, club.CustomisationStates(), allMetadata: !isPlayerSearch);
        // The shape check runs while the view holds the shared club lock, before counts/page queries.
        var unusualPlayers = view.HasPlayerRepresentations;
        var owned = view.MetadataItems();
        var (actives, freeDefaults) = await club.ClubItemsAsync(uow, view);
        // Every club owns the whole vanity catalogue as virtual items: the My Stadium pickers list them, the piles do not count them.
        var vanity = type is "equippable" or "equippables" or "vanity" ? await _fut.Vanity.ItemsAsync(uow) : new List<JsonObject>();
        var unassigned = (int)(view.CountPile("unassigned") + await _fut.Vanity.UnassignedCountAsync(uow));
        var merged = owned.Where(item => item.Text("pile") == "club").Concat(freeDefaults).Concat(actives)
            .GroupBy(item => item.Int("id")).Select(group => group.Last()).ToList();
        if (!isPlayerSearch || unusualPlayers)
        {
            var result = Search(null, merged, query, payload,
                owned.Count(item => item.Text("pile") == "squad" && ClubInventoryService.IsPlayer(item)), unassigned, vanity);
            var selectedIds = result.Arr("itemData")!.OfType<JsonObject>().Select(item => item.Int("id")).ToArray();
            var documents = (await uow.RelationalItems.GetManyAsync(uow, selectedIds)).Concat(vanity).Concat(freeDefaults).Concat(actives)
                .GroupBy(item => item.Int("id")).ToDictionary(group => group.Key, group => group.Last());
            result["itemData"] = new JsonArray(result.Arr("itemData")!.OfType<JsonObject>()
                .Select(item => (JsonNode?)(documents.TryGetValue(item.Int("id"), out var full) ? _fut.Native.SearchItem(full).Copy() : item.Copy())).ToArray());
            return new SearchBody(null, null, result);
        }
        else
        {
            var game = _fut.Game;
            var filters = new List<string> { "i.club_id=@club", "i.pile='club'", "i.family_id=1", "NOT(i.native_item_id=ANY(@activeIds))" };
            var baseFilterCount = filters.Count;
            // Actives are the last writer in the id merge, including virtual item-id collisions.
            var parameters = new List<(string Name, object? Value)> { ("activeIds", actives.Select(item => item.Int("id")).ToArray()) };
            void Filter(string sql, string name, object value) { filters.Add(sql); parameters.Add((name, value)); }
            var positionSql = "COALESCE(owned.position_override,p.position)";
            if (game.Rules.Club.PositionGroupTypes.TryGetValue(type, out var zone))
                Filter($"upper({positionSql})=ANY(@positions)", "positions", game.Rules.Club.PositionZones[game.Rules.Club.PositionZoneAliases[zone]].ToArray());
            var level = Text("level").ToLowerInvariant();
            if (level is "gold" or "silver" or "bronze")
            {
                Filter("CASE WHEN ct.card_level IN ('gold','silver','bronze') THEN ct.card_level WHEN p.rating>=@gold THEN 'gold' WHEN p.rating>=@silver THEN 'silver' ELSE 'bronze' END=@level", "level", level);
                parameters.Add(("gold", game.Packs.Rules.Tiers.GoldMinRating)); parameters.Add(("silver", game.Packs.Rules.Tiers.SilverMinRating));
            }
            var definitions = Text("defId", "resourceId").Split(',').Select(part => long.TryParse(part.Trim(), out var id) ? id : 0).Where(id => id != 0).Distinct().ToArray();
            if (definitions.Length > 0) Filter("(d.native_resource_id=ANY(@definitions) OR COALESCE(d.native_asset_id,d.native_resource_id)=ANY(@definitions))", "definitions", definitions);
            foreach (var (names, column) in new[] {
                (new[] { "maskedDefId", "assetId" }, "COALESCE(d.native_asset_id,d.native_resource_id)"),
                (new[] { "leag", "league", "leagueId" }, "COALESCE(l.native_league_id,0)"),
                (new[] { "team", "club", "teamid" }, "COALESCE(t.native_team_id,0)"),
                (new[] { "nat", "nationality", "nation" }, "COALESCE(n.native_nation_id,0)") })
            {
                var value = AsInt(criteria.Value(names));
                if (value > 0) Filter(column + "=@" + names[0], names[0], value);
            }
            var position = Text("pos", "position").ToUpperInvariant();
            if (position.Length > 0) Filter($"upper({positionSql})=@position", "position", position);
            if (Text("rare").ToUpperInvariant() is "SP" or "SPECIAL") filters.Add("r.native_rareflag>1");
            var style = AsInt(criteria.Value("playStyle"));
            if (style > 0)
            {
                var styles = game.Rules.Cards.ChemistryStyles;
                Filter($"CASE WHEN COALESCE(cs.native_effect_id,owned.native_play_style,0)=ANY(@styles) THEN COALESCE(cs.native_effect_id,owned.native_play_style,0) WHEN upper({positionSql})='GK' THEN @gkstyle ELSE @basicstyle END=@style", "style", style);
                parameters.Add(("styles", styles.Names.Keys.ToArray())); parameters.Add(("gkstyle", styles.GoalkeeperDefault)); parameters.Add(("basicstyle", styles.OutfieldDefault));
            }
            var tradeable = JsonValues.Text(criteria.Value("isUntradeable", "untradeable")).ToLowerInvariant();
            if (tradeable is "true" or "1" or "false" or "0") Filter("i.untradeable=@untradeable", "untradeable", tradeable is "true" or "1");
            if (ExcludedItemIds(criteria) is { Count: > 0 } excludedItems) Filter("NOT(i.native_item_id=ANY(@excludedItems))", "excludedItems", excludedItems.ToArray());
            var from = """
                FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
                JOIN ultimateteam.player_card_definitions p ON p.definition_id=i.definition_id
                JOIN ultimateteam.player_items owned ON owned.item_id=i.id
                JOIN ultimateteam.card_types ct ON ct.card_type_id=p.card_type_id
                JOIN ultimateteam.native_rarities r ON r.native_rarity_id=ct.native_rarity_id
                LEFT JOIN ultimateteam.teams t ON t.team_id=p.team_id
                LEFT JOIN ultimateteam.leagues l ON l.league_id=p.league_id
                LEFT JOIN ultimateteam.nations n ON n.nation_id=p.nation_id
                LEFT JOIN ultimateteam.consumable_definitions cs ON cs.definition_id=owned.style_definition_id
                """ + " WHERE " + string.Join(" AND ", filters);
            var filterParameters = parameters.ToArray();
            var countNode = criteria.Value("count", "num", "pageSize");
            var requestedCount = JsonValues.HasValue(countNode) && JsonValues.TryInt(countNode, out var parsedCount)
                ? Math.Max(0, parsedCount) : (long?)null;
            // The normal client request supplies a page size. Its page can carry the total in one
            // pass; an unspecified or zero page size needs the count before paging is calculated.
            long? matching = requestedCount is null or 0
                ? await uow.ScalarAsync<long>("-- query: club.search.count\nSELECT count(*) " + from, filterParameters)
                : null;
            var count = requestedCount ?? matching!.Value;
            var start = Math.Max(0, AsInt(criteria.Value("start", "offset", "startIndex")));
            if (start == 0 && criteria.Value("page") is { } pageNode && JsonValues.TryInt(JsonValues.HasValue(pageNode) ? pageNode : JsonValue.Create(1), out var page))
            {
                var pageIndex = Math.Max(1, page) - 1;
                start = pageIndex > long.MaxValue / Math.Max(1, count) ? long.MaxValue : pageIndex * Math.Max(1, count);
            }
            var acquired = Text("acquiredDate").ToLowerInvariant();
            var ascending = Text("sort").ToLowerInvariant() == "asc";
            var direction = ascending ? "ASC" : "DESC";
            var idDirection = ascending ? "DESC" : "ASC";
            var order = $"p.rating {direction}, r.native_rareflag {direction}, COALESCE(d.native_resource_id,d.native_asset_id,0) {direction}, i.native_item_id {idDirection}";
            if (acquired is "asc" or "desc") order = $"i.timestamp {acquired}, i.native_item_id {acquired}";
            else if (Text("sortBy").ToLowerInvariant() == "value")
            {
                // Quick-sell values are tunable rules, not guessed SQL constants: the same value Cards.QuickSellValue gives.
                parameters.Add(("valueGold", game.Packs.Rules.Tiers.GoldMinRating)); parameters.Add(("valueSilver", game.Packs.Rules.Tiers.SilverMinRating));
                order = QuickSellValueSql(game.Rules.Cards.QuickSell, parameters) + " " + direction + ", " + order;
            }
            parameters.Add(("limit", count)); parameters.Add(("offset", matching is null ? start : Math.Min(start, matching.Value)));
            List<long> ids;
            var usePlayerSearch = type == "player" && filters.Count == baseFilterCount && requestedCount is > 0
                && acquired is not ("asc" or "desc") && Text("sortBy").ToLowerInvariant() != "value";
            if (usePlayerSearch)
            {
                // The projection stores the sort keys for owned players. Count separately inside
                // the same command so an empty or out-of-range page still returns the total.
                var pageRows = await uow.QueryAsync("""
                    -- query: club.search.page
                    WITH total AS (
                        SELECT count(*) AS matching FROM ultimateteam.club_player_search
                        WHERE club_id=@club AND pile='club' AND NOT(native_item_id=ANY(@activeIds))
                    )
                    SELECT page.native_item_id, total.matching
                    FROM total LEFT JOIN LATERAL (
                        SELECT native_item_id FROM ultimateteam.club_player_search
                        WHERE club_id=@club AND pile='club' AND NOT(native_item_id=ANY(@activeIds))
                    """ + $" ORDER BY rating {direction}, native_rareflag {direction}, resource_id {direction}, native_item_id {idDirection}"
                    + " LIMIT @limit OFFSET @offset) page ON TRUE",
                    reader => (Id: reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0), Matching: reader.GetInt64(1)),
                    parameters.ToArray());
                matching = pageRows[0].Matching;
                ids = pageRows.Where(row => row.Id.HasValue).Select(row => row.Id!.Value).ToList();
            }
            else if (requestedCount is > 0)
            {
                // COUNT(*) OVER() visits the filtered rows once for both the total and the page.
                // A page beyond the end has no row to carry that total, so count it separately.
                var pageRows = await uow.QueryAsync("-- query: club.search.page\nSELECT i.native_item_id, count(*) OVER() " + from
                    + " ORDER BY " + order + " LIMIT @limit OFFSET @offset",
                    reader => (Id: reader.GetInt64(0), Matching: reader.GetInt64(1)), parameters.ToArray());
                ids = pageRows.Select(row => row.Id).ToList();
                matching = pageRows.Count > 0 ? pageRows[0].Matching
                    : await uow.ScalarAsync<long>("-- query: club.search.count\nSELECT count(*) " + from, filterParameters);
            }
            else
            {
                ids = count > 0
                    ? await uow.QueryAsync("-- query: club.search.page\nSELECT i.native_item_id " + from
                        + " ORDER BY " + order + " LIMIT @limit OFFSET @offset", reader => reader.GetInt64(0), parameters.ToArray())
                    : new List<long>();
            }
            var args = ClubPresentationService.CustomisationCounts(merged);
            args["clubPlayers"] = view.OmittedPlayers("club") + merged.Count(ClubInventoryService.IsPlayer);
            args["squadPlayers"] = view.CountPlayers("squad");
            args["consumables"] = merged.Count(NativeItems.IsConsumable); args["managers"] = merged.Count(item => item.Text("itemType") == "manager");
            args["total"] = view.OmittedPlayers("club") + merged.Count; args["unassigned"] = unassigned; args["matching"] = matching!.Value;
            var pageItems = (await uow.RelationalItems.GetManyAsync(uow, ids)).Select(item => _fut.Native.SearchItem(item)).ToList();
            if (stream)
            {
                args.Defer("items", writer =>
                {
                    writer.WriteStartArray();
                    foreach (var item in pageItems)
                    {
                        FutJson.WriteValue(writer, item);
                    }
                    writer.WriteEndArray();
                });
            }
            else
            {
                args["items"] = ClubPresentationService.ToArray(pageItems);
            }
            return new SearchBody("club/search-page", args, null);
        }
    }

    /// <param name="virtualItems">Items the search may return that are not counted in the club's piles (the vanity catalogue).</param>
    internal JsonObject Search(ClubSnapshot? snapshot, List<JsonObject> allItems, QueryValues query, JsonNode payload, int squadPlayers = 0, int unassigned = 0,
        IReadOnlyList<JsonObject>? virtualItems = null)
    {
        var searchable = virtualItems is null or { Count: 0 } ? allItems : allItems.Concat(virtualItems).ToList();
        var game = _fut.Game;
        var clubRules = game.Rules.Club;
        var criteria = SearchCriteria(query, payload);
        string Text(params string[] names) => criteria.Value(names) is { } node ? JsonValues.Str(node) : string.Empty;

        var requestedType = Normalize(JsonValues.Str(criteria.Value("type", "itemType", "ctype") ?? JsonValue.Create("player"), "player"));
        var requestedCategory = Normalize(Text("category", "cat", "searchCategory", "subType", "cardSubTypeId"));
        if (requestedType is "equippable" or "equippables" or "vanity")
        {
            // What the My Stadium picker asked for, so a picker listing the wrong item types can be traced to the request.
            _log.LogInformation("CLUB EQUIPPABLES query={Query} body={Body}", string.Join("&", query.Entries.Select(entry => $"{entry.Key}={string.Join(",", entry.Value)}")),
                payload.ToJsonString() is var body && body.Length > 600 ? body[..600] : payload.ToJsonString());
        }

        IEnumerable<JsonObject> items = requestedType switch
        {
            // My Stadium pickers share one equippables bucket and filter it by the slot's vanity types.
            // FIFA 21 PC's My Stadium slot picker searches type vanity (live 10-03), the same bucket.
            "equippable" or "equippables" or "vanity" => Equippables(searchable, AsInt(criteria.Value("excldef")), clubRules,
                SubtypeList(criteria.Value("vanitySubTypeIds"))),
            // My Stadium searches kits and badges through the Club Info bucket; an empty result crashes it.
            "clubinfo" or "clubitem" or "clubitems" when requestedCategory is "badge" or "badges" or "custom" or "11" =>
                searchable.Where(item => TypeOf(item) == "custom" || AsInt(item.Get("cardsubtypeid")) == 11),
            "clubinfo" or "clubitem" or "clubitems" =>
                searchable.Where(item => TypeOf(item) == "kit" || AsInt(item.Get("cardsubtypeid")) == 9),
            // My Stadium's badge picker searches type badge; a badge card is itemType custom (it found nothing, local 10-03).
            "badge" or "badges" => searchable.Where(item => TypeOf(item) == "custom" || AsInt(item.Get("cardsubtypeid")) == 11),
            "manager" or "managers" or "staff" => searchable.Where(item => item.Text("itemType") == "manager"),
            "player" => searchable.Where(ClubInventoryService.IsPlayer),
            // Defenders, Midfielders and Forwards arrive as their own type.
            _ when clubRules.PositionGroupTypes.TryGetValue(requestedType, out var zone) =>
                searchable.Where(item => ClubInventoryService.IsPlayer(item) && clubRules.PositionInZone(item.Str("preferredPosition"), zone)),
            "" or "any" or "all" => searchable,
            _ => searchable.Where(item => TypeOf(item) == requestedType),
        };
        // One card per club item definition in every club item search (kit, badge, ball, stadium pickers): the club's
        // active copy and the owned card both showed, each marked active (local 10-03); the owned card comes first.
        items = items.DistinctBy(item => TypeOf(item) is "kit" or "custom" or "ball" or "stadium"
            ? (TypeOf(item), AsInt(item.Get("resourceId")), 0L) : (TypeOf(item), AsInt(item.Get("id")), 1L));
        // FIFA shows the slot's current card itself and names it as excldef; listing it again doubled it (local 10-03).
        if (AsInt(criteria.Value("excldef")) is var excluded and > 0 && requestedType is not ("equippable" or "equippables" or "vanity"))
        {
            items = items.Where(item => AsInt(item.Get("resourceId")) != excluded);
        }
        if (requestedType is not ("player" or "" or "any" or "all") && !clubRules.PositionGroupTypes.ContainsKey(requestedType))
        {
            items = items.ToList();
            _log.LogInformation("CLUB ITEMS type={Type} body={Body} cards={Cards}", requestedType, payload.ToJsonString(),
                string.Join(" ", items.Select(item => $"{TypeOf(item)}:{AsInt(item.Get("id"))}/{AsInt(item.Get("resourceId"))}/{item.Text("itemState")}")));
        }

        var level = Text("level").ToLowerInvariant();
        if (level is "gold" or "silver" or "bronze")
        {
            items = items.Where(item => game.Packs.Rules.TierFor(AsInt(item.Get("rating")),
                game.Players.Find(AsInt(item.Get("resourceId")))?.Tier ?? string.Empty) == level);
        }
        // Player-name search sends base definitions as a padded list ("9014,0,0,..."), matching every version.
        var definitions = Text("defId", "resourceId").Split(',')
            .Select(part => long.TryParse(part.Trim(), out var id) ? id : 0)
            .Where(id => id != 0)
            .ToHashSet();
        if (definitions.Count > 0)
        {
            items = items.Where(item => definitions.Contains(AsInt(item.Get("resourceId"))) || definitions.Contains(AsInt(item.Get("assetId"))));
        }
        foreach (var (names, itemKey) in IdFilters)
        {
            var wanted = AsInt(criteria.Value(names));
            if (wanted > 0)
            {
                items = items.Where(item => AsInt(item.Get(itemKey)) == wanted);
            }
        }
        var position = Text("pos", "position").ToUpperInvariant();
        if (position.Length > 0)
        {
            items = items.Where(item => item.Str("preferredPosition").ToUpperInvariant() == position);
        }
        if (Text("rare").ToUpperInvariant() is "SP" or "SPECIAL")
        {
            items = items.Where(item => AsInt(item.Get("rareflag")) > 1);
        }
        var style = AsInt(criteria.Value("playStyle"));
        if (style > 0)
        {
            items = items.Where(item => ClubInventoryService.IsPlayer(item) && game.Cards.EffectivePlayStyle(item) == style);
        }
        if (ExcludedItemIds(criteria) is { Count: > 0 } excludedItems)
        {
            items = items.Where(item => !excludedItems.Contains(AsInt(item.Get("id"))));
        }
        switch (JsonValues.Text(criteria.Value("isUntradeable", "untradeable")).ToLowerInvariant())
        {
            case "true" or "1":
                items = items.Where(item => item.Bool("untradeable"));
                break;
            case "false" or "0":
                // "Tradeable only" sends false.
                items = items.Where(item => !item.Bool("untradeable"));
                break;
        }

        var sorted = Sort(items, Text("sort"), Text("acquiredDate").ToLowerInvariant(),
            Text("sortBy").ToLowerInvariant() == "value" && requestedType is not ("equippable" or "equippables"));

        var countNode = criteria.Value("count", "num", "pageSize");
        var count = JsonValues.HasValue(countNode) && JsonValues.TryInt(countNode, out var parsedCount) ? parsedCount : sorted.Count;
        var start = Math.Max(0, AsInt(criteria.Value("start", "offset", "startIndex")));
        // Some clients use 1-based page numbers instead of an offset.
        if (start == 0 && criteria.Value("page") is { } pageNode && JsonValues.TryInt(JsonValues.HasValue(pageNode) ? pageNode : JsonValue.Create(1), out var page))
        {
            start = (Math.Max(1, page) - 1) * Math.Max(1, count);
        }
        var pageItems = sorted.Skip((int)Math.Min(start, sorted.Count)).Take((int)Math.Max(0, count)).ToList();
        _log.LogInformation("CLUB PAGE start={Start} count={Count} total={Total} returned={Returned}", start, count, sorted.Count, pageItems.Count);

        var args = ClubPresentationService.CustomisationCounts(allItems);
        args["clubPlayers"] = allItems.Count(ClubInventoryService.IsPlayer);
        args["squadPlayers"] = snapshot is null ? squadPlayers : snapshot.Pile("squad").Count(ClubInventoryService.IsPlayer);
        args["consumables"] = allItems.Count(NativeItems.IsConsumable);
        args["managers"] = allItems.Count(item => item.Text("itemType") == "manager");
        args["total"] = allItems.Count;
        args["unassigned"] = snapshot?.CountPile("unassigned") ?? unassigned;
        args["matching"] = sorted.Count;
        // A loan's page card carries its games as its contract (NativeItems.SearchItem), exactly as the SQL page does.
        args["items"] = ClubPresentationService.ToArray(pageItems.Select(item => _fut.Native.SearchItem(item)));
        return TemplateLibrary.Core.Object("club/search-page", args);
    }

    /// <summary>
    /// Highest rated first with stable tie-breakers so paging is deterministic; "acquired date"
    /// and "highest quick sell" reorder that list, keeping rating as the tie-breaker.
    /// </summary>
    /// <summary>
    /// The SQL form of an owned player's quick-sell value (Cards.QuickSellValue over the search query's aliases): nothing on
    /// loan; base cards by tier and rating, specials by their multiplier (token rarities nothing); then the item's reward rules.
    /// Every value from the rules is a bound parameter.
    /// </summary>
    private static string QuickSellValueSql(QuickSellRules quickSell, List<(string Name, object? Value)> parameters)
    {
        string Parameter(object value)
        {
            var name = "qs" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add((name, value));
            return "@" + name;
        }
        // Rating times the multiplier, rounded half up, as QuickSellBand.ValueAt and QuickSellRules.BaseValue do.
        string Times(decimal perRating) => $"FLOOR(p.rating*{Parameter(perRating)}+0.5)";
        string Capped(string value, long maximum) => maximum > 0 ? $"LEAST({Parameter(maximum)}, {value})" : value;
        string Band(QuickSellBand band) => Capped($"GREATEST({Parameter(band.Minimum)}, {Times(band.PerRating)})", band.Maximum);
        string Bands(List<QuickSellBand> bands) =>
            "CASE " + string.Concat(bands.AsEnumerable().Reverse().Select(band => $"WHEN p.rating>={Parameter(band.FromRating)} THEN {Band(band)} "))
            + $"ELSE {Band(bands[0])} END";
        const string tier = "CASE WHEN ct.card_level IN ('gold','silver','bronze') THEN ct.card_level WHEN p.rating>=@valueGold THEN 'gold' WHEN p.rating>=@valueSilver THEN 'silver' ELSE 'bronze' END";
        string TierValue(TierQuickSell tier, bool rare) => rare ? Capped(Times(tier.RarePerRating), tier.RareMaximum) : Times(tier.CommonPerRating);
        string Base(bool rare) => $"CASE {tier} " + string.Concat(quickSell.Tiers.Where(entry => entry.Key != "bronze")
            .Select(entry => $"WHEN {Parameter(entry.Key)} THEN {TierValue(entry.Value, rare)} "))
            + $"ELSE {TierValue(quickSell.Tiers["bronze"], rare)} END";
        var special = "CASE " + (quickSell.ZeroValueRareflags.Count > 0 ? $"WHEN r.native_rareflag=ANY({Parameter(quickSell.ZeroValueRareflags.ToArray())}) THEN 0 " : "")
            + $"ELSE {Bands(quickSell.InForm)} END";
        var value = $"CASE WHEN r.native_rareflag=ANY({Parameter(quickSell.BaseCompetitionRareflags.ToArray())}) THEN {Base(rare: true)} "
            + $"WHEN NOT (ct.is_special OR r.native_rareflag>1) THEN CASE WHEN r.native_rareflag=1 THEN {Base(rare: true)} ELSE {Base(rare: false)} END "
            + $"ELSE {special} END";
        return $"CASE WHEN owned.loan_games>0 THEN 0 WHEN i.reward_source=ANY({Parameter(quickSell.ZeroValueRewardSources.ToArray())}) THEN 0 "
            + $"WHEN i.untradeable THEN LEAST({value}, {Parameter(quickSell.RewardUntradeableCap)}) ELSE {value} END";
    }

    private static List<JsonObject> Sort(IEnumerable<JsonObject> items, string sort, string acquired, bool byQuickSellValue)
    {
        var sorted = items
            .OrderBy(item => -item.Int("rating"))
            .ThenBy(item => -item.Int("rareflag"))
            .ThenBy(item => -ClubInventoryService.ResourceOrAsset(item))
            .ThenBy(item => item.Int("id"))
            .ToList();
        if (acquired is "asc" or "desc")
        {
            var byDate = sorted.OrderBy(item => AsInt(item.Get("timestamp"))).ThenBy(item => AsInt(item.Get("id")));
            return (acquired == "desc"
                ? sorted.OrderByDescending(item => AsInt(item.Get("timestamp"))).ThenByDescending(item => AsInt(item.Get("id")))
                : byDate).ToList();
        }
        if (byQuickSellValue)
        {
            sorted = sorted.OrderBy(item => -AsInt(item.Get("discardValue"))).ToList();
        }
        if (sort.ToLowerInvariant() == "asc")
        {
            sorted.Reverse();
        }
        return sorted;
    }

    /// <summary>
    /// The slot's card subtypes from the picker's search: CardsDLL's club search builder (0x2bf970) writes the edited
    /// slot's vanityTypes as vanitySubTypeIds, beside type equippables. A list, a single number or a comma-separated
    /// string is accepted.
    /// </summary>
    private static HashSet<long> SubtypeList(JsonNode? value) => value switch
    {
        JsonArray list => list.Select(node => JsonValues.Int(node)).Where(id => id > 0).ToHashSet(),
        JsonValue single when JsonValues.IsString(single) => JsonValues.Text(single).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => long.TryParse(part, out var id) ? id : 0).Where(id => id > 0).ToHashSet(),
        JsonValue single => JsonValues.Int(single) is var id and > 0 ? new HashSet<long> { id } : new HashSet<long>(),
        _ => new HashSet<long>(),
    };

    /// <summary>
    /// The My Stadium equippables bucket. <paramref name="slotSubtypes"/> (vanitySubTypeIds) are the card subtypes the
    /// edited slot accepts: only those are offered, so an empty TIFO slot no longer lists every pitch trophy (live 10-02).
    /// <paramref name="slotDefinition"/> (excldef) is the definition already in the slot: results keep to its item family
    /// and leave it out. Without either the slot is empty, which only happens for Crowd, Sidelines and Props slots, so
    /// Match Day items are left out.
    /// </summary>
    private static IEnumerable<JsonObject> Equippables(IReadOnlyList<JsonObject> items, long slotDefinition, ClubRules rules,
        IReadOnlySet<long> slotSubtypes)
    {
        // One card per item: a club's virtual default (the default ball, kit or badge) and an owned card of the same
        // definition both showed, each marked active (local 10-03); the owned card comes first and is kept.
        var equippable = items.Where(item => !rules.NonEquippableItemTypes.Contains(TypeOf(item)))
            .DistinctBy(item => (TypeOf(item), AsInt(item.Get("resourceId")), TypeOf(item) == "vanity" ? AsInt(item.Get("id")) : 0)).ToList();
        if (slotSubtypes.Count > 0)
        {
            equippable = equippable.Where(item => slotSubtypes.Contains(AsInt(item.Get("cardsubtypeid")))).ToList();
        }
        if (slotDefinition == 0)
        {
            return equippable.Where(item => !rules.MatchDayItemTypes.Contains(TypeOf(item)));
        }
        if (equippable.FirstOrDefault(item => AsInt(item.Get("resourceId")) == slotDefinition) is { } slotItem)
        {
            var family = (TypeOf(slotItem), AsInt(slotItem.Get("cardsubtypeid")));
            equippable = equippable.Where(item => (TypeOf(item), AsInt(item.Get("cardsubtypeid"))) == family).ToList();
        }
        return equippable.Where(item => AsInt(item.Get("resourceId")) != slotDefinition);
    }

    private static string TypeOf(JsonObject item) => item.Text("itemType").ToLowerInvariant();

    /// <summary>
    /// The search document: query values first, then the body's nested criteria object and its
    /// scalar values (FIFA 21 PC posts the paging document in the body; the web app uses the query).
    /// </summary>
    private static Criteria SearchCriteria(QueryValues query, JsonNode payload)
    {
        var criteria = new Criteria();
        foreach (var (key, values) in query.Entries.Where(entry => entry.Value.Count > 0))
        {
            criteria.Put(key, values[^1]);
        }
        if (payload is JsonObject body)
        {
            var nested = new[] { "searchCriteria", "criteria" }.Select(body.Get).FirstOrDefault(JsonValues.HasValue) ?? body.Get("filters");
            foreach (var (key, value) in nested as JsonObject ?? new JsonObject())
            {
                criteria.Put(key, value?.DeepClone());
            }
            // Lists are skipped, except the My Stadium picker's slot subtypes (vanitySubTypeIds, CardsDLL 0x2bf970).
            foreach (var (key, value) in body.Where(entry => entry.Value is not (JsonObject or JsonArray)
                || (entry.Value is JsonArray && entry.Key.Equals("vanitySubTypeIds", StringComparison.OrdinalIgnoreCase))))
            {
                criteria.Put(key, value?.DeepClone());
            }
        }
        return criteria;
    }

    /// <summary>
    /// The item ids in <c>exclitem</c>. Squad-screen searches list the squad's cards there (starters, bench and reserves),
    /// taken from the squad as the client holds it, unsaved changes included, so a card just placed in the squad is not
    /// offered again (Python server dcb45e5). The saved squad lags until FIFA saves on leaving the screen.
    /// </summary>
    private static HashSet<long> ExcludedItemIds(Criteria criteria) =>
        JsonValues.Text(criteria.Value("exclitem")).Split(',')
            .Select(part => long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id != 0)
            .ToHashSet();

    private static long AsInt(JsonNode? value) => value is not null && JsonValues.TryInt(value, out var parsed) ? parsed : 0;

    private static string Normalize(string text) =>
        text.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

    /// <summary>Search values by case-insensitive name; the last value set for a name wins.</summary>
    private sealed class Criteria
    {
        private readonly Dictionary<string, JsonNode?> _byLowerName = new(StringComparer.Ordinal);

        public void Put(string name, JsonNode? value) => _byLowerName[name.ToLowerInvariant()] = value;

        public JsonNode? Value(params string[] names) =>
            names.Select(name => name.ToLowerInvariant()).FirstOrDefault(_byLowerName.ContainsKey) is { } found ? _byLowerName[found] : null;
    }
}
