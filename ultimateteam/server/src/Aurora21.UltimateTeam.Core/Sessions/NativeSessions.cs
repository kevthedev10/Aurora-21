using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aurora21.UltimateTeam.Core.Config;

namespace Aurora21.UltimateTeam.Core.Sessions;

/// <summary>
/// Player identity for the session identity mode (ultimateteam/native-hosting-investigation.md gates 2 and 3), never from a
/// peer address or a client-supplied persona id:
/// <list type="bullet">
/// <item>An <b>auth code</b> issued for one player, which FIFA sends as <c>identification.authCode</c> in
/// <c>/ut/auth</c> and as the <c>nucleus-access-code</c> header before it (observed 2026-09-27), and as the Blaze login's
/// AUTH. Retail FIFA fetches a fresh code from Origin for every login; the launcher bridge hands FIFA the one launch code
/// every time, so a code that has been used stays valid while its player stays active (<see cref="SessionIdle"/>, at most
/// <see cref="RedeemedLifetime"/> after issue). Before, it died 15 minutes after launch: re-entering FUT or a Blaze
/// re-login after that was refused and the player had to relaunch (live 10-04: 59 refusals in a day with no restart).</item>
/// <item>A <b>UT session</b> started by a verified <c>/ut/auth</c>: a random <c>X-UT-SID</c>, which FIFA returns on
/// every later FUT game request (observed 2026-09-27). One session per player; a new one replaces the old. It expires
/// after an idle period and ends at logout.</item>
/// </list>
/// UT sessions are held in memory. Codes are also kept in an optional store file, by SHA-256 only, so a restart does not
/// end them: the client's re-login and re-auth after a deploy then succeed. Both values are bearer secrets, so they need
/// HTTPS before they cross the internet.
/// </summary>
public sealed class NativeSessions
{
    /// <summary>The longest a used code keeps working, however active its player is.</summary>
    public static readonly TimeSpan RedeemedLifetime = TimeSpan.FromHours(24);

    private sealed class Code(PlayerIdentity player, DateTimeOffset issued, DateTimeOffset expires, bool redeemed)
    {
        public PlayerIdentity Player { get; } = player;
        public DateTimeOffset Issued { get; } = issued;
        public DateTimeOffset Expires { get; } = expires;
        public bool Redeemed { get; set; } = redeemed;
    }

    private sealed class Session(PlayerIdentity player, DateTimeOffset lastUsed)
    {
        public PlayerIdentity Player { get; } = player;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }

    // Keyed by the code's SHA-256 (hex), so the store file never holds a usable code.
    private readonly ConcurrentDictionary<string, Code> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActive = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionByPlayer = new(StringComparer.Ordinal);
    private readonly object _sessionGate = new();
    private readonly NativeAuthSettings _settings;
    private readonly TimeProvider _time;
    private readonly string? _storePath;
    private readonly object _storeGate = new();
    private DateTimeOffset _savedAt = DateTimeOffset.MinValue;

