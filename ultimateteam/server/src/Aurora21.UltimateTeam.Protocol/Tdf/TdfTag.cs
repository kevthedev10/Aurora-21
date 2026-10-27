using System.Buffers;
using System.Text;

namespace Aurora21.UltimateTeam.Protocol.Tdf;

/// <summary>Packs and unpacks the 1-4 character Blaze field labels (3 bytes plus a type byte).</summary>
public static class TdfTag
{
    public static void Write(IBufferWriter<byte> output, string tag, TdfType type)
    {
        Span<byte> header = stackalloc byte[4];
        Encode(tag, type, header);
        output.Write(header);
    }

    public static byte[] Encode(string tag, TdfType type)
    {
        var header = new byte[4];
        Encode(tag, type, header);
        return header;
    }

    public static void Encode(string tag, TdfType type, Span<byte> output)
    {
        var raw = Encoding.ASCII.GetBytes(tag);
        if (raw.Length is < 1 or > 4)
        {
            throw new ArgumentException($"TDF tag must be 1-4 bytes: {tag}", nameof(tag));
        }

        output[0] = 0;
        output[1] = 0;
        output[2] = 0;
        output[3] = (byte)type;
        output[0] |= (byte)((raw[0] & 0x40) << 1);
        output[0] |= (byte)((raw[0] & 0x10) << 2);
        output[0] |= (byte)((raw[0] & 0x0F) << 2);
        if (raw.Length > 1)
        {
            output[0] |= (byte)((raw[1] & 0x40) >> 5);
            output[0] |= (byte)((raw[1] & 0x10) >> 4);
            output[1] |= (byte)((raw[1] & 0x0F) << 4);
        }
        if (raw.Length > 2)
        {
            output[1] |= (byte)((raw[2] & 0x40) >> 3);
            output[1] |= (byte)((raw[2] & 0x10) >> 2);
            output[1] |= (byte)((raw[2] & 0x0C) >> 2);
            output[2] |= (byte)((raw[2] & 0x03) << 6);
        }
        if (raw.Length > 3)
        {
            output[2] |= (byte)((raw[3] & 0x40) >> 1);
            output[2] |= (byte)(raw[3] & 0x1F);
        }
    }

    /// <summary>Decodes a packed 3-byte label back to text (inverse of <see cref="Encode(string, TdfType, Span{byte})"/>).</summary>
    public static string Decode(ReadOnlySpan<byte> packed)
    {
        var value = (packed[0] << 16) | (packed[1] << 8) | packed[2];
        Span<char> chars = stackalloc char[4];
        var length = 0;
        for (var i = 0; i < 4; i++)
        {
            var bits = (value >> (18 - 6 * i)) & 0x3F;
            if (bits == 0)
            {
                continue;
            }
            chars[length++] = (char)(bits + 0x20);
        }
        return new string(chars[..length]);
    }
}
