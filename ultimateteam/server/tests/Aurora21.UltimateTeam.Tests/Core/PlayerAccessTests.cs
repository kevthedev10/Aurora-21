using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Tests.Api;
using Aurora21.UltimateTeam.Tests.Protocol;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Hosted access (migration 063): what the panel changes survives a restart, and Discord roles decide who plays and who
/// passes maintenance. Roles come from a synthetic Discord server; nothing talks to discord.com.
/// </summary>
public sealed class PlayerAccessTests
{
    private const string Player = "123456789012345678";
    private const string Stranger = "987654321098765432";

    [Fact]
    public async Task InitialAuroraPolicyEnablesMaintenanceAndOnlyAdmitsFoundersAndDevelopers()
    {
        using var first = TestRuntime.Create();
        using var second = TestRuntime.Create(connectionString: first.Detach());
        var access = await PlayerAccess.LoadAsync(second.Game.UnitOfWork, CancellationToken.None);
        var expected = new[] { "1531102023108395169", "1546887158424670238", "1546861326037549056" };
        Assert.True(access.Settings.Maintenance);
        Assert.Equal(expected, access.Settings.PlayRoles);
        Assert.Equal(expected, access.Settings.BypassRoles);
        foreach (var role in expected)
        {
            var decision = access.Decide(Stranger, new HashSet<string> { role }, 999, false);
            Assert.True(decision.Allowed);
            Assert.True(decision.Bypass);
        }
        foreach (var role in new[] { "1532046167284908133", "1546860858326515732" })
            Assert.Equal(AccessRefusal.NoRole, access.Decide(Stranger, new HashSet<string> { role }, 0, false).Refusal);
        var developerWithWhitelist = access.Decide(Stranger, new HashSet<string> { expected[2], "1546860858326515732" }, 999, false);
        Assert.True(developerWithWhitelist.Allowed);
        Assert.True(developerWithWhitelist.Bypass);
        Assert.Equal(AccessRefusal.RolesUnavailable, access.Decide(Stranger, null, 0, false).Refusal);
        // The seed runs once: later operator choices survive ordinary restarts.
        await access.UpdateSettingsAsync(maintenance: false, playRoles: ["999999999999999999"], bypassRoles: []);
        using var third = TestRuntime.Create(connectionString: second.Detach());
        var saved = await PlayerAccess.LoadAsync(third.Game.UnitOfWork, CancellationToken.None);
        Assert.False(saved.Settings.Maintenance);
        Assert.Equal(["999999999999999999"], saved.Settings.PlayRoles);
        Assert.Empty(saved.Settings.BypassRoles);
    }

    [Fact]
    public async Task MissingPersistentAccessPolicyDoesNotFallBackToOpenAccess()
    {
        using var runtime = TestRuntime.Create();
        await runtime.Game.UnitOfWork.RunAsync(0, "synthetic-missing-access-policy", work =>
            work.ExecuteAsync("DELETE FROM ultimateteam.server_access"), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlayerAccess.LoadAsync(runtime.Game.UnitOfWork, CancellationToken.None));
    }

    [Fact]
    public async Task EditsPersistAcrossARestart()
    {
        string connection;
        using (var first = TestRuntime.Create())
        {
            var access = await PlayerAccess.LoadAsync(first.Game.UnitOfWork, CancellationToken.None);
            await access.UpdatePlayerAsync(Player, true, true, "synthetic note", CancellationToken.None);
            await access.UpdateSettingsAsync(true, "Back soon.", 12, FakeDiscordApi.GuildId, [FakeDiscordApi.PlayRole], [FakeDiscordApi.BypassRole]);
            await access.RecordLaunchAsync(Stranger, "Stranger\u0007 Name", CancellationToken.None);
            connection = first.Detach();
        }
        using var second = TestRuntime.Create(connectionString: connection);
        var reloaded = await PlayerAccess.LoadAsync(second.Game.UnitOfWork, CancellationToken.None);
        var entry = reloaded.Find(Player)!;
        Assert.True(entry.Blocked);
        Assert.True(entry.MaintenanceBypass);
        Assert.Equal("synthetic note", entry.Note);
        var settings = reloaded.Settings;
        Assert.True(settings.Maintenance);
        Assert.Equal("Back soon.", settings.MaintenanceMessage);
        Assert.Equal(12, settings.MaxPlayers);
        Assert.Equal(FakeDiscordApi.GuildId, settings.GuildId);
        Assert.Equal([FakeDiscordApi.PlayRole], settings.PlayRoles);
        Assert.Equal([FakeDiscordApi.BypassRole], settings.BypassRoles);
        Assert.Equal("Stranger Name", reloaded.Find(Stranger)!.DisplayName);
        await Assert.ThrowsAsync<ArgumentException>(() => reloaded.UpdateSettingsAsync(playRoles: ["not-a-role"]));
    }

