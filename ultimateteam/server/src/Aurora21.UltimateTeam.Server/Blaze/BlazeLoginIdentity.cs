using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Protocol.Tdf;

namespace Aurora21.UltimateTeam.Server.Blaze;

/// <summary>Extracts only a well-formed, unique top-level AUTH from the Blaze Login request.</summary>
public static class BlazeLoginIdentity
{
    public static PlayerIdentity? PlayerFor(ReadOnlySpan<byte> payload, NativeSessions sessions)
    {
        if (!TdfReader.TryParse(payload, out var fields))
        {
            return null;
        }
        var auth = fields.Where(field => field.Tag == "AUTH").ToArray();
        return auth.Length == 1 && auth[0].Type == TdfType.String
            ? sessions.PlayerForCode(auth[0].Text)
            : null;
    }
}
