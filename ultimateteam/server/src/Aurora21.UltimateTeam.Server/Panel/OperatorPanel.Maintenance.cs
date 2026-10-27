using System.Globalization;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Maintenance (Aurora16 PANEL-CONTRACTS 6.5), for Aurora 21's access model. Players are verified Discord accounts: who
/// may play is who holds a play role in the Aurora Discord server (asked through the bot, Aurora 16's role tools), the
/// bypass and block lists hold Discord ids (never addresses), and maintenance lets in bypass roles and listed accounts.
/// Changes apply to the next launch; a block also ends that player's session at once. Backups are taken by the backup
/// service (a request is dropped for it); a supervised VPS worker can likewise restart the server container.
/// </summary>
public sealed partial class OperatorPanel
{
    private const string NoAddressLists = "Aurora 21 identifies players by their verified Discord account, never by address: use the Discord id lists and roles.";

    private async Task<(int, object)?> MaintenanceAsync(ApiRequest request)
    {
        var sub = string.Join('/', request.Parts.Skip(1));
        switch (sub, request.Method)
        {
            case ("", "GET"):
                return (200, MaintenanceState(request));
            case ("blaze-access", "POST"):
                return (200, await SaveAccessAsync(request));
            case ("game-speed", "POST"):
                return (200, SaveGameSpeed(request));
            case ("pregame-departure", "GET"):
                return (200, new { mode = options.PregameDeparture?.Mode, modes = PregameDeparture.Modes });
            case ("pregame-departure", "POST"):
                return (200, SavePregameDeparture(request));
            case ("backup", "POST"):
                return (202, RequestBackup(request));
            case ("discord/roles", "GET"):
                return (200, await DiscordRoleListAsync(request));
            case ("discord/check", "POST"):
                return (200, await DiscordCheckAsync(request));
            case ("allow-ips", "POST"):
                throw new PanelError(400, "Aurora 21's panel is reachable only through the SSH tunnel to its loopback port; there is no address list to edit.");
            case ("restart", "POST"):
                return (202, RequestRestart(request));
            case ("" or "blaze-access" or "game-speed" or "pregame-departure" or "backup" or "allow-ips" or "restart" or "discord/roles" or "discord/check", _):
                throw new PanelError(405, "method not allowed");
            default:
                return null;
        }
    }

    private object MaintenanceState(ApiRequest request)
    {
        var access = issuer.Access;
        var settings = access.Settings;
        var entries = access.Players;
        var online = issuer.Presence?.Online() ?? [];
        return new
        {
            allowIps = "",
            yourIp = request.Remote,
            blazeAccess = new
            {
                maintenance = settings.Maintenance,
                maintenanceMessage = settings.MaintenanceMessage,
                bypassCount = entries.Count(entry => entry.MaintenanceBypass) + settings.BypassRoles.Count,
                blockedCount = entries.Count(entry => entry.Blocked),
                maxPlayers = settings.MaxPlayers,
                newMatches = settings.NewMatches,
                blazeAlive = true,
                bypassIps = "",
                bypassNames = string.Join(", ", entries.Where(entry => entry.MaintenanceBypass).Select(entry => entry.DiscordId).Order(StringComparer.Ordinal)),
                bypassRoles = string.Join(", ", settings.BypassRoles),
                playRoles = string.Join(", ", settings.PlayRoles),
                guildId = settings.GuildId,
                blockedIps = string.Join(", ", entries.Where(entry => entry.Blocked).Select(entry => entry.DiscordId).Order(StringComparer.Ordinal)),
                connected = online.Count,
                controls = true,
                blazeControls = new { known = true, loaded = true },
                discord = new
                {
                    unavailable = false,
                    tokenSet = issuer.Roles.TokenSet,
                    guildId = settings.GuildId,
                    configured = issuer.Roles.TokenSet && settings.GuildId.Length > 0,
                    cacheDays = 0,
                    cacheMinutes = (int)DiscordRoles.FreshFor.TotalMinutes,
                    cached = issuer.Roles.Cached,
                },
                launchIdentity = issuer.Configured,
            },
            backups = BackupState(),
            onlineGameSpeed = options.GameSpeed is { } speed ? new { value = speed.Value ?? OnlineGameSpeed.Normal } : null,
            services = options.Services.Select(service => new
            {
                key = service.Key,
                label = service.Label,
                cost = "A restart disconnects all players. The VPS worker restarts the server container with Docker Compose.",
                pids = Array.Empty<int>(),
            }),
            supervised = false,
            restart = RestartState(),
            online = online.Select(player => new
            {
                blazeId = PlayerIdentity.Of(player.PlayerId).PersonaId,
                name = access.Find(player.PlayerId)?.DisplayName ?? player.PlayerId,
                club = (string?)null,
                remote = player.Remote,
                since = Iso(player.Since),
            }),
        };
    }

