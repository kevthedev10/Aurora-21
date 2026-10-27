using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>One file a client install manifest names.</summary>
public sealed record ClientInstallFile(string Name, long Size, string Sha256);

/// <summary>A validated client install manifest: its version, raw JSON and the files it names.</summary>
public sealed record ClientInstallManifest(string Version, byte[] Json, IReadOnlyList<ClientInstallFile> Files);

/// <summary>
/// The launcher's Install button (Aurora 16 <c>/client/install/*</c>): <c>manifest.json</c> in the install folder and the
/// files it names. The manifest is served only when valid (schema 1, a version of letters, digits and <c>._-</c>, each
/// file a plain name with a 64-hex SHA-256 and a size) and re-read when it changes; only named files are served, never
/// one whose size on disk disagrees (503). Files are immutable and resume with <c>Range: bytes=N-</c>.
/// </summary>
public sealed partial class ClientInstall(string? directory)
{
    public const string ManifestName = "manifest.json";
    private const int MaximumManifestBytes = 64 * 1024;
    private readonly object _gate = new();
    private (DateTime Written, long Length, ClientInstallManifest? Manifest)? _cached;

    /// <summary>The current valid manifest, or null (no folder, no manifest, or one that fails validation).</summary>
    public ClientInstallManifest? Manifest()
    {
        if (directory is null) return null;
        var path = Path.Combine(directory, ManifestName);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumManifestBytes) return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        lock (_gate)
        {
            if (_cached is { } cached && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length) return cached.Manifest;
            ClientInstallManifest? manifest;
            try { manifest = Parse(File.ReadAllBytes(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { manifest = null; }
            _cached = (info.LastWriteTimeUtc, info.Length, manifest);
            return manifest;
        }
    }

    /// <summary>Validates manifest bytes; null when anything is wrong.</summary>
    public static ClientInstallManifest? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("version", out var versionNode) || versionNode.ValueKind != JsonValueKind.String ||
                versionNode.GetString() is not { } version || !Version().IsMatch(version) ||
                !root.TryGetProperty("files", out var filesNode) || filesNode.ValueKind != JsonValueKind.Array) return null;
            var files = new List<ClientInstallFile>();
            foreach (var file in filesNode.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String || name.GetString() is not { } fileName || !FileName().IsMatch(fileName) ||
                    fileName == ManifestName || !file.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
                    !size.TryGetInt64(out var bytes) || bytes <= 0 || !file.TryGetProperty("sha256", out var hash) ||
                    hash.ValueKind != JsonValueKind.String || hash.GetString() is not { } sha || !Sha256().IsMatch(sha)) return null;
                files.Add(new ClientInstallFile(fileName, bytes, sha.ToLowerInvariant()));
            }
            if (files.Count == 0 || files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) return null;
            return new ClientInstallManifest(version, json, files);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Serves the manifest or one file it names; 404 for anything else.</summary>
    public async Task ServeAsync(HttpContext context, string name)
    {
        var manifest = Manifest();
        if (manifest is null)
        {
            await Json(context, 404, """{"error":"no_install_published"}""");
            return;
        }
        if (name == ManifestName)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = manifest.Json.Length;
            if (context.Request.Method != "HEAD") await context.Response.Body.WriteAsync(manifest.Json, context.RequestAborted);
            return;
        }
        var entry = manifest.Files.FirstOrDefault(file => file.Name == name);
        if (entry is null)
        {
            await Json(context, 404, """{"error":"not_found"}""");
            return;
        }
        var path = Path.Combine(directory!, entry.Name);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.Size)
        {
            await Json(context, 503, """{"error":"install_file_unavailable"}""");
            return;
        }
        long start = 0;
        var range = context.Request.Headers.Range.ToString();
        if (range.Length > 0)
        {
            var match = RangeHeader().Match(range);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                start >= entry.Size)
            {
                context.Response.Headers.ContentRange = $"bytes */{entry.Size}";
                await Json(context, 416, """{"error":"range_not_satisfiable"}""");
                return;
            }
        }
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers["X-Content-SHA256"] = entry.Sha256;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.ContentType = "application/octet-stream";
        if (start > 0)
        {
            context.Response.StatusCode = 206;
            context.Response.Headers.ContentRange = $"bytes {start}-{entry.Size - 1}/{entry.Size}";
        }
        context.Response.ContentLength = entry.Size - start;
        if (context.Request.Method == "HEAD") return;
        await context.Response.SendFileAsync(path, start, entry.Size - start, context.RequestAborted);
    }

    private static async Task Json(HttpContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Version();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex FileName();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256();

    [GeneratedRegex(@"^bytes=([0-9]{1,18})-$")]
    private static partial Regex RangeHeader();
}
