using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using FifaLibrary;
using FrostySdk;
using FrostySdk.Frostbite;
using FrostySdk.Managers;

internal static class CaptionMod
{

    internal static void Build(AssetManager manager, AssetEntry[] assets, string output, JsonObject manifest)
    {
        var edits = manifest["captions"]!.AsArray().OfType<JsonObject>().ToDictionary(e => e["stringId"]!.GetValue<string>(), e => e);
        if (edits.Count != 2 || !edits.ContainsKey("FUT_Champions_TopX_upper") || !edits.ContainsKey("FUT_SQUADBATTLE_TOP100_TAB")) throw new InvalidDataException("Unexpected caption keys");
        foreach (var edit in edits.Values)
        {
            var size = edit["top"]!.GetValue<int>();
            var minimum = edit["mode"]!.GetValue<string>() == "champions" ? 6 : 1;
            if (size < minimum || size > 200 || edit["sourceNumber"]!.GetValue<string>() != "200") throw new InvalidDataException("Invalid caption edit");
        }
        var languages = manifest["languages"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray();
        if (languages.Length != 21 || languages.Distinct().Count() != 21) throw new InvalidDataException("Expected 21 unique languages");
        var textEdits = manifest["textEdits"]!.AsArray().OfType<JsonObject>().ToDictionary(e => e["stringId"]!.GetValue<string>(), e => e);
        if (textEdits.Count == 0 || edits.Keys.Intersect(textEdits.Keys).Any()) throw new InvalidDataException("Invalid currency edit keys");
        var championsEdits = manifest["championsTextEdits"]?.AsArray().OfType<JsonObject>()
            .ToDictionary(e => e["stringId"]!.GetValue<string>(), e => e) ?? new Dictionary<string, JsonObject>();
        if (edits.Keys.Intersect(championsEdits.Keys).Any() || textEdits.Keys.Intersect(championsEdits.Keys).Any())
            throw new InvalidDataException("Champions text overlaps existing caption or currency edit");
        foreach (var edit in textEdits.Values)
            if (!edit["languages"]!.AsObject().Select(e => e.Key).Order().SequenceEqual(languages.Order()))
                throw new InvalidDataException("Currency edit language set differs from manifest");
        foreach (var edit in championsEdits.Values)
            if (edit["languages"]!.AsObject().Count == 0 ||
                edit["languages"]!.AsObject().Any(e => !languages.Contains(e.Key)))
                throw new InvalidDataException("Invalid Champions edit language set");
        var reports = new List<object>();
        var additions = manifest["textAdditions"]!.AsArray().OfType<JsonObject>().ToArray();
        if (additions.Length != 1 || additions[0]["stringId"]?.GetValue<string>() != "FUT_STORE_CAT_KITS_NAME"
            || additions[0]["target"]?.GetValue<string>() != "TOKENS")
            throw new InvalidDataException("Unexpected Store caption additions");
        var entries = assets.Where(e => e.Name.StartsWith("data/loc/", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name).ToArray();
        if (!entries.Select(e => Path.GetFileNameWithoutExtension(e.Name)).Order().SequenceEqual(languages.Order()))
            throw new InvalidDataException("Manifest language set differs from installed game: " + string.Join(", ", entries.Select(e => Path.GetFileNameWithoutExtension(e.Name))));
        if (entries.Length != 21) throw new InvalidDataException($"Expected 21 installed languages, got {entries.Length}");
        Directory.CreateDirectory(output);
        foreach (var entry in entries)
        {
        var language = Path.GetFileNameWithoutExtension(entry.Name);
        var languageChampionsEdits = championsEdits.Where(e => e.Value["languages"]![language] is not null)
            .ToDictionary(e => e.Key, e => e.Value["languages"]![language]!.AsObject());
        var metaEntry = assets.Single(e => e.Name.Equals($"data/loc/{language}-meta.xml", StringComparison.OrdinalIgnoreCase));
        using var dbSource = manager.GetCustomAsset("legacy", entry);
        using var metaSource = manager.GetCustomAsset("legacy", metaEntry);
        var source = Read(dbSource); var metadata = Read(metaSource);
        using var working = new MemoryStream(); working.Write(source); working.Position = 0;
        using var meta = new MemoryStream(metadata);
        using var database = new DbFile(working, meta);
        var before = Snapshot(database);
        var changed = 0;
        var captions = new Dictionary<string, (string Original, string Updated)>();
        foreach (var table in database.GetTables())
        {
            foreach (var row in table.Records.Take(table.NRecords))
            {
                var key = row.GetStringField("stringid");
                if (!edits.ContainsKey(key) && !textEdits.ContainsKey(key) && !languageChampionsEdits.ContainsKey(key)) continue;
                var originalCaption = row.GetStringField("sourcetext");
                string newCaption;
                if (edits.TryGetValue(key, out var edit))
                {
                    if (originalCaption.Split("200").Length != 2) throw new InvalidDataException($"Unexpected caption in {language}: {originalCaption}");
                    newCaption = originalCaption.Replace("200", edit["top"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                }
                else if (textEdits.ContainsKey(key))
                {
                    var textEdit = textEdits[key]["languages"]![language]!.AsObject();
                    newCaption = LocalisationTextEdit.Apply(originalCaption, textEdit, $"{language}/{key}");
                }
                else newCaption = ChampionsTextEdit.Apply(originalCaption, languageChampionsEdits[key], $"{language}/{key}");
                captions.Add(key, (originalCaption, newCaption));
                if (!row.SetField("sourcetext", newCaption)) throw new InvalidDataException("Could not set caption");
                changed++;
            }
        }
        if (changed != edits.Count + textEdits.Count + languageChampionsEdits.Count)
            throw new InvalidDataException($"Expected {edits.Count + textEdits.Count + languageChampionsEdits.Count} localisation rows; found {changed}");
        // The first native table is already at the unsigned-short record limit. Repartition the sorted
        // hash index across both existing tables, preserving every original field and metadata descriptor.
        var tables = database.GetTables().OrderBy(t => t.ToString(), StringComparer.Ordinal).ToArray();
        if (!tables.Select(t => t.ToString()).SequenceEqual(new[] { "LanguageStrings1", "LanguageStrings2" }))
            throw new InvalidDataException("Unexpected localisation table layout");
        var merged = tables[0].ConvertToDataTable().Clone();
        foreach (var table in tables)
            foreach (DataRow row in table.ConvertToDataTable().Rows) merged.ImportRow(row);
        if (!merged.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Order().SequenceEqual(new[] { "hashid", "sourcetext", "stringid" }))
            throw new InvalidDataException("Unexpected localisation fields");
        // Some unrelated native rows carry legacy/stale hashes. Preserve those verbatim; verify the
        // algorithm against every existing Store category key before hashing our new category key.
        foreach (var row in merged.Rows.Cast<DataRow>().Where(r => ((string)r["stringid"]).StartsWith("FUT_STORE_CAT_", StringComparison.Ordinal)))
            if (unchecked((int)FifaUtil.ComputeLanguageHash((string)row["stringid"])) != (int)row["hashid"])
                throw new InvalidDataException("Native localisation hash algorithm mismatch");
        foreach (var addition in additions)
        {
            var key = addition["stringId"]!.GetValue<string>();
            var hash = unchecked((int)FifaUtil.ComputeLanguageHash(key));
            if (merged.Rows.Cast<DataRow>().Any(r => (string)r["stringid"] == key || (int)r["hashid"] == hash))
                throw new InvalidDataException("Store caption already exists or its hash collides");
            var row = merged.NewRow();
            row["stringid"] = key; row["sourcetext"] = addition["target"]!.GetValue<string>(); row["hashid"] = hash;
            merged.Rows.Add(row);
        }
        var ordered = merged.Rows.Cast<DataRow>().OrderBy(r => (int)r["hashid"]).ToArray();
        for (var index = 0; index < tables.Length; index++)
        {
            var partition = merged.Clone();
            foreach (var row in ordered.Skip(index * ushort.MaxValue).Take(ushort.MaxValue)) partition.ImportRow(row);
            tables[index].ConvertFromDataTable(partition);
        }
        using var saved = new MemoryStream();
        var dbWriter = new DbWriter(saved, database.Platform);
        database.SaveDb(dbWriter);
        database.ComputeAllCrc(new DbReader(saved, database.Platform), dbWriter);
        var modified = saved.ToArray();
        using var checkData = new MemoryStream(modified);
        using var checkMeta = new MemoryStream(metadata);
        using var check = new DbFile(checkData, checkMeta);
        var after = Snapshot(check);
        if (!before.Keys.Order().SequenceEqual(after.Keys.Order())) throw new InvalidDataException("Table keys changed");
        var differences = new List<string>();
        var originalRows = before.Values.SelectMany(t => t.Rows.Cast<DataRow>()).ToDictionary(r => (string)r["stringid"]);
        var updatedRows = after.Values.SelectMany(t => t.Rows.Cast<DataRow>()).ToDictionary(r => (string)r["stringid"]);
        if (updatedRows.Count != originalRows.Count + additions.Length) throw new InvalidDataException("Unexpected added rows");
        foreach (var (key, a) in originalRows)
        {
            if (!updatedRows.TryGetValue(key, out var b)) throw new InvalidDataException("Original row removed");
                foreach (DataColumn column in a.Table.Columns)
                    if (!Equals(a[column.ColumnName], b[column.ColumnName]))
                    {
                        if (column.ColumnName != "sourcetext" || !captions.TryGetValue(key, out var caption)
                            || a[column.ColumnName].ToString() != caption.Original || b[column.ColumnName].ToString() != caption.Updated)
                            throw new InvalidDataException($"Unrelated change in {key}, column {column.ColumnName}");
                        differences.Add($"{key}: {caption.Original} -> {caption.Updated}");
                    }
        }
        foreach (var addition in additions)
        {
            var key = addition["stringId"]!.GetValue<string>(); var row = updatedRows[key];
            if ((string)row["sourcetext"] != addition["target"]!.GetValue<string>()
                || (int)row["hashid"] != unchecked((int)FifaUtil.ComputeLanguageHash(key)))
                throw new InvalidDataException("Store caption failed roundtrip");
        }
        if (differences.Count != captions.Values.Count(c => c.Original != c.Updated)) throw new InvalidDataException("Unexpected row differences");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, language + ".db"), modified);
        reports.Add(new { language, sourceSha256 = Hash(source), modifiedSha256 = Hash(modified), differences,
            additions = additions.Select(a => a.DeepClone()).ToArray(), tables = after.ToDictionary(e => e.Key, e => e.Value.Rows.Count) });
        manager.ModifyCustomAsset("legacy", entry.Name, modified);
        Console.WriteLine($"CAPTION {language}: verified {differences.Count} changed fields");
        }
        File.WriteAllText(Path.Combine(output, "caption-validation.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        CurrencyArt.Apply(manager, manifest, output);
        var settings = new ModSettings
        {
            Title = "Aurora 21 client UI", Author = "Aurora 21", Category = "UI", Version = manifest["revision"]!.GetValue<string>(),
            Description = "Combines leaderboard captions, Aurora Tokens labels, the Tokens Store tab and client artwork.",
        };
        using var file = File.Create(Path.Combine(output, "aurora21-leaderboards-" + manifest["revision"]!.GetValue<string>() + ".fbmod"));
        using var writer = new FrostbiteModWriter(file, settings);
        writer.WriteProject();
        Console.WriteLine("CLIENT-UI-MOD verified all original fields and the Tokens Store caption addition; exported combined fbmod");
    }
    private static Dictionary<string, DataTable> Snapshot(DbFile db) =>
        db.GetTables().ToDictionary(t => t.ToString(), t => t.ConvertToDataTable());
    private static byte[] Read(Stream stream) { using var result = new MemoryStream(); stream.CopyTo(result); return result.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
