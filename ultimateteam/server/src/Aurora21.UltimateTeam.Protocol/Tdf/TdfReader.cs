using System.Text;

namespace Aurora21.UltimateTeam.Protocol.Tdf;

/// <summary>One decoded TDF member. Scalars use <see cref="Integer"/>/<see cref="Text"/>/<see cref="Bytes"/>; containers use <see cref="Children"/>.</summary>
public sealed record TdfField(string Tag, TdfType Type)
{
    public long Integer { get; init; }
    public string? Text { get; init; }
    public byte[]? Bytes { get; init; }
    public IReadOnlyList<TdfField> Children { get; init; } = Array.Empty<TdfField>();
}

/// <summary>
/// Structured TDF decoder: walks the real structure in wire order and only falls back to a raw
/// byte search for the packed tag when a payload cannot be parsed.
/// </summary>
public static class TdfReader
{
    private const int MaxDepth = 32;

    /// <summary>The most entries a byte-searched map may claim before it is treated as corrupt.</summary>
    private const int MaxScannedMapEntries = 4096;

    public static bool TryParse(ReadOnlySpan<byte> payload, out IReadOnlyList<TdfField> fields)
    {
        try
        {
            var position = 0;
            var result = new List<TdfField>();
            while (position < payload.Length)
            {
                result.Add(ReadMember(payload, ref position, 0));
            }
            fields = result;
            return true;
        }
        catch (FormatException)
        {
            fields = Array.Empty<TdfField>();
            return false;
        }
    }

    public static string? FindString(ReadOnlySpan<byte> payload, string tag)
    {
        if (TryParse(payload, out var fields))
        {
            return Find(fields, tag, TdfType.String)?.Text;
        }
        return ScanString(payload, tag);
    }

    public static long? FindInteger(ReadOnlySpan<byte> payload, string tag)
    {
        if (TryParse(payload, out var fields))
        {
            return Find(fields, tag, TdfType.Integer)?.Integer;
        }
        return ScanInteger(payload, tag);
    }

    /// <summary>Every integer member with this tag, in wire order (for example each MACI of a request).</summary>
    public static IReadOnlyList<long> FindAllIntegers(ReadOnlySpan<byte> payload, string tag) =>
        TryParse(payload, out var fields)
            ? FindAll(fields, tag, TdfType.Integer).Select(field => field.Integer).ToList()
            : ScanAll<long>(payload, tag, TdfType.Integer, ScanIntegerAt);

    /// <summary>Every string member with this tag, in wire order, up to <paramref name="limit"/> values.</summary>
    public static IReadOnlyList<string> FindAllStrings(ReadOnlySpan<byte> payload, string tag, int limit = 64) =>
        (TryParse(payload, out var fields)
            ? FindAll(fields, tag, TdfType.String).Select(field => field.Text ?? string.Empty).ToList()
            : ScanAll<string>(payload, tag, TdfType.String, ScanStringAt))
        .Take(limit)
        .ToList();

    /// <summary>The first <c>list&lt;string&gt;</c> member with this tag; empty values are skipped.</summary>
    public static IReadOnlyList<string> FindStringList(ReadOnlySpan<byte> payload, string tag, int limit = 64)
    {
        if (TryParse(payload, out var fields))
        {
            return (FindAll(fields, tag, TdfType.List).FirstOrDefault()?.Children ?? Array.Empty<TdfField>())
                .Where(child => child.Type == TdfType.String && !string.IsNullOrEmpty(child.Text))
                .Select(child => child.Text!)
                .Take(limit)
                .ToList();
        }
        return ScanStringList(payload, tag, limit);
    }