    public NativeSessions(NativeAuthSettings settings, TimeProvider? time = null, string? storePath = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _storePath = storePath;
        Load();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private TimeSpan SessionIdle => TimeSpan.FromSeconds(_settings.SessionIdleSeconds);

    public int CodeLifetimeSeconds => _settings.CodeLifetimeSeconds;

    /// <summary>Issues a new auth code for <paramref name="player"/>: 40 lowercase hex characters (160 random bits).</summary>
    public string IssueCode(PlayerIdentity player)
    {
        Purge();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        _codes[Hash(code)] = new Code(player, Now, Now.AddSeconds(_settings.CodeLifetimeSeconds), redeemed: false);
        Save(force: true);
        return code;
    }

    /// <summary>Registers the launcher's synthetic loopback test code. Never expose this through an HTTP route.</summary>
    public void RegisterLocalTestCode(string code, PlayerIdentity player)
    {
        if (code.Length != 40 || code.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("Local test code must be 40 lowercase hex characters.", nameof(code));
        }
        Purge();
        if (!_codes.TryAdd(Hash(code), new Code(player, Now, Now.AddSeconds(_settings.CodeLifetimeSeconds), redeemed: false)))
        {
            throw new ArgumentException("Local test code is already registered.", nameof(code));
        }
    }

    /// <summary>
    /// The player a code was issued for: an unused code until it expires, a used one while its player stays active; null
    /// for a missing, unknown, expired, idle or revoked code. A successful use counts as the player's activity.
    /// </summary>
    public PlayerIdentity? PlayerForCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || !_codes.TryGetValue(Hash(code), out var entry) || !IsValid(entry, Now))
        {
            return null;
        }
        var first = !entry.Redeemed;
        entry.Redeemed = true;
        _lastActive[entry.Player.Id] = Now;
        Save(force: first);
        return entry.Player;
    }

    /// <summary>Notes activity of <paramref name="playerId"/>, which keeps its used codes working.</summary>
    public void Touch(string playerId)
    {
        _lastActive[playerId] = Now;
        Save(force: false);
    }

    private bool IsValid(Code entry, DateTimeOffset now) =>
        entry.Expires > now
        || (entry.Redeemed && now - entry.Issued < RedeemedLifetime
            && _lastActive.TryGetValue(entry.Player.Id, out var active) && now - active <= SessionIdle);

    /// <summary>Starts a UT session for a verified player and returns its <c>X-UT-SID</c>; any earlier session of the player ends.</summary>
    public string StartSession(PlayerIdentity player)
    {
        Purge();
        var sid = "ut." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        lock (_sessionGate)
        {
            if (_sessionByPlayer.TryGetValue(player.Id, out var previous))
            {
                _sessions.TryRemove(previous, out _);
            }
            _sessions[sid] = new Session(player, Now);
            _sessionByPlayer[player.Id] = sid;
        }
        return sid;
    }

    /// <summary>The player of a live UT session, refreshing its idle time; null for a missing, unknown, ended or idle-expired one.</summary>
    public PlayerIdentity? PlayerForSession(string? sid)
    {
        if (string.IsNullOrEmpty(sid) || !_sessions.TryGetValue(sid, out var session))
        {
            return null;
        }
        var now = Now;
        lock (session)
        {
            if (now - session.LastUsed > SessionIdle)
            {
                EndSession(sid);
                return null;
            }
            session.LastUsed = now;
        }
        Touch(session.Player.Id);
        return session.Player;
    }

    /// <summary>Ends one UT session (logout).</summary>
    public void EndSession(string sid)
    {
        lock (_sessionGate)
        {
            if (_sessions.TryRemove(sid, out var session) && _sessionByPlayer.TryGetValue(session.Player.Id, out var current) && current == sid)
            {
                _sessionByPlayer.TryRemove(session.Player.Id, out _);
            }
        }
    }

    /// <summary>Revokes everything a player holds: its codes and its session.</summary>
    public void Revoke(string playerId)
    {
        foreach (var (code, entry) in _codes)
        {
            if (entry.Player.Id == playerId)
            {
                _codes.TryRemove(code, out _);
            }
        }
        _lastActive.TryRemove(playerId, out _);
        if (_sessionByPlayer.TryGetValue(playerId, out var sid))
        {
            EndSession(sid);
        }
        Save(force: true);
    }

    /// <summary>Drops codes that can no longer work and idle sessions.</summary>
    private void Purge()
    {
        var now = Now;
        foreach (var (code, entry) in _codes)
        {
            if (!IsValid(entry, now))
            {
                _codes.TryRemove(code, out _);
            }
        }
        foreach (var (player, active) in _lastActive)
        {
            if (now - active > RedeemedLifetime)
            {
                _lastActive.TryRemove(player, out _);
            }
        }
        foreach (var (sid, session) in _sessions)
        {
            if (now - session.LastUsed > SessionIdle)
            {
                EndSession(sid);
            }
        }
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    private sealed record StoredCode(string Player, DateTimeOffset Issued, DateTimeOffset Expires, bool Redeemed);

    private sealed record Store(Dictionary<string, StoredCode> Codes, Dictionary<string, DateTimeOffset> LastActive);

    /// <summary>Restores the codes a previous run issued (a missing or unreadable file starts empty).</summary>
    private void Load()
    {
        if (_storePath is null || !File.Exists(_storePath))
        {
            return;
        }
        try
        {
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_storePath));
            foreach (var (player, active) in store?.LastActive ?? new())
            {
                _lastActive[player] = active;
            }
            foreach (var (hash, code) in store?.Codes ?? new())
            {
                _codes[hash] = new Code(PlayerIdentity.Of(code.Player), code.Issued, code.Expires, code.Redeemed);
            }
            Purge();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _codes.Clear();
            _lastActive.Clear();
        }
    }

    /// <summary>Writes the store: at once for a new, first-used or revoked code, at most once a minute for activity alone.</summary>
    private void Save(bool force)
    {
        if (_storePath is null)
        {
            return;
        }
        lock (_storeGate)
        {
            var now = Now;
            if (!force && now - _savedAt < TimeSpan.FromMinutes(1))
            {
                return;
            }
            _savedAt = now;
            var store = new Store(
                _codes.ToDictionary(pair => pair.Key, pair => new StoredCode(pair.Value.Player.Id, pair.Value.Issued, pair.Value.Expires, pair.Value.Redeemed)),
                _lastActive.ToDictionary(pair => pair.Key, pair => pair.Value));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_storePath))!);
                var temporary = _storePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(store));
                File.Move(temporary, _storePath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The in-memory codes still work; only a restart would lose them.
            }
        }
    }
}
