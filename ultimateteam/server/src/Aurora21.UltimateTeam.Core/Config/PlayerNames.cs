using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// Players' display names on a shared server (Python server 62ac9ad, 81c770e): each player's own name, recorded by the
/// sign-in gateway in <c>state/player-names.json</c> (identity id to name), instead of one placeholder every club shared,
/// which named both players of a match alike in FUT profiles, Blaze logins, rosters and the opponent lookup.
/// </summary>
public static partial class PlayerNames
{
    private const int PersonaMaxLength = 16;
    private const int LeaderboardMaxLength = 20;
    private const int MinimumLength = 3;

    /// <summary>The name the gateway recorded for an identity, or null (no file, unreadable file or no entry).</summary>
    public static string? Recorded(string path, string identityId)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject names && names[identityId] is JsonValue value
                && value.TryGetValue<string>(out var name) ? name : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static readonly object RecordGate = new();

    /// <summary>
    /// Records an identity's name for <see cref="Recorded"/> (the hosted launch API does, from the verified launch),
    /// keeping every other entry. The file is replaced whole, so a reader never sees half a write; an unreadable file
    /// is kept aside as <c>.bad</c> rather than silently dropped. Returns whether the stored name changed.
    /// </summary>
    public static bool Record(string path, string identityId, string name)
    {
        lock (RecordGate)
        {
            JsonObject names;
            try
            {
                names = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch (FileNotFoundException) { names = new JsonObject(); }
            catch (DirectoryNotFoundException) { names = new JsonObject(); }
            catch (JsonException)
            {
                File.Copy(path, path + ".bad", overwrite: true);
                names = new JsonObject();
            }
            if (names[identityId] is JsonValue current && current.TryGetValue<string>(out var existing) && existing == name)
            {
                return false;
            }
            names[identityId] = name;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, names.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
    }

    /// <summary>An EA-style persona name: letters, digits, "_", "-" and ".", at most 16; Player&lt;id&gt; when too little is left.</summary>
    public static string Persona(string identityId, string? raw)
    {
        var cleaned = PersonaCharacters().Replace(raw ?? string.Empty, string.Empty);
        cleaned = cleaned[..Math.Min(cleaned.Length, PersonaMaxLength)];
        return cleaned.Length >= MinimumLength ? cleaned : Fallback(identityId);
    }

    /// <summary>
    /// A leaderboard row's name: the persona alphabet plus single spaces, at most 20 characters, or null when too little
    /// is left. The Leaderboards screen hung on "Retrieving Results" once a row held any other character (emoji, maths
    /// letters, diacritics).
    /// </summary>
    public static string? Readable(string? raw)
    {
        var cleaned = Whitespace().Replace(LeaderboardCharacters().Replace(raw ?? string.Empty, string.Empty), " ").Trim();
        cleaned = cleaned[..Math.Min(cleaned.Length, LeaderboardMaxLength)].Trim();
        return cleaned.Length >= MinimumLength ? cleaned : null;
    }

    private static string Fallback(string identityId) => "Player" + identityId[Math.Max(0, identityId.Length - 6)..];

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex PersonaCharacters();

    [GeneratedRegex("[^A-Za-z0-9 _.-]")]
    private static partial Regex LeaderboardCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
