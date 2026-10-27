using System.Security.Cryptography;
using System.Text;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// Who a request belongs to. The local server has one player; a shared server derives stable,
/// distinct Blaze ids for each player from their identity, so two players on one roster never
/// share persona ids and the identity itself is never exposed.
/// </summary>
public sealed record PlayerIdentity(string Id, long PersonaId, long NucleusId, long ClubId)
{
    /// <summary>The identity of the only player of a local server.</summary>
    public const string SinglePlayerId = "single-player";

    private const long IdBase = 1_000_000_000;
    private const long PeerIdBase = 100_000_000_000_000_000;
    private const ulong PeerIdSpan = 900_000_000_000_000_000;
    private const int MinimumIdLength = 5;
    private const int MaximumIdLength = 32;

    public bool IsSinglePlayer => Id == SinglePlayerId;

    /// <summary>The local player, with the configured ids.</summary>
    public static PlayerIdentity SinglePlayer(ServerConfig config) => new(SinglePlayerId, config.PersonaId, config.NucleusId, config.ClubId);

    /// <summary>A player of a shared server, identified by a 5-32 digit id (such as a Discord id).</summary>
    public static PlayerIdentity Of(string id)
    {
        if (id.Length is < MinimumIdLength or > MaximumIdLength || !id.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("invalid player identity", nameof(id));
        }
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes("project21:" + id));
        long Id(int offset) => IdBase + ((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(offset, 4)) % IdBase);
        return new PlayerIdentity(id, Id(0), Id(4), Id(8));
    }

    /// <summary>A stable numeric identity for a trusted peer address (LAN or VPN), without exposing the address.</summary>
    public static PlayerIdentity FromPeerAddress(string address)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("project21-peer:" + address));
        var id = PeerIdBase + (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest.AsSpan(0, 8)) % PeerIdSpan);
        return Of(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
