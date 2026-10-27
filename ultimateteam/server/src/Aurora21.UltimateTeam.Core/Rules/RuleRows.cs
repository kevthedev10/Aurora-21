using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// One individual rule (migration 073): the value at one JSON location of a rules file. <see cref="Path"/> holds object
/// keys as strings and list positions as numbers; <see cref="Value"/> is a scalar (or JSON null), or an empty list or
/// object; <see cref="Ordinal"/> is the position in the file.
/// </summary>
public sealed record RuleRow(string File, JsonArray Path, JsonNode? Value, int Ordinal)
{
    public string PathText => Path.ToJsonString();
}

/// <summary>
/// Game rules as PostgreSQL rows (live-content-editing-plan.md, step 6). The server reads its rules from these rows only.
/// A file's rows are filled from the bundled file when it has none; a key a code update adds to a bundled file is added
/// at start-up; every other change goes through a reviewed content changeset (the operator panel, including its
/// "Import from files").
/// </summary>
public static class RuleRows
{
    // Serializes start-up filling across server processes sharing one database.
    private const long FillLock = 210_065;

    /// <summary>Every value of a document as rows, in document order (comments included, so a file rebuilds exactly).</summary>
    public static List<RuleRow> Flatten(string file, JsonNode document, JsonArray? prefix = null, int firstOrdinal = 0)
    {
        var rows = new List<RuleRow>();
        void Walk(JsonNode? node, List<JsonNode?> path)
        {
            switch (node)
            {
                case JsonObject { Count: > 0 } obj:
                    foreach (var (key, child) in obj) Walk(child, [.. path, JsonValue.Create(key)]);
                    break;
                case JsonArray { Count: > 0 } list:
                    for (var index = 0; index < list.Count; index++) Walk(list[index], [.. path, JsonValue.Create(index)]);
                    break;
                default:
                    if (path.Count == 0) throw new InvalidDataException($"The {file} rules must be a JSON object with at least one value.");
                    rows.Add(new RuleRow(file, new JsonArray(path.Select(segment => segment?.DeepClone()).ToArray()), node?.DeepClone(), firstOrdinal + rows.Count));
                    break;
            }
        }
        Walk(document, prefix is null ? [] : prefix.Select(segment => segment?.DeepClone()).ToList());
        return rows;
    }

