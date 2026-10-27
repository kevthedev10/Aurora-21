using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Http;

namespace Aurora21.UltimateTeam.Server.Access;

/// <summary>The outcome of a launch request: an HTTP status, and either the issued code or an error word.</summary>
public sealed record LaunchOutcome(int Status, string? Error, string? Code, PlayerAccessEntry? Player, AccessDecision? Decision)
{
    public bool Issued => Code is not null;
}

/// <summary>
/// Turns a verified launch assertion into a one-time game code, for both launch routes (<c>POST /client/launch</c> and
/// <c>POST /api/v1/launch/code</c>): verify the assertion, record the launch (so the panel lists every account that
/// tried), learn the account's Discord roles when the rules need them, apply the live access rules, then issue the
/// code bound to that Discord account in <see cref="NativeSessions"/>. The code reaches FIFA only through the launcher's
/// helper; this never logs it or the assertion.
/// </summary>
public sealed class LaunchIssuer(LaunchAssertionVerifier? verifier, NativeSessions sessions, PlayerAccess access, DiscordRoles roles,
    PlayerPresence? presence = null, MathGroups? mathGroups = null, Func<long>? nowSeconds = null, string? playerNamesPath = null)
{
    public int CodeLifetimeSeconds => sessions.CodeLifetimeSeconds;

    /// <summary>Whether an account-service key is configured; without one no code can be issued.</summary>
    public bool Configured => verifier is not null;

    public PlayerAccess Access => access;

    public DiscordRoles Roles => roles;

    public PlayerPresence? Presence => presence;

    /// <summary>
    /// The decision for this account now, asking Discord for its roles when the rules need them (a remembered answer
    /// is used while fresh). <paramref name="member"/> is what Discord said, or null when it was not asked.
    /// </summary>
    public async Task<(AccessDecision Decision, MemberRoles? Member)> DecideAsync(string discordId, CancellationToken cancellationToken)
    {
        var settings = access.Settings;
        var member = settings.NeedsRoles ? await roles.MemberAsync(settings.GuildId, discordId, fresh: false, cancellationToken) : null;
        var decision = access.Decide(discordId, member?.Known, presence?.OnlineCount ?? 0,
            presence?.IsOnline(PlayerIdentity.Of(discordId).Id) ?? false);
        return (decision, member);
    }

    /// <param name="assertion">The account service's signed launch assertion.</param>
    /// <param name="claimedDiscordId">The id the launcher says it is signed in as; when given it must match the assertion.</param>
    /// <param name="launcherName">The launcher's name for the account, used when neither the assertion nor Discord gives one.</param>
    public async Task<LaunchOutcome> IssueAsync(string? assertion, string? claimedDiscordId, string? launcherName,
        string? windowsHeader, string? mathHeader, CancellationToken cancellationToken)
    {
        if (verifier is null) return new(503, "launch_not_configured", null, null, null);
        var verified = verifier.Verify(assertion);
        if (verified is null) return new(401, "unauthorized", null, null, null);
        if (claimedDiscordId is not null && claimedDiscordId != verified.DiscordId)
            return new(401, "discord_id_mismatch", null, null, null);
        var (decision, member) = await DecideAsync(verified.DiscordId, cancellationToken);
        var entry = await access.RecordLaunchAsync(verified.DiscordId, verified.Name ?? member?.Username ?? launcherName, cancellationToken);
        if (!decision.Allowed)
        {
            var error = decision.Refusal switch
            {
                AccessRefusal.NoRole => "no_role",
                AccessRefusal.Blocked => "blocked",
                AccessRefusal.Maintenance => "maintenance",
                AccessRefusal.RolesUnavailable => "roles_unavailable",
                _ => "full",
            };
            return new(decision.Refusal == AccessRefusal.RolesUnavailable ? 503 : 403, error, null, entry, decision);
        }
        var player = PlayerIdentity.Of(verified.DiscordId);
        if (playerNamesPath is not null && entry.DisplayName is { } display)
        {
            // The club and persona are named after this (ClubNames / PlayerNames), from the next club open.
            PlayerNames.Record(playerNamesPath, player.Id, display);
        }
        var code = sessions.IssueCode(player);
        mathGroups?.NoteLaunch(player.Id, windowsHeader, mathHeader,
            (nowSeconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()))());
        return new(200, null, code, entry, decision);
    }

    /// <summary>
    /// Applies a panel block: the player's codes and FUT sessions stop working and its Blaze connection closes now,
    /// rather than at the next launch. Returns how many Blaze connections were closed.
    /// </summary>
    public int Revoke(string discordId)
    {
        var player = PlayerIdentity.Of(discordId);
        sessions.Revoke(player.Id);
        return presence?.Disconnect(player.Id) ?? 0;
    }

    /// <summary>The player an unexpired launch code was issued for, without consuming it (the runtime helper's maths report).</summary>
    public PlayerIdentity? PlayerForCode(string? code) => sessions.PlayerForCode(code);
}
