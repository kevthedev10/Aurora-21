using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// A shared-server player's own club name and abbreviation (production Python server CLUB_IDENTITY's CLUB_NAME, 67754d6):
/// every account was served the placeholder club "Local FC"/"LCL", and FIFA printed it on both sides of a match (09-18).
/// The name is the player's recorded name plus a suffix, the abbreviation its first letters padded from a hash of the
/// account; both are pure functions of the account, so they never need configuring and never change between sessions.
/// </summary>
public static partial class ClubNames
{
    /// <summary>The Python server's salt: keeping it keeps every account's padded abbreviation.</summary>
    private const string Salt = "project21:club:";

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The first digest byte that pads a short abbreviation (Python server _derived_club_abbr).</summary>
    private const int AbbreviationPadOffset = 16;

    /// <summary>A name this short is not a name: the club takes the account's fallback name instead.</summary>
    private const int MinimumLength = 3;

    /// <summary>The account's derived club name and abbreviation, from its identity id and recorded display name.</summary>
    public static (string Name, string Abbreviation) For(string identityId, string? displayName, ClubNameRules rules)
    {
        var name = Name(identityId, displayName, rules);
        return (name, Abbreviation(identityId, name, rules));
    }

    /// <summary>
    /// The display name kept to letters, digits, spaces and _ . ' - with single spaces and no separator at either end, plus
    /// the suffix when that fits the maximum length; otherwise the name cut to the maximum. Too short a name becomes
    /// Player followed by the identity's last six digits.
    /// </summary>
    public static string Name(string identityId, string? displayName, ClubNameRules rules)
    {
        var fallback = "Player" + identityId[Math.Max(0, identityId.Length - 6)..];
        var cleaned = Whitespace().Replace(NameCharacters().Replace(displayName ?? string.Empty, string.Empty), " ").Trim(' ', '.', '-', '_', '\'');
        if (cleaned.Length < MinimumLength)
        {
            cleaned = fallback;
        }
        var suffixed = cleaned + rules.NameSuffix;
        if (suffixed.Length <= rules.NameMaxLength)
        {
            return suffixed;
        }
        var cut = cleaned[..Math.Min(cleaned.Length, rules.NameMaxLength)].Trim();
        return cut.Length > 0 ? cut : fallback[..Math.Min(fallback.Length, rules.NameMaxLength)];
    }

    /// <summary>The club name's first letters and digits, upper case, padded with letters chosen by the account's hash.</summary>
    public static string Abbreviation(string identityId, string clubName, ClubNameRules rules)
    {
        var abbreviation = new StringBuilder(string.Concat(clubName.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).Take(rules.AbbreviationLength)));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + identityId));
        for (var index = 0; abbreviation.Length < rules.AbbreviationLength; index++)
        {
            abbreviation.Append(Alphabet[digest[(AbbreviationPadOffset + index) % digest.Length] % Alphabet.Length]);
        }
        return abbreviation.ToString();
    }

    [GeneratedRegex("[^A-Za-z0-9 _.'-]")]
    private static partial Regex NameCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
