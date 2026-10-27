using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Helpers.Json;

/// <summary>
/// Lenient reading of schemaless JSON values (stored items, request bodies, sbc.json). A value is
/// <em>empty</em> when it is null, false, zero, an empty string or an empty array or object; the
/// <c>fallback</c> overloads treat empty values as absent.
/// </summary>
public static class JsonValues
{
    /// <summary>True unless the value is empty (null, false, 0, "", [] or {}).</summary>
    public static bool HasValue(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value => ValueHasValue(value),
        _ => false,
    };

    /// <summary>The value as an integer: numbers are truncated, booleans are 0/1 and numeric strings are parsed.</summary>
    public static bool TryInt(JsonNode? node, out long result)
    {
        result = 0;
        if (node is not JsonValue value)
        {
            return false;
        }
        switch (Kind(value))
        {
            case JsonValueKind.Number:
                if (value.TryGetValue(out long l))
                {
                    result = l;
                    return true;
                }
                if (value.TryGetValue(out JsonElement e))
                {
                    if (e.TryGetInt64(out l))
                    {
                        result = l;
                        return true;
                    }
                    return TryTruncate(e.GetDouble(), out result);
                }
                if (value.TryGetValue(out int i))
                {
                    result = i;
                    return true;
                }
                if (value.TryGetValue(out double d))
                {
                    return TryTruncate(d, out result);
                }
                return false;
            case JsonValueKind.True:
                result = 1;
                return true;
            case JsonValueKind.False:
                result = 0;
                return true;
            case JsonValueKind.String:
                return long.TryParse(value.GetValue<string>().Trim().Replace("_", string.Empty, StringComparison.Ordinal),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
            default:
                return false;
        }
    }

    /// <summary>The value as an integer, or <paramref name="fallback"/> when it is empty or not a number.</summary>
    public static long Int(JsonNode? node, long fallback = 0) =>
        HasValue(node) && TryInt(node, out var value) ? value : fallback;

    /// <summary>The value as a double, or <paramref name="fallback"/> when it is empty or not a number.</summary>
    public static double Float(JsonNode? node, double fallback = 0)
    {
        if (!HasValue(node) || node is not JsonValue value)
        {
            return fallback;
        }
        switch (Kind(value))
        {
            case JsonValueKind.Number:
                if (value.TryGetValue(out JsonElement e))
                {
                    return e.GetDouble();
                }
                if (value.TryGetValue(out double d))
                {
                    return d;
                }
                return TryInt(value, out var l) ? l : fallback;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.String:
                return double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed : fallback;
            default:
                return fallback;
        }
    }

    /// <summary>The value as text, or <paramref name="fallback"/> when it is empty.</summary>
    public static string Str(JsonNode? node, string fallback = "") =>
        HasValue(node) ? Text(node!) : fallback;

    /// <summary>
    /// The value as text; only null uses <paramref name="fallback"/>. Booleans read as
    /// <c>True</c>/<c>False</c> and other values as their JSON text (stored documents depend on this).
    /// </summary>
    public static string Text(JsonNode? node, string fallback = "")
    {
        if (node is null)
        {
            return fallback;
        }
        if (node is JsonValue value)
        {
            switch (Kind(value))
            {
                case JsonValueKind.String:
                    return value.GetValue<string>();
                case JsonValueKind.True:
                    return "True";
                case JsonValueKind.False:
                    return "False";
                case JsonValueKind.Null:
                    return fallback;
                case JsonValueKind.Number:
                    // The JSON text of a number, without routing a long through a stream and a byte[].
                    // A parsed element keeps its written form (1.0 stays "1.0"); doubles keep
                    // FutJson's formatting rules, so they still go through the writer.
                    if (value.TryGetValue(out JsonElement number))
                    {
                        return number.GetRawText();
                    }
                    if (value.TryGetValue(out long integer))
                    {
                        return integer.ToString(CultureInfo.InvariantCulture);
                    }
                    if (value.TryGetValue(out int small))
                    {
                        return small.ToString(CultureInfo.InvariantCulture);
                    }
                    return FutJson.Serialize(node);
            }
        }
        return FutJson.Serialize(node);
    }

    /// <summary>The integers in a JSON array, skipping entries that are not numbers; empty when the value is not an array.</summary>
    public static List<long> Longs(JsonNode? node) =>
        (node as JsonArray ?? new JsonArray()).Select(item => TryInt(item, out var value) ? (long?)value : null).OfType<long>().ToList();

    public static JsonValueKind Kind(JsonNode? node) => node switch
    {
        null => JsonValueKind.Null,
        JsonObject => JsonValueKind.Object,
        JsonArray => JsonValueKind.Array,
        JsonValue value => ValueKind(value),
        _ => JsonValueKind.Undefined,
    };

    /// <summary>
    /// The kind of a leaf without writing it out. <see cref="JsonValue.GetValueKind"/> serializes the
    /// value to a pooled buffer to discover what it is, which made this one of the server's largest
    /// allocation sites; the backing value answers the same question for free. Anything not recognised
    /// still falls through to the original call, so the answer never changes.
    /// </summary>
    private static JsonValueKind ValueKind(JsonValue value)
    {
        // Values from parsed documents carry their element, which already knows its kind.
        if (value.TryGetValue(out JsonElement element))
        {
            return element.ValueKind;
        }
        if (value.TryGetValue(out string? _))
        {
            return JsonValueKind.String;
        }
        if (value.TryGetValue(out bool flag))
        {
            return flag ? JsonValueKind.True : JsonValueKind.False;
        }
        if (value.TryGetValue(out long _) || value.TryGetValue(out int _) || value.TryGetValue(out double _)
            || value.TryGetValue(out decimal _) || value.TryGetValue(out float _))
        {
            return JsonValueKind.Number;
        }
        return value.GetValueKind();
    }

    public static bool IsString(JsonNode? node) => Kind(node) == JsonValueKind.String;

    /// <summary>A JSON number written without a fraction or exponent (booleans and numeric strings do not count).</summary>
    public static bool IsStrictInt(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue json || Kind(json) != JsonValueKind.Number)
        {
            return false;
        }
        if (json.TryGetValue(out long l))
        {
            value = l;
            return true;
        }
        if (json.TryGetValue(out int i))
        {
            value = i;
            return true;
        }
        if (json.TryGetValue(out JsonElement e))
        {
            var raw = e.GetRawText();
            if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
            {
                return false;
            }
            return e.TryGetInt64(out value);
        }
        return false;
    }

