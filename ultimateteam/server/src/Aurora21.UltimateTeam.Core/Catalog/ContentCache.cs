using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Catalog;

public sealed record CachedContent(byte[] Body, string ContentType);

public sealed class ContentFile
{
    public required string RemotePath { get; init; }
    public required string ContentType { get; init; }
}

/// <summary>Resources/Content/fut-content.json: the CDN and the allowlisted asset files.</summary>
public sealed class ContentSettings
{
    public required string CdnBase { get; init; }
    public required int MaxAssetBytes { get; init; }
    public required string PlayerHeadPath { get; init; }
    public required string ItemBackgroundPath { get; init; }
    public required int DownloadTimeoutSeconds { get; init; }
    public required Dictionary<string, ContentFile> Files { get; init; }

    public static ContentSettings Load() =>
        RulesJson.Deserialize<ContentSettings>(EmbeddedResources.ReadText(typeof(ContentSettings).Assembly, "Content/fut-content.json"), "fut-content.json");
}

/// <summary>
/// Public FUT presentation assets requested through the local FUT listener. The bundled
/// copy wins, then the per-user cache, then the EA CDN (validated and cached for next time).
/// Different assets download concurrently; concurrent requests for one asset share one download.
/// </summary>
public sealed class ContentCache : IDisposable
{
    private const int DdsHeaderBytes = 128;
    private const int ReadChunkBytes = 81920;
    private const string UserAgent = "FIFA21-UltimateTeam/0.2";
    private static readonly TimeSpan MissingHeadLifetime = TimeSpan.FromSeconds(60);
    private const int MaximumMissingHeads = 1024;

    private readonly RuntimePaths _paths;
    // Read per request: the cache outlives content reloads, which replace the catalog and cards.
    private readonly Func<(PlayerCatalog Players, Cards Cards)> _content;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ContentSettings _settings = ContentSettings.Load();
    private readonly TimeSpan _downloadTimeout;
    private readonly TimeProvider _time;
    // Only confirmed CDN portrait 404s; local files and uploaded heads always take precedence.
    // Guarded by _lifecycleGate, and bounded even if many distinct missing ids are requested.
    private readonly Dictionary<string, long> _missingHeads = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Lazy<Task<byte[]?>>> _customHeads = new();

    /// <summary>
    /// Reads an operator-uploaded head's DDS (ultimateteam.custom_player_heads, migration 074) by its id. Uploaded heads never
    /// change, so each is read once and kept. Null: no uploaded heads are served.
    /// </summary>
    public Func<long, CancellationToken, Task<byte[]?>>? CustomHeads { get; init; }
    private readonly object _lifecycleGate = new();
    private Task? _stopTask;
    private Task _cancellationTask = Task.CompletedTask;
    private int _disposed;

    /// <param name="http">A caller-owned client (tests); by default the cache creates and disposes its own.</param>
    /// <param name="downloadTimeout">Overrides <c>downloadTimeoutSeconds</c> from fut-content.json (tests).</param>
    public ContentCache(RuntimePaths paths, PlayerCatalog players, Cards cards, ILogger logger, HttpClient? http = null,
        TimeSpan? downloadTimeout = null, TimeProvider? timeProvider = null)
        : this(paths, () => (players, cards), logger, http, downloadTimeout, timeProvider)
    {
    }

    /// <param name="content">The current catalog and cards (the live content), read for each player head.</param>
    public ContentCache(RuntimePaths paths, Func<(PlayerCatalog Players, Cards Cards)> content, ILogger logger, HttpClient? http = null,
        TimeSpan? downloadTimeout = null, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _content = content;
        _logger = logger;
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromSeconds(_settings.DownloadTimeoutSeconds);
        _time = timeProvider ?? TimeProvider.System;
        if (_downloadTimeout <= TimeSpan.Zero)
        {
            throw new InvalidDataException("fut-content.json downloadTimeoutSeconds must be greater than zero");
        }
        _ownsHttp = http is null;
        // The per-download timeout below bounds each request, including reading its body.
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (_ownsHttp)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
    }

    /// <summary>Allowlisted static files (lower-cased request path).</summary>
    public async Task<CachedContent?> GetFutContentAsync(string lowerPath, CancellationToken cancellationToken = default)
    {
        if (!_settings.Files.TryGetValue(lowerPath, out var asset)
            || RuntimePaths.ContainedPath(_paths.ContentCacheDirectory, lowerPath) is not { } cachePath)
        {
            return null;
        }
        var body = await TryReadAsync(RuntimePaths.ContainedPath(_paths.ContentRoot, asset.RemotePath), IsNotEmpty, cancellationToken)
                   ?? await FetchAsync(_settings.CdnBase + asset.RemotePath, cachePath, IsJson, $"FUT content asset {lowerPath}", cancellationToken);
        return body is null ? null : new CachedContent(body, asset.ContentType);
    }

