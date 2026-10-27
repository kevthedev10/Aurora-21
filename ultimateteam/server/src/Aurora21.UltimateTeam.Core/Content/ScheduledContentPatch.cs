using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>Applies a scheduled edit's delta to current content without replacing unrelated intervening edits.</summary>
public static class ScheduledContentPatch
{
    public static JsonObject? Rebase(string area, JsonObject key, ContentOperation operation,
        JsonObject? baseline, JsonObject? desired, JsonObject? live)
    {
        var context = $"{area} {key.ToJsonString()}";
        if (operation == ContentOperation.Insert)
        {
            if (baseline is not null || desired is null) throw new ContentChangeException(400, "An insert requires a new value and no baseline.");
            if (live is not null) throw Conflict(context, "row already exists");
            return (JsonObject)desired.DeepClone();
        }
        if (baseline is null) throw new ContentChangeException(400, "An existing-row edit requires its baseline.");
        if (operation == ContentOperation.Hide)
        {
            if (desired is not null) throw new ContentChangeException(400, "A hide change has no value.");
            if (!JsonNode.DeepEquals(baseline, live)) throw Conflict(context, "row changed before removal");
            return null;
        }
        if (operation != ContentOperation.Update || desired is null) throw new ContentChangeException(400, "An update requires its new value.");
        if (live is null) throw Conflict(context, "row was removed");
        var merged = Merge(new(true, baseline), new(true, desired), new(true, live), context, area, "");
        var result = (JsonObject)merged.Node!;
        // Concurrent moves to different buckets must not manufacture duplicate promo membership.
        if (area == "promo" && result["buckets"] is JsonArray buckets)
        {
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bucket in buckets.OfType<JsonObject>())
                if (bucket["cards"] is JsonArray cards)
                    foreach (var card in cards)
                        if (card is not null && !members.Add(card.ToJsonString())) throw Conflict(context, "card moved to conflicting buckets");
        }
        return result;
    }

    private readonly record struct Slot(bool Exists, JsonNode? Node);
    private static bool Equal(Slot left, Slot right) => left.Exists == right.Exists && JsonNode.DeepEquals(left.Node, right.Node);
    private static Slot Copy(Slot value) => new(value.Exists, value.Node?.DeepClone());

    private static Slot Merge(Slot before, Slot desired, Slot live, string context, string area, string path)
    {
        if (Equal(before, desired)) return Copy(live);
        if (Equal(before, live) || Equal(desired, live)) return Copy(desired);
        if (before.Node is JsonObject b && desired.Node is JsonObject d && live.Node is JsonObject l)
        {
            var result = new JsonObject();
            foreach (var name in b.Select(p => p.Key).Concat(d.Select(p => p.Key)).Concat(l.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
            {
                var value = Merge(Property(b, name), Property(d, name), Property(l, name), context, area, path + "/" + name);
                if (value.Exists) result[name] = value.Node;
            }
            return new(true, result);
        }
        if (before.Node is JsonArray ba && desired.Node is JsonArray da && live.Node is JsonArray la)
        {
            if (area == "promo" && path.StartsWith("/buckets/", StringComparison.Ordinal) && path.EndsWith("/cards", StringComparison.Ordinal))
                return new(true, MergeMembers(ba, da, la, context, path));
            var field = StableKey(ba, da, la);
            if (field is not null) return new(true, MergeRows(ba, da, la, field, context, area, path));
        }
        throw Conflict(context, $"overlapping edits at {(path.Length == 0 ? "/" : path)}");
    }

    private static Slot Property(JsonObject row, string name) => new(row.TryGetPropertyValue(name, out var node), node);

    private static string? StableKey(params JsonArray[] arrays)
    {
        var rows = arrays.SelectMany(a => a).ToArray();
        if (rows.Length == 0) return null;
        foreach (var name in new[] { "packId", "setKey", "group", "id", "code" })
            if (rows.All(n => n is JsonObject o && o[name] is JsonValue) &&
                arrays.All(a => a.Select(n => n![name]!.ToJsonString()).Distinct(StringComparer.Ordinal).Count() == a.Count)) return name;
        return null;
    }

    private static JsonArray MergeRows(JsonArray before, JsonArray desired, JsonArray live, string field, string context, string area, string path)
    {
        static Dictionary<string, JsonNode?> Index(JsonArray array, string field) =>
            array.ToDictionary(n => n![field]!.ToJsonString(), n => n, StringComparer.Ordinal);
        var b = Index(before, field); var d = Index(desired, field); var l = Index(live, field);
        var result = new JsonArray();
        foreach (var id in l.Keys.Concat(d.Keys).Concat(b.Keys).Distinct(StringComparer.Ordinal))
        {
            var value = Merge(new(b.TryGetValue(id, out var bn), bn), new(d.TryGetValue(id, out var dn), dn),
                new(l.TryGetValue(id, out var ln), ln), context, area, path + "/" + id);
            if (value.Exists) result.Add(value.Node);
        }
        return result;
    }

    private static JsonArray MergeMembers(JsonArray before, JsonArray desired, JsonArray live, string context, string path)
    {
        static bool Valid(JsonArray array) => array.All(n => n is JsonValue v && v.TryGetValue<long>(out _)) &&
            array.Select(n => n!.ToJsonString()).Distinct(StringComparer.Ordinal).Count() == array.Count;
        if (!Valid(before) || !Valid(desired) || !Valid(live)) throw Conflict(context, $"invalid card membership at {path}");
        var b = before.Select(n => n!.ToJsonString()).ToHashSet(StringComparer.Ordinal);
        var d = desired.Select(n => n!.ToJsonString()).ToHashSet(StringComparer.Ordinal);
        var removed = b.Except(d).ToHashSet(StringComparer.Ordinal);
        var result = new JsonArray(live.Where(n => !removed.Contains(n!.ToJsonString())).Select(n => n!.DeepClone()).ToArray());
        var current = result.Select(n => n!.ToJsonString()).ToHashSet(StringComparer.Ordinal);
        foreach (var node in desired)
            if (!b.Contains(node!.ToJsonString()) && current.Add(node.ToJsonString())) result.Add(node.DeepClone());
        return result;
    }

    private static ContentChangeException Conflict(string context, string problem) => new(409, $"Scheduled {context}: {problem}; review this drop.");
}