    [Fact]
    public async Task RolesDecideWhoPlaysAndWhoPassesMaintenanceAndABlockAlwaysWins()
    {
        var discord = FakeDiscordApi.WithPlayers(Player);
        var access = PlayerAccess.InMemory(FakeDiscordApi.RequirePlayRole());
        var roles = new DiscordRoles(discord);
        var issuer = new LaunchIssuer(null, new Aurora21.UltimateTeam.Core.Sessions.NativeSessions(new(900, 7200)), access, roles);
        Assert.True((await issuer.DecideAsync(Player, CancellationToken.None)).Decision.Allowed);
        Assert.Equal(AccessRefusal.NoRole, (await issuer.DecideAsync(Stranger, CancellationToken.None)).Decision.Refusal);

        await access.UpdateSettingsAsync(maintenance: true);
        Assert.Equal(AccessRefusal.Maintenance, (await issuer.DecideAsync(Player, CancellationToken.None)).Decision.Refusal);
        discord.Members[Player] = [FakeDiscordApi.PlayRole, FakeDiscordApi.BypassRole];
        var fresh = new LaunchIssuer(null, new Aurora21.UltimateTeam.Core.Sessions.NativeSessions(new(900, 7200)), access, new DiscordRoles(discord));
        var bypass = (await fresh.DecideAsync(Player, CancellationToken.None)).Decision;
        Assert.True(bypass.Allowed);
        Assert.True(bypass.Bypass);

        await access.UpdatePlayerAsync(Player, true, null, null, CancellationToken.None);
        Assert.Equal(AccessRefusal.Blocked, (await fresh.DecideAsync(Player, CancellationToken.None)).Decision.Refusal);

        // No play roles: every verified account plays, and Discord is not asked while maintenance is off.
        var open = PlayerAccess.InMemory();
        var calls = discord.Calls;
        Assert.True((await new LaunchIssuer(null, new Aurora21.UltimateTeam.Core.Sessions.NativeSessions(new(900, 7200)), open, new DiscordRoles(discord))
            .DecideAsync(Stranger, CancellationToken.None)).Decision.Allowed);
        Assert.Equal(calls, discord.Calls);
    }

    [Fact]
    public async Task AnOutageUsesARecentAnswerAndNeverAdmitsAnUnknownAccount()
    {
        var discord = FakeDiscordApi.WithPlayers(Player);
        var clock = new TestRuntime.MovableTime(1_790_000_000);
        var roles = new DiscordRoles(discord, clock);
        var known = await roles.MemberAsync(FakeDiscordApi.GuildId, Player, fresh: false, CancellationToken.None);
        Assert.Equal("ok", known.Status);
        clock.Now += 60;
        await roles.MemberAsync(FakeDiscordApi.GuildId, Player, fresh: false, CancellationToken.None);
        Assert.Equal(1, discord.Calls);

        discord.Down = true;
        clock.Now += 3600;
        var remembered = await roles.MemberAsync(FakeDiscordApi.GuildId, Player, fresh: false, CancellationToken.None);
        Assert.Equal("ok", remembered.Status);
        Assert.NotNull(remembered.Problem);
        var unknown = await roles.MemberAsync(FakeDiscordApi.GuildId, Stranger, fresh: false, CancellationToken.None);
        Assert.Equal("unavailable", unknown.Status);
        Assert.Null(unknown.Known);
        Assert.Equal(AccessRefusal.RolesUnavailable,
            PlayerAccess.InMemory(FakeDiscordApi.RequirePlayRole()).Decide(Stranger, unknown.Known, 0, false).Refusal);

        clock.Now += (long)DiscordRoles.StaleFor.TotalSeconds;
        Assert.Equal("unavailable", (await roles.MemberAsync(FakeDiscordApi.GuildId, Player, fresh: false, CancellationToken.None)).Status);
        Assert.Equal("not_configured", (await DiscordRoles.FromToken(null).MemberAsync(FakeDiscordApi.GuildId, Player, false, CancellationToken.None)).Status);
    }
}