    /// <summary>One native dynamic player-head DDS (custom override, cache, then CDN).</summary>
    public async Task<byte[]?> GetPlayerHeadAsync(long resourceId, CancellationToken cancellationToken = default)
    {
        if (resourceId <= 0)
        {
            return null;
        }

        // Custom rarities use new resource ids; the photo must come from the real card.
        var (players, cards) = _content();
        var meta = players.Find(resourceId);
        // A head uploaded in the panel's card editor and published for this card.
        if (CustomHeads is not null && meta?.IntKeep("customHeadId", 0) is long headId && headId > 0)
        {
            var uploaded = await _customHeads.GetOrAdd(headId, id => new Lazy<Task<byte[]?>>(() => CustomHeads(id, CancellationToken.None))).Value;
            if (uploaded is not null && IsValidDds(uploaded))
            {
                _logger.LogInformation("Serving uploaded player head resource={Resource} head={Head}", resourceId, headId);
                return uploaded;
            }
            _customHeads.TryRemove(headId, out _);
            _logger.LogWarning("Ignoring missing or invalid uploaded player head resource={Resource} head={Head}", resourceId, headId);
        }
        var selectedHead = meta?.IntKeep("headResourceId", 0) ?? 0;
        // An explicit base face is carried by FIFA's fut2dheads.big, not a dynamic DDS.
        if (selectedHead > 0 && selectedHead == meta?.IntKeep("assetId", 0)) return null;
        var customResource = selectedHead > 0 ? selectedHead : resourceId;
        var customPath = Path.Combine(_paths.CustomPlayerHeadsDirectory, $"p{customResource}.dds");
        if (File.Exists(customPath))
        {
            if (await TryReadAsync(customPath, IsValidDds, cancellationToken) is { } custom)
            {
                _logger.LogInformation("Serving custom player head resource={Resource} file={File}", resourceId, Path.GetFileName(customPath));
                return custom;
            }
            _logger.LogWarning("Ignoring invalid custom player head resource={Resource} file={File}", resourceId, customPath);
        }
        var sourceId = selectedHead > 0 ? selectedHead : cards.IsAquaPro(resourceId)
            ? meta?.IntKeep("assetId", resourceId) ?? resourceId
            : meta?.IntKeep("dynamicImageResourceId", resourceId) ?? resourceId;
        var relative = RuleText.Format(_settings.PlayerHeadPath, ("resourceId", sourceId));
        return RuntimePaths.ContainedPath(_paths.ContentCacheDirectory, relative) is { } cachePath
            // A missing head is a real 404, which lets FIFA fall back to the base face in fut2dheads.big.
            ? await FetchAsync($"{_settings.CdnBase}/{relative}", cachePath, IsValidDds, $"dynamic player head resource={resourceId} source={sourceId}", cancellationToken, rememberNotFound: true)
            : null;
    }

    /// <summary>
    /// One card-background DDS, by the rarity's guid and the file name (the route has already constrained both): the
    /// bundled copy, the cache, then the CDN (Python server <c>_cached_item_background</c>). Null when none is available.
    /// </summary>
    public async Task<byte[]?> GetItemBackgroundAsync(string guid, string file, CancellationToken cancellationToken = default)
    {
        var relative = RuleText.Format(_settings.ItemBackgroundPath, ("guid", guid), ("file", file));
        if (await TryReadAsync(RuntimePaths.ContainedPath(_paths.ContentRoot, relative), IsValidDds, cancellationToken) is { } bundled)
        {
            return bundled;
        }
        return RuntimePaths.ContainedPath(_paths.ContentCacheDirectory, relative) is { } cachePath
            ? await FetchAsync($"{_settings.CdnBase}/{relative}", cachePath, IsValidDds, $"card background {guid}/{file}", cancellationToken)
            : null;
    }

    /// <summary>Closes download admission and requests cancellation; does not imply work has finished.</summary>
    public void RequestStop()
    {
        lock (_lifecycleGate)
        {
            if (_disposed != 0)
            {
                return;
            }
            _disposed = 1;
            // Cancellation is requested immediately; callback execution cannot block the admission lock.
            _cancellationTask = _stopping.CancelAsync();
        }
    }

    /// <summary>Cancels and awaits all started shared downloads before disposing owned resources.</summary>
    public Task StopAsync()
    {
        lock (_lifecycleGate)
        {
            RequestStop();
            return _stopTask ??= FinishStopAsync(_downloads.Values.Where(entry => entry.IsValueCreated).Select(entry => entry.Value).ToArray());
        }
    }

