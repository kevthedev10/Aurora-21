using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>Phase 4 Step 4: CDN downloads are async, single-flight per asset, bounded by a timeout and cancellable.</summary>
public sealed class ContentCacheTests
{
    // Resource ids outside the catalogue: the head path uses the id itself and no custom override exists.
    private const long HeadA = 990_000_001;
    private const long HeadB = 990_000_002;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly byte[] Dds = "DDS "u8.ToArray().Concat(new byte[124]).ToArray();

    [Fact]
    public async Task PublishedHeadChoicesReplaceBundledProPortraitsWithoutRecreatingTheCache()
    {
        using var rt = TestRuntime.Create();
        const long resource = 167961228, baseId = 189068;
        var upload = new byte[32528];
        "DDS "u8.CopyTo(upload);
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Ok(Dds)));
        using var content = new ContentCache(rt.Game.Paths, () => (rt.Game.Players, rt.Game.Cards),
            NullLogger.Instance, new HttpClient(handler), Patience) { CustomHeads = (_, _) => Task.FromResult<byte[]?>(upload) };
        Assert.NotNull(await content.GetPlayerHeadAsync(resource));
        Assert.Equal(0, handler.Calls(resource));
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var key = new JsonObject { ["resourceId"] = resource };
        async Task Choose(long? existing, long? upload)
        {
            var live = (await rt.Game.UnitOfWork.RunReadAsync(0, w => area.ReadAsync(w, key)))!;
            var card = live["card"]!.DeepClone().AsObject();
            card["head_resource_id"] = existing;
            card["custom_head_id"] = upload;
            await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update,
                new JsonObject { ["card"] = card }, "tester");
            await changes.PublishAsync("choose portrait", "tester");
        }
        await Choose(baseId, null);
        Assert.Null(await content.GetPlayerHeadAsync(resource)); // FIFA's bundled base face.
        Assert.Equal(0, handler.Calls(baseId));
        var dynamicHead = rt.Game.Players.Entries.First(entry => entry.Value.Int("assetId") == baseId
            && entry.Key != resource && entry.Key != baseId).Key;
        await Choose(dynamicHead, null);
        Assert.Equal(Dds, await content.GetPlayerHeadAsync(resource));
        Assert.Equal(1, handler.Calls(dynamicHead));
        var uploadedHead = await rt.Game.UnitOfWork.RunAsync(0, "portrait-fixture", async w => await w.ScalarAsync<long>(
            "INSERT INTO ultimateteam.custom_player_heads(native_asset_id,label,png,dds) VALUES(@asset,'test',@png,@dds) RETURNING head_id",
            ("asset", baseId), ("png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), ("dds", upload)));
        await Choose(null, uploadedHead);
        Assert.Equal(upload, await content.GetPlayerHeadAsync(resource)); // Upload wins over bundled Pro head.
        await Choose(null, null);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(rt.Game.Paths.CustomPlayerHeadsDirectory, $"p{resource}.dds")),
            await content.GetPlayerHeadAsync(resource));
    }

    [Fact]
    public async Task ConcurrentRequestsForOneAssetShareOneDownload()
    {
        using var rt = TestRuntime.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Ok(Dds);
        });
        using var content = Cache(rt, handler);

        var requests = Enumerable.Range(0, 8).Select(_ => content.GetPlayerHeadAsync(HeadA)).ToList();
        await handler.WaitForCallsAsync(HeadA, 1);
        release.SetResult();
        var bodies = await Task.WhenAll(requests).WaitAsync(Patience);

        Assert.All(bodies, body => Assert.Equal(Dds, body));
        Assert.Equal(1, handler.Calls(HeadA));
        Assert.True(File.Exists(HeadCachePath(rt, HeadA)));
        Assert.Equal(Dds, await content.GetPlayerHeadAsync(HeadA));
        Assert.Equal(1, handler.Calls(HeadA));
    }

    [Fact]
    public async Task ADifferentAssetDownloadsWhileAnotherIsStillDownloading()
    {
        using var rt = TestRuntime.Create();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith($"p{HeadA}.dds", StringComparison.Ordinal))
            {
                await releaseA.Task.WaitAsync(token);
            }
            return Ok(Dds);
        });
        using var content = Cache(rt, handler);

        var slow = content.GetPlayerHeadAsync(HeadA);
        await handler.WaitForCallsAsync(HeadA, 1);
        // Before Step 4 one global lock made this wait for the first download.
        Assert.Equal(Dds, await content.GetPlayerHeadAsync(HeadB).WaitAsync(Patience));
        Assert.False(slow.IsCompleted);

        releaseA.SetResult();
        Assert.Equal(Dds, await slow.WaitAsync(Patience));
    }

    [Fact]
    public async Task ATimedOutDownloadIsNotCachedAndTheNextRequestTriesAgain()
    {
        using var rt = TestRuntime.Create();
        var calls = 0;
        var handler = new ScriptedHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            return Ok(Dds);
        });
        using var content = Cache(rt, handler, TimeSpan.FromMilliseconds(250));

        Assert.Null(await content.GetPlayerHeadAsync(HeadA).WaitAsync(Patience));
        Assert.False(File.Exists(HeadCachePath(rt, HeadA)));

        Assert.Equal(Dds, await content.GetPlayerHeadAsync(HeadA).WaitAsync(Patience));
        Assert.Equal(2, handler.Calls(HeadA));
    }

    [Fact]
    public async Task ACancelledRequesterStopsWaitingWithoutCancellingTheSharedDownload()
    {
        using var rt = TestRuntime.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Ok(Dds);
        });
        using var content = Cache(rt, handler);
        using var abandoned = new CancellationTokenSource();

        var cancelled = content.GetPlayerHeadAsync(HeadA, abandoned.Token);
        var waiting = content.GetPlayerHeadAsync(HeadA);
        await handler.WaitForCallsAsync(HeadA, 1);
        abandoned.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience));

        release.SetResult();
        Assert.Equal(Dds, await waiting.WaitAsync(Patience));
        Assert.Equal(1, handler.Calls(HeadA));
    }

    [Fact]
    public async Task DisposingTheCacheCancelsADownloadInProgress()
    {
        using var rt = TestRuntime.Create();
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Ok(Dds);
        });
        var content = Cache(rt, handler);

        var request = content.GetPlayerHeadAsync(HeadA);
        await handler.WaitForCallsAsync(HeadA, 1);
        content.Dispose();

        Assert.Null(await request.WaitAsync(Patience));
        Assert.False(File.Exists(HeadCachePath(rt, HeadA)));
        Assert.Null(await content.GetPlayerHeadAsync(HeadB).WaitAsync(Patience));
    }

    [Fact]
    public async Task AnInvalidOrFailedDownloadIsNotCached()
    {
        using var rt = TestRuntime.Create();
        var handler = new ScriptedHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith($"p{HeadA}.dds", StringComparison.Ordinal)
                ? Ok("<html>not a DDS</html>"u8.ToArray())
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var content = Cache(rt, handler);

        Assert.Null(await content.GetPlayerHeadAsync(HeadA));
        Assert.Null(await content.GetPlayerHeadAsync(HeadB));
        Assert.False(File.Exists(HeadCachePath(rt, HeadA)));
        Assert.False(File.Exists(HeadCachePath(rt, HeadB)));
    }

    [Fact]
    public async Task StopWaitsForCancellationCleanupIsIdempotentAndClosesAdmission()
    {
        using var rt = TestRuntime.Create();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); }
            await release.Task;
            token.ThrowIfCancellationRequested();
            return Ok(Dds);
        });
        using var content = Cache(rt, handler);
        var request = content.GetPlayerHeadAsync(HeadA);
        await handler.WaitForCallsAsync(HeadA, 1);
        var stopped = content.StopAsync();
        await cancelled.Task.WaitAsync(Patience);
        Assert.Same(stopped, content.StopAsync());
        Assert.False(stopped.IsCompleted);
        Assert.Null(await content.GetPlayerHeadAsync(HeadB));
        Assert.Equal(0, handler.Calls(HeadB));
        release.SetResult();
        await stopped.WaitAsync(Patience);
        Assert.Null(await request);
        Assert.Empty(Directory.EnumerateFiles(rt.Game.Paths.ContentCacheDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentStartsAndStopLeaveNoUnobservedDownload()
    {
        using var rt = TestRuntime.Create();
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Ok(Dds);
        });
        using var content = Cache(rt, handler);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            await go.Task;
            return await content.GetPlayerHeadAsync(HeadA + index);
        })).ToArray();
        var stop = Task.Run(async () => { await go.Task; await content.StopAsync(); });
        go.SetResult();
        await stop.WaitAsync(Patience);
        Assert.All(await Task.WhenAll(requests).WaitAsync(Patience), Assert.Null);
        Assert.Null(await content.GetPlayerHeadAsync(HeadB + 100));
    }

    [Fact]
    public async Task HostShutdownCancelsDownloadsBeforeAwaitingListenersAndDisposesPoolLast()
    {
        using var rt = TestRuntime.Create();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); }
            await release.Task;
            token.ThrowIfCancellationRequested();
            return Ok(Dds);
        });
        using var content = Cache(rt, handler);
        var request = content.GetPlayerHeadAsync(HeadA);
        await handler.WaitForCallsAsync(HeadA, 1);
        var relayReleased = false;
        var shutdown = new HostShutdown(Patience, NullLogger.Instance, content.RequestStop,
            token => cancelled.Task.WaitAsync(token), content.StopAsync, _ => Task.FromResult(true),
            () => { Assert.True(content.StopAsync().IsCompletedSuccessfully); relayReleased = true; });
        var stopping = shutdown.StopAsync();
        try
        {
            await cancelled.Task.WaitAsync(Patience);
            Assert.False(stopping.IsCompleted);
            Assert.False(relayReleased);
            release.SetResult();
            Assert.True(await stopping.WaitAsync(Patience));
            Assert.True(relayReleased);
            Assert.Null(await request);
            await rt.Game.StopAsync();
            Assert.True(rt.Game.StopAsync().IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            await content.StopAsync();
        }
    }

    // Sudamericana's background as FIFA asked for it on 2026-09-24 (the rarity's guid in futitemraritytunables.json).
    private const string SudamericanaGuid = "ab719e69-0d3e-430c-8e67-80a106de93c1";
    private const string SmallBackground = "cards_bg_s_1_52_0.dds";

    [Fact]
    public async Task ACardBackgroundDownloadsOnceFromItsCdnPathAndIsCached()
    {
        using var rt = TestRuntime.Create();
        var requested = new ConcurrentQueue<string>();
        var handler = new ScriptedHandler((request, _) =>
        {
            requested.Enqueue(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Ok(Dds));
        });
        using var content = Cache(rt, handler);

        Assert.Equal(Dds, await content.GetItemBackgroundAsync(SudamericanaGuid, SmallBackground));
        Assert.Equal(Dds, await content.GetItemBackgroundAsync(SudamericanaGuid, SmallBackground));

        // One download, from the CDN's own mixed-case folder, then the cached copy.
        var url = Assert.Single(requested);
        Assert.EndsWith($"/2021/fut/items/images/backgrounds/itemBGs/{SudamericanaGuid}/{SmallBackground}", url, StringComparison.Ordinal);
        Assert.True(File.Exists(BackgroundCachePath(rt, SudamericanaGuid, SmallBackground)));
    }

    [Fact]
    public async Task AnInvalidOrMissingCardBackgroundIsNotCached()
    {
        using var rt = TestRuntime.Create();
        var handler = new ScriptedHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("_s_1_52_0.dds", StringComparison.Ordinal)
                ? Ok("<html>not a DDS</html>"u8.ToArray())
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var content = Cache(rt, handler);

        Assert.Null(await content.GetItemBackgroundAsync(SudamericanaGuid, SmallBackground));
        Assert.Null(await content.GetItemBackgroundAsync(SudamericanaGuid, "cards_bg_j_1_52_0.dds"));
        Assert.False(File.Exists(BackgroundCachePath(rt, SudamericanaGuid, SmallBackground)));
        Assert.False(File.Exists(BackgroundCachePath(rt, SudamericanaGuid, "cards_bg_j_1_52_0.dds")));
    }

    /// <summary>
    /// The route FIFA uses, with and without a verified player: the cached DDS as a long-lived binary. A path outside the
    /// guid/cards_bg pattern keeps the empty unknown-endpoint answer. Seeding the cache keeps the test off the network.
    /// </summary>
    [Fact]
    public async Task TheCardBackgroundRouteServesTheDdsToEveryCaller()
    {
        using var rt = TestRuntime.Create();
        var cached = BackgroundCachePath(rt, SudamericanaGuid, SmallBackground);
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        await File.WriteAllBytesAsync(cached, Dds);
        var path = $"/fut/items/images/backgrounds/itemBGs/{SudamericanaGuid}/{SmallBackground}";

        foreach (var response in new[] { await rt.Router.HandleAnonymousAsync(Get(path)), rt.Router.Handle(Get(path)) })
        {
            Assert.NotNull(response);
            Assert.Equal(200, response.Status);
            Assert.Equal(Dds, response.Body);
            Assert.Equal("application/octet-stream", response.ContentType);
            Assert.Contains(response.Headers, header => header.Key == "Cache-Control" && header.Value == "public, max-age=31536000, immutable");
        }
        foreach (var other in new[]
                 {
                     $"/fut/items/images/backgrounds/itemBGs/{SudamericanaGuid}/../{SmallBackground}",
                     $"/fut/items/images/backgrounds/itemBGs/not-a-guid/{SmallBackground}",
                     $"/fut/items/images/backgrounds/itemBGs/{SudamericanaGuid}/cards_bg_s_1_52_0.png",
                 })
        {
            var response = await rt.Router.HandleAnonymousAsync(Get(other));
            Assert.NotNull(response);
            Assert.NotEqual("application/octet-stream", response.ContentType);
        }
    }

    private static FutRequest Get(string path) =>
        new("GET", path, Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>());

    private static string BackgroundCachePath(TestRuntime rt, string guid, string file) =>
        Path.Combine(rt.Game.Paths.ContentCacheDirectory, "fut", "items", "images", "backgrounds", "itemBGs", guid, file);

    private static ContentCache Cache(TestRuntime rt, ScriptedHandler handler, TimeSpan? timeout = null) =>
        new(rt.Game.Paths, rt.Game.Players, rt.Game.Cards, NullLogger.Instance, new HttpClient(handler), timeout ?? Patience);

    private static string HeadCachePath(TestRuntime rt, long resourceId) =>
        Path.Combine(rt.Game.Paths.ContentCacheDirectory, "fut", "playerheads", "g4", "single", $"p{resourceId}.dds");

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>A CDN stand-in that counts requests per player head and answers through a script.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private readonly ConcurrentDictionary<long, int> _calls = new();

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public int Calls(long resourceId) => _calls.GetValueOrDefault(resourceId);

        public async Task WaitForCallsAsync(long resourceId, int count)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (Calls(resourceId) < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"head {resourceId} was requested {Calls(resourceId)} times, expected {count}");
                await Task.Delay(10);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            if (name.StartsWith('p') && long.TryParse(name[1..], out var resourceId))
            {
                _calls.AddOrUpdate(resourceId, 1, (_, calls) => calls + 1);
            }
            return _respond(request, cancellationToken);
        }
    }
}
