using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Config;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>The verified, embedded setup package. No paths are resolved against the current directory.</summary>
internal sealed class RelationalSetupPackage
{
    internal sealed record SqlFile(string Path, string Sql);
    private static readonly Lazy<RelationalSetupPackage> Embedded = new(LoadEmbedded);

    private RelationalSetupPackage(string version, string hash, SqlFile[] files, string[] tables, string ledgerSql)
    {
        VersionCode = version;
        PackageSha256 = hash;
        Files = files;
        TableNames = tables;
        LedgerSql = ledgerSql;
    }

    internal string VersionCode { get; }
    internal string PackageSha256 { get; }
    internal IReadOnlyList<SqlFile> Files { get; }
    internal string[] TableNames { get; }
    internal string LedgerSql { get; }
    internal static RelationalSetupPackage Load() => Embedded.Value;

    private static RelationalSetupPackage LoadEmbedded() => Parse(ReadResource("manifest.json"), ReadResource);

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(RelationalSetupPackage).Assembly.GetManifestResourceStream("DatabaseSetup/" + name)
            ?? throw new RuntimeSetupException($"Missing embedded database setup resource: {name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // The injectable reader is used only by tests to verify corrupted-artifact failures.
    internal static RelationalSetupPackage Parse(byte[] manifestBytes, Func<string, byte[]> read)
    {
        using var doc = JsonDocument.Parse(manifestBytes);
        var manifest = doc.RootElement;
        Require(manifest.GetProperty("formatVersion").GetInt32() == 2, "Unsupported manifest format.");
        Require(manifest.GetProperty("postgresMajor").GetInt32() == 18, "Setup requires PostgreSQL 18.");
        Require(manifest.GetProperty("namespace").GetString() == "ultimateteam", "Unexpected setup namespace.");
        var version = manifest.GetProperty("versionCode").GetString()!;
        Require(version == "ultimateteam-relational-v1", "Unsupported setup version.");
        var entries = manifest.GetProperty("files").EnumerateArray().Select(f => new
        {
            path = f.GetProperty("path").GetString()!,
            sha256 = f.GetProperty("sha256").GetString()!,
            bytes = f.GetProperty("bytes").GetInt32(),
        }).ToArray();
        var order = manifest.GetProperty("executionOrder").EnumerateArray().Select(f => f.GetString()!).ToArray();
        Require(entries.Length == 13 && entries.Select(f => f.path).Distinct(StringComparer.Ordinal).Count() == 13,
            "Setup must contain 13 distinct SQL files.");
        Require(order.SequenceEqual(entries.Select(f => f.path)), "Setup execution order differs from the hashed file order.");
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(entries.Select(f => new { f.path, f.sha256 })));
        Require(hash == manifest.GetProperty("packageSha256").GetString(), "Setup package hash mismatch.");
        var files = entries.Select(f =>
        {
            Require(Regex.IsMatch(f.path, @"^(schema|seed)/[0-9]{3}-[a-z-]+\.sql$", RegexOptions.CultureInvariant),
                "Invalid setup resource path.");
            var bytes = read(f.path);
            Require(bytes.Length == f.bytes && Hash(bytes) == f.sha256, $"Setup SQL hash/size mismatch: {f.path}.");
            return new SqlFile(f.path, new UTF8Encoding(false, true).GetString(bytes));
        }).ToArray();
        Require(files[0].Path == "schema/001-tables.sql", "Missing initial table script.");
        var sql = files[0].Sql;
        const string begin = "--BEGIN-LEDGER-DDL";
        const string end = "--END-LEDGER-DDL";
        var start = sql.IndexOf(begin, StringComparison.Ordinal);
        var finish = sql.IndexOf(end, StringComparison.Ordinal);
        Require(start >= 0 && finish > start, "Missing ledger DDL markers.");
        var ledger = sql[(start + begin.Length)..finish];
        var tables = Regex.Matches(sql, @"CREATE TABLE (?:IF NOT EXISTS )?ultimateteam\.([a-z_]+)\s*\(",
                RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        Require(tables.Length == 67 && tables.Distinct(StringComparer.Ordinal).Count() == 67, "Unexpected setup table list.");
        return new RelationalSetupPackage(version, hash, files, tables, ledger);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string error)
    {
        if (!condition) throw new RuntimeSetupException(error);
    }
}
