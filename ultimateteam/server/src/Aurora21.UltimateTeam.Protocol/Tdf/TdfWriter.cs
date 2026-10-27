using System.Buffers;
using System.Text;

namespace Aurora21.UltimateTeam.Protocol.Tdf;

/// <summary>
/// Fluent writer for Blaze TDF payloads. Field order is exactly the call order,
/// which is what the FIFA 21 client expects (it reads tags sequentially).
/// </summary>
public sealed class TdfWriter
{
    /// <summary>The byte that closes a struct.</summary>
    private const byte StructEnd = 0;

    /// <summary>The highest union member index; 0x7F marks an unset union.</summary>
    private const byte MaxUnionIndex = 0x7F;

    /// <summary>The member tag of a union's selected value.</summary>
    public const string UnionValueTag = "VALU";

    private readonly ArrayBufferWriter<byte> _buffer = new();

    public static byte[] Build(Action<TdfWriter> fields)
    {
        var writer = new TdfWriter();
        fields(writer);
        return writer.ToArray();
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    /// <summary>Writes a shared group of members in place, keeping the fluent chain.</summary>
    public TdfWriter Also(Action<TdfWriter> members)
    {
        members(this);
        return this;
    }

    public TdfWriter Int(string tag, long value)
    {
        TdfTag.Write(_buffer, tag, TdfType.Integer);
        WriteVarInt(_buffer, value);
        return this;
    }

    public TdfWriter Str(string tag, string value)
    {
        TdfTag.Write(_buffer, tag, TdfType.String);
        WriteString(value);
        return this;
    }

    public TdfWriter Blob(string tag, ReadOnlySpan<byte> value)
    {
        TdfTag.Write(_buffer, tag, TdfType.Blob);
        WriteVarInt(_buffer, value.Length);
        _buffer.Write(value);
        return this;
    }

    /// <summary>An absent Heat2 variable: a one-byte presence flag of zero.</summary>
    public TdfWriter NullVariable(string tag)
    {
        TdfTag.Write(_buffer, tag, TdfType.Variable);
        WriteByte(0);
        return this;
    }

    public TdfWriter ObjectId(string tag, long component, long entityType, long entityId)
    {
        TdfTag.Write(_buffer, tag, TdfType.ObjectId);
        WriteVarInt(_buffer, component);
        WriteVarInt(_buffer, entityType);
        WriteVarInt(_buffer, entityId);
        return this;
    }

    public TdfWriter Struct(string tag, Action<TdfWriter> fields)
    {
        TdfTag.Write(_buffer, tag, TdfType.Struct);
        WriteStructBody(fields);
        return this;
    }

    /// <summary>
    /// A struct whose members are already-encoded TDF bytes (for example a request relayed verbatim inside a
    /// notification), closed like any other struct.
    /// </summary>
    public TdfWriter StructRaw(string tag, ReadOnlySpan<byte> members)
    {
        TdfTag.Write(_buffer, tag, TdfType.Struct);
        _buffer.Write(members);
        WriteByte(StructEnd);
        return this;
    }

    /// <summary>A Heat2 union whose selected member is the struct <c>VALU</c>.</summary>
    public TdfWriter Union(string tag, byte activeIndex, Action<TdfWriter> value) => Union(tag, activeIndex, UnionValueTag, value);

    /// <summary>
    /// A class-member Heat2 union whose selected struct member carries its own member tag. NetworkAddress members
    /// are all tagged <c>VALU</c>, but GameSetupReason members are DLSC/IJGS/IMSC/MMSC/RDSC: FIFA 21's decoder
    /// looks for the member's own tag after the active index, and under <c>VALU</c> it keeps the defaults.
    /// </summary>
    public TdfWriter Union(string tag, byte activeIndex, string memberTag, Action<TdfWriter> value)
    {
        if (activeIndex > MaxUnionIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(activeIndex), "invalid TDF union active index");
        }
        TdfTag.Write(_buffer, tag, TdfType.Union);
        WriteByte(activeIndex);
        return Struct(memberTag, value);
    }

