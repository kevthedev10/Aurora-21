using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Protocol.Tdf;

namespace Aurora21.UltimateTeam.Core.Services.Auth;

/// <summary>
/// Redacted evidence of which credential the game sends where, for the hosting investigation (ultimateteam/native-hosting-investigation.md):
/// whether the auth code the auth bridge hands FIFA, the access token it exchanges it for, or this club's session token
/// reaches the Blaze login and UT auth. A value this server issued is named; any other string is shown only as its length
/// and a SHA-256 prefix, and a large number only by its digit count, so a real EA credential or account id (EA App mode)
/// is never logged. Small numbers (flags, platform ids) are shown as they are.
/// </summary>
public static class CredentialEvidence
{
    private const int MaxEntries = 64;
    private const long SmallNumber = 100_000;

    /// <summary>A string value: named when this server issued it, otherwise its length and hash prefix.</summary>
    public static string Describe(string? value, GameContext game)
    {
        if (value is null)
        {
            return "absent";
        }
        if (value.Length == 0)
        {
            return "empty";
        }
        return value switch
        {
            LocalAuthService.AuthCode => "local-auth-code",
            LocalAuthService.AccessToken => "local-access-token",
            LocalAuthService.RefreshToken => "local-refresh-token",
            _ when value == game.Token => "club-session-token",
            _ when value == game.Sid => "club-sid",
            _ when value == game.UtSid => "club-ut-sid",
            _ when value.Contains(LocalAuthService.AccessToken, StringComparison.Ordinal) => $"contains-local-access-token(len={value.Length})",
            _ when game.Token.Length > 0 && value.Contains(game.Token, StringComparison.Ordinal) => $"contains-club-session-token(len={value.Length})",
            // Short plain words (a platform, "nucleus:rest"): no digits, so not a token.
            _ when value.Length <= 16 && value.All(c => char.IsAsciiLetter(c) || c is '_' or '-' or '.' or ' ' or ':' or '/') => value,
            _ => $"other(len={value.Length},sha256={Prefix(Encoding.UTF8.GetBytes(value))})",
        };
    }

    /// <summary>A number: small ones as they are, this club's persona and Nucleus ids by name, others by digit count.</summary>
    public static string Describe(long value, GameContext game) =>
        value == game.Config.PersonaId ? "local-persona-id"
        : value == game.Config.NucleusId ? "local-nucleus-id"
        : Math.Abs(value) < SmallNumber ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"number(digits={Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture).Length})";

    /// <summary>Every member of a Blaze request, as path=description.</summary>
    public static string DescribeTdf(ReadOnlySpan<byte> payload, GameContext game)
    {
        if (payload.IsEmpty)
        {
            return "<empty>";
        }
        if (!TdfReader.TryParse(payload, out var fields))
        {
            return $"<unparsed len={payload.Length}>";
        }
        var entries = new List<string>();
        foreach (var field in fields)
        {
            Walk(field, field.Tag, entries, game);
        }
        return Join(entries);
    }

    /// <summary>Every leaf of a JSON body, as path=description.</summary>
    public static string DescribeJson(byte[] body, GameContext game)
    {
        if (body.Length == 0)
        {
            return "<empty>";
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return $"<not json len={body.Length}>";
        }
        var entries = new List<string>();
        Walk(root, "$", entries, game);
        return Join(entries);
    }

    /// <summary>A form or query string's fields, as name=description.</summary>
    public static string DescribeForm(string text, GameContext game)
    {
        if (text.Length == 0)
        {
            return "<empty>";
        }
        var entries = new List<string>();
        foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Take(MaxEntries))
        {
            var parts = pair.Split('=', 2);
            entries.Add($"{Unescape(parts[0])}={Describe(parts.Length > 1 ? Unescape(parts[1]) : null, game)}");
        }
        return Join(entries);
    }

    /// <summary>The headers that can carry a credential (auth, token, session, sid, nucleus or cookie in the name), described.</summary>
    public static string DescribeHeaders(IEnumerable<KeyValuePair<string, string>> headers, GameContext game)
    {
        var entries = headers
            .Where(header => header.Key.Contains("auth", StringComparison.OrdinalIgnoreCase) || header.Key.Contains("token", StringComparison.OrdinalIgnoreCase)
                             || header.Key.Contains("session", StringComparison.OrdinalIgnoreCase) || header.Key.Contains("sid", StringComparison.OrdinalIgnoreCase)
                             || header.Key.Contains("nucleus", StringComparison.OrdinalIgnoreCase) || header.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase))
            .Select(header => $"{header.Key.ToLowerInvariant()}={Describe(header.Value, game)}")
            .ToList();
        return entries.Count == 0 ? "<none>" : Join(entries);
    }

    private static void Walk(TdfField field, string path, List<string> entries, GameContext game)
    {
        if (entries.Count >= MaxEntries)
        {
            return;
        }
        switch (field.Type)
        {
            case TdfType.String:
                entries.Add($"{path}={Describe(field.Text, game)}");
                break;
            case TdfType.Integer:
            case TdfType.TimeValue:
                entries.Add($"{path}={Describe(field.Integer, game)}");
                break;
            case TdfType.Blob:
                entries.Add($"{path}=blob(len={field.Bytes?.Length ?? 0},sha256={Prefix(field.Bytes ?? [])})");
                break;
            default:
                for (var index = 0; index < field.Children.Count; index++)
                {
                    var child = field.Children[index];
                    Walk(child, child.Tag.Length > 0 ? $"{path}.{child.Tag}" : $"{path}[{index}]", entries, game);
                }
                break;
        }
    }

    private static void Walk(JsonNode? node, string path, List<string> entries, GameContext game)
    {
        if (entries.Count >= MaxEntries)
        {
            return;
        }
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    Walk(child, $"{path}.{key}", entries, game);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    Walk(array[index], $"{path}[{index}]", entries, game);
                }
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                entries.Add($"{path}={Describe(text, game)}");
                break;
            case JsonValue value when value.TryGetValue<long>(out var number):
                entries.Add($"{path}={Describe(number, game)}");
                break;
            case JsonValue value:
                entries.Add($"{path}={value.GetValueKind().ToString().ToLowerInvariant()}");
                break;
            default:
                entries.Add($"{path}=null");
                break;
        }
    }

    private static string Join(List<string> entries) =>
        entries.Count == 0 ? "<none>" : string.Join(' ', entries) + (entries.Count >= MaxEntries ? " ..." : string.Empty);

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private static string Prefix(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
}
