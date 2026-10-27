namespace Aurora21.UltimateTeam.Core.Models;

/// <summary>A client request that violates a club rule.</summary>
public sealed class ClubRuleException : Exception
{
    public ClubRuleException(string message) : base(message)
    {
    }
}
