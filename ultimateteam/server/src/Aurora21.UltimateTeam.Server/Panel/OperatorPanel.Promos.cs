using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Server.Panel;

public sealed partial class OperatorPanel
{
    private async Task<ContentSnapshot> PromoPreviewSnapshotAsync(bool pending, CancellationToken token, ApiRequest? request = null)
    {
        if (!pending) return game.Live.Current;
        if (request?.Query("dropId") is { } text && long.TryParse(text,out var id))
        {
            var drop=await Schedule.GetDropAsync(id,token);
            return await Schedule.ProjectAsync(drop.StartsAt,id,token);
        }
        return (await _changesets.OpenAsync(token)) is { Changes.Count: > 0 } ? await _changesets.PreviewAsync(token) : game.Live.Current;
    }

    private static DateTimeOffset PreviewTime(string? value, DateTimeOffset fallback)
    {
        if (value is null) return fallback;
        try { return new DateTimeOffset(PromoEdits.Window(new JsonObject { ["starts_at"] = value, ["ends_at"] = "9999-12-31T23:59:59Z" }).Start); }
        catch (ContentChangeException error) { throw new PanelError(400, error.Message); }
    }

    private async Task<(int, object)?> PromosAsync(ApiRequest request)
    {
        try
        {
            var root = request.Parts[0];
            if (root == "promos" && request.Parts is [_, "distribution"])
            {
                if (request.Method != "POST") throw new PanelError(405, "method not allowed");
                return (200, await PromoDistributionAsync(request));
            }
            if (root == "promos" && request.Parts is [_, "availability"])
            {
                if (request.Method != "GET") throw new PanelError(405, "method not allowed");
                var snapshot = await PromoPreviewSnapshotAsync(request.Query("pending") == "1", request.Aborted, request);
                var at = PreviewTime(request.Query("at") is { Length: > 0 } text ? text : null, game.Time.GetUtcNow());
                var active = snapshot.PromoAvailability.ActivePromoIds(at);
                var iconId = snapshot.PromoAvailability.IconPromoId(at);
                var draw = Core.GameContext.CaptureCardDraw(snapshot, at);
                return (200, new
                {
                    revision = snapshot.Revision, at = Iso(at), pending = request.Query("pending") == "1",
                    iconPromo = snapshot.Catalog.Promos.SingleOrDefault(p => p.PromoId == iconId)?.Code,
                    eligibleCards = snapshot.Players.Entries.Count(e => draw.General(e.Key) && !snapshot.Cards.IsGrantOnly(e.Key) && !snapshot.Cards.IsAquaPro(e.Key)),
                    promos = snapshot.Catalog.Promos.OrderBy(p => p.Code).Select(p => new
                    {
                        code = p.Code, name = p.Name, category = p.OddsCategory, generallyAvailable = active.Contains(p.PromoId),
                        cards = snapshot.Catalog.PromoCards.Count(c => c.PromoId == p.PromoId),
                    }), changedClubState = false,
                });
            }
            if (root == "promos" && request.Parts is [_, var promoCode, "buckets", "from-ratings"])
            {
                if (request.Method != "POST") throw new PanelError(405, "method not allowed");
                var snapshot = await PromoPreviewSnapshotAsync((FlagField(request.Body, "pending") ?? false), request.Aborted, request);
                var promo = snapshot.Catalog.Promos.SingleOrDefault(p => p.Code == promoCode) ?? throw new PanelError(404, "No such promo.");
                var ranges = ObjectField(request.Body, "definition")?["ranges"] as JsonArray
                    ?? throw new PanelError(400, "definition.ranges must list rating ranges with code, name, draw_weight, min_rating and max_rating.");
                if (ranges.Count is < 1 or > 200) throw new PanelError(400, "Choose 1–200 rating ranges.");
                var members = snapshot.Catalog.PromoCards.Where(m => m.PromoId == promo.PromoId)
                    .Select(m => snapshot.Catalog.PlayersById[m.PlayerCardDefinitionId]).ToArray();
                var used = new HashSet<long>();
                var codes = new HashSet<string>();
                var buckets = new JsonArray();
                var coveredRatings = new HashSet<long>();
                foreach (var node in ranges)
                {
                    var range = node as JsonObject ?? throw new PanelError(400, "Each range must be an object.");
                    var code = PromoEdits.Code(range);
                    var name = PromoEdits.Text(range, "name", 80);
                    var weight = PromoEdits.Weight(range);
                    if (!JsonValues.IsStrictInt(range["min_rating"], out var min) || !JsonValues.IsStrictInt(range["max_rating"], out var max)
                        || min < 0 || max > 99 || min > max || !codes.Add(code)) throw new PanelError(400, "Rating ranges need unique codes and valid bounds from 0 to 99.");
                    for (var rating = min; rating <= max; rating++)
                        if (!coveredRatings.Add(rating)) throw new PanelError(400, "Rating ranges overlap.");
                    var cards = members.Where(c => c.Rating >= min && c.Rating <= max)
                        .Select(c => snapshot.Catalog.DefinitionsById[c.DefinitionId].NativeResourceId!.Value.Value).Order().ToArray();
                    if (cards.Length == 0) continue;
                    used.UnionWith(cards);
                    buckets.Add(new JsonObject { ["code"] = code, ["name"] = name, ["draw_weight"] = weight,
                        ["cards"] = new JsonArray(cards.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) });
                }
                return (200, new { promo = promo.Code, buckets,
                    samples = used.Order().Take(12).Select(id => CardView(id, snapshot.Players.Find(id)!)).ToArray(),
                    unassigned = members.Select(c => snapshot.Catalog.DefinitionsById[c.DefinitionId].NativeResourceId!.Value.Value).Where(c => !used.Contains(c)).Order(), staged = false });
            }
            IContentArea area = root switch { "promos" => new PromoArea(), "promo-events" => new PromoTimingArea(false), _ => new PromoTimingArea(true) };
            if (request.Method != "GET") throw new PanelError(405, "Stage edits through content/changeset/changes.");
            if (request.Parts.Length == 2)
            {
                var key = area.NormalizeKey(new JsonObject { ["code"] = request.Parts[1] });
                var row = request.Query("dropId") is { Length: >0 } text && long.TryParse(text,out var dropId)
                    ? await game.UnitOfWork.RunReadAsync(0,async work=>await Schedule.DraftBaselineAsync(work,await Schedule.DraftIdAsync(dropId,request.Aborted),area,key),request.Aborted)
                    : await game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key), request.Aborted);
                return row is null ? throw new PanelError(404, "No such definition.") : (200, new { area = area.Name, key, definition = row });
            }
            if (request.Parts.Length != 1) return null;
            var projected=game.Live.Current;
            if(request.Query("dropId") is { Length: >0 } selectedText && long.TryParse(selectedText,out var selectedId))
            {
                var selected=await Schedule.GetDropAsync(selectedId,request.Aborted);
                if(selected.Status is "draft" or "scheduled" or "failed") projected=await Schedule.ProjectAsync(selected.StartsAt,null,request.Aborted);
            }
            var catalog = projected.Catalog;
            var ownedEventCodes = root == "promo-events" && request.Query("standalone") == "1"
                ? await game.UnitOfWork.RunReadAsync(0, async work => await work.QueryAsync("SELECT DISTINCT c.target_key->>'code' FROM ultimateteam.content_changes c JOIN ultimateteam.content_drops d USING(changeset_id) WHERE c.area='promo_event' AND c.operation='insert'", r => r.GetString(0)), request.Aborted)
                : [];
            return (200, new
            {
                area = area.Name, revision = projected.Revision,
                definitions = root switch
                {
                    "promos" => catalog.Promos.OrderBy(p => p.Code).Select(p => (object)new { code = p.Code, name = p.Name, category = p.OddsCategory, weight = p.DrawWeight,
                        status = p.Status, cards = catalog.PromoCards.Count(c => c.PromoId == p.PromoId), buckets = catalog.PromoBuckets.Count(b => b.PromoId == p.PromoId && b.Enabled),
                        artwork = catalog.PromoCards.Where(c => c.PromoId == p.PromoId)
                            .OrderByDescending(c => catalog.PlayersById[c.PlayerCardDefinitionId].Rating).ThenBy(c => c.PlayerCardDefinitionId.Value)
                            .Take(3).Select(c => CardArt(catalog.DefinitionsById[c.PlayerCardDefinitionId].NativeResourceId!.Value.Value)).ToArray() }).ToArray(),
                    "promo-events" => catalog.PromoEvents.Where(e => !ownedEventCodes.Contains(e.Code)).OrderBy(e => e.Code).Select(e => (object)new { code = e.Code, name = e.Name, enabled = e.Enabled,
                        starts_at = Iso(e.StartsAt), ends_at = Iso(e.EndsAt), promos = catalog.PromoEventPromos.Where(l => l.EventId == e.EventId && l.Enabled).Select(l => catalog.Promos.Single(p => p.PromoId == l.PromoId).Code).Order() }).ToArray(),
                    _ => catalog.IconRotations.OrderBy(r => r.Code).Select(r => (object)new { code = r.Code, name = r.Name, enabled = r.Enabled,
                        starts_at = Iso(r.StartsAt), ends_at = Iso(r.EndsAt), promo = catalog.Promos.Single(p => p.PromoId == r.PromoId).Code }).ToArray(),
                },
            });
        }
        catch (ContentChangeException error) { throw new PanelError(error.Status, error.Message); }
        catch (InvalidOperationException error) { throw new PanelError(400, error.Message); }
    }

    private async Task<object> PackOddsAsync(ApiRequest request)
    {
        var id = (WholeField(request.Body, "packId") ?? 0);
        var samples = (WholeField(request.Body, "samples") ?? 500);
        var seed = TextField(request.Body, "seed") ?? "panel-odds";
        if (id <= 0 || samples is < 1 or > 1000 || seed.Length is < 1 or > 100)
            throw new PanelError(400, "Choose a packId, 1–1000 samples and a seed of 1–100 characters.");
        ContentSnapshot snapshot;
        try { snapshot = await PromoPreviewSnapshotAsync((FlagField(request.Body, "pending") ?? false), request.Aborted, request); }
        catch (ContentChangeException error) { throw new PanelError(error.Status, error.Message); }
        var at = PreviewTime(TextField(request.Body, "at"), game.Time.GetUtcNow());
        var config = snapshot.Rules.Packs.Pack(id) ?? throw new PanelError(404, "No such pack.");
        var hits = new SortedDictionary<string, int> { ["rare_82_plus"] = 0, ["rare_84_plus"] = 0, ["rare_88_plus"] = 0 };
        foreach (var category in snapshot.Catalog.Promos.Where(p => p.Status == "active").Select(p => p.OddsCategory).Distinct()) hits[category] = 0;
        // Classification follows each card's eligible route, including per-group overrides.
        var sourceConfigs = config["player_picks"] is JsonArray groups
            ? groups.OfType<JsonObject>().Select(g => MergePreviewSources(config, g)).Append(config).ToArray() : new[] { config };
        var active = sourceConfigs.SelectMany(c => Core.GameContext.CaptureCardDraw(snapshot, at, c).ActivePromos).ToHashSet();
        try
        {
            for (var index = 0; index < samples; index++)
            {
                request.Aborted.ThrowIfCancellationRequested();
                var items = players.Local.Fut.Packs.PreviewPlayerPack(config, GameRandom.Seeded("panel-pack-odds", seed, index), snapshot, at);
                var found = new HashSet<string>();
                foreach (var item in items.OfType<JsonObject>())
                {
                    if (item.Text("itemType") != "player") continue;
                    var resource = item.Int("resourceId");
                    var rare = item.Int("rareflag") != 0;
                    foreach (var floor in new[] { 82, 84, 88 })
                        if (rare && item.Int("rating") >= floor) found.Add($"rare_{floor}_plus");
                    foreach (var category in hits.Keys.Where(c => !c.StartsWith("rare_", StringComparison.Ordinal)))
                        if (snapshot.PromoCardSelector.Contains(category, resource, config.Text("resource_pool") == "totw_1_3" ? null : active)) found.Add(category);
                }
                foreach (var code in found) hits[code]++;
            }
        }
        catch (NotSupportedException error) { throw new PanelError(422, error.Message); }
        catch (InvalidOperationException error) { throw new PanelError(422, $"This pack cannot be simulated: {error.Message}"); }
        return new { packId = id, samples, seed, at = Iso(at), revision = snapshot.Revision, pending = (FlagField(request.Body, "pending") ?? false),
            scope = config["player_picks"] is JsonArray || config.Int("player_pick_count") > 0 ? "At least one offered pick choice or bonus card; selection by the player is not simulated." : "At least one player card in the whole pack.",
            metrics = hits.Select(h => OddsMetric(h.Key, h.Value, (int)samples)), changedClubState = false,
            warnings = new[] { "Sample estimates with 95% confidence intervals, not exact published odds. A zero count does not prove zero probability." } };
    }

    private async Task<object> PromoDistributionAsync(ApiRequest request)
    {
        var snapshot = await PromoPreviewSnapshotAsync(FlagField(request.Body, "pending") ?? false, request.Aborted,request);
        var at = PreviewTime(TextField(request.Body, "at"), game.Time.GetUtcNow());
        var category = TextField(request.Body, "category") ?? throw new PanelError(400, "category is required.");
        var config = ObjectField(request.Body, "filters") ?? new JsonObject();
        var draw = Core.GameContext.CaptureCardDraw(snapshot, at, config);
        var minimum = config.Int("min_rating");
        var maximum = config.Int("max_rating", 99);
        HashSet<long>? Values(string key) => config[key] is JsonArray list ? list.Select(v => JsonValues.Int(v)).ToHashSet() : null;
        var flags = Values("allowed_rareflags");
        var teams = Values("allowed_team_ids");
        var nations = Values("allowed_nations");
        var eligible = snapshot.Players.Entries.Where(e => draw.General(e.Key) && e.Value.Rating >= minimum && e.Value.Rating <= maximum
            && !snapshot.Cards.IsGrantOnly(e.Key) && !snapshot.Cards.IsAquaPro(e.Key) && snapshot.Cards.RarityAllowed(e.Value.Rareflag, flags)
            && (flags is null || flags.Contains(e.Value.Rareflag)) && (teams is null || teams.Contains(e.Value.Int("teamid")))
            && (nations is null || nations.Contains(e.Value.Int("nation")))).Select(e => e.Key).ToHashSet();
        var distribution = draw.Selector.Distribution(category, eligible, activePromoIds: draw.ActivePromos);
        var limit = WholeField(request.Body, "limit") ?? 100;
        if (limit is < 1 or > 1000) throw new PanelError(400, "limit is 1–1000.");
        var shown = distribution.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take((int)limit).ToArray();
        return new { category, at = Iso(at), revision = snapshot.Revision, totalCards = distribution.Count,
            scope = "Conditional on selecting this category, before earlier cards in the pack remove candidates; overlapping promo routes are added.",
            cards = shown.Select(p => new { resourceId = p.Key, probability = p.Value, name = snapshot.Players.Find(p.Key)!.Text("name") }),
            omittedProbability = Math.Max(0, distribution.Values.Sum() - shown.Sum(p => p.Value)), changedClubState = false };
    }

    private static JsonObject MergePreviewSources(JsonObject pack, JsonObject group)
    {
        var merged = pack.DeepClone().AsObject();
        foreach (var key in new[] { "include_general_pool", "promo_codes" })
            if (group.ContainsKey(key)) merged[key] = group[key]?.DeepClone();
        return merged;
    }

    private static object OddsMetric(string code, int hits, int samples)
    {
        const double z = 1.959963984540054;
        var p = (double)hits / samples;
        var denominator = 1 + z * z / samples;
        var center = (p + z * z / (2 * samples)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / samples + z * z / (4.0 * samples * samples)) / denominator;
        return new { code, hits, probability = p, lower95 = Math.Max(0, center - margin), upper95 = Math.Min(1, center + margin) };
    }
}
