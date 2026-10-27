using System.Globalization;
using System.Text.RegularExpressions;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>Fills <c>{name}</c> placeholders in display text taken from a data file.</summary>
public static partial class RuleText
{
    /// <summary>
    /// Replaces each <c>{name}</c> that has a value in one pass (substituted text is never
    /// re-scanned); placeholders without a value are left as written.
    /// </summary>
    public static string Format(string text, params (string Name, object Value)[] values)
    {
        if (values.Length == 0)
        {
            return text;
        }
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            lookup[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        return PlaceholderPattern().Replace(text, match => lookup.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderPattern();
}
