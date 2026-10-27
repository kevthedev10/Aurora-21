using System.Buffers;
using System.Buffers.Binary;

namespace Aurora21.UltimateTeam.Protocol.Fire2;

public enum Fire2MessageType : byte
{
    Message = 0,
    Reply = 1,
    Notification = 2,
    ErrorReply = 3,
    Ping = 4,
    PingReply = 5,
}

/// <summary>
/// A Blaze Fire2 frame: a 16-byte big-endian header (payload size u32, metadata size u16,
/// component u16, command u16, message number u24, type in the top 3 bits of byte 13),
/// followed by the metadata and the TDF payload.
/// </summary>
public sealed record Fire2Frame(
    ushort Component,
    ushort Command,
    int MessageNumber,
    Fire2MessageType Type,
    byte[] Metadata,
    byte[] Payload,
    byte[] Raw)
{
    public const int HeaderSize = 16;
    /// <summary>
    /// Largest inbound payload accepted. The client's frames are small: 7,731 frames captured in live
    /// sessions (login, util, user sessions, association, stats and messaging) were at most 245 bytes
    /// including the header. Matchmaking (GameManager) requests have not been captured yet. The limit
    /// leaves about 1,000x headroom while bounding what one connection can make the server buffer;
    /// a refused frame is logged and saved by the connection handler.
    /// </summary>
    public const int MaxPayloadSize = 256 * 1024;

    public static byte[] Build(int component, int command, int messageNumber, Fire2MessageType type,
        ReadOnlySpan<byte> payload = default, ReadOnlySpan<byte> metadata = default)
    {
        var frame = new byte[HeaderSize + metadata.Length + payload.Length];
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)metadata.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)component);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)command);
        var number = messageNumber & 0xFFFFFF;
        span[10] = (byte)(number >> 16);
        span[11] = (byte)(number >> 8);
        span[12] = (byte)number;
        span[13] = (byte)(((int)type & 0x07) << 5);
        metadata.CopyTo(span[HeaderSize..]);
        payload.CopyTo(span[(HeaderSize + metadata.Length)..]);
        return frame;
    }

    /// <summary>
    /// Attempts to read one complete frame from the start of <paramref name="buffer"/>.
    /// Returns false when more data is needed; throws <see cref="FormatException"/> for
    /// unreasonable sizes so the caller can drop the connection.
    /// </summary>
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, out Fire2Frame? frame)
    {
        frame = null;
        if (buffer.Length < HeaderSize)
        {
            return false;
        }
        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);
        var payloadSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        var metadataSize = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (payloadSize > MaxPayloadSize)
        {
            throw new FormatException($"unreasonable Fire2 sizes payload={payloadSize} metadata={metadataSize} " +
                $"component={BinaryPrimitives.ReadUInt16BigEndian(header[6..])} command={BinaryPrimitives.ReadUInt16BigEndian(header[8..])} limit={MaxPayloadSize}");
        }
        var total = HeaderSize + metadataSize + (long)payloadSize;
        if (buffer.Length < total)
        {
            return false;
        }

        var raw = buffer.Slice(0, total).ToArray();
        buffer = buffer.Slice(total);
        frame = Parse(raw);
        return true;
    }

    public static Fire2Frame Parse(byte[] raw)
    {
        var span = raw.AsSpan();
        var payloadSize = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
        var metadataSize = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        return new Fire2Frame(
            BinaryPrimitives.ReadUInt16BigEndian(span[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[8..]),
            (span[10] << 16) | (span[11] << 8) | span[12],
            (Fire2MessageType)(span[13] >> 5),
            span.Slice(HeaderSize, metadataSize).ToArray(),
            span.Slice(HeaderSize + metadataSize, payloadSize).ToArray(),
            raw);
    }
}
