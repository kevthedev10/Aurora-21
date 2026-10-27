using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Sessions;

namespace Aurora21.UltimateTeam.Server.Access;

/// <summary>
/// The hosted launch switches (migration 063 <c>server_access</c>): maintenance and its message, the player cap, and
/// which Discord roles may play or bypass maintenance in the Aurora Discord server (<see cref="GuildId"/>); and whether new
/// matches may start (migration 108, applied through <see cref="MatchGate"/>).
/// </summary>
public sealed record AccessSettings(bool Maintenance, string MaintenanceMessage, int MaxPlayers, string GuildId,
    IReadOnlyList<string> PlayRoles, IReadOnlyList<string> BypassRoles, bool NewMatches = true)
{
    public const int MaximumMessageLength = 300;
    public const int MaximumPlayerLimit = 10_000;
    public const int MaximumRoles = 50;
    // In-memory tools/test baseline. Persistent server policy is seeded by database migrations.
    public static AccessSettings Default { get; } = new(false, string.Empty, 0, string.Empty, [], []);

    /// <summary>Whether a launch needs the player's roles: play roles are set, or maintenance with bypass roles.</summary>
    public bool NeedsRoles => PlayRoles.Count > 0 || (Maintenance && BypassRoles.Count > 0);

    /// <summary>Applies the match pause to the running server's shared <see cref="MatchGate"/>.</summary>
    public void ApplyTo(MatchGate gate) => gate.Open = NewMatches;
}

/// <summary>One verified Discord account's access (migration 063 <c>player_access</c>).</summary>
public sealed record PlayerAccessEntry(string DiscordId, bool Blocked, bool MaintenanceBypass, string? DisplayName, string? Note,
    DateTimeOffset? LastLaunchAt, DateTimeOffset UpdatedAt);

/// <summary>Why a launch is refused; <see cref="None"/> admits it.</summary>
public enum AccessRefusal { None, NoRole, Blocked, Maintenance, Full, RolesUnavailable }

/// <summary>A launch decision: the refusal, and whether maintenance is on but this player may still play.</summary>
public sealed record AccessDecision(AccessRefusal Refusal, bool Bypass)
{
    public bool Allowed => Refusal == AccessRefusal.None;

    /// <summary>The launcher's reason word (Aurora 16 <c>/client/health</c> plus Aurora 21's role reasons); empty when admitted.</summary>
    public string Reason => Refusal switch
    {
        AccessRefusal.Maintenance => "maintenance",
        AccessRefusal.Full => "full",
        AccessRefusal.Blocked => "blocked",
        AccessRefusal.NoRole => "no_role",
        AccessRefusal.RolesUnavailable => "roles_unavailable",
        _ => string.Empty,
    };
}

/// <summary>
/// Who may launch into the hosted server, edited live from the operator panel. Reads use an immutable snapshot; every
/// write goes to PostgreSQL first and then replaces the snapshot, so the launch API and the panel agree and a restart
/// loads the same state. Without a database (unit tests) the snapshot is the only state. Role membership is not stored
/// here: <see cref="DiscordRoles"/> asks Discord, and <see cref="Decide"/> takes its answer.
/// </summary>
public sealed class PlayerAccess
{
    public const int MaximumNameLength = 64;
    public const int MaximumNoteLength = 300;

    private sealed record Snapshot(AccessSettings Settings, IReadOnlyDictionary<string, PlayerAccessEntry> Players);

    private readonly ClubUnitOfWorkRunner? _database;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private Snapshot _snapshot;

