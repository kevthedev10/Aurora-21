using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Project21Launcher;

internal sealed record CloudMediaSyncResult(
    bool Enabled,
    bool Changed,
    int Downloaded,
    int Available,
    string? Error);

internal static class CloudMediaManager
{
    private const int ManifestVersion = 1;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumItems = 256;
    private const long MaximumMusicBytes = 80L * 1024 * 1024;
    private const long MaximumVideoBytes = 512L * 1024 * 1024;
    private const long MaximumManifestMediaBytes = 2L * 1024 * 1024 * 1024;
    private static readonly Regex ItemIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly HttpClient Client = CreateClient();

    private sealed class CloudMediaConfiguration
    {
        public bool Enabled { get; init; }
        public string BaseUrl { get; init; } = string.Empty;
    }

    private sealed class AccessStore
    {
        public int Version { get; set; }
        public string ProtectedAccessToken { get; set; } = string.Empty;
    }

    private sealed class CloudMediaManifest
    {
        public int Version { get; set; }
        public List<CloudMediaItem> Items { get; set; } = new();
    }

    private sealed class CloudMediaItem
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private static string CacheRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Project21",
            "MediaCache");

    private static string ActiveManifestPath => Path.Combine(CacheRoot, "active-manifest.json");

    public static IReadOnlyList<string> GetCachedMusicFiles() => GetCachedFiles("music");

    public static IReadOnlyList<string> GetCachedVideoFiles() => GetCachedFiles("video");

    public static async Task<CloudMediaSyncResult> SyncAsync(string launcherRoot, CancellationToken cancellationToken)
    {
        CloudMediaConfiguration configuration = LoadConfiguration(launcherRoot);
        if (!configuration.Enabled)
        {
            return new CloudMediaSyncResult(false, false, 0, GetCachedFiles("music").Count + GetCachedFiles("video").Count, null);
        }

        string? accessToken = LoadAccessToken();

        try
        {
            CloudMediaManifest manifest = await DownloadManifestAsync(configuration.BaseUrl, accessToken, cancellationToken);
            List<CloudMediaItem> items = ValidateManifest(manifest);
            Directory.CreateDirectory(CacheRoot);

            int downloaded = 0;
            bool manifestChanged = !ManifestMatchesActive(manifest);
            using SemaphoreSlim downloadSlots = new(initialCount: 4, maxCount: 4);
            Task[] downloads = items.Select(async item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string localPath = GetLocalPath(item);
                await downloadSlots.WaitAsync(cancellationToken);
                try
                {
                    if (await FileMatchesAsync(localPath, item, cancellationToken)) return;
                    // Four concurrent R2 streams make good use of typical broadband without
                    // opening an unbounded connection for every item in a large playlist.
                    await DownloadItemAsync(configuration.BaseUrl, accessToken, item, localPath, cancellationToken);
                    Interlocked.Increment(ref downloaded);
                }
                finally
                {
                    downloadSlots.Release();
                }
            }).ToArray();
            await Task.WhenAll(downloads);
            bool changed = manifestChanged || downloaded > 0;

            SaveActiveManifest(manifest);
            CleanupInactiveCache(items);
            return new CloudMediaSyncResult(true, changed, downloaded, items.Count, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException or
            JsonException or CryptographicException or InvalidDataException)
        {
            int available = GetCachedFiles("music").Count + GetCachedFiles("video").Count;
            return new CloudMediaSyncResult(true, false, 0, available, exception.Message);
        }
        finally
        {
            accessToken = null;
        }
    }

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private static CloudMediaConfiguration LoadConfiguration(string launcherRoot)
    {
        string path = Path.Combine(launcherRoot, "control-plane.json");
        if (!File.Exists(path)) return new CloudMediaConfiguration();

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            bool enabled =
                root.TryGetProperty("enabled", out JsonElement enabledValue) &&
                enabledValue.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("mediaEnabled", out JsonElement mediaValue) &&
                mediaValue.ValueKind == JsonValueKind.True;
            string baseUrl = root.TryGetProperty("baseUrl", out JsonElement baseUrlValue)
                ? baseUrlValue.GetString() ?? string.Empty
                : string.Empty;
            if (!enabled ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return new CloudMediaConfiguration();
            }

            return new CloudMediaConfiguration
            {
                Enabled = true,
                BaseUrl = baseUrl.TrimEnd('/'),
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CloudMediaConfiguration();
        }
    }

    private static string? LoadAccessToken()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Project21",
            "player-access.json");
        if (!File.Exists(path)) return null;

        byte[]? protectedBytes = null;
        byte[]? clearBytes = null;
        try
        {
            AccessStore? store = JsonSerializer.Deserialize<AccessStore>(File.ReadAllText(path), JsonOptions);
            if (store is null || store.Version != 1 || string.IsNullOrWhiteSpace(store.ProtectedAccessToken))
            {
                return null;
            }

            protectedBytes = Convert.FromBase64String(store.ProtectedAccessToken);
            clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                Encoding.UTF8.GetBytes("Project21.PlayerAccess.v1"),
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private static async Task<CloudMediaManifest> DownloadManifestAsync(
        string baseUrl,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            baseUrl + "/api/device/media/manifest",
            accessToken);
        using HttpResponseMessage response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidDataException("Cloud media access is not authorized. Reconnect Discord in the launcher.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Cloud media manifest returned HTTP {(int)response.StatusCode}.");
        }

        byte[] payload = await ReadBoundedAsync(response.Content, MaximumManifestBytes, cancellationToken);
        CloudMediaManifest? manifest = JsonSerializer.Deserialize<CloudMediaManifest>(payload, JsonOptions);
        return manifest ?? throw new InvalidDataException("Cloud media manifest is empty.");
    }

    private static List<CloudMediaItem> ValidateManifest(CloudMediaManifest manifest)
    {
        if (manifest.Version != ManifestVersion)
        {
            throw new InvalidDataException($"Unsupported cloud media manifest version {manifest.Version}.");
        }
        if (manifest.Items is null)
        {
            throw new InvalidDataException("Cloud media manifest has no items array.");
        }
        if (manifest.Items.Count > MaximumItems)
        {
            throw new InvalidDataException("Cloud media manifest contains too many items.");
        }

        List<CloudMediaItem> enabled = new();
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (CloudMediaItem item in manifest.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.Sha256))
            {
                throw new InvalidDataException("Cloud media manifest contains an incomplete item.");
            }
            if (!item.Enabled) continue;
            string type = item.Type.Trim().ToLowerInvariant();
            string objectPath = item.Path.Replace('\\', '/');
            string extension = Path.GetExtension(objectPath).ToLowerInvariant();
            bool supported =
                type == "music" && extension == ".mp3" ||
                type == "video" && extension is ".mp4" or ".m4v" or ".wmv" or ".avi" or ".mov";
            long maximumBytes = type == "music" ? MaximumMusicBytes : MaximumVideoBytes;

            if (!ItemIdPattern.IsMatch(item.Id) ||
                !ids.Add(item.Id) ||
                !supported ||
                !objectPath.StartsWith(type + "/", StringComparison.Ordinal) ||
                objectPath.Contains("..", StringComparison.Ordinal) ||
                objectPath.Contains("//", StringComparison.Ordinal) ||
                objectPath.Length > 300 ||
                item.Bytes <= 0 ||
                item.Bytes > maximumBytes ||
                !Regex.IsMatch(item.Sha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException($"Cloud media manifest item '{item.Id}' is invalid.");
            }

            item.Type = type;
            item.Path = objectPath;
            item.Sha256 = item.Sha256.ToUpperInvariant();
            if (item.Bytes > MaximumManifestMediaBytes - totalBytes)
            {
                throw new InvalidDataException("Cloud media manifest exceeds the local cache limit.");
            }
            totalBytes += item.Bytes;
            enabled.Add(item);
        }
        return enabled;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string uri, string? accessToken)
    {
        HttpRequestMessage request = new(method, uri);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task DownloadItemAsync(
        string baseUrl,
        string? accessToken,
        CloudMediaItem item,
        string localPath,
        CancellationToken cancellationToken)
    {
        string encodedPath = string.Join(
            "/",
            item.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            baseUrl + "/api/device/media/object/" + encodedPath,
            accessToken);
        request.Headers.Accept.Clear();
        using HttpResponseMessage response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Cloud media item '{item.Id}' returned HTTP {(int)response.StatusCode}.");
        }
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != item.Bytes)
        {
            throw new InvalidDataException($"Cloud media item '{item.Id}' has an unexpected size.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        string temporaryPath = localPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            // Close both streams before replacing the final cache file. Windows does not
            // permit File.Move to rename a file while this process still has it open.
            {
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                written = checked(written + read);
                if (written > item.Bytes)
                {
                    throw new InvalidDataException($"Cloud media item '{item.Id}' exceeded its declared size.");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);

            string actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (written != item.Bytes || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(item.Sha256)))
            {
                throw new InvalidDataException($"Cloud media item '{item.Id}' failed its integrity check.");
            }
            }

            File.Move(temporaryPath, localPath, true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static async Task<bool> FileMatchesAsync(
        string path,
        CloudMediaItem item,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length != item.Bytes) return false;

        byte[] hash = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        }, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(item.Sha256));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new InvalidDataException("Cloud media manifest is too large.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("Cloud media manifest is too large.");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static bool ManifestMatchesActive(CloudMediaManifest manifest)
    {
        try
        {
            if (!File.Exists(ActiveManifestPath)) return false;
            CloudMediaManifest? active = JsonSerializer.Deserialize<CloudMediaManifest>(
                File.ReadAllText(ActiveManifestPath),
                JsonOptions);
            return active is not null &&
                JsonSerializer.Serialize(active, JsonOptions) == JsonSerializer.Serialize(manifest, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void SaveActiveManifest(CloudMediaManifest manifest)
    {
        string temporaryPath = ActiveManifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, ActiveManifestPath, true);
    }

    private static IReadOnlyList<string> GetCachedFiles(string type)
    {
        try
        {
            if (!File.Exists(ActiveManifestPath)) return Array.Empty<string>();
            CloudMediaManifest? manifest = JsonSerializer.Deserialize<CloudMediaManifest>(
                File.ReadAllText(ActiveManifestPath),
                JsonOptions);
            if (manifest is null || manifest.Version != ManifestVersion) return Array.Empty<string>();
            return ValidateManifest(manifest)
                .Where(item => item.Enabled && string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase))
                .Select(GetLocalPath)
                .Where(File.Exists)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Array.Empty<string>();
        }
    }

    private static string GetLocalPath(CloudMediaItem item)
    {
        string extension = Path.GetExtension(item.Path).ToLowerInvariant();
        return Path.Combine(CacheRoot, item.Type, item.Id + extension);
    }

    private static void CleanupInactiveCache(IReadOnlyCollection<CloudMediaItem> activeItems)
    {
        HashSet<string> activePaths = activeItems
            .Select(GetLocalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string type in new[] { "music", "video" })
        {
            string directory = Path.Combine(CacheRoot, type);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (activePaths.Contains(path) || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