    /// <summary>The document the rows describe. List items follow the rows' order; their stored positions only group values.</summary>
    public static JsonObject Rebuild(IEnumerable<RuleRow> rows)
    {
        var root = new JsonObject();
        // A list's items are keyed by stored position while rebuilding, and appended in row order.
        var listItems = new Dictionary<JsonArray, Dictionary<int, JsonNode>>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows.OrderBy(row => row.Ordinal))
        {
            JsonNode container = root;
            for (var i = 0; i < row.Path.Count; i++)
            {
                var last = i == row.Path.Count - 1;
                JsonNode? Next() => last ? row.Value?.DeepClone() : IsPosition(row.Path[i + 1]) ? new JsonArray() : new JsonObject();
                switch (container)
                {
                    case JsonObject obj when row.Path[i] is JsonValue key && key.GetValueKind() == JsonValueKind.String:
                    {
                        var name = key.GetValue<string>();
                        if (last)
                        {
                            if (obj.ContainsKey(name)) throw Conflict(row);
                            obj[name] = Next();
                        }
                        else
                        {
                            if (!obj.TryGetPropertyValue(name, out var child)) obj[name] = child = Next();
                            container = child ?? throw Conflict(row);
                        }
                        break;
                    }
                    case JsonArray list when IsPosition(row.Path[i]):
                    {
                        var position = row.Path[i]!.GetValue<int>();
                        var items = listItems.TryGetValue(list, out var known) ? known : listItems[list] = new Dictionary<int, JsonNode>();
                        if (last)
                        {
                            if (items.ContainsKey(position)) throw Conflict(row);
                            var value = Next();
                            list.Add(value);
                            if (value is not null) items[position] = value;
                        }
                        else
                        {
                            if (!items.TryGetValue(position, out var child))
                            {
                                child = Next()!;
                                list.Add(child);
                                items[position] = child;
                            }
                            container = child;
                        }
                        break;
                    }
                    default:
                        throw Conflict(row);
                }
            }
        }
        return root;
    }

    /// <summary>The value at <paramref name="path"/> of a document (a whole subtree for a list or object), or null when absent.</summary>
    public static JsonNode? Find(JsonNode document, JsonArray path, out bool found)
    {
        JsonNode? node = document;
        found = false;
        foreach (var segment in path)
        {
            if (node is JsonObject obj && segment is JsonValue key && key.GetValueKind() == JsonValueKind.String
                && obj.TryGetPropertyValue(key.GetValue<string>(), out var child)) node = child;
            else if (node is JsonArray list && IsPosition(segment) && segment!.GetValue<int>() < list.Count) node = list[segment.GetValue<int>()];
            else return null;
        }
        found = true;
        return node;
    }

    /// <summary>Every file's rows, in file order.</summary>
    public static async ValueTask<Dictionary<string, List<RuleRow>>> ReadAsync(IUnitOfWork work, string? file = null)
    {
        var rows = await work.QueryAsync("""
            SELECT file, path::text, value::text, ordinal FROM ultimateteam.content_rules
            WHERE @file::text IS NULL OR file = @file ORDER BY file, ordinal
            """, row => new RuleRow(row.GetString(0), (JsonArray)JsonNode.Parse(row.GetString(1))!, JsonNode.Parse(row.GetString(2)),
            row.GetInt32(3)), ("file", file));
        return rows.GroupBy(row => row.File, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
    }

    /// <summary>Every rules file rebuilt from the database, as the JSON text <see cref="GameRules.FromDocuments"/> reads.</summary>
    public static async ValueTask<IReadOnlyDictionary<string, string>> DocumentsAsync(IUnitOfWork work) =>
        (await ReadAsync(work)).ToDictionary(pair => pair.Key, pair => Rebuild(pair.Value).ToJsonString(), StringComparer.Ordinal);

    /// <summary>Replaces one file's rows with the rows of <paramref name="document"/>.</summary>
    public static async ValueTask WriteAsync(IUnitOfWork work, string file, JsonObject document)
    {
        await work.ExecuteAsync("DELETE FROM ultimateteam.content_rules WHERE file = @file", ("file", file));
        await InsertAsync(work, Flatten(file, document));
    }

    private static async ValueTask InsertAsync(IUnitOfWork work, IReadOnlyList<RuleRow> rows)
    {
        if (rows.Count == 0) return;
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.content_rules(file, path, value, ordinal)
            SELECT file, path::jsonb, value::jsonb, ordinal FROM unnest(@files::text[], @paths::text[], @values::text[], @ordinals::int[])
                AS row(file, path, value, ordinal)
            """, ("files", rows.Select(row => row.File).ToArray()), ("paths", rows.Select(row => row.PathText).ToArray()),
            ("values", rows.Select(row => row.Value?.ToJsonString() ?? "null").ToArray()), ("ordinals", rows.Select(row => row.Ordinal).ToArray()));
    }

    /// <summary>
    /// Start-up filling: a file without rows gets every row of its bundled file; a file with rows gets only the keys a code
    /// update added to the bundled file (a missing object key whose parent object exists). Existing values and list items
    /// are never changed here. Returns what it added, for the log.
    /// </summary>
    public static async ValueTask<IReadOnlyList<string>> FillAsync(IUnitOfWork work, IReadOnlyDictionary<string, string> bundled, ILogger log)
    {
        await work.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", FillLock));
        var stored = await ReadAsync(work);
        // A missing key may be a deliberate published removal. The latest published change for that path decides;
        // an import or later insert removes the tombstone without a separate mutable table.
        var removed = await work.QueryAsync("""
            SELECT latest.file, latest.path::text FROM (
                SELECT DISTINCT ON (c.target_key) c.target_key->>'file' AS file,
                    c.target_key->'path' AS path, c.operation
                FROM ultimateteam.content_changes c
                JOIN ultimateteam.content_changesets s ON s.changeset_id = c.changeset_id
                WHERE c.area = 'rules' AND s.status = 'published'
                ORDER BY c.target_key, s.changeset_id DESC, c.change_id DESC
            ) latest WHERE latest.operation = 'hide'
            """, row => (File: row.GetString(0), Path: JsonNode.Parse(row.GetString(1))!.ToJsonString()));
        var hidden = removed.ToHashSet();
        var added = new List<string>();
        foreach (var file in GameRules.DatabaseFiles)
        {
            var document = JsonNode.Parse(bundled[file]) as JsonObject ?? throw new InvalidDataException($"The bundled {file} rules are not a JSON object.");
            if (!stored.TryGetValue(file, out var rows))
            {
                var all = Flatten(file, document);
                await InsertAsync(work, all);
                added.Add($"{file}: all {all.Count} rules");
                continue;
            }
            var current = Rebuild(rows);
            var missing = MissingKeys(current, document, [])
                .Where(entry => !hidden.Contains((file, entry.Path.ToJsonString()))).ToList();
            if (missing.Count == 0) continue;
            foreach (var (path, value) in missing)
            {
                Set(current, path, value);
                added.Add($"{file}: {path.ToJsonString()}");
            }
            await WriteAsync(work, file, current);
        }
        foreach (var entry in added) log.LogInformation("RULES added from the bundled files: {Entry}", entry);
        return added;
    }

    // Object keys of the bundled document missing from the stored one, where the parent object exists in both (lists are skipped).
    private static List<(JsonArray Path, JsonNode? Value)> MissingKeys(JsonNode? stored, JsonNode? bundled, List<JsonNode?> path)
    {
        var missing = new List<(JsonArray, JsonNode?)>();
        if (stored is not JsonObject storedObject || bundled is not JsonObject bundledObject) return missing;
        foreach (var (key, value) in bundledObject)
        {
            List<JsonNode?> childPath = [.. path, JsonValue.Create(key)];
            if (!storedObject.TryGetPropertyValue(key, out var existing))
                missing.Add((new JsonArray(childPath.Select(segment => segment?.DeepClone()).ToArray()), value?.DeepClone()));
            else
                missing.AddRange(MissingKeys(existing, value, childPath));
        }
        return missing;
    }

    /// <summary>
    /// The changes that make the stored rules match the bundled files, at the smallest differing location: a differing value,
    /// a key only in the file (insert), a key only in the database (remove). A list whose length differs is replaced whole.
    /// </summary>
    public static List<(string File, JsonArray Path, ContentDifference Kind, JsonNode? Value)> Differences(
        IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> bundled, string? onlyFile = null)
    {
        var differences = new List<(string, JsonArray, ContentDifference, JsonNode?)>();
        foreach (var file in GameRules.DatabaseFiles.Where(file => onlyFile is null || file == onlyFile))
        {
            var from = stored.TryGetValue(file, out var text) ? JsonNode.Parse(text) : new JsonObject();
            var to = JsonNode.Parse(bundled[file]);
            void Compare(JsonNode? a, JsonNode? b, List<JsonNode?> path)
            {
                JsonArray Here() => new(path.Select(segment => segment?.DeepClone()).ToArray());
                if (JsonNode.DeepEquals(a, b)) return;
                if (a is JsonObject left && b is JsonObject right)
                {
                    foreach (var (key, value) in right)
                    {
                        if (left.TryGetPropertyValue(key, out var existing)) Compare(existing, value, [.. path, JsonValue.Create(key)]);
                        else differences.Add((file, new JsonArray([.. path.Select(segment => segment?.DeepClone()), JsonValue.Create(key)]), ContentDifference.Insert, value?.DeepClone()));
                    }
                    foreach (var (key, _) in left.Where(pair => !right.ContainsKey(pair.Key)))
                        differences.Add((file, new JsonArray([.. path.Select(segment => segment?.DeepClone()), JsonValue.Create(key)]), ContentDifference.Remove, null));
                }
                else if (a is JsonArray listA && b is JsonArray listB && listA.Count == listB.Count && listA.Count > 0)
                {
                    for (var index = 0; index < listA.Count; index++) Compare(listA[index], listB[index], [.. path, JsonValue.Create(index)]);
                }
                else
                {
                    differences.Add((file, Here(), ContentDifference.Update, b?.DeepClone()));
                }
            }
            Compare(from, to, []);
        }
        return differences;
    }

    /// <summary>Sets (or adds) the value at <paramref name="path"/>; a list position equal to the list's length appends.</summary>
    public static void Set(JsonObject document, JsonArray path, JsonNode? value)
    {
        var (parent, last) = Parent(document, path);
        switch (parent)
        {
            case JsonObject obj when last is JsonValue key && key.GetValueKind() == JsonValueKind.String:
                obj[key.GetValue<string>()] = value?.DeepClone();
                break;
            case JsonArray list when IsPosition(last) && last!.GetValue<int>() < list.Count:
                list[last.GetValue<int>()] = value?.DeepClone();
                break;
            case JsonArray list when IsPosition(last) && last!.GetValue<int>() == list.Count:
                list.Add(value?.DeepClone());
                break;
            default:
                throw new InvalidDataException($"No rule can be set at {path.ToJsonString()}.");
        }
    }

    /// <summary>Removes the value at <paramref name="path"/>; later list items move up one position.</summary>
    public static void Remove(JsonObject document, JsonArray path)
    {
        var (parent, last) = Parent(document, path);
        switch (parent)
        {
            case JsonObject obj when last is JsonValue key && key.GetValueKind() == JsonValueKind.String && obj.ContainsKey(key.GetValue<string>()):
                obj.Remove(key.GetValue<string>());
                break;
            case JsonArray list when IsPosition(last) && last!.GetValue<int>() < list.Count:
                list.RemoveAt(last.GetValue<int>());
                break;
            default:
                throw new InvalidDataException($"No rule exists at {path.ToJsonString()}.");
        }
    }

    public static bool IsPosition(JsonNode? segment) =>
        segment is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int position) && position >= 0;

    private static (JsonNode? Parent, JsonNode? Last) Parent(JsonObject document, JsonArray path)
    {
        if (path.Count == 0) throw new InvalidDataException("A rule path names at least one key.");
        var parentPath = new JsonArray(path.Take(path.Count - 1).Select(segment => segment?.DeepClone()).ToArray());
        return (Find(document, parentPath, out var found) is { } parent && found ? parent : null, path[^1]);
    }

    private static InvalidDataException Conflict(RuleRow row) =>
        new($"The {row.File} rules rows disagree about the shape at {row.PathText}.");
}

/// <summary>How a stored rules document differs from a bundled file at one location.</summary>
public enum ContentDifference { Update, Insert, Remove }
