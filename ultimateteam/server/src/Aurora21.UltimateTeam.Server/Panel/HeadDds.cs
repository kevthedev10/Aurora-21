using System.Buffers.Binary;
using System.IO.Compression;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Writes an uploaded player head the way FIFA 21 reads its dynamic heads: 180 × 180, DXT5 (BC3), one mip level, a 128-byte
/// DDS header as in runtime-source/ultimateteam21/custom-playerheads (flags 0x81007, linear size 32,400, pixel format
/// FOURCC "DXT5", caps TEXTURE). Colour fitting follows the alpha-weighted least-squares approach used by the Dost repair tool.
/// Previews are decoded from the resulting DDS, so they include its compression.
/// </summary>
internal static class HeadDds
{
    public const int Size = 180;
    public const int RgbaBytes = Size * Size * 4;
    public const int HeaderBytes = 128;
    public const int DataBytes = Size / 4 * (Size / 4) * 16;
    public const int FileBytes = HeaderBytes + DataBytes;

    /// <summary>A complete DDS file from 180 × 180 RGBA pixels (row-major, 4 bytes each).</summary>
    public static byte[] FromRgba(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != RgbaBytes) throw new ArgumentException($"A head is {Size} × {Size} RGBA ({RgbaBytes} bytes).", nameof(rgba));
        var file = new byte[FileBytes];
        WriteHeader(file.AsSpan(0, HeaderBytes));
        var block = new byte[64];
        var offset = HeaderBytes;
        for (var by = 0; by < Size; by += 4)
        {
            for (var bx = 0; bx < Size; bx += 4)
            {
                for (var y = 0; y < 4; y++)
                    rgba.Slice(((by + y) * Size + bx) * 4, 16).CopyTo(block.AsSpan(y * 16, 16));
                EncodeBlock(block, file.AsSpan(offset, 16));
                offset += 16;
            }
        }
        return file;
    }

    private static void WriteHeader(Span<byte> header)
    {
        "DDS "u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 124);        // header size
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00081007); // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Size);      // height
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Size);      // width
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], DataBytes); // linear size
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 1);         // mip levels
        BinaryPrimitives.WriteUInt32LittleEndian(header[76..], 32);        // pixel format size
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 0x4);       // DDPF_FOURCC
        "DXT5"u8.CopyTo(header[84..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], 0x1000);   // DDSCAPS_TEXTURE
    }

    /// <summary>One 4 × 4 block (64 bytes RGBA) as 16 bytes of BC3: 8 of alpha, 8 of colour.</summary>
    private static void EncodeBlock(ReadOnlySpan<byte> pixels, Span<byte> output)
    {
        // Alpha: the block's extremes and the six values between them (alpha0 > alpha1 selects that 8-value mode).
        byte max = 0, min = 255;
        for (var i = 0; i < 16; i++) { var a = pixels[i * 4 + 3]; max = Math.Max(max, a); min = Math.Min(min, a); }
        Span<int> alphas = stackalloc int[8];
        var bestAlphaError = int.MaxValue;
        for (var mode = 0; mode < 2; mode++)
        {
            alphas[0] = mode == 0 ? max : min; alphas[1] = mode == 0 ? min : max;
            var denominator = mode == 0 ? 7 : 5;
            for (var i = 1; i < denominator; i++) alphas[i + 1] = ((denominator - i) * alphas[0] + i * alphas[1]) / denominator;
            if (mode == 1) { alphas[6] = 0; alphas[7] = 255; }
            ulong bits = 0; var error = 0;
            for (var i = 0; i < 16; i++)
            {
                var alpha = pixels[i * 4 + 3]; var best = 0;
                for (var j = 1; j < 8; j++) if (Math.Abs(alphas[j] - alpha) < Math.Abs(alphas[best] - alpha)) best = j;
                var delta = alphas[best] - alpha; error += delta * delta;
                bits |= (ulong)best << (3 * i);
            }
            if (error >= bestAlphaError) continue;
            bestAlphaError = error; output[0] = (byte)alphas[0]; output[1] = (byte)alphas[1];
            for (var i = 0; i < 6; i++) output[2 + i] = (byte)(bits >> (8 * i));
        }

        FitColours(pixels, output[8..]);
    }

    // Port of tools/fifa21-currency-art/bcn.py's weighted palette refinement.
    // Both an actual visible colour pair and the channel bounds seed the fit.
    private static void FitColours(ReadOnlySpan<byte> pixels, Span<byte> output)
    {
        Span<double> first = stackalloc double[3], second = stackalloc double[3];
        Span<double> rhs0 = stackalloc double[3], rhs1 = stackalloc double[3];
        Span<int> palette = stackalloc int[12];
        double bestError = double.PositiveInfinity;
        int far0 = 0, far1 = 0, farDistance = -1;
        for (var i = 0; i < 16; i++)
            for (var j = i; j < 16; j++)
            {
                if (pixels[i * 4 + 3] == 0 || pixels[j * 4 + 3] == 0) continue;
                var distance = 0;
                for (var k = 0; k < 3; k++) { var d = pixels[i * 4 + k] - pixels[j * 4 + k]; distance += d * d; }
                if (distance > farDistance) { farDistance = distance; far0 = i; far1 = j; }
            }
        for (var seed = 0; seed < 2; seed++)
        {
            for (var k = 0; k < 3; k++)
            {
                first[k] = seed == 0 ? pixels[far0 * 4 + k] : 0;
                second[k] = seed == 0 ? pixels[far1 * 4 + k] : 255;
                if (seed == 1)
                    for (var i = 0; i < 16; i++)
                    {
                        if (pixels[i * 4 + 3] == 0) continue;
                        first[k] = Math.Max(first[k], pixels[i * 4 + k]);
                        second[k] = Math.Min(second[k], pixels[i * 4 + k]);
                    }
            }
            for (var iteration = 0; iteration < 8; iteration++)
            {
                ushort c0 = Rgb565(first), c1 = Rgb565(second);
                if (c0 < c1) (c0, c1) = (c1, c0);
                ColourPalette(c0, c1, palette);
                double error = 0, a = 0, b = 0, c = 0;
                rhs0.Clear(); rhs1.Clear();
                uint bits = 0;
                for (var i = 0; i < 16; i++)
                {
                    var nearest = 0; var distance = int.MaxValue;
                    for (var j = 0; j < 4; j++)
                    {
                        var d = 0;
                        for (var k = 0; k < 3; k++) { var delta = pixels[i * 4 + k] - palette[j * 3 + k]; d += delta * delta; }
                        if (d < distance) { distance = d; nearest = j; }
                    }
                    bits |= (uint)nearest << (i * 2);
                    var weight = Math.Pow(pixels[i * 4 + 3] / 255d, 2);
                    error += distance * weight;
                    var t = nearest switch { 0 => 1d, 1 => 0d, 2 => 2d / 3, _ => 1d / 3 };
                    var u = 1 - t;
                    a += weight * t * t; b += weight * t * u; c += weight * u * u;
                    for (var k = 0; k < 3; k++) { rhs0[k] += pixels[i * 4 + k] * weight * t; rhs1[k] += pixels[i * 4 + k] * weight * u; }
                }
                if (error < bestError)
                {
                    bestError = error;
                    BinaryPrimitives.WriteUInt16LittleEndian(output, c0);
                    BinaryPrimitives.WriteUInt16LittleEndian(output[2..], c1);
                    BinaryPrimitives.WriteUInt32LittleEndian(output[4..], bits);
                }
                var determinant = a * c - b * b;
                if (determinant <= 1e-10) break;
                for (var k = 0; k < 3; k++)
                {
                    first[k] = (rhs0[k] * c - rhs1[k] * b) / determinant;
                    second[k] = (rhs1[k] * a - rhs0[k] * b) / determinant;
                }
            }
        }
    }

    private static ushort Rgb565(ReadOnlySpan<double> rgb) => (ushort)(
        ((int)Math.Floor(Math.Clamp(rgb[0], 0, 255) * 31 / 255 + .5) << 11) |
        ((int)Math.Floor(Math.Clamp(rgb[1], 0, 255) * 63 / 255 + .5) << 5) |
        (int)Math.Floor(Math.Clamp(rgb[2], 0, 255) * 31 / 255 + .5));

    private static void ColourPalette(ushort first, ushort second, Span<int> palette)
    {
        Expand(first, palette[..3]); Expand(second, palette.Slice(3, 3));
        for (var k = 0; k < 3; k++)
        {
            palette[6 + k] = (2 * palette[k] + palette[3 + k]) / 3;
            palette[9 + k] = (palette[k] + 2 * palette[3 + k]) / 3;
        }
    }

    public static byte[] Decode(ReadOnlySpan<byte> dds)
    {
        if (dds.Length != FileBytes || !dds[..4].SequenceEqual("DDS "u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(dds[12..]) != Size
            || BinaryPrimitives.ReadUInt32LittleEndian(dds[16..]) != Size
            || !dds.Slice(84, 4).SequenceEqual("DXT5"u8)) throw new ArgumentException("Expected a 180 × 180 DXT5 head.", nameof(dds));
        var rgba = new byte[RgbaBytes];
        Span<int> palette = stackalloc int[12], alpha = stackalloc int[8];
        var offset = HeaderBytes;
        for (var by = 0; by < Size; by += 4)
            for (var bx = 0; bx < Size; bx += 4)
            {
                var block = dds.Slice(offset, 16); offset += 16;
                alpha[0] = block[0]; alpha[1] = block[1];
                var denominator = alpha[0] > alpha[1] ? 7 : 5;
                for (var i = 1; i < denominator; i++) alpha[i + 1] = ((denominator - i) * alpha[0] + i * alpha[1]) / denominator;
                if (denominator == 5) { alpha[6] = 0; alpha[7] = 255; }
                ulong alphaBits = 0;
                for (var i = 0; i < 6; i++) alphaBits |= (ulong)block[2 + i] << (i * 8);
                ColourPalette(BinaryPrimitives.ReadUInt16LittleEndian(block[8..]), BinaryPrimitives.ReadUInt16LittleEndian(block[10..]), palette);
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
                for (var i = 0; i < 16; i++)
                {
                    var pixel = ((by + i / 4) * Size + bx + i % 4) * 4;
                    var colour = (int)((bits >> (i * 2)) & 3);
                    for (var k = 0; k < 3; k++) rgba[pixel + k] = (byte)palette[colour * 3 + k];
                    rgba[pixel + 3] = (byte)alpha[(int)((alphaBits >> (i * 3)) & 7)];
                }
            }
        return rgba;
    }

    /// <summary>Lossless PNG of decoded game pixels; portable on the Linux server too.</summary>
    public static byte[] PreviewPng(ReadOnlySpan<byte> dds)
    {
        var rgba = Decode(dds);
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13]; header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, Size); BinaryPrimitives.WriteUInt32BigEndian(header[4..], Size);
        header[8] = 8; header[9] = 6;
        Chunk(result, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < Size; y++) { zip.WriteByte(0); zip.Write(rgba.AsSpan(y * Size * 4, Size * 4)); }
        Chunk(result, "IDAT"u8, compressed.ToArray()); Chunk(result, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return result.ToArray();
    }

    private static void Chunk(Stream stream, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length); stream.Write(word); stream.Write(kind); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (var bytes in new[] { kind.ToArray(), data.ToArray() })
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); stream.Write(word);
    }


    private static void Expand(ushort color, Span<int> rgb)
    {
        var r = (color >> 11) & 31; var g = (color >> 5) & 63; var b = color & 31;
        rgb[0] = (r << 3) | (r >> 2); rgb[1] = (g << 2) | (g >> 4); rgb[2] = (b << 3) | (b >> 2);
    }
}
