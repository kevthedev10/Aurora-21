using System.Net;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>Isolated asset-cache checks: synthetic ids, mocked CDN, no database or live runtime.</summary>
public sealed class PortraitCacheTests
{
    private const long Head = 990_000_001;
    private static readonly byte[] Dds = "DDS "u8.ToArray().Concat(new byte[124]).ToArray();

    [Fact]
    public async Task MissingPortraitIsRememberedForSixtySecondsThenRetried()
    {
        await using var fixture = new Fixture(HttpStatusCode.NotFound);
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Equal(1, fixture.Handler.Calls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task CustomPortraitImmediatelyOverridesARememberedCdn404()
    {
        await using var fixture = new Fixture(HttpStatusCode.NotFound);
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        Directory.CreateDirectory(fixture.Paths.CustomPlayerHeadsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Paths.CustomPlayerHeadsDirectory, $"p{Head}.dds"), Dds);
        Assert.Equal(Dds, await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task AnExistingDiskCacheAlsoOverridesARememberedCdn404()
    {
        await using var fixture = new Fixture(HttpStatusCode.NotFound);
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        var path = Path.Combine(fixture.Paths.ContentCacheDirectory, "fut", "playerheads", "g4", "single", $"p{Head}.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Dds);
        Assert.Equal(Dds, await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OtherHttpFailuresAreNotNegativelyCached(HttpStatusCode status)
    {
        await using var fixture = new Fixture(status);
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task MissingIdMemoryIsBoundedAndExpires()
    {
        await using var fixture = new Fixture(HttpStatusCode.NotFound);
        for (var i = 0; i < 1025; i++) Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head + i));
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        Assert.Equal(1025, fixture.Handler.Calls);
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head + 1024)); // No room to remember this id.
        Assert.Equal(1026, fixture.Handler.Calls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head + 1025));
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head + 1025));
        Assert.Equal(1027, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ACancelledRequestStillCancelsWhenThePortraitIsRememberedMissing()
    {
        await using var fixture = new Fixture(HttpStatusCode.NotFound);
        Assert.Null(await fixture.Cache.GetPlayerHeadAsync(Head));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Cache.GetPlayerHeadAsync(Head, cancelled.Token));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    private sealed class PortraitClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class PortraitHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora21-portrait-test-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        public RuntimePaths Paths { get; }
        public PortraitClock Clock { get; } = new();
        public PortraitHandler Handler { get; }
        public ContentCache Cache { get; }

        public Fixture(HttpStatusCode status)
        {
            Directory.CreateDirectory(_root);
            Paths = new RuntimePaths(_root, _root, _root, runtimeRootOverridden: true);
            var source = RulesDirectory();
            T Read<T>(string name) => RulesJson.Deserialize<T>(File.ReadAllText(Path.Combine(source, name + ".json")), name);
            var players = new PlayerCatalog();
            var cards = new Cards(players, Read<CardRules>("cards"), Read<PackRules>("packs"), Read<MarketRules>("market"));
            Handler = new PortraitHandler(status);
            _http = new HttpClient(Handler);
            Cache = new ContentCache(Paths, players, cards, NullLogger.Instance, _http, timeProvider: Clock);
        }

        private static string RulesDirectory()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "ultimateteam", "runtime-source", "ultimateteam21", "rules");
                if (File.Exists(Path.Combine(path, "cards.json"))) return path;
            }
            throw new InvalidOperationException("Cannot locate the bundled rules fixture.");
        }

        public async ValueTask DisposeAsync()
        {
            await Cache.StopAsync();
            _http.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