    private object SaveGameSpeed(ApiRequest request)
    {
        if (options.GameSpeed is not { } gameSpeed) throw new PanelError(400, "Online matchmaking is unavailable on this server.");
        foreach (var property in request.Body.EnumerateObject())
            if (property.Name != "value") throw new PanelError(400, $"The game-speed edit does not take {property.Name}.");
        var speed = WholeField(request.Body, "value");
        if (speed is null or < OnlineGameSpeed.Slow or > OnlineGameSpeed.Fast)
            throw new PanelError(400, "Game speed is 0 (Slow), 1 (Normal) or 2 (Fast).");
        gameSpeed.Value = speed;
        _log.LogInformation("PANEL {Remote} set online.gameSpeed = {Value}", request.Remote, speed);
        return new { value = speed };
    }

    /// <summary>Sets how the next departure before IN_GAME is announced (announce, silent or self); a restart returns to the start-up mode.</summary>
    private object SavePregameDeparture(ApiRequest request)
    {
        if (options.PregameDeparture is not { } setting) throw new PanelError(400, "Online matchmaking is unavailable on this server.");
        foreach (var property in request.Body.EnumerateObject())
            if (property.Name != "mode") throw new PanelError(400, $"The pregame-departure edit does not take {property.Name}.");
        var mode = PregameDeparture.Parse(TextField(request.Body, "mode"))
            ?? throw new PanelError(400, $"The pregame departure mode is one of {string.Join(", ", PregameDeparture.Modes)}.");
        setting.Mode = mode;
        _log.LogInformation("PANEL {Remote} set pregame-departure = {Mode}", request.Remote, mode);
        return new { mode };
    }

