using System.Security.Cryptography;
using System.Text;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

namespace Aurora21.UltimateTeam.Server.Blaze;

/// <summary>
/// The per-login Blaze session keys FIFA resumes with. After a dropped TCP link FIFA 21 opens a new connection and sends
/// Util.Ping with Fire2 metadata whose SKEY is the KEY of our UserSessions 30722/8 notification, then replays its unanswered
/// requests with their original message numbers, without PreAuth or Login (blaze-intel captures, 6 reconnects).
/// <para>
/// A key is a random 128-bit secret issued to one verified connection; only its SHA-256 is kept. It resumes only while
/// that connection is still open on the server: the client gives up after 20 s of silence, long before our 120 s idle
/// timeout, so a link that died silently is still open here. A closed, aborted (panel block, maintenance kick) or replaced
/// connection takes its key with it, and a fresh login for the same player drops that player's older key.
/// </para>
/// <para>
/// The one exception is a connection closed in the middle of a match: <see cref="Linger"/> keeps its key resumable for a
/// few seconds (FeatureSwitches.ResumeGraceSeconds), so a resume that arrives just after the close still takes the match over.
/// </para>
/// </summary>
public sealed class BlazeSessionKeys
{
    /// <summary>The open connection a key currently belongs to.</summary>
    public sealed record Owner(BlazeClient Client, Action Abort, CancellationToken Closed)
    {
        public string PlayerId => Client.PlayerId;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
    // Closed connections whose keys still resume until the given Environment.TickCount64.
    private readonly Dictionary<BlazeClient, long> _lingering = new(ReferenceEqualityComparer.Instance);

    /// <summary>A new random key (32 lowercase hex characters, 128 bits).</summary>
    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>A short, non-reversible label for logs; never log the key itself.</summary>
    public static string Fingerprint(string key) => Hash(key)[..12];

    /// <summary>Registers a freshly logged-in connection's key and drops any older key the same player held.</summary>
    public void Register(string key, Owner owner)
    {
        var hash = Hash(key);
        lock (_gate)
        {
            foreach (var stale in _owners.Where(pair => pair.Value.PlayerId == owner.PlayerId).Select(pair => pair.Key).ToList())
            {
                _owners.Remove(stale);
            }
            _owners[hash] = owner;
        }
    }

    /// <summary>The open connection that owns <paramref name="key"/>, or null when the key is unknown or its connection closed.</summary>
    public Owner? Find(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        lock (_gate)
        {
            return _owners.TryGetValue(Hash(key), out var owner) && ResumableLocked(owner) ? owner : null;
        }
    }

    /// <summary>
    /// Moves <paramref name="key"/> to the resuming connection and returns the connection it leaves, or null when the key is
    /// unknown, its connection closed, or another resume took it first. The key must belong to <paramref name="next"/>'s player.
    /// </summary>
    public Owner? TakeOver(string key, Owner next)
    {
        var hash = Hash(key);
        lock (_gate)
        {
            if (!_owners.TryGetValue(hash, out var previous) || !ResumableLocked(previous)
                || previous.PlayerId != next.PlayerId || ReferenceEquals(previous.Client, next.Client))
            {
                return null;
            }
            _owners[hash] = next;
            _lingering.Remove(previous.Client);
            return previous;
        }
    }

    /// <summary>
    /// Keeps the keys a closed connection still owns resumable for <paramref name="grace"/>; returns whether it owned any.
    /// The caller releases it afterwards (<see cref="Release"/>), which is a no-op once a resume has taken the key over.
    /// </summary>
    public bool Linger(BlazeClient client, TimeSpan grace)
    {
        lock (_gate)
        {
            if (grace <= TimeSpan.Zero || !_owners.Values.Any(owner => ReferenceEquals(owner.Client, client)))
            {
                return false;
            }
            _lingering[client] = Environment.TickCount64 + (long)grace.TotalMilliseconds;
            return true;
        }
    }

    /// <summary>Drops the key a closing connection still owns (a key it handed to a resumed connection stays).</summary>
    public void Release(BlazeClient client)
    {
        lock (_gate)
        {
            _lingering.Remove(client);
            foreach (var hash in _owners.Where(pair => ReferenceEquals(pair.Value.Client, client)).Select(pair => pair.Key).ToList())
            {
                _owners.Remove(hash);
            }
        }
    }

    private bool ResumableLocked(Owner owner) =>
        !owner.Closed.IsCancellationRequested
        || (_lingering.TryGetValue(owner.Client, out var until) && Environment.TickCount64 < until);

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
