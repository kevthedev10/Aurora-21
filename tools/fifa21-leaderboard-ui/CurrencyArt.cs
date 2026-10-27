using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrostySdk.Managers;

internal static class CurrencyArt
{
    internal static JsonObject[] Edits(JsonObject manifest) =>
        manifest["artwork"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];

    internal static string[] AssetNames(JsonObject manifest) =>
        Edits(manifest).Select(e => e["name"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray();

    internal static void Apply(AssetManager manager, JsonObject manifest, string output)
    {
        var reports = new JsonArray();
        foreach (var group in Edits(manifest).GroupBy(e => e["name"]!.GetValue<string>()))
        {
            var name = group.Key;
            if (!name.StartsWith("data/ui/external/ion_fut/", StringComparison.Ordinal) || name.Contains("..") || name.Contains('\\'))
                throw new InvalidDataException("Unexpected client artwork asset path: " + name);
            var entry = manager.EnumerateCustomAssets("legacy").Single(e => e.Name == name);
            using var stream = manager.GetCustomAsset("legacy", entry);
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            var original = bytes.ToArray();
            var modified = Patch(original, group.ToArray());
            var target = ExpectedPath(output, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, modified);
            manager.ModifyCustomAsset("legacy", name, modified);
            reports.Add(new JsonObject { ["name"] = name, ["textures"] = group.Count(),
                ["sourceSha256"] = Hash(original), ["modifiedSha256"] = Hash(modified),
                ["preservedLength"] = modified.Length == original.Length });
        }
        File.WriteAllText(Path.Combine(output, "artwork-validation.json"), reports.ToJsonString());
        Console.WriteLine($"CLIENT-ART verified {Edits(manifest).Length} DDS replacements in {reports.Count} native assets; all other container bytes preserved.");
    }

    internal static string ExpectedPath(string output, string name) => Path.Combine(output, "artwork", name.Replace('/', Path.DirectorySeparatorChar));

    internal static byte[] ReadReplacement(JsonObject edit)
    {
        var file = edit["replacementFile"]!.GetValue<string>();
        if (!Regex.IsMatch(file, "^[a-z0-9-]+\\.dds$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Unexpected replacement filename");
        using var stream = typeof(CurrencyArt).Assembly.GetManifestResourceStream("CurrencyArt." + file)
            ?? throw new InvalidDataException("Missing bundled client artwork: " + file);
        using var result = new MemoryStream(); stream.CopyTo(result);
        var data = result.ToArray();
        if (Hash(data) != edit["replacementSha256"]!.GetValue<string>())
            throw new InvalidDataException("Bundled client artwork hash mismatch: " + file);
        return data;
    }

    internal static byte[] Patch(byte[] original, JsonObject[] edits, Func<JsonObject, byte[]>? read = null)
    {
        read ??= ReadReplacement;
        var modified = original.ToArray();
        var spans = new List<(int Offset, int Length)>();
        foreach (var edit in edits)
        {
            if (Hash(original) != edit["sourceAssetSha256"]!.GetValue<string>())
                throw new InvalidDataException("Currency source asset hash mismatch");
            var member = edit["member"]?.GetValue<string>();
            var (offset, length) = member is null ? (0, original.Length) : FindMember(original, member);
            if (spans.Any(s => offset < s.Offset + s.Length && s.Offset < offset + length))
                throw new InvalidDataException("Overlapping currency replacements");
            spans.Add((offset, length));
            var source = original.AsSpan(offset, length);
            var replacement = read(edit);
            if (Hash(source) != edit["sourceBitmapSha256"]!.GetValue<string>() ||
                Hash(replacement) != edit["replacementSha256"]!.GetValue<string>())
                throw new InvalidDataException("Currency bitmap hash mismatch");
            if (length < 128 || !source[..4].SequenceEqual("DDS "u8) || replacement.Length != length ||
                !source[..128].SequenceEqual(replacement.AsSpan(0, 128)))
                throw new InvalidDataException("Currency DDS layout/header changed");
            replacement.CopyTo(modified, offset);
        }
        return modified;
    }

    private static (int Offset, int Length) FindMember(byte[] data, string member)
    {
        if (data.Length < 16 || !(data.AsSpan(0, 4).SequenceEqual("BIGF"u8) || data.AsSpan(0, 4).SequenceEqual("BIG4"u8)))
            throw new InvalidDataException("Unexpected currency BIG signature");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        var end = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4));
        if (count > 100000 || end > data.Length || end < 16) throw new InvalidDataException("Invalid BIG index");
        var found = new List<(int, int)>();
        var position = 16;
        for (var i = 0; i < count; i++)
        {
            if (position + 8 >= end) throw new InvalidDataException("Truncated BIG index");
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4, 4));
            var stop = Array.IndexOf(data, (byte)0, position + 8, (int)end - position - 8);
            if (stop < 0) throw new InvalidDataException("Unterminated BIG member");
            var name = Encoding.ASCII.GetString(data, position + 8, stop - position - 8);
            position = stop + 1;
            if (name != member) continue;
            if (offset < end || (ulong)offset + length > (ulong)data.Length || length == 0)
                throw new InvalidDataException("Invalid BIG bitmap bounds");
            found.Add(((int)offset, (int)length));
        }
        return found.Count == 1 ? found[0] : throw new InvalidDataException("Missing/duplicate BIG bitmap: " + member);
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    internal static void SelfTest()
    {
        // Two bitmap members with unrelated header/index/padding/script bytes.
        var bitmap = new byte[144]; "DDS "u8.CopyTo(bitmap); bitmap[4] = 124;
        var replacement = bitmap.ToArray(); replacement[128] = 42;
        var big = new byte[450]; "BIGF"u8.CopyTo(big);
        BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(12), 36);
        BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(16), 64);
        BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(20), 144); big[24] = (byte)'2';
        BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(26), 256);
        BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(30), 144); big[34] = (byte)'3';
        bitmap.CopyTo(big, 64); bitmap.CopyTo(big, 256); big[440] = 99;
        var edit = new JsonObject { ["member"] = "2", ["sourceAssetSha256"] = Hash(big),
            ["sourceBitmapSha256"] = Hash(bitmap), ["replacementSha256"] = Hash(replacement) };
        var result = Patch(big, [edit], _ => replacement);
        if (result.Length != big.Length || result[192] != 42 || result.Where((b, i) => i != 192 && b != big[i]).Any())
            throw new InvalidDataException("Unrelated container bytes changed");
        void Refuses(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidDataException("Unsafe replacement accepted");
        }
        Refuses(() => Patch(big, [edit, edit], _ => replacement));
        var wrong = replacement.ToArray(); wrong[12] = 1;
        var badHeader = (JsonObject)edit.DeepClone(); badHeader["replacementSha256"] = Hash(wrong);
        Refuses(() => Patch(big, [badHeader], _ => wrong));
        var wrongMember = (JsonObject)edit.DeepClone(); wrongMember["member"] = "9";
        Refuses(() => Patch(big, [wrongMember], _ => replacement));
        var altered = big.ToArray(); altered[440]++;
        Refuses(() => Patch(altered, [edit], _ => replacement));
        Refuses(() => Patch(big, [edit], _ => bitmap));
        Console.WriteLine("PASS: currency container edits preserve all unrelated bytes; wrong source, payload, header, member and overlapping edits refused.");
    }
}