    private PlayerAccess(ClubUnitOfWorkRunner? database, Snapshot snapshot, TimeProvider? time)
    {
        _database = database;
        _snapshot = snapshot;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Access held only in memory, for tests and tools.</summary>
    public static PlayerAccess InMemory(AccessSettings? settings = null, TimeProvider? time = null) =>
        new(null, new Snapshot(settings ?? AccessSettings.Default, new Dictionary<string, PlayerAccessEntry>(StringComparer.Ordinal)), time);

    /// <summary>Loads access from PostgreSQL.</summary>
    public static async Task<PlayerAccess> LoadAsync(ClubUnitOfWorkRunner database, CancellationToken cancellationToken, TimeProvider? time = null) =>
        new(database, await ReadAsync(database, cancellationToken), time);

    public AccessSettings Settings => _snapshot.Settings;

    public IReadOnlyCollection<PlayerAccessEntry> Players => _snapshot.Players.Values.ToArray();

    public PlayerAccessEntry? Find(string discordId) => _snapshot.Players.GetValueOrDefault(discordId);

    /// <summary>Discord ids by persona id, for panel rows that start from a club.</summary>
    public IReadOnlyDictionary<long, string> DiscordByPersona() => _snapshot.Players.Keys
        .ToDictionary(id => Core.Config.PlayerIdentity.Of(id).PersonaId, id => id);

    /// <summary>
    /// Whether this player may launch now, in Aurora 16's order: a block always refuses; then the play roles (when any
    /// are set, the player must hold one); then maintenance, unless the player is on the bypass list or holds a bypass
    /// role; then the player cap, which a player already online or bypassing never hits. <paramref name="roles"/> is
    /// the player's roles in the Aurora Discord server, or null when they could not be learned.
    /// </summary>
    public AccessDecision Decide(string discordId, IReadOnlySet<string>? roles, int onlinePlayers, bool alreadyOnline)
    {
        var snapshot = _snapshot;
        var entry = snapshot.Players.GetValueOrDefault(discordId);
        var settings = snapshot.Settings;
        if (entry?.Blocked == true) return new(AccessRefusal.Blocked, false);
        if (settings.PlayRoles.Count > 0)
        {
            if (roles is null) return new(AccessRefusal.RolesUnavailable, false);
            if (!settings.PlayRoles.Any(roles.Contains)) return new(AccessRefusal.NoRole, false);
        }
        var bypass = settings.Maintenance && (entry?.MaintenanceBypass == true || (roles is not null && settings.BypassRoles.Any(roles.Contains)));
        if (settings.Maintenance && !bypass)
        {
            return roles is null && settings.BypassRoles.Count > 0 && entry?.MaintenanceBypass != true
                ? new(AccessRefusal.RolesUnavailable, false)
                : new(AccessRefusal.Maintenance, false);
        }
        if (settings.MaxPlayers > 0 && !alreadyOnline && !bypass && onlinePlayers >= settings.MaxPlayers)
            return new(AccessRefusal.Full, false);
        return new(AccessRefusal.None, bypass);
    }

    /// <summary>Changes the given switches; null leaves one as it is. Returns the new switches.</summary>
    public async Task<AccessSettings> UpdateSettingsAsync(bool? maintenance = null, string? message = null, int? maxPlayers = null,
        string? guildId = null, IReadOnlyList<string>? playRoles = null, IReadOnlyList<string>? bypassRoles = null,
        bool? newMatches = null, CancellationToken cancellationToken = default)
    {
        if (message is not null) message = CleanText(message, AccessSettings.MaximumMessageLength, nameof(message)) ?? string.Empty;
        if (maxPlayers is < 0 or > AccessSettings.MaximumPlayerLimit) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
        if (guildId is not null && guildId.Length > 0 && !IsDiscordId(guildId)) throw new ArgumentException("A Discord server id is 5 to 32 digits.", nameof(guildId));
        playRoles = playRoles is null ? null : Roles(playRoles, nameof(playRoles));
        bypassRoles = bypassRoles is null ? null : Roles(bypassRoles, nameof(bypassRoles));
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var current = _snapshot.Settings;
            var next = new AccessSettings(maintenance ?? current.Maintenance, message ?? current.MaintenanceMessage,
                maxPlayers ?? current.MaxPlayers, guildId ?? current.GuildId, playRoles ?? current.PlayRoles, bypassRoles ?? current.BypassRoles,
                newMatches ?? current.NewMatches);
            if (_database is not null)
            {
                await _database.RunAsync(0, "player-access-settings", work => work.ExecuteAsync("""
                    UPDATE ultimateteam.server_access SET maintenance=@maintenance,maintenance_message=@message,max_players=@limit,
                        discord_guild_id=@guild,play_role_ids=@play,bypass_role_ids=@bypass,new_matches=@matches,
                        updated_at=now() WHERE only_row
                    """, ("maintenance", next.Maintenance), ("message", next.MaintenanceMessage), ("limit", next.MaxPlayers),
                    ("guild", next.GuildId), ("play", next.PlayRoles.ToArray()), ("bypass", next.BypassRoles.ToArray()),
                    ("matches", next.NewMatches)), cancellationToken);
            }
            _snapshot = _snapshot with { Settings = next };
            return next;
        }
        finally { _writes.Release(); }
    }

