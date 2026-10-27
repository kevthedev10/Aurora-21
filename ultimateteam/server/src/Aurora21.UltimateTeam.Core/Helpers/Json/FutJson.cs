using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Helpers.Json;

/// <summary>
/// The FUT wire JSON: compact, UTF-8 without escaping non-ASCII text, keys in insertion order,
/// and whole-number doubles written with a decimal point (<c>1.0</c>, not <c>1</c>) so values the
/// client reads as floats keep their type.
/// </summary>
public static class FutJson
{
    /// <summary>The wire writer settings. Generated writers must use these to emit the same bytes.</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 256,
    };

    public static byte[] SerializeToUtf8(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            Write(writer, node);
        }
        return stream.ToArray();
    }

    public static string Serialize(JsonNode? node) => Encoding.UTF8.GetString(SerializeToUtf8(node));

    /// <summary>A request body as JSON: empty or invalid bodies become an empty object.</summary>
    public static JsonNode SafeParse(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(body), NodeOptions, DocumentOptions) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public static JsonNode? Parse(string text) => JsonNode.Parse(text, NodeOptions, DocumentOptions);

    public static JsonObject? ParseObject(string text)
    {
        try
        {
            return Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes one value with the wire's number and escaping rules; used by generated writers.</summary>
    public static void WriteValue(Utf8JsonWriter writer, JsonNode? node) => Write(writer, node);

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj)
                {
                    writer.WritePropertyName(key);
                    Write(writer, value);
                }
                writer.WriteEndObject();
                return;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var value in array)
                {
                    Write(writer, value);
                }
                writer.WriteEndArray();
                return;
            case JsonValue value:
                if (value.TryGetValue(out JsonElement element))
                {
                    element.WriteTo(writer);
                    return;
                }
                if (value.TryGetValue(out double d))
                {
                    writer.WriteRawValue(FormatDouble(d), skipInputValidation: true);
                    return;
                }
                if (value.TryGetValue(out float f))
                {
                    writer.WriteRawValue(FormatDouble(f), skipInputValidation: true);
                    return;
                }
                value.WriteTo(writer);
                return;
        }
    }

    /// <summary>
    /// Shortest round-trip digits, always with a decimal point or an exponent; exponent notation
    /// below 1e-4 and from 1e16 (e.g. <c>0.1</c>, <c>1.0</c>, <c>1e-05</c>, <c>1e+16</c>).
    /// </summary>
    public static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }
        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }
        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // "E16" yields up to 17 significant digits; "R" yields the shortest round-trip
        // digits. Extract digits and decimal exponent from the round-trip form.
        var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = roundTrip.StartsWith('-');
        if (negative)
        {
            roundTrip = roundTrip[1..];
        }
        var exponent = 0;
        var ePosition = roundTrip.IndexOfAny(['E', 'e']);
        if (ePosition >= 0)
        {
            exponent = int.Parse(roundTrip[(ePosition + 1)..], CultureInfo.InvariantCulture);
            roundTrip = roundTrip[..ePosition];
        }
        var point = roundTrip.IndexOf('.');
        var digits = point >= 0 ? roundTrip.Remove(point, 1) : roundTrip;
        var pointPosition = (point >= 0 ? point : roundTrip.Length) + exponent;
        var trimmedLeading = digits.TrimStart('0');
        pointPosition -= digits.Length - trimmedLeading.Length;
        digits = trimmedLeading.TrimEnd('0');
        if (digits.Length == 0)
        {
            return negative ? "-0.0" : "0.0";
        }

        // Scientific exponent of the leading digit.
        var sciExponent = pointPosition - 1;
        var builder = new StringBuilder();
        if (negative)
        {
            builder.Append('-');
        }
        if (sciExponent < -4 || sciExponent >= 16)
        {
            builder.Append(digits[0]);
            if (digits.Length > 1)
            {
                builder.Append('.').Append(digits.AsSpan(1));
            }
            builder.Append('e').Append(sciExponent < 0 ? '-' : '+').Append(Math.Abs(sciExponent).ToString("00", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
        if (pointPosition <= 0)
        {
            builder.Append("0.").Append('0', -pointPosition).Append(digits);
        }
        else if (pointPosition >= digits.Length)
        {
            builder.Append(digits).Append('0', pointPosition - digits.Length).Append(".0");
        }
        else
        {
            builder.Append(digits.AsSpan(0, pointPosition)).Append('.').Append(digits.AsSpan(pointPosition));
        }
        return builder.ToString();
    }
}