    /// <summary>
    /// A list of unions in the Heat1-compatible layout FIFA 21 decodes: the list advertises struct
    /// elements, and each element is its active index followed by the selected struct body
    /// without the <c>VALU</c> member header.
    /// </summary>
    public TdfWriter ListUnion(string tag, IReadOnlyCollection<(byte ActiveIndex, Action<TdfWriter> Value)> unions)
    {
        WriteListHeader(tag, TdfType.Struct, unions.Count);
        foreach (var (activeIndex, value) in unions)
        {
            if (activeIndex > MaxUnionIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(unions), "invalid TDF union active index");
            }
            WriteByte(activeIndex);
            WriteStructBody(value);
        }
        return this;
    }

    public TdfWriter ListInt(string tag, IReadOnlyCollection<long> values)
    {
        WriteListHeader(tag, TdfType.Integer, values.Count);
        foreach (var value in values)
        {
            WriteVarInt(_buffer, value);
        }
        return this;
    }

    public TdfWriter ListString(string tag, IReadOnlyCollection<string> values)
    {
        WriteListHeader(tag, TdfType.String, values.Count);
        foreach (var value in values)
        {
            WriteString(value);
        }
        return this;
    }

    public TdfWriter ListStruct(string tag, IReadOnlyCollection<Action<TdfWriter>> structs)
    {
        WriteListHeader(tag, TdfType.Struct, structs.Count);
        foreach (var fields in structs)
        {
            WriteStructBody(fields);
        }
        return this;
    }

    public TdfWriter MapStringString(string tag, IReadOnlyCollection<KeyValuePair<string, string>> entries)
    {
        WriteMapHeader(tag, TdfType.String, TdfType.String, entries.Count);
        foreach (var (key, value) in entries)
        {
            WriteString(key);
            WriteString(value);
        }
        return this;
    }

    public TdfWriter MapStringStruct(string tag, IReadOnlyCollection<KeyValuePair<string, Action<TdfWriter>>> entries)
    {
        WriteMapHeader(tag, TdfType.String, TdfType.Struct, entries.Count);
        foreach (var (key, fields) in entries)
        {
            WriteString(key);
            WriteStructBody(fields);
        }
        return this;
    }

    /// <summary>
    /// Blaze varint: the first byte carries 6 value bits plus a continuation flag,
    /// later bytes carry 7. Negative values are written as their 64-bit pattern.
    /// </summary>
    public static void WriteVarInt(IBufferWriter<byte> output, long signedValue)
    {
        var value = unchecked((ulong)signedValue);
        Span<byte> bytes = stackalloc byte[10];
        var length = 0;
        if (value < 0x40)
        {
            bytes[length++] = (byte)value;
        }
        else
        {
            bytes[length++] = (byte)((value & 0x3F) | 0x80);
            value >>= 6;
            while (value >= 0x80)
            {
                bytes[length++] = (byte)((value & 0x7F) | 0x80);
                value >>= 7;
            }
            bytes[length++] = (byte)value;
        }
        output.Write(bytes[..length]);
    }

    private void WriteListHeader(string tag, TdfType elementType, int count)
    {
        TdfTag.Write(_buffer, tag, TdfType.List);
        WriteByte((byte)elementType);
        WriteVarInt(_buffer, count);
    }

    private void WriteMapHeader(string tag, TdfType keyType, TdfType valueType, int count)
    {
        TdfTag.Write(_buffer, tag, TdfType.Map);
        WriteByte((byte)keyType);
        WriteByte((byte)valueType);
        WriteVarInt(_buffer, count);
    }

    private void WriteStructBody(Action<TdfWriter> fields)
    {
        fields(this);
        WriteByte(StructEnd);
    }

    /// <summary>A length-prefixed, NUL-terminated UTF-8 string.</summary>
    private void WriteString(string value)
    {
        var raw = Encoding.UTF8.GetBytes(value);
        WriteVarInt(_buffer, raw.Length + 1);
        _buffer.Write(raw);
        WriteByte(0);
    }

    private void WriteByte(byte value) => _buffer.Write([value]);
}
