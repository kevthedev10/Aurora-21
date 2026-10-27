namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// The runtime cannot start or run a maintenance command because of its environment: roots,
/// the PostgreSQL cluster or binaries, or an incompatible database. Reported without a stack trace.
/// </summary>
public sealed class RuntimeSetupException : InvalidOperationException
{
    public RuntimeSetupException(string message) : base(message)
    {
    }
}
