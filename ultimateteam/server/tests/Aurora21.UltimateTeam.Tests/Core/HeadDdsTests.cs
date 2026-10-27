using System.Buffers.Binary;
using System.IO.Compression;
using Aurora21.UltimateTeam.Server.Panel;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Uploaded player heads are written the way FIFA 21 reads the custom heads that already work
/// (runtime-source/ultimateteam21/custom-playerheads): 180 × 180 DXT5, one mip level, the same 128-byte header.
/// </summary>
public sealed class HeadDdsTests
{
    [Fact]
    public void TheHeaderMatchesAWorkingCustomHead()
    {
        // First 128 bytes of the working p50522519.dds fixture (Git blob dcb895880f4559bb125fdded7fc24c485850f6a4).
        // Main removed that retired player's full portrait; keep only its verified DDS header for this format check.
        var reference = Convert.FromHexString(
            "444453207c00000007100800b4000000b4000000907e00000000000001000000" +
            "494d4147454d414749434b000000000000000000000000000000000000000000" +
            "0000000000000000000000002000000004000000445854350000000000000000" +
            "0000000000000000000000000010000000000000000000000000000000000000");
        var written = HeadDds.FromRgba(new byte[HeadDds.RgbaBytes]);
        Assert.Equal(32528, written.Length);
        // Everything but the free-text reserved area (ImageMagick signs it) is the same.
        Assert.Equal(reference.AsSpan(0, 0x20).ToArray(), written.AsSpan(0, 0x20).ToArray());
        Assert.Equal(reference.AsSpan(0x4c, 128 - 0x4c).ToArray(), written.AsSpan(0x4c, 128 - 0x4c).ToArray());
    }

    [Fact]
    public void ASolidColourDecodesBackToThatColour()
    {
        var rgba = new byte[HeadDds.RgbaBytes];
        for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = 200; rgba[i + 1] = 40; rgba[i + 2] = 120; rgba[i + 3] = 255; }
        var block = HeadDds.FromRgba(rgba).AsSpan(HeadDds.HeaderBytes, 16);
        Assert.Equal(255, block[0]);
        var color = BinaryPrimitives.ReadUInt16LittleEndian(block[8..]);
        var (r, g, b) = ((color >> 11) & 31, (color >> 5) & 63, color & 31);
        Assert.InRange((r << 3) | (r >> 2), 192, 208);
        Assert.InRange((g << 2) | (g >> 4), 32, 48);
        Assert.InRange((b << 3) | (b >> 2), 112, 128);
        Assert.Throws<ArgumentException>(() => HeadDds.FromRgba(new byte[10]));
    }
    [Fact]
    public void PaletteFittingPreservesOpposingColourChannelsAndIgnoresTransparentRgb()
    {
        var rgba = new byte[HeadDds.RgbaBytes];
        for (var y = 0; y < HeadDds.Size; y++)
            for (var x = 0; x < HeadDds.Size; x++)
            {
                var pixel = (y * HeadDds.Size + x) * 4;
                rgba[pixel] = (byte)((x % 4) * 85); rgba[pixel + 1] = (byte)(255 - rgba[pixel]);
                rgba[pixel + 2] = 66; rgba[pixel + 3] = 255;
            }
        var decoded = HeadDds.Decode(HeadDds.FromRgba(rgba));
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var k = 0; k < 3; k++) Assert.InRange(Math.Abs(decoded[i + k] - rgba[i + k]), 0, 4);
            Assert.Equal(255, decoded[i + 3]);
        }
        for (var i = 0; i < rgba.Length; i += 4) if ((i / 4) % 2 == 0) rgba[i + 3] = 0;
        var original = HeadDds.FromRgba(rgba);
        for (var i = 0; i < rgba.Length; i += 4) if (rgba[i + 3] == 0) { rgba[i] = 255; rgba[i + 1] = 0; rgba[i + 2] = 255; }
        var noisy = HeadDds.FromRgba(rgba);
        for (var block = HeadDds.HeaderBytes; block < original.Length; block += 16)
            Assert.Equal(original.AsSpan(block + 8, 4).ToArray(), noisy.AsSpan(block + 8, 4).ToArray());
    }

    [Theory]
    [InlineData(240, 30, 60)]
    [InlineData(30, 240, 255)]
    [InlineData(128, 128, 255)]
    public void PreviewDecodesBothBc3AlphaModes(int first, int second, int expectedLastAlpha)
    {
        var dds = HeadDds.FromRgba(new byte[HeadDds.RgbaBytes]);
        dds[128] = (byte)first; dds[129] = (byte)second;
        // Independently specified block: RGB565 red, blue; colour index 2, alpha index 7.
        for (var i = 130; i < 136; i++) dds[i] = 255;
        BinaryPrimitives.WriteUInt16LittleEndian(dds.AsSpan(136), 0xf800);
        BinaryPrimitives.WriteUInt16LittleEndian(dds.AsSpan(138), 0x001f);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(140), 0xaaaaaaaa);
        var decoded = HeadDds.Decode(dds);
        Assert.Equal(new byte[] { 170, 0, 85, (byte)expectedLastAlpha }, decoded[..4]);
        Assert.Equal(decoded[..16], decoded.AsSpan(HeadDds.Size * 4, 16).ToArray());
    }

    [Fact]
    public void PngPreviewContainsTheDecodedDdsPixels()
    {
        var pixels = new byte[HeadDds.RgbaBytes];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 201; pixels[i + 1] = 50; pixels[i + 2] = 123; pixels[i + 3] = (byte)((i / 4) % 256); }
        var dds = HeadDds.FromRgba(pixels);
        var png = HeadDds.PreviewPng(dds);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(180u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)));
        using var compressed = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8)) compressed.Write(png.AsSpan(offset + 8, count));
            offset += count + 12;
        }
        compressed.Position = 0;
        using var zip = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream(); zip.CopyTo(raw);
        var scanlines = raw.ToArray(); var decoded = HeadDds.Decode(dds);
        Assert.Equal((180 * 4 + 1) * 180, scanlines.Length);
        for (var y = 0; y < 180; y++)
        {
            Assert.Equal(0, scanlines[y * 721]);
            Assert.Equal(decoded.AsSpan(y * 720, 720).ToArray(), scanlines.AsSpan(y * 721 + 1, 720).ToArray());
        }
    }

}
