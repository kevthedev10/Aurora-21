namespace Aurora21.UltimateTeam.Core.Models;

/// <summary>A local OAuth endpoint's status and serialized JSON body.</summary>
public sealed record AuthResponse(int Status, byte[] Body);
