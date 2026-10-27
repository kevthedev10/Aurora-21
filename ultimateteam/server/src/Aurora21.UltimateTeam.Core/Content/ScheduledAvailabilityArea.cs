using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>An explicit availability instruction, keyed by stable content identity rather than a whole live list.</summary>
public sealed class ScheduledAvailabilityArea(GameContext game, IContentArea store, IContentArea? tokenStore = null) : IContentArea
{
    public string Name => "content_window";
    public int ApplyOrder => 140;
    public JsonObject NormalizeKey(JsonObject key)
    {
        var kind = PromoEdits.Text(key, "kind", 20);
        var target = PromoEdits.Text(key, "target", 80);
        if (kind is not ("store" or "token" or "sbc" or "objective")) throw new ContentChangeException(400, "Availability kind is store, token, sbc or objective.");
        if (kind != "sbc" && (!long.TryParse(target, out var number) || number < 1)) throw new ContentChangeException(400, "The target needs a positive content id.");
        return new JsonObject { ["kind"] = kind, ["target"] = target };
    }
    private static JsonObject RuleKey => new() { ["file"] = "objectives", ["path"] = new JsonArray("live") };
    private static JsonObject SbcKey => new() { ["scope"] = "live" };
    private IContentArea Area(JsonObject key) => key["kind"]!.GetValue<string>() switch
    {
        "store" => store,
        "token" => tokenStore ?? throw new ContentChangeException(400, "Token store windows are unavailable."),
        "sbc" => new SbcLiveArea(game),
        _ => new RulesArea(),
    };
    private JsonObject ListKey(JsonObject key) => key["kind"]!.GetValue<string>() switch
    { "store" or "token" => new JsonObject { ["scope"] = "public" }, "sbc" => SbcKey, _ => RuleKey };
    private static string IdField(JsonObject key) => key["kind"]!.GetValue<string>() switch { "store" or "token" => "packId", "sbc" => "setKey", _ => "group" };
    private static string ListField(JsonObject key) => key["kind"]!.GetValue<string>() == "objective" ? "value" : "listing";
    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
    {
        var list = await Area(key).ReadAsync(work, ListKey(key));
        var target = key["target"]!.GetValue<string>();
        var found = (list?[ListField(key)] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => r[IdField(key)]?.ToString() == target);
        return new JsonObject { ["enabled"] = found?["enabled"]?.DeepClone() ?? JsonValue.Create(false) };
    }
    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation != ContentOperation.Update || after is null) throw new ContentChangeException(400, "Availability is an update with enabled true or false.");
        PromoEdits.Fields(after, "enabled"); PromoEdits.Enabled(after);
        if (!await work.ScalarAsync<bool>("SELECT coalesce(current_setting('ultimateteam.content_publish',true),'')='on'")) return;
        var kind = key["kind"]!.GetValue<string>(); var target = key["target"]!.GetValue<string>();
        bool exists;
        if (kind is "store" or "token") exists = await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.store_packs WHERE pack_id=@id AND NOT coalesce(always_present,false))", ("id", long.Parse(target)));
        else if (kind == "sbc") exists = game.Sbc.Sets.Any(s => s.Key == target) || await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.sbc_sets WHERE set_key=@key)", ("key", target));
        else
        {
            var document = await new RulesArea().ReadAsync(work, new JsonObject { ["file"] = "objectives", ["path"] = new JsonArray("grindGroups") });
            var groups = (document?["value"] as JsonArray)?.OfType<JsonObject>() ?? [];
            var gifts = await new RulesArea().ReadAsync(work, new JsonObject { ["file"] = "objectives", ["path"] = new JsonArray("gifts") });
            var welcome = await new RulesArea().ReadAsync(work, new JsonObject { ["file"] = "objectives", ["path"] = new JsonArray("welcomeGroup") });
            exists = groups.Concat((gifts?["value"] as JsonArray)?.OfType<JsonObject>() ?? []).Any(g => g["id"]?.ToString() == target) || welcome?["value"]?["id"]?.ToString() == target;
        }
        if (!exists) throw new ContentChangeException(422, $"Unknown {kind} content: {target}.");
    }
    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var area = Area(key); var listKey = ListKey(key); var list = await area.ReadAsync(work, listKey);
        var wasMissing = list is null;
        list ??= new JsonObject { [ListField(key)] = new JsonArray() };
        if (key["kind"]!.GetValue<string>() == "objective" && list["value"] is null)
        {
            var document = RuleRows.Rebuild((await RuleRows.ReadAsync(work, "objectives"))["objectives"]);
            var rules = Aurora21.UltimateTeam.Core.Rules.RulesJson.Deserialize<Aurora21.UltimateTeam.Core.Rules.ObjectiveRules>(document.ToJsonString(), "objectives rules");
            list["value"] = new JsonArray(rules.LiveGroupIds.Select(id => (JsonNode)new JsonObject { ["group"] = id, ["enabled"] = true }).ToArray());
        }
        var rows = list[ListField(key)] as JsonArray ?? new JsonArray();
        list[ListField(key)] = rows;
        var target = key["target"]!.GetValue<string>(); var field = IdField(key);
        var row = rows.OfType<JsonObject>().FirstOrDefault(r => r[field]?.ToString() == target);
        if (row is null)
        {
            row = new JsonObject { [field] = key["kind"]!.GetValue<string>() == "sbc" ? JsonValue.Create(target) : JsonValue.Create(long.Parse(target)) };
            if (key["kind"]!.GetValue<string>() is "store" or "token")
            {
                var prices = await work.QueryAsync("SELECT coalesce(price,0),store_untradeable FROM ultimateteam.store_packs WHERE pack_id=@id", r => (Price: r.GetInt64(0), Trade: !r.IsDBNull(1) && r.GetBoolean(1)), ("id", long.Parse(target)));
                row["price"] = prices.Single().Price; row["untradeable"] = prices.Single().Trade;
                if (key["kind"]!.GetValue<string>() == "token")
                {
                    var existing = await work.QueryAsync("SELECT price,untradeable FROM ultimateteam.token_store_listings WHERE pack_id=@id", r => (Price: r.GetInt64(0), Trade: r.IsDBNull(1) || r.GetBoolean(1)), ("id", long.Parse(target)));
                    if (existing.Count == 0) throw new ContentChangeException(400, "Set the token price in Token Store Lineup first.");
                    row["price"] = existing[0].Price; row["untradeable"] = existing[0].Trade;
                }
            }
            rows.Add(row);
        }
        row["enabled"] = after!["enabled"]!.DeepClone();
        await area.ValidateAsync(work, wasMissing ? ContentOperation.Insert : ContentOperation.Update, listKey, list);
        await area.ApplyAsync(work, wasMissing ? ContentOperation.Insert : ContentOperation.Update, listKey, list);
    }
}
