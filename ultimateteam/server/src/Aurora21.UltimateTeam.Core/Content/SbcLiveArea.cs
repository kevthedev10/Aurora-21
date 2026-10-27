using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>
/// The live SBC list (migration 142), like the store listing: the catalogue holds every set, and the game shows a set only
/// while it is listed here and switched on. One change, keyed <c>{"scope": "live"}</c>, holds the whole list as
/// <c>{"listing": [{"setKey", "enabled"}]}</c>. It applies after the catalogue edits, so a set added in the same publish can be listed.
/// </summary>
public sealed class SbcLiveArea(GameContext game) : IContentArea
{
    public const string AreaName = "sbc_live";
    public const int MaxListed = 500;

    public string Name => AreaName;

    public int ApplyOrder => 130;

    public JsonObject NormalizeKey(JsonObject key) => key["scope"] is JsonValue value && value.TryGetValue(out string? scope) && scope == "live"
        ? new JsonObject { ["scope"] = "live" }
        : throw new ContentChangeException(400, "The live SBC list key is {scope: live}.");

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) =>
        new() { ["listing"] = Listing(await SbcLiveRows.ReadAsync(work)) };

    public static JsonArray Listing(IEnumerable<SbcLiveEntry> entries) =>
        new(entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (JsonNode?)new JsonObject { ["setKey"] = entry.Key, ["enabled"] = entry.Enabled }).ToArray());

    public ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation != ContentOperation.Update)
            throw new ContentChangeException(400, "The live SBC list is changed as a whole.");
        // Keys may name sets being added in this same changeset; Apply checks them against the catalogue after those inserts.
        _ = Entries(after);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var listing = Entries(after);
        var added = (await SbcSetRows.ReadAsync(work)).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = listing.Where(entry => !added.Contains(entry.Key) && game.Sbc.BundledSet(entry.Key) is null).Select(entry => entry.Key).ToList();
        if (unknown.Count > 0)
            throw new ContentChangeException(400, $"The live SBC list names {string.Join(", ", unknown)}, which is not in the SBC catalogue.");
        await SbcLiveRows.WriteAsync(work, listing);
    }

    private static List<SbcLiveEntry> Entries(JsonObject? after)
    {
        if (after is null || after.Count != 1 || after["listing"] is not JsonArray listing || listing.Count > MaxListed)
            throw new ContentChangeException(400, $"A live SBC list change is {{\"listing\": [up to {MaxListed} {{setKey, enabled}}]}}.");
        var entries = new List<SbcLiveEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in listing)
        {
            if (node is not JsonObject entry || entry.Count != 2 || entry["setKey"] is not JsonValue keyValue || !keyValue.TryGetValue(out string? setKey)
                || !SbcSetArea.IsSetKey(setKey) || !seen.Add(setKey) || entry["enabled"] is not JsonValue enabledValue || !enabledValue.TryGetValue(out bool enabled))
                throw new ContentChangeException(400, "Each listed SBC is {setKey, enabled} with a unique set key and enabled true or false.");
            entries.Add(new SbcLiveEntry(setKey, enabled));
        }
        return entries;
    }
}