    private static bool ValueHasValue(JsonValue value)
    {
        switch (Kind(value))
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.String:
                return value.GetValue<string>().Length > 0;
            case JsonValueKind.Number:
                if (value.TryGetValue(out long l))
                {
                    return l != 0;
                }
                if (value.TryGetValue(out JsonElement e))
                {
                    return e.GetDouble() != 0;
                }
                if (value.TryGetValue(out int i))
                {
                    return i != 0;
                }
                return value.TryGetValue(out double d) && d != 0;
            default:
                return false;
        }
    }

    private static bool TryTruncate(double value, out long result)
    {
        result = 0;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }
        result = (long)Math.Truncate(value);
        return true;
    }
}

/// <summary>Accessors for loosely typed JSON objects; see <see cref="JsonValues"/> for how values are read.</summary>
public static class JsonObjectExtensions
{
    /// <summary>The stored value, or null when the key is missing.</summary>
    public static JsonNode? Get(this JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) ? node : null;

    /// <summary>The stored value (even null) when the key is present, otherwise <paramref name="whenMissing"/>.</summary>
    public static JsonNode? GetOr(this JsonObject obj, string key, JsonNode? whenMissing) =>
        obj.TryGetPropertyValue(key, out var node) ? node : whenMissing;

    /// <summary>The value as an integer, or <paramref name="fallback"/> when missing, empty or not a number.</summary>
    public static long Int(this JsonObject obj, string key, long fallback = 0) => JsonValues.Int(obj.Get(key), fallback);

    /// <summary>The value as an integer: <paramref name="whenMissing"/> for a missing key, <paramref name="whenFalsy"/> for an empty value.</summary>
    public static long IntOr(this JsonObject obj, string key, long whenMissing, long whenFalsy = 0) =>
        JsonValues.Int(obj.TryGetPropertyValue(key, out var node) ? node : JsonValue.Create(whenMissing), whenFalsy);

    /// <summary>The value as an integer where a stored zero is kept; missing, null or non-numeric values use the fallback.</summary>
    public static long IntKeep(this JsonObject obj, string key, long fallback = 0)
    {
        var node = obj.Get(key);
        return node is not null && JsonValues.TryInt(node, out var value) ? value : fallback;
    }

    /// <summary>The value as text, or <paramref name="fallback"/> when missing or empty.</summary>
    public static string Str(this JsonObject obj, string key, string fallback = "") => JsonValues.Str(obj.Get(key), fallback);

    /// <summary>The value as text, or <paramref name="fallback"/> when missing or null (empty strings are kept).</summary>
    public static string Text(this JsonObject obj, string key, string fallback = "") => JsonValues.Text(obj.Get(key), fallback);

    /// <summary>Whether the value is non-empty, or <paramref name="fallback"/> when the key is missing.</summary>
    public static bool Bool(this JsonObject obj, string key, bool fallback = false) =>
        obj.TryGetPropertyValue(key, out var node) ? JsonValues.HasValue(node) : fallback;

    public static double Float(this JsonObject obj, string key, double fallback = 0) => JsonValues.Float(obj.Get(key), fallback);

    public static JsonObject? Obj(this JsonObject obj, string key) => obj.Get(key) as JsonObject;

    public static JsonArray? Arr(this JsonObject obj, string key) => obj.Get(key) as JsonArray;

    /// <summary>A deep copy.</summary>
    public static JsonObject Copy(this JsonObject obj) => (JsonObject)obj.DeepClone();

    public static JsonObject Set(this JsonObject obj, string key, JsonNode? value)
    {
        obj[key] = value is not null && value.Parent is not null ? value.DeepClone() : value;
        return obj;
    }

    /// <summary>Copies every entry of <paramref name="other"/> in: existing keys are replaced in place, new keys are appended.</summary>
    public static JsonObject Update(this JsonObject obj, JsonObject other)
    {
        foreach (var (key, value) in other)
        {
            obj[key] = value?.DeepClone();
        }
        return obj;
    }

    /// <summary>Runs <paramref name="action"/> on the object and returns it (for inline construction).</summary>
    public static JsonObject Also(this JsonObject obj, Action<JsonObject> action)
    {
        action(obj);
        return obj;
    }

    public static JsonNode? Pop(this JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
        {
            return null;
        }
        obj.Remove(key);
        return node is null ? null : node.DeepClone();
    }
}
