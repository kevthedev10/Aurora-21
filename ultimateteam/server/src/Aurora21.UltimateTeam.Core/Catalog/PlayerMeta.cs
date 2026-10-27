using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// One card definition from players.json/custom-players.json. The raw JSON element stays
/// in the shared document (the catalog is ~42 MB); frequently used fields are decoded once.
/// </summary>
public sealed class PlayerMeta
{
    private readonly JsonElement _element;
    private readonly bool _readOnly;
    private readonly Dictionary<string, JsonNode?> _overrides = new(StringComparer.Ordinal);

    public PlayerMeta(JsonElement element) : this(element, readOnly: false)
    {
    }

    internal PlayerMeta(JsonElement element, bool readOnly)
    {
        _element = element;
        _readOnly = readOnly;
        Refresh();
    }

    public long Rating { get; private set; }

    /// <summary>The card's rarity (0 common, 1 rare, above 1 a special edition).</summary>
    public long Rareflag { get; private set; }

    public string Position { get; private set; } = string.Empty;

    /// <summary>The definition's own <c>special</c> flag.</summary>
    public bool Special { get; private set; }

    /// <summary>Lower-cased <c>tier</c> value (may be empty or non-standard).</summary>
    public string Tier { get; private set; } = string.Empty;

    public bool IsSpecial => Special || Rareflag > 1;

    public bool Has(string key) =>
        _overrides.ContainsKey(key) || (_element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(key, out _));

    public JsonNode? Get(string key)
    {
        if (_overrides.TryGetValue(key, out var value))
        {
            return value;
        }
        if (_element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(key, out var property))
        {
            return property.ValueKind switch
            {
                JsonValueKind.Object => JsonObject.Create(property),
                JsonValueKind.Array => JsonArray.Create(property),
                JsonValueKind.Null => null,
                _ => JsonValue.Create(property),
            };
        }
        return null;
    }

    public long Int(string key, long fallback = 0) => JsonValues.Int(Get(key), fallback);

    public long IntKeep(string key, long fallback = 0)
    {
        var node = Get(key);
        return node is not null && JsonValues.TryInt(node, out var value) ? value : fallback;
    }

    /// <summary>The value as an integer: <paramref name="whenMissing"/> for a missing key, <paramref name="whenFalsy"/> for an empty value.</summary>
    public long IntOr(string key, long whenMissing, long whenFalsy = 0) =>
        JsonValues.Int(Has(key) ? Get(key) : JsonValue.Create(whenMissing), whenFalsy);

    public string Str(string key, string fallback = "") => JsonValues.Str(Get(key), fallback);

    public string Text(string key, string fallback = "") => JsonValues.Text(Get(key), fallback);

    public bool Bool(string key, bool fallback = false) => Has(key) ? JsonValues.HasValue(Get(key)) : fallback;

    public void Override(string key, JsonNode? value)
    {
        if (_readOnly) throw new InvalidOperationException("Shared relational catalog metadata is immutable; clone before applying an override.");
        _overrides[key] = value;
        Refresh();
    }

    public PlayerMeta CloneWithOverrides()
    {
        var clone = new PlayerMeta(_element);
        foreach (var (key, value) in _overrides)
        {
            clone._overrides[key] = value?.DeepClone();
        }
        clone.Refresh();
        return clone;
    }

    /// <summary>Materializes the full definition (used for diagnostics and tests).</summary>
    public JsonObject ToJsonObject()
    {
        var result = _element.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(_element.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        foreach (var (key, value) in _overrides)
        {
            result[key] = value?.DeepClone();
        }
        return result;
    }

    private void Refresh()
    {
        Rating = Int("rating");
        Rareflag = Int("rareflag");
        Position = Str("preferredPosition");
        Special = Bool("special");
        Tier = Str("tier").ToLowerInvariant();
    }
}
