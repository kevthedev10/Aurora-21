using System.Globalization;
using System.Text;

namespace Aurora21.UltimateTeam.Core.Web;

/// <summary>Ordered multi-value query and form decoding: repeated keys keep every value in order.</summary>
public sealed class QueryValues
{
    private readonly List<KeyValuePair<string, List<string>>> _entries = new();

    public IReadOnlyList<KeyValuePair<string, List<string>>> Entries => _entries;

    public IEnumerable<string> Keys => _entries.Select(entry => entry.Key);

    public int Count => _entries.Count;

    public bool ContainsKey(string key) => _entries.Any(entry => entry.Key == key);

    public IReadOnlyList<string> Get(string key) =>
        _entries.FirstOrDefault(entry => entry.Key == key).Value ?? (IReadOnlyList<string>)Array.Empty<string>();

    public string? First(string key) => Get(key) is { Count: > 0 } values ? values[0] : null;

    public string? Last(string key) => Get(key) is { Count: > 0 } values ? values[^1] : null;

    /// <summary>The distinct positive ids in every comma-separated value of <paramref name="key"/>, in order.</summary>
    public List<long> Ids(string key, int limit = int.MaxValue) => Get(key)
        .SelectMany(value => value.Split(','))
        .Select(part => long.TryParse(part.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ? id : 0)
        .Where(id => id > 0)
        .Distinct()
        .Take(limit)
        .ToList();

    internal void Add(string key, string value)
    {
        var index = _entries.FindIndex(entry => entry.Key == key);
        if (index < 0)
        {
            _entries.Add(new(key, new List<string> { value }));
        }
        else
        {
            _entries[index].Value.Add(value);
        }
    }
}

public static class QueryString
{
    /// <summary>Lenient parsing (blank values dropped unless requested; malformed fields skipped).</summary>
    public static QueryValues Parse(string query, bool keepBlankValues = false)
    {
        var result = new QueryValues();
        foreach (var field in query.Split('&'))
        {
            if (field.Length == 0)
            {
                continue;
            }
            var separator = field.IndexOf('=');
            if (separator < 0)
            {
                if (!keepBlankValues)
                {
                    continue;
                }
                result.Add(Unquote(field, strict: false), string.Empty);
                continue;
            }
            var value = field[(separator + 1)..];
            if (value.Length > 0 || keepBlankValues)
            {
                result.Add(Unquote(field[..separator], strict: false), Unquote(value, strict: false));
            }
        }
        return result;
    }

    /// <summary>
    /// Strict decoding for security-sensitive requests: blank values are kept, and a malformed pair,
    /// invalid percent-encoding or more than <paramref name="maxFields"/> fields fails the whole query.
    /// </summary>
    public static bool TryParseStrict(string query, int maxFields, out QueryValues result)
    {
        result = new QueryValues();
        if (query.Length == 0)
        {
            return true;
        }
        if (query.Count(c => c == '&') + 1 > maxFields)
        {
            return false;
        }
        foreach (var field in query.Split('&'))
        {
            var separator = field.IndexOf('=');
            if (separator < 0)
            {
                return false;
            }
            try
            {
                result.Add(Unquote(field[..separator], strict: true), Unquote(field[(separator + 1)..], strict: true));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }
        return true;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: false);

    /// <summary><c>unquote(s.replace("+", " "))</c>.</summary>
    private static string Unquote(string text, bool strict)
    {
        text = text.Replace('+', ' ');
        if (!text.Contains('%'))
        {
            return text;
        }
        var bytes = new List<byte>();
        var builder = new StringBuilder();
        void Flush()
        {
            if (bytes.Count > 0)
            {
                builder.Append((strict ? StrictUtf8 : LenientUtf8).GetString(bytes.ToArray()));
                bytes.Clear();
            }
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            Flush();
            builder.Append(text[i]);
        }
        Flush();
        return builder.ToString();
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
}