    /// <summary>
    /// Saves what the Maintenance card changed: maintenance, its message and the cap, the new-match pause, the Discord
    /// server and its play and bypass roles, and the per-account bypass and block lists. A list replaces that flag for everyone: listed ids
    /// get it, the rest lose it. A newly blocked player is ended at once.
    /// </summary>
    private async Task<object> SaveAccessAsync(ApiRequest request)
    {
        var body = request.Body;
        if (!string.IsNullOrWhiteSpace(TextField(body, "bypassIps"))) throw new PanelError(400, NoAddressLists);
        var maintenance = FlagField(body, "maintenance");
        var message = Field(body, "maintenanceMessage") is not null ? TextField(body, "maintenanceMessage") ?? "" : null;
        int? cap = null;
        if (WholeField(body, "maxPlayers") is { } limit)
        {
            if (limit is < 0 or > AccessSettings.MaximumPlayerLimit) throw new PanelError(400, $"The player cap is a whole number from 0 to {AccessSettings.MaximumPlayerLimit}.");
            cap = (int)limit;
        }
        var guild = Field(body, "guildId") is not null ? (TextField(body, "guildId") ?? "").Trim() : null;
        if (guild is { Length: > 0 } && !PlayerAccess.IsDiscordId(guild)) throw new PanelError(400, "The Discord server id is 5 to 32 digits (Discord: right-click the server, Copy Server ID).");
        var playRoles = Field(body, "playRoles") is not null ? DiscordList(TextField(body, "playRoles"), "Roles that may play") : null;
        var bypassRoles = Field(body, "bypassRoles") is not null ? DiscordList(TextField(body, "bypassRoles"), "Maintenance bypass roles") : null;
        var bypass = Field(body, "bypassNames") is not null ? DiscordList(TextField(body, "bypassNames"), "Maintenance bypass") : null;
        var blocked = Field(body, "blockedIps") is not null ? DiscordList(TextField(body, "blockedIps"), "Blocked") : null;
        var newMatches = FlagField(body, "newMatches");
        var changed = new List<string>();
        var access = issuer.Access;
        try
        {
            if (maintenance is not null || message is not null || cap is not null || guild is not null || playRoles is not null || bypassRoles is not null
                || newMatches is not null)
            {
                var before = access.Settings;
                var after = await access.UpdateSettingsAsync(maintenance, message, cap, guild, playRoles, bypassRoles,
                    newMatches, request.Aborted);
                after.ApplyTo(game.MatchGate);
                if (before.NewMatches != after.NewMatches) changed.Add($"new matches {(after.NewMatches ? "resumed" : "paused")}");
                if (before.Maintenance != after.Maintenance) changed.Add($"maintenance {(after.Maintenance ? "on" : "off")}");
                if (before.MaintenanceMessage != after.MaintenanceMessage) changed.Add("maintenance message");
                if (before.MaxPlayers != after.MaxPlayers) changed.Add($"player cap {after.MaxPlayers}");
                if (before.GuildId != after.GuildId) changed.Add("Discord server");
                if (!before.PlayRoles.SequenceEqual(after.PlayRoles)) changed.Add($"play roles ({after.PlayRoles.Count})");
                if (!before.BypassRoles.SequenceEqual(after.BypassRoles)) changed.Add($"bypass roles ({after.BypassRoles.Count})");
            }
            if (bypass is not null) changed.AddRange(await ReplaceFlagAsync(bypass, entry => entry.MaintenanceBypass, (id, on) => access.UpdatePlayerAsync(id, null, on, null, request.Aborted), "bypass"));
            if (blocked is not null)
            {
                var before = access.Players.Where(entry => entry.Blocked).Select(entry => entry.DiscordId).ToHashSet(StringComparer.Ordinal);
                changed.AddRange(await ReplaceFlagAsync(blocked, entry => entry.Blocked, (id, on) => access.UpdatePlayerAsync(id, on, null, null, request.Aborted), "blocked"));
                foreach (var id in blocked.Where(id => !before.Contains(id))) issuer.Revoke(id);
            }
        }
        catch (ArgumentException error) { throw new PanelError(400, error.Message); }
        if (changed.Count > 0) _log.LogInformation("PANEL {Remote} changed access: {Changed}", request.Remote, string.Join(", ", changed));
        return new { changed };
    }

    /// <summary>Sets a flag on exactly the listed ids; returns a line per change.</summary>
    private async Task<IEnumerable<string>> ReplaceFlagAsync(IReadOnlyList<string> listed, Func<PlayerAccessEntry, bool> flag,
        Func<string, bool, Task<PlayerAccessEntry>> set, string name)
    {
        var wanted = listed.ToHashSet(StringComparer.Ordinal);
        var changes = new List<string>();
        foreach (var entry in issuer.Access.Players.Where(entry => flag(entry) && !wanted.Contains(entry.DiscordId)).ToArray())
        {
            await set(entry.DiscordId, false);
            changes.Add($"{name} off {entry.DiscordId}");
        }
        foreach (var id in wanted.Where(id => issuer.Access.Find(id) is not { } entry || !flag(entry)))
        {
            await set(id, true);
            changes.Add($"{name} on {id}");
        }
        return changes;
    }

    /// <summary>The Discord server's roles for the picker (Aurora 16's role card).</summary>
    private async Task<object> DiscordRoleListAsync(ApiRequest request)
    {
        var settings = issuer.Access.Settings;
        if (!issuer.Roles.TokenSet) throw new PanelError(503, "No Discord bot token: set DISCORD_BOT_TOKEN in the VPS .env and redeploy.");
        if (settings.GuildId.Length == 0) throw new PanelError(409, "Set the Discord server id first.");
        var roles = await issuer.Roles.GuildRolesAsync(settings.GuildId, request.Aborted);
        return new { roles = roles.Select(role => new { id = role.Id, name = role.Name }) };
    }

