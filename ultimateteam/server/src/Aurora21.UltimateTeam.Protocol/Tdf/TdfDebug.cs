using System.Text;

namespace Aurora21.UltimateTeam.Protocol.Tdf;

/// <summary>Non-mutating, best-effort description of an unknown TDF payload for diagnostics.</summary>
public static class TdfDebug
{
    public static string Summarize(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
        {
            return "<empty>";
        }

        var strings = new List<string>();
        var current = new StringBuilder();
        foreach (var b in raw)
        {
            if (b is >= 32 and <= 126)
            {
                current.Append((char)b);
                continue;
            }
            if (current.Length >= 3)
            {
                strings.Add(current.ToString());
            }
            current.Clear();
        }
        if (current.Length >= 3)
        {
            strings.Add(current.ToString());
        }

        var tags = new List<string>();
        for (var i = 0; i < Math.Max(0, raw.Length - 3); i++)
        {
            var name = TdfTag.Decode(raw.Slice(i, 3)).TrimEnd();
            if (name.Length is >= 2 and <= 4 && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
                && !tags.Contains(name))
            {
                tags.Add(name);
            }
            if (tags.Count >= 24)
            {
                break;
            }
        }

        var hex = Convert.ToHexString(raw[..Math.Min(256, raw.Length)]).ToLowerInvariant();
        return $"hex={hex} ascii=[{string.Join(", ", strings.Take(20))}] tags=[{string.Join(", ", tags)}]";
    }
}
