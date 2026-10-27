using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// Strict deserialization for bundled data files: camelCase names, unknown members rejected so a
/// typo fails loudly, and top-level keys starting with "_" treated as comments.
/// </summary>
public static class RulesJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <exception cref="InvalidDataException">The document is not a valid <typeparamref name="T"/>; the message names <paramref name="source"/>.</exception>
    public static T Deserialize<T>(string text, string source)
    {
        try
        {
            var document = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("expected a JSON object");
            foreach (var comment in document.Select(entry => entry.Key).Where(key => key.StartsWith('_')).ToList())
            {
                document.Remove(comment);
            }
            return document.Deserialize<T>(Options) ?? throw new InvalidDataException("the file is empty");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Invalid data file {source}: {error.Message}", error);
        }
    }
}