    /// <summary>What Discord says about one account now, and what that means for playing and for maintenance.</summary>
    private async Task<object> DiscordCheckAsync(ApiRequest request)
    {
        var id = (TextField(request.Body, "id") ?? "").Trim();
        if (!PlayerAccess.IsDiscordId(id)) throw new PanelError(400, "Enter a Discord user id (5 to 32 digits).");
        var settings = issuer.Access.Settings;
        var member = await issuer.Roles.MemberAsync(settings.GuildId, id, fresh: true, request.Aborted);
        var roles = member.Known;
        var names = roles is null || roles.Count == 0 ? [] : settings.GuildId.Length == 0 ? roles.ToArray()
            : (await issuer.Roles.GuildRolesAsync(settings.GuildId, request.Aborted)).Where(role => roles.Contains(role.Id)).Select(role => role.Name).ToArray();
        var entry = issuer.Access.Find(id);
        return new
        {
            status = member.Status,
            problem = member.Problem,
            username = member.Username,
            roles = names,
            canPlay = entry?.Blocked != true && (settings.PlayRoles.Count == 0 || (roles is not null && settings.PlayRoles.Any(roles.Contains))),
            bypass = entry?.MaintenanceBypass == true || (roles is not null && settings.BypassRoles.Any(roles.Contains)),
            blocked = entry?.Blocked == true,
        };
    }

    private object BackupState()
    {
        var directory = options.BackupDirectory;
        if (directory is null) return new { error = "This server was started without --backup-dir, so it does not list the backup service's dumps." };
        var files = Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles("*.dump").OrderByDescending(file => file.LastWriteTimeUtc).ToArray()
            : [];
        var pending = options.BackupRequestDirectory is { } requests && Directory.Exists(requests) &&
                      Directory.EnumerateFiles(requests, "*.request").Any();
        return new
        {
            dir = directory,
            keep = int.TryParse(Environment.GetEnvironmentVariable("BACKUP_KEEP_DAILY"), NumberStyles.None, CultureInfo.InvariantCulture, out var keep) ? keep : 7,
            minIntervalSec = 60,
            inProgress = pending,
            database = (object?)null,
            list = files.Select(file => new { name = file.Name, bytes = file.Length, createdAt = Iso(file.LastWriteTimeUtc), ignored = (string?)null }),
        };
    }

    /// <summary>Drops a request for the backup service, which takes a dump within about a minute.</summary>
    private object RequestBackup(ApiRequest request)
    {
        if (options.BackupRequestDirectory is not { } requests || options.BackupDirectory is null)
            throw new PanelError(409, "This server was started without a backup folder, so it cannot ask for a backup.");
        Directory.CreateDirectory(requests);
        if (Directory.EnumerateFiles(requests, "*.request").Any()) throw new PanelError(409, "A backup was already requested: it appears in the list within a minute.");
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(requests, stamp + ".request"), "manual\n");
        _log.LogInformation("PANEL {Remote} requested a database backup", request.Remote);
        return new { requested = true, at = stamp };
    }

    private sealed record RestartStatus(bool available, bool pending, string service, string? reason);

    private RestartStatus RestartState()
    {
        var dir = options.RestartRequestDirectory;
        var active = dir is not null && Directory.Exists(dir) && File.Exists(Path.Combine(dir, "worker.heartbeat")) &&
                     DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(dir, "worker.heartbeat")) < TimeSpan.FromSeconds(20);
        var pending = dir is not null && (File.Exists(Path.Combine(dir, "restart-server.request")) ||
                                           File.Exists(Path.Combine(dir, "restart-server.in-progress")));
        return new RestartStatus(active, pending, "server", active ? null : "The VPS restart worker is not running.");
    }

    private object RequestRestart(ApiRequest request)
    {
        if (TextField(request.Body, "service") is not "server" || FlagField(request.Body, "confirm") is not true)
            throw new PanelError(400, "Confirm a restart of the server service.");
        var state = RestartState();
        if (!state.available) throw new PanelError(409, "The VPS restart worker is not running.");
        if (state.pending) throw new PanelError(409, "A server restart is already pending.");
        var dir = options.RestartRequestDirectory!;
        try
        {
            using var file = new FileStream(Path.Combine(dir, "restart-server.request"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file);
            writer.WriteLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (IOException) { throw new PanelError(409, "A server restart is already pending, or the request could not be written."); }
        _log.LogWarning("PANEL {Remote} requested a Docker restart of the server; connected players will be disconnected", request.Remote);
        return new { requested = true, service = "server" };
    }
}
