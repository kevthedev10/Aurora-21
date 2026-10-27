using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// The launcher bridge hands FIFA its one launch code for every login, where retail fetched a fresh one each time: a used
/// code keeps working while its player stays active (live 10-04: re-entering FUT 15 minutes after launch was refused).
/// </summary>
public sealed class NativeSessionsTests : IDisposable
{
    private static readonly PlayerIdentity Player = PlayerIdentity.Of("111111111111111111");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-sessions-" + Guid.NewGuid().ToString("N"));

    private static NativeSessions Create(TestRuntime.MovableTime clock, string? store = null) =>
        new(new NativeAuthSettings(900, 7200), clock, store);

    [Fact]
    public void AnUnusedCodeStillExpires()
    {
        var clock = new TestRuntime.MovableTime(1_790_000_000);
        var sessions = Create(clock);
        var code = sessions.IssueCode(Player);
        clock.Now += 901;
        Assert.Null(sessions.PlayerForCode(code));
    }

    [Fact]
    public void AUsedCodeKeepsWorkingWhileItsPlayerIsActive()
    {
        var clock = new TestRuntime.MovableTime(1_790_000_000);
        var sessions = Create(clock);
        var code = sessions.IssueCode(Player);
        Assert.Equal(Player, sessions.PlayerForCode(code));   // the Blaze login at launch
        var sid = sessions.StartSession(Player);
        for (var minute = 0; minute < 180; minute += 30)
        {
            clock.Now += 30 * 60;
            Assert.Equal(Player, sessions.PlayerForSession(sid));
        }
        // Three hours after launch the player leaves FUT and comes back: the same code re-authenticates.
        Assert.Equal(Player, sessions.PlayerForCode(code));
    }

    [Fact]
    public void AUsedCodeStopsAfterTheIdleTimeTheDayCapOrARevoke()
    {
        var clock = new TestRuntime.MovableTime(1_790_000_000);
        var sessions = Create(clock);
        var idle = sessions.IssueCode(Player);
        Assert.NotNull(sessions.PlayerForCode(idle));
        clock.Now += 7201;
        Assert.Null(sessions.PlayerForCode(idle));

        var capped = sessions.IssueCode(Player);
        Assert.NotNull(sessions.PlayerForCode(capped));
        for (var hour = 0; hour < 24; hour++)
        {
            clock.Now += 3600;
            sessions.Touch(Player.Id);
        }
        Assert.Null(sessions.PlayerForCode(capped));

        var revoked = sessions.IssueCode(Player);
        Assert.NotNull(sessions.PlayerForCode(revoked));
        sessions.Revoke(Player.Id);
        Assert.Null(sessions.PlayerForCode(revoked));
    }

    [Fact]
    public void CodesSurviveARestartAndTheStoreHoldsNoUsableCode()
    {
        var clock = new TestRuntime.MovableTime(1_790_000_000);
        var store = Path.Combine(_root, "native-auth-codes.json");
        var code = Create(clock, store).IssueCode(Player);
        var before = Create(clock, store);
        Assert.Equal(Player, before.PlayerForCode(code));
        Assert.DoesNotContain(code, File.ReadAllText(store), StringComparison.Ordinal);

        clock.Now += 1800;
        var restarted = Create(clock, store);
        Assert.Equal(Player, restarted.PlayerForCode(code));
        restarted.Revoke(Player.Id);
        Assert.Null(Create(clock, store).PlayerForCode(code));
    }

    [Fact]
    public void AnUnreadableStoreStartsEmpty()
    {
        var store = Path.Combine(_root, "native-auth-codes.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(store, "{ not json");
        var sessions = Create(new TestRuntime.MovableTime(1_790_000_000), store);
        Assert.Null(sessions.PlayerForCode(new string('a', 40)));
        Assert.Equal(Player, sessions.PlayerForCode(sessions.IssueCode(Player)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
