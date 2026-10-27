using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Live content (live-content-editing-plan.md, step 1): one content snapshot shared by every club context, pinned per
/// unit of work and replaced whole by a reload that goes through the start-up loader and validation.
/// </summary>
public sealed class LiveContentTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public async Task APinnedUnitOfWorkFinishesOnItsRevisionWhileEveryClubSeesTheReload()
    {
        using var rt = TestRuntime.Create();
        using var other = await rt.Game.ForPlayerAsync(PlayerIdentity.Of("123456789012345678"));
        var before = rt.Game.Live.Current;
        Assert.Equal(1, before.Revision);
        Assert.Same(rt.Game.Live, other.Live);

        using (rt.Game.Live.Pin())
        {
            var next = await rt.Game.ReloadContentAsync();
            Assert.Equal(2, next.Revision);
            Assert.Same(next, rt.Game.Live.Current);
            // Inside the pin every read, from any club context, stays on the revision the unit of work started with.
            Assert.Same(before.Players, rt.Game.Players);
            Assert.Same(before.Cards, other.Cards);
            Assert.Same(before.Rules, other.Rules);
        }

        var current = rt.Game.Live.Current;
        Assert.Same(current.Players, rt.Game.Players);
        Assert.Same(current.Players, other.Players);
        Assert.NotSame(before.Players, rt.Game.Players);
        Assert.Equal(before.Catalog.RevisionSha256, current.Catalog.RevisionSha256);
    }

    [Fact]
    public async Task AReloadThatFailsValidationChangesNothing()
    {
        using var rt = TestRuntime.Create();
        var before = rt.Game.Live.Current;
        // A database migrated past this server's version fails the same readiness check start-up applies.
        await ExecuteAsync(rt, "INSERT INTO ultimateteam.schema_migrations(version) VALUES(999)");

        await Assert.ThrowsAsync<RuntimeSetupException>(() => rt.Game.ReloadContentAsync());
        Assert.Same(before, rt.Game.Live.Current);
        Assert.Same(before.Players, rt.Game.Players);

        await ExecuteAsync(rt, "DELETE FROM ultimateteam.schema_migrations WHERE version=999");
        Assert.Equal(before.Revision + 1, (await rt.Game.ReloadContentAsync()).Revision);
    }

    [Fact]
    public async Task AReloadOfUnchangedContentLeavesResponsesByteIdentical()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_790_000_000));
        var calls = new (string Method, string Path, JsonNode? Body)[]
        {
            ("POST", $"{G}/club", new JsonObject { ["type"] = "player", ["sort"] = "desc", ["start"] = 0, ["count"] = 50 }),
            ("GET", $"{G}/squad/active", null),
            ("GET", $"{G}/club?type=clubInfo&cat=badge&count=20", null),
        };
        var before = calls.Select(call => rt.CallRaw(call.Method, call.Path, call.Body?.DeepClone()).Response.Body).ToArray();

        await rt.Game.ReloadContentAsync();

        for (var i = 0; i < calls.Length; i++)
        {
            Assert.Equal(before[i], rt.CallRaw(calls[i].Method, calls[i].Path, calls[i].Body?.DeepClone()).Response.Body);
        }
    }

    [Fact]
    public void EveryFutRequestReadsContentThroughItsPin()
    {
        using var rt = TestRuntime.Create();
        var unpinned = new List<string>();
        rt.Game.Live.UnpinnedReadObserver = () => { lock (unpinned) unpinned.Add(Environment.StackTrace); };
        try
        {
            rt.Call("GET", $"{G}/user");
            rt.Call("GET", $"{G}/userMassInfo?screen=futlivemsggen4");
            rt.Call("POST", $"{G}/club", new JsonObject { ["type"] = "player", ["sort"] = "desc", ["start"] = 0, ["count"] = 20 });
            rt.Call("GET", $"{G}/squad/active");
            rt.Call("GET", $"{G}/store/purchasegroup/all");
            rt.Call("GET", $"{G}/scmp/campaign");
        }
        finally
        {
            rt.Game.Live.UnpinnedReadObserver = null;
        }
        Assert.True(unpinned.Count == 0, $"{unpinned.Count} content read(s) outside a pin; first:\n{unpinned.FirstOrDefault()}");
    }

    private static async Task ExecuteAsync(TestRuntime rt, string sql)
    {
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