    /// <summary>The first ObjectId member with this tag.</summary>
    public static (long Component, long EntityType, long EntityId)? FindObjectId(ReadOnlySpan<byte> payload, string tag)
    {
        if (TryParse(payload, out var fields))
        {
            return Find(fields, tag, TdfType.ObjectId) is { } field
                ? (field.Children[0].Integer, field.Children[1].Integer, field.Integer)
                : null;
        }
        var index = payload.IndexOf(TdfTag.Encode(tag, TdfType.ObjectId));
        if (index < 0)
        {
            return null;
        }
        try
        {
            var position = index + 4;
            return (ReadVarInt(payload, ref position), ReadVarInt(payload, ref position), ReadVarInt(payload, ref position));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The first <c>map&lt;string,string&gt;</c> member with this tag.</summary>
    public static IReadOnlyDictionary<string, string> FindStringMap(ReadOnlySpan<byte> payload, string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryParse(payload, out var fields))
        {
            if (FindAll(fields, tag, TdfType.Map).FirstOrDefault() is { } map
                && map.Children.All(child => child.Type == TdfType.String))
            {
                for (var i = 0; i + 1 < map.Children.Count; i += 2)
                {
                    result[map.Children[i].Text ?? string.Empty] = map.Children[i + 1].Text ?? string.Empty;
                }
            }
            return result;
        }
        var index = payload.IndexOf(TdfTag.Encode(tag, TdfType.Map));
        if (index < 0 || index + 6 > payload.Length
            || payload[index + 4] != (byte)TdfType.String || payload[index + 5] != (byte)TdfType.String)
        {
            return result;
        }
        try
        {
            var position = index + 6;
            var count = ReadVarInt(payload, ref position);
            if (count > MaxScannedMapEntries)
            {
                return result;
            }
            for (var i = 0; i < count; i++)
            {
                var key = ReadScannedString(payload, ref position);
                result[key] = ReadScannedString(payload, ref position);
            }
        }
        catch (FormatException)
        {
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// The first <c>map&lt;string, generic&gt;</c> member with this tag, values as text: StartMatchmakingScenario's SCNA
    /// search criteria (Python server 51a1670, FUT_Rivals, FUT_Draft and FUT_Champions captures of 2026-09-24). Each value
    /// is 0x01 (present), the value's type id (6 string, 22 integer), the value and a 0x00 terminator; integers come back in
    /// decimal. Any other shape returns an empty map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FindGenericMap(ReadOnlySpan<byte> payload, string tag)
    {
        const byte GenericValue = 0xC;
        const long GenericString = 6;
        const long GenericInteger = 22;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = payload.IndexOf(TdfTag.Encode(tag, TdfType.Map));
        if (index < 0 || index + 6 > payload.Length || payload[index + 4] != (byte)TdfType.String || payload[index + 5] != GenericValue)
        {
            return result;
        }
        try
        {
            var position = index + 6;
            var count = ReadVarInt(payload, ref position);
            if (count < 0 || count > MaxScannedMapEntries)
            {
                return result;
            }
            for (var i = 0; i < count; i++)
            {
                var key = ReadScannedString(payload, ref position);
                if (ReadByte(payload, ref position) != 1)
                {
                    result.Clear();
                    return result;
                }
                var type = ReadVarInt(payload, ref position);
                string value;
                if (type == GenericString)
                {
                    value = ReadScannedString(payload, ref position);
                }
                else if (type == GenericInteger)
                {
                    value = ReadVarInt(payload, ref position).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    result.Clear();
                    return result;
                }
                if (ReadByte(payload, ref position) != 0)
                {
                    result.Clear();
                    return result;
                }
                result[key] = value;
            }
        }
        catch (FormatException)
        {
            result.Clear();
        }
        return result;
    }

    /// <summary>The first blob member with this tag, without interpreting its contents.</summary>
    public static byte[]? FindBlob(ReadOnlySpan<byte> payload, string tag)
    {
        if (TryParse(payload, out var fields))
        {
            return Find(fields, tag, TdfType.Blob)?.Bytes;
        }
        var index = payload.IndexOf(TdfTag.Encode(tag, TdfType.Blob));
        if (index < 0)
        {
            return null;
        }
        try
        {
            var position = index + 4;
            var length = ReadVarInt(payload, ref position);
            return length < 0 || position + length > payload.Length ? null : payload.Slice(position, (int)length).ToArray();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Every member with this tag and type, depth first (wire order).</summary>
    public static IEnumerable<TdfField> FindAll(IEnumerable<TdfField> fields, string tag, TdfType type)
    {
        foreach (var field in fields)
        {
            if (field.Tag == tag && field.Type == type)
            {
                yield return field;
            }
            foreach (var nested in FindAll(field.Children, tag, type))
            {
                yield return nested;
            }
        }
    }

    public static TdfField? Find(IEnumerable<TdfField> fields, string tag, TdfType type)
    {
        foreach (var field in fields)
        {
            if (field.Tag == tag && field.Type == type)
            {
                return field;
            }
            var nested = Find(field.Children, tag, type);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// A Blaze varint: the first byte carries a continuation flag (0x80), the sign (0x40) and 6 value bits, later bytes
    /// 7 bits each. FIFA 21 writes negative numbers sign-magnitude (-1 = 0x41). A 64-bit two's-complement varint, which
    /// <see cref="TdfWriter"/> emits for negatives, has the sign bit clear and still reads back as the same value.
    /// </summary>
    public static long ReadVarInt(ReadOnlySpan<byte> raw, ref int position)
    {
        if (position >= raw.Length)
        {
            throw new FormatException("truncated TDF varint");
        }
        var first = raw[position++];
        var negative = (first & 0x40) != 0;
        var value = (ulong)(first & 0x3F);
        var shift = 6;
        if ((first & 0x80) != 0)
        {
            while (true)
            {
                if (position >= raw.Length)
                {
                    throw new FormatException("truncated TDF varint");
                }
                var next = raw[position++];
                if (shift < 64)
                {
                    value |= (ulong)(next & 0x7F) << shift;
                }
                if ((next & 0x80) == 0)
                {
                    break;
                }
                shift += 7;
            }
        }
        if (!negative)
        {
            return unchecked((long)value);
        }
        // The client decodes a "negative zero" as the most negative value.
        return value == 0 ? long.MinValue : -unchecked((long)value);
    }

    private static TdfField ReadMember(ReadOnlySpan<byte> raw, ref int position, int depth)
    {
        if (position + 4 > raw.Length)
        {
            throw new FormatException("truncated TDF member header");
        }
        var tag = TdfTag.Decode(raw.Slice(position, 3));
        var type = (TdfType)raw[position + 3];
        position += 4;
        return ReadValue(raw, ref position, tag, type, depth);
    }

    private static TdfField ReadValue(ReadOnlySpan<byte> raw, ref int position, string tag, TdfType type, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("TDF nesting too deep");
        }
        switch (type)
        {
            case TdfType.Integer:
            case TdfType.TimeValue:
                return new TdfField(tag, type) { Integer = ReadVarInt(raw, ref position) };
            case TdfType.String:
                {
                    var length = CheckedLength(raw, ref position);
                    var bytes = raw.Slice(position, length);
                    position += length;
                    if (bytes.Length > 0 && bytes[^1] == 0)
                    {
                        bytes = bytes[..^1];
                    }
                    return new TdfField(tag, type) { Text = Encoding.UTF8.GetString(bytes) };
                }
            case TdfType.Blob:
                {
                    var length = CheckedLength(raw, ref position);
                    var bytes = raw.Slice(position, length).ToArray();
                    position += length;
                    return new TdfField(tag, type) { Bytes = bytes };
                }
            case TdfType.Struct:
                return new TdfField(tag, type) { Children = ReadStructBody(raw, ref position, depth + 1) };
            case TdfType.List:
                {
                    var elementType = (TdfType)ReadByte(raw, ref position);
                    var count = CheckedCount(raw, ref position);
                    var children = new List<TdfField>(Math.Min(count, 1024));
                    for (var i = 0; i < count; i++)
                    {
                        children.Add(ReadValue(raw, ref position, string.Empty, elementType, depth + 1));
                    }
                    return new TdfField(tag, type) { Children = children };
                }
            case TdfType.Map:
                {
                    var keyType = (TdfType)ReadByte(raw, ref position);
                    var valueType = (TdfType)ReadByte(raw, ref position);
                    var count = CheckedCount(raw, ref position);
                    var children = new List<TdfField>(Math.Min(count * 2, 2048));
                    for (var i = 0; i < count; i++)
                    {
                        children.Add(ReadValue(raw, ref position, string.Empty, keyType, depth + 1));
                        children.Add(ReadValue(raw, ref position, string.Empty, valueType, depth + 1));
                    }
                    return new TdfField(tag, type) { Children = children };
                }
            case TdfType.Union:
                {
                    var active = ReadByte(raw, ref position);
                    if (active == 0x7F)
                    {
                        return new TdfField(tag, type) { Integer = active };
                    }
                    return new TdfField(tag, type) { Integer = active, Children = new[] { ReadMember(raw, ref position, depth + 1) } };
                }
            case TdfType.Variable:
                {
                    var present = ReadByte(raw, ref position);
                    if (present == 0)
                    {
                        return new TdfField(tag, type);
                    }
                    var typeId = ReadVarInt(raw, ref position);
                    return new TdfField(tag, type) { Integer = typeId, Children = ReadStructBody(raw, ref position, depth + 1) };
                }
            case TdfType.ObjectType:
                {
                    var component = ReadVarInt(raw, ref position);
                    var entityType = ReadVarInt(raw, ref position);
                    return new TdfField(tag, type) { Children = new[] { Scalar(component), Scalar(entityType) } };
                }
            case TdfType.ObjectId:
                {
                    var component = ReadVarInt(raw, ref position);
                    var entityType = ReadVarInt(raw, ref position);
                    var entityId = ReadVarInt(raw, ref position);
                    return new TdfField(tag, type) { Integer = entityId, Children = new[] { Scalar(component), Scalar(entityType) } };
                }
            case TdfType.Float:
                {
                    if (position + 4 > raw.Length)
                    {
                        throw new FormatException("truncated TDF float");
                    }
                    var bytes = raw.Slice(position, 4).ToArray();
                    position += 4;
                    return new TdfField(tag, type) { Bytes = bytes };
                }
            case TdfType.Generic:
                {
                    // A present byte, the value's TDF type id, the value without a member header, then 0x00
                    // (for example the StartMatchmakingScenario SCNA attribute values).
                    var present = ReadByte(raw, ref position);
                    if (present == 0)
                    {
                        return new TdfField(tag, type);
                    }
                    var typeId = ReadVarInt(raw, ref position);
                    var value = ReadValue(raw, ref position, string.Empty, GenericWireType(typeId), depth + 1);
                    if (ReadByte(raw, ref position) != 0)
                    {
                        throw new FormatException("unterminated TDF generic value");
                    }
                    return new TdfField(tag, type) { Integer = typeId, Children = new[] { value } };
                }
            default:
                throw new FormatException($"unsupported TDF type {(int)type}");
        }
    }

    /// <summary>The wire type of a generic value's scalar TDF type id; structured values are not decoded here.</summary>
    private static TdfType GenericWireType(long typeId) => typeId switch
    {
        4 => TdfType.Float,
        6 => TdfType.String,
        9 => TdfType.Blob,
        5 or 8 or (>= 14 and <= 23) => TdfType.Integer,
        _ => throw new FormatException($"unsupported generic TDF type id {typeId}"),
    };

    private static IReadOnlyList<TdfField> ReadStructBody(ReadOnlySpan<byte> raw, ref int position, int depth)
    {
        var children = new List<TdfField>();
        while (true)
        {
            if (position >= raw.Length)
            {
                throw new FormatException("unterminated TDF struct");
            }
            if (raw[position] == 0)
            {
                position++;
                return children;
            }
            children.Add(ReadMember(raw, ref position, depth));
        }
    }

    private static TdfField Scalar(long value) => new(string.Empty, TdfType.Integer) { Integer = value };

    private static byte ReadByte(ReadOnlySpan<byte> raw, ref int position)
    {
        if (position >= raw.Length)
        {
            throw new FormatException("truncated TDF payload");
        }
        return raw[position++];
    }

    private static int CheckedLength(ReadOnlySpan<byte> raw, ref int position)
    {
        var length = ReadVarInt(raw, ref position);
        if (length < 0 || length > raw.Length - position)
        {
            throw new FormatException("TDF length exceeds payload");
        }
        return (int)length;
    }

    private static int CheckedCount(ReadOnlySpan<byte> raw, ref int position)
    {
        var count = ReadVarInt(raw, ref position);
        if (count < 0 || count > raw.Length)
        {
            throw new FormatException("TDF element count exceeds payload");
        }
        return (int)count;
    }

    private delegate bool ScanValue<T>(ReadOnlySpan<byte> raw, ref int position, out T value);

    /// <summary>Byte-search fallback: decodes every occurrence of the packed tag in an unparsable payload.</summary>
    private static List<T> ScanAll<T>(ReadOnlySpan<byte> raw, string tag, TdfType type, ScanValue<T> decode)
    {
        var marker = TdfTag.Encode(tag, type);
        var values = new List<T>();
        var offset = 0;
        while (offset < raw.Length)
        {
            var found = raw[offset..].IndexOf(marker);
            if (found < 0)
            {
                break;
            }
            var position = offset + found + marker.Length;
            if (decode(raw, ref position, out var value))
            {
                values.Add(value);
                offset = Math.Max(offset + found + 1, position);
            }
            else
            {
                offset += found + 1;
            }
        }
        return values;
    }

    private static bool ScanIntegerAt(ReadOnlySpan<byte> raw, ref int position, out long value)
    {
        try
        {
            value = ReadVarInt(raw, ref position);
            return true;
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }

    private static bool ScanStringAt(ReadOnlySpan<byte> raw, ref int position, out string value)
    {
        try
        {
            value = ReadScannedString(raw, ref position);
            return true;
        }
        catch (FormatException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static List<string> ScanStringList(ReadOnlySpan<byte> raw, string tag, int limit)
    {
        var names = new List<string>();
        var index = raw.IndexOf(TdfTag.Encode(tag, TdfType.List));
        if (index < 0 || index + 4 >= raw.Length || raw[index + 4] != (byte)TdfType.String)
        {
            return names;
        }
        try
        {
            var position = index + 5;
            var count = Math.Min(ReadVarInt(raw, ref position), limit);
            for (var i = 0; i < count; i++)
            {
                if (ReadScannedString(raw, ref position) is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }
        catch (FormatException)
        {
        }
        return names;
    }

    /// <summary>A non-empty, length-prefixed string at <paramref name="position"/>; a trailing NUL is dropped.</summary>
    private static string ReadScannedString(ReadOnlySpan<byte> raw, ref int position)
    {
        var length = ReadVarInt(raw, ref position);
        if (length <= 0 || position + length > raw.Length)
        {
            throw new FormatException("truncated TDF string");
        }
        var bytes = raw.Slice(position, (int)length);
        position += (int)length;
        return Encoding.UTF8.GetString(bytes[^1] == 0 ? bytes[..^1] : bytes);
    }

    private static string? ScanString(ReadOnlySpan<byte> raw, string tag)
    {
        var marker = TdfTag.Encode(tag, TdfType.String);
        var index = raw.IndexOf(marker);
        if (index < 0)
        {
            return null;
        }
        var position = index + 4;
        try
        {
            var length = ReadVarInt(raw, ref position);
            if (length <= 0 || position + length > raw.Length)
            {
                return null;
            }
            var bytes = raw.Slice(position, (int)length);
            if (bytes[^1] == 0)
            {
                bytes = bytes[..^1];
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static long? ScanInteger(ReadOnlySpan<byte> raw, string tag)
    {
        var marker = TdfTag.Encode(tag, TdfType.Integer);
        var index = raw.IndexOf(marker);
        if (index < 0)
        {
            return null;
        }
        var position = index + 4;
        try
        {
            return ReadVarInt(raw, ref position);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
