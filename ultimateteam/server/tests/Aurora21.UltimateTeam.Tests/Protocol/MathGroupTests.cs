using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// Windows maths groups (Python server 43d3bac, c3992c5): FIFA's maths differs between Windows builds, so online searchers
/// pair only within one group. A player's group comes from the launcher's health check (its PowerShell User-Agent names the
/// Windows build) or from the maths bridge's report of the ucrtbase it switched FIFA to.
/// </summary>
public sealed class MathGroupTests
{
    private const long Now = 1_790_000_000;
    private const string Windows11 = "Mozilla/5.0 (Windows NT; Windows NT 10.0; en-US) WindowsPowerShell/5.1.26100.1882";
    private const string Windows10 = "Mozilla/5.0 (Windows NT; Windows NT 10.0; en-US) WindowsPowerShell/5.1.19041.4648";

    private static MathGroupRules Rules(bool enabled = true) => new()
    {
        Enabled = enabled,
        NewBuild = 26100,
        BridgeGroups = new Dictionary<long, string> { [19041] = "pre24h2", [22621] = "pre24h2", [26100] = "24h2" },
    };

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "ultimateteam21-math-" + Guid.NewGuid().ToString("N"), "state", "windows-builds.json");

    [Fact]
    public void AWindowsBuildOrABridgedUcrtbaseDecidesTheGroup()
    {
        var path = TempPath();
        try
        {
            var groups = new MathGroups(path, Rules(), NullLogger.Instance);
            groups.Note("111111", Windows11, null, Now);
            groups.Note("222222", Windows10, null, Now);
            Assert.Equal(("24h2", "pre24h2"), (groups.GroupOf("111111"), groups.GroupOf("222222")));
            Assert.False(groups.Compatible("111111", "222222"));
            // An unknown group pairs with anyone; an unrelated request records nothing.
            groups.Note("333333", "Mozilla/5.0", "garbage", Now);
            Assert.Null(groups.GroupOf("333333"));
            Assert.True(groups.Compatible("111111", "333333") && groups.Compatible("333333", "222222"));

            // The bridge switched the Windows 10 player to the 24H2 ucrtbase: same maths as Windows 11.
            groups.Note("222222", null, "ucrt=10.0.26100.1; sha256=0123456789abcdef; fma3=1", Now + 1);
            Assert.Equal("24h2", groups.GroupOf("222222"));
            Assert.True(groups.Compatible("111111", "222222"));
            // Without FMA3, or with a ucrtbase never measured, it is a group of its own.
            groups.Note("222222", null, "ucrt=10.0.26100.1;sha256=0123456789ABCDEF;fma3=0", Now + 2);
            Assert.Equal("24h2-nofma3", groups.GroupOf("222222"));
            groups.Note("222222", null, "ucrt=10.0.25000.1; sha256=0123456789abcdef; fma3=1", Now + 3);
            Assert.Equal("ucrt25000", groups.GroupOf("222222"));
            // A new launch's health check drops the last launch's bridge report.
            groups.Note("222222", Windows10, null, Now + 4);
            Assert.Equal("pre24h2", groups.GroupOf("222222"));

            // Kept in state/windows-builds.json: a restart still knows each group.
            var restarted = new MathGroups(path, Rules(), NullLogger.Instance);
            Assert.Equal(("24h2", "pre24h2"), (restarted.GroupOf("111111"), restarted.GroupOf("222222")));
            // The rule off pairs everyone.
            Assert.True(new MathGroups(path, Rules(enabled: false), NullLogger.Instance).Compatible("111111", "222222"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    [Fact]
    public void ABridgedCpuWithoutAvx2IsAGroupOfItsOwn()
    {
        var path = TempPath();
        try
        {
            var groups = new MathGroups(path, Rules(), NullLogger.Instance);
            groups.Note("111111", null, "v=1; ucrt=10.0.26100.1742; sha256=2E5FB14B7BF85402; fma3=1; avx2=1", Now);
            // A bridge from before 10-07 sends no avx2: it keeps the AVX2 group, as before.
            groups.Note("222222", null, "v=1; ucrt=10.0.26100.1742; sha256=2E5FB14B7BF85402; fma3=1", Now);
            // An FX-8300: FMA3 but no AVX2.
            groups.Note("333333", null, "v=1; ucrt=10.0.26100.1742; sha256=2E5FB14B7BF85402; fma3=1; avx2=0", Now);
            groups.Note("444444", null, "v=1;ucrt=10.0.26100.1742;sha256=2E5FB14B7BF85402;fma3=0;avx2=0", Now);
            Assert.Equal(("24h2", "24h2"), (groups.GroupOf("111111"), groups.GroupOf("222222")));
            Assert.Equal(("24h2-noavx2", "24h2-nofma3-noavx2"), (groups.GroupOf("333333"), groups.GroupOf("444444")));
            Assert.True(groups.Compatible("111111", "222222"));
            Assert.False(groups.Compatible("111111", "333333") || groups.Compatible("333333", "444444"));
            // Kept across a restart.
            Assert.Equal("24h2-noavx2", new MathGroups(path, Rules(), NullLogger.Instance).GroupOf("333333"));
            // splitNoAvx2 false pairs it with the AVX2 players again.
            var noSplit = Rules();
            noSplit.SplitNoAvx2 = false;
            Assert.True(new MathGroups(path, noSplit, NullLogger.Instance).Compatible("111111", "333333"));
            // A new launch's health check drops the last launch's CPU report with its bridge report.
            groups.Note("333333", Windows11, null, Now + 1);
            Assert.Equal("24h2", groups.GroupOf("333333"));
            Assert.True(MathGroups.IsBridgeReport("v=1; ucrt=10.0.26100.1742; sha256=2E5FB14B7BF85402; fma3=1; avx2=0"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    [Fact]
    public void TheBootstrapRequestNotesTheCallersGroup()
    {
        using var rt = TestRuntime.Create();
        var headers = new[] { new KeyValuePair<string, string>("User-Agent", Windows10) };
        rt.Router.Handle(new FutRequest("GET", "/ultimateteam/auth/bootstrap", headers, Array.Empty<byte>()));
        Assert.Equal("pre24h2", rt.Game.MathGroups.GroupOf(rt.Game.Player.Id));
        var bridge = new[] { new KeyValuePair<string, string>("X-Aurora21-Math", "ucrt=10.0.26100.1; sha256=0123456789abcdef; fma3=1") };
        rt.Router.Handle(new FutRequest("GET", "/ultimateteam/auth/bootstrap", bridge, Array.Empty<byte>()));
        Assert.Equal("24h2", rt.Game.MathGroups.GroupOf(rt.Game.Player.Id));
    }

    [Fact]
    public void SearchersPairOnlyWithinOneMathGroup()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        var path = TempPath();
        try
        {
            var groups = new MathGroups(path, Rules(), NullLogger.Instance);
            groups.Note("111111", Windows11, null, Now);
            groups.Note("222222", Windows10, null, Now);
            groups.Note("333333", Windows11, null, Now);
            var registry = new MatchmakingRegistry(null, NullLogger.Instance, compatible: groups.Compatible);
            var leases = new[] { "111111", "222222", "333333" }.Select(id => players.Acquire(PlayerIdentity.Of(id))).ToList();
            try
            {
                var clients = leases.Select(lease => new BlazeClient(lease.Session, new BlazeResponses(lease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1")).ToList();
                registry.Start("111111", "FUT_Rivals", Array.Empty<byte>(), clients[0]);
                registry.Start("222222", "FUT_Rivals", Array.Empty<byte>(), clients[1]);
                Assert.Null(registry.GameIdFor("111111"));
                Assert.Null(registry.GameIdFor("222222"));
                // The next searcher of the first one's group pairs with it, past the one waiting longer.
                registry.Start("333333", "FUT_Rivals", Array.Empty<byte>(), clients[2]);
                Assert.NotNull(registry.GameIdFor("333333"));
                Assert.Equal(registry.GameIdFor("111111"), registry.GameIdFor("333333"));
                Assert.Null(registry.GameIdFor("222222"));
            }
            finally
            {
                leases.ForEach(lease => lease.Dispose());
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }
}