    private async Task FinishStopAsync(Task<byte[]?>[] downloads)
    {
        try
        {
            await Task.WhenAll(downloads.Cast<Task>().Append(_cancellationTask)).ConfigureAwait(false);
        }
        finally
        {
            if (_ownsHttp) _http.Dispose();
            _stopping.Dispose();
        }
    }

    public void Dispose() => _ = StopAsync();

    /// <summary>
    /// The cached copy, or a CDN download that passes <paramref name="valid"/>, written to the cache
    /// atomically. Concurrent requests for the same cache path share one download; a requester that
    /// cancels stops waiting without cancelling the download for the others.
    /// </summary>
    private async Task<byte[]?> FetchAsync(string url, string cachePath, Func<byte[], bool> valid, string description,
        CancellationToken cancellationToken, bool rememberNotFound = false)
    {
        if (await TryReadAsync(cachePath, valid, cancellationToken) is { } cached)
        {
            return cached;
        }
        Task<byte[]?> task;
        lock (_lifecycleGate)
        {
            if (_disposed != 0)
            {
                return null;
            }
            if (rememberNotFound && _missingHeads.TryGetValue(cachePath, out var missingSince))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_time.GetElapsedTime(missingSince) < MissingHeadLifetime) return null;
                _missingHeads.Remove(cachePath);
            }
            var download = _downloads.GetOrAdd(cachePath, _ => new Lazy<Task<byte[]?>>(
                () => DownloadToCacheAsync(url, cachePath, valid, description, rememberNotFound), LazyThreadSafetyMode.ExecutionAndPublication));
            // Starting and registering are serialized with stopping, so its snapshot misses no started work.
            task = download.Value;
        }
        return await task.WaitAsync(cancellationToken);
    }

    private async Task<byte[]?> DownloadToCacheAsync(string url, string cachePath, Func<byte[], bool> valid, string description, bool rememberNotFound)
    {
        string? temporary = null;
        try
        {
            // A copy cached meanwhile (by an earlier download or another process) is reused.
            if (await TryReadAsync(cachePath, valid, CancellationToken.None) is { } raced)
            {
                return raced;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timeout.CancelAfter(_downloadTimeout);
            var body = await DownloadAsync(url, timeout.Token);
            if (body.Length > _settings.MaxAssetBytes || !valid(body))
            {
                throw new InvalidDataException("unexpected size or content");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            // A unique temporary name: another server process may be caching the same asset.
            temporary = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporary, body, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(temporary, cachePath, overwrite: true);
            _logger.LogInformation("Cached {Description} ({Bytes} bytes)", description, body.Length);
            return body;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException
                                          or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (rememberNotFound && error is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
            {
                lock (_lifecycleGate)
                {
                    foreach (var key in _missingHeads.Where(pair => _time.GetElapsedTime(pair.Value) >= MissingHeadLifetime)
                                 .Select(pair => pair.Key).ToArray())
                        _missingHeads.Remove(key);
                    if (_missingHeads.Count < MaximumMissingHeads)
                        _missingHeads[cachePath] = _time.GetTimestamp();
                }
            }
            var reason = error is OperationCanceledException && Volatile.Read(ref _disposed) == 0
                ? $"no response within {_downloadTimeout.TotalSeconds:0.#} s"
                : error.Message;
            _logger.LogInformation("Unavailable: {Description}: {Error}", description, reason);
            return null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Unable to remove this download's temporary cache file: {Error}", error.Message);
                }
            }
            // Only confirmed portrait 404s are remembered separately; transient failures retry.
            _downloads.TryRemove(cachePath, out _);
        }
    }

    private static bool IsNotEmpty(byte[] body) => body.Length > 0;

    /// <summary>A CDN error page must never be cached in place of a JSON asset.</summary>
    private static bool IsJson(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }
        try
        {
            using (JsonDocument.Parse(Encoding.UTF8.GetString(body)))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidDds(byte[] body) => body.Length >= DdsHeaderBytes && body.AsSpan().StartsWith("DDS "u8);

    private static async Task<byte[]?> TryReadAsync(string? path, Func<byte[], bool> valid, CancellationToken cancellationToken)
    {
        if (path is null)
        {
            return null;
        }
        try
        {
            var body = await File.ReadAllBytesAsync(path, cancellationToken);
            return valid(body) ? body : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The response body, stopping just past the size limit.</summary>
    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        int read;
        while (buffer.Length <= _settings.MaxAssetBytes && (read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
