using System.Reflection;
using System.Runtime.InteropServices;
using FMT.FileTools;
using FrostySdk.Attributes;
using FrostySdk.IO;

// Exercises the loaded FMT overlays using synthetic bytes/types, without indexing game assets.
internal static class FmtWarningSelfTest
{
    public static void Run()
    {
        SevenBitLongs();
        LegacyChecksums();
        LaterClassPropertyLookup();
        ZStreamLayout();
        Console.WriteLine("FMT overlay self-test passed: 64-bit varints, legacy checksum bytes, complete property lookup and zlib stream layout.");
    }

    private static void SevenBitLongs()
    {
        foreach (var value in new[] { 0L, 1L, 127L, 128L, int.MaxValue, 1L << 31, (1L << 32) + 17, (1L << 55) + 123,
            long.MaxValue, long.MinValue, -1L })
        {
            var encoded = new List<byte>();
            var remaining = unchecked((ulong)value);
            do
            {
                var next = (byte)(remaining & 0x7f);
                remaining >>= 7;
                encoded.Add(remaining == 0 ? next : (byte)(next | 0x80));
            } while (remaining != 0);
            var size = encoded.Count;
            encoded.Add(0x42);
            using var stream = new MemoryStream(encoded.ToArray());
            using var reader = new NativeReader(stream);
            var actual = reader.Read7BitEncodedLong();
            Require(actual == value, $"64-bit varint {value} decoded as {actual}.");
            Require(reader.Position == size && reader.ReadByte() == 0x42, "Varint read crossed its terminator.");
        }
    }

    private static void LegacyChecksums()
    {
        var method = typeof(FrostySdk.Utils).GetMethod("CalcFletcher32Internal", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidDataException("Reviewed FMT checksum method was not found.");
        var random = new Random(2107);
        var inputs = new List<byte[]> { Array.Empty<byte>(), new byte[] { 0x80 }, new byte[] { 0xff, 0xff }, Enumerable.Repeat((byte)0xff, 7217).ToArray() };
        foreach (var length in new[] { 1, 2, 3, 719, 720, 721, 1441, 4097, 65537 })
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            inputs.Add(bytes);
        }
        Require((uint)method.Invoke(null, [new byte[] { 0x80 }])! == 0x80008000u, "Checksum high sign bit changed.");
        foreach (var bytes in inputs)
            Require((uint)method.Invoke(null, [bytes])! == LegacyChecksum(bytes), $"Legacy checksum changed for {bytes.Length} bytes.");
    }

    // The pinned upstream algorithm, including its original signed upper-half expression.
    // Widen its result explicitly before OR so this reference does not recreate CS0675.
    private static uint LegacyChecksum(byte[] bytes)
    {
        unchecked
        {
            var words = bytes.Length >> 1;
            var offset = 0;
            uint sum = 0, total = 0;
            while (words > 0)
            {
                var block = Math.Min(words, 360);
                words -= block;
                while (block-- > 0)
                {
                    sum += (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
                    offset += 2;
                    total += sum;
                }
                sum = (ushort)((sum >> 16) + sum);
                total = (ushort)(total + (total >> 16));
            }
            if ((bytes.Length & 1) != 0)
            {
                sum += (ushort)(bytes[offset] << 8);
                total += sum;
            }
            long signedHigh = ((int)sum & -65536) + (int)(sum << 16);
            long low = (ushort)total + (long)(total >> 16);
            return (uint)(signedHigh | low);
        }
    }

    private static void LaterClassPropertyLookup()
    {
        var descriptor = typeof(EbxSharedTypeDescriptors);
        var assembly = descriptor.GetField("EbxClassesAssembly", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidDataException("Reviewed EBX assembly cache was not found.");
        var types = descriptor.GetField("EbxClassesTypes", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidDataException("Reviewed EBX type cache was not found.");
        var oldAssembly = assembly.GetValue(null);
        var oldTypes = types.GetValue(null);
        try
        {
            assembly.SetValue(null, typeof(FmtWarningSelfTest).Assembly);
            types.SetValue(null, new[] { typeof(FirstSyntheticClass), typeof(LaterSyntheticClass) });
            Require(EbxSharedTypeDescriptors.GetPropertyName(0xa21e0001u) == nameof(FirstSyntheticClass.FirstProperty), "First EBX class property was missed.");
            Require(EbxSharedTypeDescriptors.GetPropertyName(0xa21e0002u) == nameof(LaterSyntheticClass.LaterProperty), "EBX lookup stopped before the later class.");
            Require(EbxSharedTypeDescriptors.GetPropertyName(0xa21e0003u) is null, "Unknown EBX hash resolved to a property.");
        }
        finally
        {
            assembly.SetValue(null, oldAssembly);
            types.SetValue(null, oldTypes);
        }
    }

    private sealed class FirstSyntheticClass
    {
        [Hash(0xa21e0001u)] public int FirstProperty { get; set; }
    }

    private sealed class LaterSyntheticClass
    {
        [Hash(0xa21e0002u)] public int LaterProperty { get; set; }
    }

    private static void ZStreamLayout()
    {
        var type = typeof(FrostySdk.Utils).Assembly.GetType("FrostySdk.ZLib+ZStream", throwOnError: true)!;
        var x64 = IntPtr.Size == 8;
        Require(Marshal.SizeOf(type) == (x64 ? 88 : 56), "Native zlib stream size changed.");
        var fields = new[] { "next_in", "avail_in", "total_in", "next_out", "avail_out", "total_out", "msg", "state", "zalloc", "zfree", "opaque", "data_type", "adler", "reserved" };
        var offsets = x64 ? new[] { 0, 8, 12, 16, 24, 28, 32, 40, 48, 56, 64, 72, 76, 80 }
            : new[] { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52 };
        for (var index = 0; index < fields.Length; index++)
            Require(Marshal.OffsetOf(type, fields[index]).ToInt64() == offsets[index], $"Native zlib field {fields[index]} moved.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