    /// <summary>Creates or changes one player's block, bypass or note; null leaves one as it is. Returns the stored row.</summary>
    public async Task<PlayerAccessEntry> UpdatePlayerAsync(string discordId, bool? blocked, bool? maintenanceBypass, string? note,
        CancellationToken cancellationToken)
    {
        RequireId(discordId);
        if (note is not null) note = CleanText(note, MaximumNoteLength, nameof(note));
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var current = Find(discordId);
            var next = new PlayerAccessEntry(discordId, blocked ?? current?.Blocked ?? false, maintenanceBypass ?? current?.MaintenanceBypass ?? false,
                current?.DisplayName, note is null ? current?.Note : note.Length == 0 ? null : note, current?.LastLaunchAt, _time.GetUtcNow());
            if (_database is not null)
            {
                next = await _database.RunAsync(0, "player-access-edit", async work => (await work.QueryAsync("""
                    INSERT INTO ultimateteam.player_access(discord_id,blocked,maintenance_bypass,note)
                    VALUES(@id,@blocked,@bypass,@note::text)
                    ON CONFLICT (discord_id) DO UPDATE SET blocked=excluded.blocked,maintenance_bypass=excluded.maintenance_bypass,
                        note=excluded.note,updated_at=now()
                    RETURNING
                    """ + Columns, ReadEntry, ("id", discordId), ("blocked", next.Blocked), ("bypass", next.MaintenanceBypass),
                    ("note", next.Note))).Single(), cancellationToken);
            }
            Replace(next);
            return next;
        }
        finally { _writes.Release(); }
    }

    /// <summary>
    /// Records a verified launch: its time and a name for the account (a label only; the id is what the assertion
    /// proved). Every account that tries gets a row, so the panel lists it.
    /// </summary>
    public async Task<PlayerAccessEntry> RecordLaunchAsync(string discordId, string? name, CancellationToken cancellationToken)
    {
        RequireId(discordId);
        var cleaned = name is null ? null : CleanName(name);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var current = Find(discordId);
            var now = _time.GetUtcNow();
            var next = current is null
                ? new PlayerAccessEntry(discordId, false, false, cleaned, null, now, now)
                : current with { DisplayName = cleaned ?? current.DisplayName, LastLaunchAt = now };
            if (_database is not null)
            {
                next = await _database.RunAsync(0, "player-access-launch", async work => (await work.QueryAsync("""
                    INSERT INTO ultimateteam.player_access(discord_id,display_name,last_launch_at) VALUES(@id,@name::text,now())
                    ON CONFLICT (discord_id) DO UPDATE SET
                        display_name=COALESCE(excluded.display_name,ultimateteam.player_access.display_name),last_launch_at=now()
                    RETURNING
                    """ + Columns, ReadEntry, ("id", discordId), ("name", cleaned))).Single(), cancellationToken);
            }
            Replace(next);
            return next;
        }
        finally { _writes.Release(); }
    }

    private const string Columns = " discord_id,blocked,maintenance_bypass,display_name,note,last_launch_at,updated_at";

    private static async Task<Snapshot> ReadAsync(ClubUnitOfWorkRunner database, CancellationToken cancellationToken) =>
        await database.RunReadAsync(0, async work =>
        {
            var settings = (await work.QueryAsync("""
                SELECT maintenance,maintenance_message,max_players,discord_guild_id,play_role_ids,bypass_role_ids,
                    new_matches
                FROM ultimateteam.server_access WHERE only_row
                """, row => new AccessSettings(row.GetBoolean(0), row.GetString(1), row.GetInt32(2), row.GetString(3),
                    row.GetFieldValue<string[]>(4), row.GetFieldValue<string[]>(5), row.GetBoolean(6)))).Single();
            var players = await work.QueryAsync("SELECT" + Columns + " FROM ultimateteam.player_access", ReadEntry);
            return new Snapshot(settings, players.ToDictionary(entry => entry.DiscordId, StringComparer.Ordinal));
        }, cancellationToken);

    private static PlayerAccessEntry ReadEntry(Npgsql.NpgsqlDataReader row) => new(row.GetString(0), row.GetBoolean(1), row.GetBoolean(2),
        row.IsDBNull(3) ? null : row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4),
        row.IsDBNull(5) ? null : new DateTimeOffset(row.GetDateTime(5), TimeSpan.Zero), new DateTimeOffset(row.GetDateTime(6), TimeSpan.Zero));

    private void Replace(PlayerAccessEntry entry)
    {
        var players = new Dictionary<string, PlayerAccessEntry>(_snapshot.Players, StringComparer.Ordinal) { [entry.DiscordId] = entry };
        _snapshot = _snapshot with { Players = players };
    }

    private static string[] Roles(IReadOnlyList<string> roles, string parameter)
    {
        var distinct = roles.Select(role => role.Trim()).Where(role => role.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > AccessSettings.MaximumRoles || distinct.Any(role => !IsDiscordId(role)))
            throw new ArgumentException($"Discord role ids are 5 to 32 digits, at most {AccessSettings.MaximumRoles}.", parameter);
        return distinct;
    }

    /// <summary>A Discord id (user, role or server): 5 to 32 ASCII digits (the rule of <see cref="Core.Config.PlayerIdentity.Of"/>).</summary>
    public static bool IsDiscordId(string? id) => id is { Length: >= 5 and <= 32 } && id.All(char.IsAsciiDigit);

    private static string RequireId(string id) =>
        IsDiscordId(id) ? id : throw new ArgumentException("A Discord id is 5 to 32 digits.", nameof(id));

    /// <summary>A launcher-supplied name kept to one printable line of at most 64 characters; null when nothing is left.</summary>
    public static string? CleanName(string name)
    {
        var cleaned = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length > MaximumNameLength) cleaned = cleaned[..MaximumNameLength].Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? CleanText(string text, int maximum, string parameter)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > maximum || trimmed.Any(char.IsControl)) throw new ArgumentException("Text is too long or has control characters.", parameter);
        return trimmed;
    }
}
