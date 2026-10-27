using System.IO.Compression;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;

namespace WzImgMCP.Tests;

/// <summary>
/// Decoding and encoding of every WZ canvas pixel format on raw BGRA32 buffers. These tests use no GDI+ and no
/// image library, so they check MapleLib's pixel code itself. Decoding tests build the raw WZ payload by hand and
/// compare with values worked out from the format definition; encoding tests compare with the quantisation each
/// format is defined to apply.
/// </summary>
public class CanvasPixelFormatTests
{
    #region helpers

    private static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var z = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static byte[] Decode(WzPngFormat format, int width, int height, byte[] raw)
    {
        var png = new WzPngProperty();
        png.SetCompressedBytes(Zlib(raw), width, height, format);
        var pixels = png.GetPixelsBgra32(out int w, out int h);
        Assert.NotNull(pixels);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
        Assert.Equal(width * height * 4, pixels!.Length);
        return pixels;
    }

    private static WzPngProperty Encode(byte[] bgra, int width, int height, SurfaceFormat? force)
    {
        var png = new WzPngProperty();
        png.SetPixelsBgra32(bgra, width, height, force);
        return png;
    }

    private static byte[] Decoded(WzPngProperty png, int width, int height)
    {
        var pixels = png.GetPixelsBgra32(out int w, out int h);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
        return pixels!;
    }

    private static byte Expand5(int c) => (byte)((c << 3) | (c >> 2));
    private static byte Expand6(int c) => (byte)((c << 2) | (c >> 4));

    /// <summary>The 8 bit to 5/6 bit quantiser MapleLib is defined to use: round(v * max / 255), to the nearest value.</summary>
    private static int Quantize(int value, int max) => (int)Math.Round(value * max / 255.0, MidpointRounding.AwayFromZero);

    private static byte[] Pattern(int width, int height, Func<int, int, (byte b, byte g, byte r, byte a)> pixel)
    {
        var buf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                int o = (y * width + x) * 4;
                buf[o] = b; buf[o + 1] = g; buf[o + 2] = r; buf[o + 3] = a;
            }
        return buf;
    }

    /// <summary>Pseudo random, deterministic pixels with a wide range of colours and alpha values.</summary>
    private static byte[] Noise(int width, int height, int seed, bool opaque)
    {
        var rng = new Random(seed);
        var buf = new byte[width * height * 4];
        rng.NextBytes(buf);
        if (opaque)
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
        return buf;
    }

    #endregion

    #region decoding raw WZ payloads

    [Fact]
    public void Decode_Bgra8888_IsIdentity()
    {
        var raw = Noise(7, 5, 1, opaque: false);
        Assert.Equal(raw, Decode(WzPngFormat.Format2, 7, 5, raw));
    }

    [Fact]
    public void Decode_Bgra4444_ExpandsEachNibbleTimes17()
    {
        // 3x2 pixels. Each pixel is 2 bytes: low byte = B | G << 4, high byte = R | A << 4.
        var raw = new byte[] { 0x10, 0xF0, 0xA5, 0x3C, 0xFF, 0xFF,
                               0x00, 0x00, 0x21, 0x84, 0x7E, 0x91 };
        var px = Decode(WzPngFormat.Format1, 3, 2, raw);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal((raw[i * 2] & 0xF) * 17, px[i * 4]);          // B
            Assert.Equal((raw[i * 2] >> 4) * 17, px[i * 4 + 1]);       // G
            Assert.Equal((raw[i * 2 + 1] & 0xF) * 17, px[i * 4 + 2]);  // R
            Assert.Equal((raw[i * 2 + 1] >> 4) * 17, px[i * 4 + 3]);   // A
        }
    }

    [Fact]
    public void Decode_Argb1555_ExpandsFiveBitChannelsAndOneBitAlpha()
    {
        // 4x1 pixels: A R5 G5 B5 packed into 16 bits, little endian.
        ushort[] values = { 0x0000, 0x8000 | (31 << 10), (1 << 5) | 17 | 0x8000, (12 << 10) | (7 << 5) | 30 };
        var raw = values.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToArray();
        var px = Decode(WzPngFormat.Format257, 4, 1, raw);
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            Assert.Equal(Expand5(v & 31), px[i * 4]);
            Assert.Equal(Expand5((v >> 5) & 31), px[i * 4 + 1]);
            Assert.Equal(Expand5((v >> 10) & 31), px[i * 4 + 2]);
            Assert.Equal((v & 0x8000) != 0 ? 255 : 0, px[i * 4 + 3]);
        }
    }

    [Fact]
    public void Decode_Rgb565_ExpandsChannelsAndIsOpaque()
    {
        ushort[] values = { 0x0000, 0xFFFF, 0xF800, 0x07E0, 0x001F, 0x8410, 0x1234, 0xABCD };
        var raw = values.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToArray();
        var px = Decode(WzPngFormat.Format513, 4, 2, raw);
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            Assert.Equal(Expand5(v & 31), px[i * 4]);
            Assert.Equal(Expand6((v >> 5) & 63), px[i * 4 + 1]);
            Assert.Equal(Expand5((v >> 11) & 31), px[i * 4 + 2]);
            Assert.Equal(255, px[i * 4 + 3]);
        }
    }

    [Fact]
    public void Decode_Format517_FillsEach16x16BlockWithOneRgb565Value()
    {
        // 20x18 pixels = 2x2 blocks (the last row and column of blocks are partial).
        ushort[] blocks = { 0xF800, 0x07E0, 0x001F, 0xFFFF };
        var raw = blocks.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToArray();
        var px = Decode(WzPngFormat.Format517, 20, 18, raw);
        for (int y = 0; y < 18; y++)
            for (int x = 0; x < 20; x++)
            {
                int v = blocks[(y / 16) * 2 + x / 16];
                int o = (y * 20 + x) * 4;
                Assert.Equal(Expand5(v & 31), px[o]);
                Assert.Equal(Expand6((v >> 5) & 63), px[o + 1]);
                Assert.Equal(Expand5((v >> 11) & 31), px[o + 2]);
                Assert.Equal(255, px[o + 3]);
            }
    }

    /// <summary>The DXT1 colour palette as MapleLib defines it.</summary>
    private static (byte b, byte g, byte r)[] Dxt1Palette(ushort c0, ushort c1)
    {
        (byte b, byte g, byte r) Rgb(ushort v) => (Expand5(v & 31), Expand6((v >> 5) & 63), Expand5((v >> 11) & 31));
        var p0 = Rgb(c0);
        var p1 = Rgb(c1);
        if (c0 > c1)
            return new[]
            {
                p0, p1,
                ((byte)((p0.b * 2 + p1.b + 1) / 3), (byte)((p0.g * 2 + p1.g + 1) / 3), (byte)((p0.r * 2 + p1.r + 1) / 3)),
                ((byte)((p0.b + p1.b * 2 + 1) / 3), (byte)((p0.g + p1.g * 2 + 1) / 3), (byte)((p0.r + p1.r * 2 + 1) / 3))
            };
        return new[]
        {
            p0, p1,
            ((byte)((p0.b + p1.b) / 2), (byte)((p0.g + p1.g) / 2), (byte)((p0.r + p1.r) / 2)),
            ((byte)0, (byte)0, (byte)0)
        };
    }

    private static byte[] ColorBlock(ushort c0, ushort c1, Func<int, int> index)
    {
        var block = new List<byte> { (byte)c0, (byte)(c0 >> 8), (byte)c1, (byte)(c1 >> 8) };
        for (int row = 0; row < 4; row++)
        {
            int b = 0;
            for (int col = 0; col < 4; col++)
                b |= (index(row * 4 + col) & 3) << (col * 2);
            block.Add((byte)b);
        }
        return block.ToArray();
    }

    [Theory]
    [InlineData(WzPngFormat.Format1026)]
    [InlineData(WzPngFormat.Format3)]
    public void Decode_Dxt3_UsesFourBitAlphaAndTheDxt1Palette(WzPngFormat format)
    {
        var raw = new List<byte>();
        for (int i = 0; i < 16; i += 2)
            raw.Add((byte)(i | ((i + 1) << 4))); // alpha nibble of pixel n = n
        ushort c0 = 0xF81F, c1 = 0x07E0; // magenta, green (c0 > c1: four colour palette)
        raw.AddRange(ColorBlock(c0, c1, i => (i * 3 + i / 4) & 3));
        var px = Decode(format, 4, 4, raw.ToArray());

        var palette = Dxt1Palette(c0, c1);
        for (int i = 0; i < 16; i++)
        {
            var c = palette[(i * 3 + i / 4) & 3];
            Assert.Equal(c.b, px[i * 4]);
            Assert.Equal(c.g, px[i * 4 + 1]);
            Assert.Equal(c.r, px[i * 4 + 2]);
            Assert.Equal(i * 17, px[i * 4 + 3]);
        }
    }

    [Fact]
    public void Decode_Dxt3_CropsPartialBlocks()
    {
        // 6x5 pixels need 2x2 blocks; only the 6x5 visible pixels may be written.
        var raw = new List<byte>();
        for (int block = 0; block < 4; block++)
        {
            for (int i = 0; i < 8; i++) raw.Add(0xFF);               // alpha 255
            raw.AddRange(ColorBlock(0xF800, 0x0000, _ => 0));        // every pixel = c0 (red)
        }
        var px = Decode(WzPngFormat.Format1026, 6, 5, raw.ToArray());
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(0, px[i * 4]);
            Assert.Equal(0, px[i * 4 + 1]);
            Assert.Equal(255, px[i * 4 + 2]);
            Assert.Equal(255, px[i * 4 + 3]);
        }
    }

    private static byte[] Dxt5AlphaBlock(byte a0, byte a1, Func<int, int> index)
    {
        long bits = 0;
        for (int i = 0; i < 16; i++)
            bits |= (long)(index(i) & 7) << (i * 3);
        var block = new List<byte> { a0, a1 };
        for (int i = 0; i < 6; i++)
            block.Add((byte)(bits >> (i * 8)));
        return block.ToArray();
    }

    [Theory]
    [InlineData(255, 16)]  // a0 > a1: eight alpha steps between the endpoints
    [InlineData(40, 200)]  // a0 <= a1: six steps plus the fixed 0 and 255
    public void Decode_Dxt5_UsesTheAlphaCodebook(int a0, int a1)
    {
        var alpha = new int[8];
        alpha[0] = a0;
        alpha[1] = a1;
        if (a0 > a1)
            for (int i = 2; i < 8; i++) alpha[i] = ((8 - i) * a0 + (i - 1) * a1 + 3) / 7;
        else
        {
            for (int i = 2; i < 6; i++) alpha[i] = ((6 - i) * a0 + (i - 1) * a1 + 2) / 5;
            alpha[6] = 0;
            alpha[7] = 255;
        }

        ushort c0 = 0x07FF, c1 = 0x7800;
        var raw = new List<byte>();
        raw.AddRange(Dxt5AlphaBlock((byte)a0, (byte)a1, i => (i * 5 + 1) & 7));
        raw.AddRange(ColorBlock(c0, c1, i => (i + 2) & 3));
        var px = Decode(WzPngFormat.Format2050, 4, 4, raw.ToArray());

        var palette = Dxt1Palette(c0, c1);
        for (int i = 0; i < 16; i++)
        {
            var c = palette[(i + 2) & 3];
            Assert.Equal(c.b, px[i * 4]);
            Assert.Equal(c.g, px[i * 4 + 1]);
            Assert.Equal(c.r, px[i * 4 + 2]);
            Assert.Equal(alpha[(i * 5 + 1) & 7], px[i * 4 + 3]);
        }
    }

    /// <summary>An independent per-block reference decoder for DXT3 / DXT5 (same palette and codebook definitions).</summary>
    private static byte[] ReferenceDxt(byte[] raw, int width, int height, bool dxt5)
    {
        var output = new byte[width * height * 4];
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                int off = (by * blocksX + bx) * 16;
                var alpha = new int[16];
                if (dxt5)
                {
                    int a0 = raw[off], a1 = raw[off + 1];
                    var book = new int[8];
                    book[0] = a0;
                    book[1] = a1;
                    if (a0 > a1)
                        for (int i = 2; i < 8; i++) book[i] = ((8 - i) * a0 + (i - 1) * a1 + 3) / 7;
                    else
                    {
                        for (int i = 2; i < 6; i++) book[i] = ((6 - i) * a0 + (i - 1) * a1 + 2) / 5;
                        book[6] = 0;
                        book[7] = 255;
                    }
                    long bits = 0;
                    for (int i = 0; i < 6; i++) bits |= (long)raw[off + 2 + i] << (i * 8);
                    for (int n = 0; n < 16; n++) alpha[n] = book[(int)((bits >> (n * 3)) & 7)];
                }
                else
                {
                    for (int n = 0; n < 16; n++)
                        alpha[n] = ((raw[off + n / 2] >> ((n % 2) * 4)) & 0xF) * 17;
                }

                ushort c0 = (ushort)(raw[off + 8] | (raw[off + 9] << 8));
                ushort c1 = (ushort)(raw[off + 10] | (raw[off + 11] << 8));
                var palette = Dxt1Palette(c0, c1);
                for (int j = 0; j < 4; j++)
                    for (int i = 0; i < 4; i++)
                    {
                        int x = bx * 4 + i, y = by * 4 + j;
                        if (x >= width || y >= height) continue;
                        var c = palette[(raw[off + 12 + j] >> (i * 2)) & 3];
                        int o = (y * width + x) * 4;
                        output[o] = c.b; output[o + 1] = c.g; output[o + 2] = c.r; output[o + 3] = (byte)alpha[j * 4 + i];
                    }
            }
        return output;
    }

    [Theory]
    [InlineData(WzPngFormat.Format1026, 1100, 1000)]  // >= 1M pixels: the SIMD / parallel DXT3 path
    [InlineData(WzPngFormat.Format1026, 1101, 1001)]  // partial blocks on the right and bottom edges
    [InlineData(WzPngFormat.Format1026, 130, 70)]     // more than 16 blocks: the parallel scalar path
    [InlineData(WzPngFormat.Format2050, 1101, 1001)]
    [InlineData(WzPngFormat.Format2050, 130, 70)]
    public void Decode_Dxt_RandomBlocks_MatchTheReferenceDecoder(WzPngFormat format, int width, int height)
    {
        int blocks = ((width + 3) / 4) * ((height + 3) / 4);
        var raw = new byte[blocks * 16];
        new Random(width * 31 + height).NextBytes(raw);
        var expected = ReferenceDxt(raw, width, height, dxt5: format == WzPngFormat.Format2050);
        Assert.Equal(expected, Decode(format, width, height, raw));
    }

    [Fact]
    public void Decode_Bc7_Mode6SingleColorBlock()
    {
        // BC7 mode 6: 7 bit mode prefix (0b1000000), R0 R1 G0 G1 B0 B1 A0 A1 (7 bits each), P0 P1, 63 index bits.
        var bits = new List<bool>();
        void Put(int value, int count) { for (int i = 0; i < count; i++) bits.Add(((value >> i) & 1) != 0); }
        Put(0x40, 7);
        foreach (int endpoint in new[] { 0x40, 0x40, 0x20, 0x20, 0x10, 0x10, 0x7F, 0x7F })
            Put(endpoint, 7);
        Put(1, 1);
        Put(1, 1);
        Put(0, 63);
        Assert.Equal(128, bits.Count);
        var block = new byte[16];
        for (int i = 0; i < 128; i++)
            if (bits[i]) block[i / 8] |= (byte)(1 << (i % 8));

        var px = Decode(WzPngFormat.Format4098, 4, 4, block);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0x21, px[i * 4]);      // B = (0x10 << 1) | 1
            Assert.Equal(0x41, px[i * 4 + 1]);  // G
            Assert.Equal(0x81, px[i * 4 + 2]);  // R
            Assert.Equal(0xFF, px[i * 4 + 3]);  // A = (0x7F << 1) | 1
        }
    }

    [Fact]
    public void Decode_TruncatedPayload_Throws()
    {
        var png = new WzPngProperty();
        png.SetCompressedBytes(Zlib(new byte[10]), 8, 8, WzPngFormat.Format2);
        Assert.ThrowsAny<Exception>(() => png.GetPixelsBgra32(out _, out _));
    }

    #endregion

    #region encoding

    [Fact]
    public void Encode_Bgra8888_IsExact_IncludingColourUnderZeroAlpha()
    {
        // Odd size, every channel value, alpha from 0 to 255.
        var src = Noise(31, 17, 7, opaque: false);
        var png = Encode(src, 31, 17, SurfaceFormat.Bgra32);
        Assert.Equal(WzPngFormat.Format2, png.Format);
        Assert.Equal(src, Decoded(png, 31, 17));
    }

    [Fact]
    public void Encode_Bgra4444_KeepsTheHighNibbleOfEachChannel()
    {
        var src = Noise(13, 9, 11, opaque: false);
        var png = Encode(src, 13, 9, SurfaceFormat.Bgra4444);
        Assert.Equal(WzPngFormat.Format1, png.Format);
        var px = Decoded(png, 13, 9);
        for (int i = 0; i < src.Length; i++)
            Assert.Equal((src[i] >> 4) * 17, px[i]);
    }

    [Fact]
    public void Encode_Rgb565_RoundsToFiveAndSixBitsAndDropsAlpha()
    {
        var src = Noise(13, 9, 12, opaque: false); // 13x9: not a multiple of 16, so Format513
        var png = Encode(src, 13, 9, SurfaceFormat.Bgr565);
        Assert.Equal(WzPngFormat.Format513, png.Format);
        var px = Decoded(png, 13, 9);
        for (int i = 0; i < 13 * 9; i++)
        {
            Assert.Equal(Expand5(Quantize(src[i * 4], 31)), px[i * 4]);
            Assert.Equal(Expand6(Quantize(src[i * 4 + 1], 63)), px[i * 4 + 1]);
            Assert.Equal(Expand5(Quantize(src[i * 4 + 2], 31)), px[i * 4 + 2]);
            Assert.Equal(255, px[i * 4 + 3]);
        }
    }

    [Fact]
    public void Encode_Argb1555_RoundsToFiveBitsAndKeepsOnlyTheTopAlphaBit()
    {
        var src = Noise(13, 9, 13, opaque: false);
        var png = Encode(src, 13, 9, SurfaceFormat.Bgra5551);
        Assert.Equal(WzPngFormat.Format257, png.Format);
        var px = Decoded(png, 13, 9);
        for (int i = 0; i < 13 * 9; i++)
        {
            Assert.Equal(Expand5(Quantize(src[i * 4], 31)), px[i * 4]);
            Assert.Equal(Expand5(Quantize(src[i * 4 + 1], 31)), px[i * 4 + 1]);
            Assert.Equal(Expand5(Quantize(src[i * 4 + 2], 31)), px[i * 4 + 2]);
            Assert.Equal(src[i * 4 + 3] >= 128 ? 255 : 0, px[i * 4 + 3]);
        }
    }

    [Fact]
    public void Encode_Rgb565_OnA16Grid_UsesFormat517OnlyWhenNothingIsLost()
    {
        // 32x32 with a constant colour per 16x16 block: Format517 loses nothing.
        var flat = Pattern(32, 32, (x, y) => ((byte)(x / 16 * 120), (byte)(y / 16 * 120), 200, 255));
        var png = Encode(flat, 32, 32, SurfaceFormat.Bgr565);
        Assert.Equal(WzPngFormat.Format517, png.Format);
        var px = Decoded(png, 32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int o = (y * 32 + x) * 4;
                Assert.Equal(Expand5(Quantize(x / 16 * 120, 31)), px[o]);
                Assert.Equal(Expand6(Quantize(y / 16 * 120, 63)), px[o + 1]);
            }

        // A gradient on the same grid must not be reduced to one pixel per block.
        var ramp = Pattern(32, 32, (x, y) => ((byte)(x * 8), (byte)(y * 8), 100, 255));
        png = Encode(ramp, 32, 32, SurfaceFormat.Bgr565);
        Assert.Equal(WzPngFormat.Format513, png.Format);
        px = Decoded(png, 32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int o = (y * 32 + x) * 4;
                Assert.Equal(Expand5(Quantize(x * 8, 31)), px[o]);
                Assert.Equal(Expand6(Quantize(y * 8, 63)), px[o + 1]);
            }
    }

    [Theory]
    [InlineData(true)]  // DXT3
    [InlineData(false)] // DXT5
    public void Encode_Dxt_TwoColourBlocks_AreExact(bool dxt3)
    {
        // Two colours that 565 represents exactly and alpha values that the format represents exactly.
        // DXT3 stores a 4-bit alpha (multiples of 17); DXT5 interpolates between the block's min and max alpha.
        var src = Pattern(16, 16, (x, y) => ((x + y) % 2 == 0)
            ? ((byte)0, (byte)0, (byte)0, (byte)0)
            : ((byte)0, Expand6(63), Expand5(31), (byte)255));
        var png = Encode(src, 16, 16, dxt3 ? SurfaceFormat.Dxt3 : SurfaceFormat.Dxt5);
        Assert.Equal(dxt3 ? WzPngFormat.Format1026 : WzPngFormat.Format2050, png.Format);
        var px = Decoded(png, 16, 16);
        for (int i = 0; i < 16 * 16; i++)
        {
            bool opaque = src[i * 4 + 3] == 255;
            Assert.Equal(src[i * 4 + 3], px[i * 4 + 3]);
            if (opaque)
            {
                Assert.Equal(0, px[i * 4]);
                Assert.Equal(255, px[i * 4 + 1]);
                Assert.Equal(255, px[i * 4 + 2]);
            }
        }
    }

    [Fact]
    public void Encode_Dxt3_QuantisesAlphaToFourBits()
    {
        var src = Pattern(8, 8, (x, y) => ((byte)0, (byte)0, (byte)0, (byte)((x + y * 8) * 4)));
        var px = Decoded(Encode(src, 8, 8, SurfaceFormat.Dxt3), 8, 8);
        for (int i = 0; i < 64; i++)
            Assert.Equal((src[i * 4 + 3] >> 4) * 17, px[i * 4 + 3]);
    }

    [Fact]
    public void Encode_Dxt5_AlphaRamp_StaysWithinHalfACodebookStep()
    {
        // Alpha ramp 0..252 inside one 4x4 block row: the 8-entry codebook has a step of at most 255 / 7.
        var src = Pattern(8, 8, (x, y) => ((byte)10, (byte)20, (byte)30, (byte)((x * 4 + y * 4) * 255 / 56)));
        var px = Decoded(Encode(src, 8, 8, SurfaceFormat.Dxt5), 8, 8);
        for (int i = 0; i < 64; i++)
            Assert.InRange(Math.Abs(px[i * 4 + 3] - src[i * 4 + 3]), 0, 19);
    }

    [Theory]
    [InlineData(SurfaceFormat.Dxt3)]
    [InlineData(SurfaceFormat.Dxt5)]
    public void Encode_Dxt_RefusesSizesThatAreNotAMultipleOfFour(SurfaceFormat format)
    {
        var png = new WzPngProperty();
        var ex = Assert.Throws<ArgumentException>(() => png.SetPixelsBgra32(new byte[5 * 3 * 4], 5, 3, format));
        Assert.Contains("multiples of 4", ex.Message);
    }

    [Fact]
    public void Encode_RefusesBadBuffersAndSizes()
    {
        var png = new WzPngProperty();
        Assert.Throws<ArgumentException>(() => png.SetPixelsBgra32(new byte[10], 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => png.SetPixelsBgra32(new byte[0], 0, 0));
        Assert.Throws<ArgumentException>(() => png.SetPixelsBgra32(new byte[4], 100000, 1));
    }

    [Fact]
    public void Encode_Auto_PicksAFormatAndDecodesToTheSameSize()
    {
        // Many colours and a full range of alpha at an odd size: no 16-bit or DXT format is suitable, so the
        // automatic choice is lossless BGRA8888.
        var src = Noise(31, 17, 21, opaque: false);
        var png = Encode(src, 31, 17, null);
        Assert.Equal(WzPngFormat.Format2, png.Format);
        Assert.Equal(src, Decoded(png, 31, 17));
    }

    [Theory]
    [InlineData(SurfaceFormat.Bgra32)]
    [InlineData(SurfaceFormat.Bgra4444)]
    public void Encode_ThenDecode_IsStable_ForFormatsWithAnExactExpansion(SurfaceFormat format)
    {
        var src = Noise(16, 16, 31, opaque: false);
        var first = Decoded(Encode(src, 16, 16, format), 16, 16);
        var second = Decoded(Encode(first, 16, 16, format), 16, 16);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(SurfaceFormat.Bgra32)]
    [InlineData(SurfaceFormat.Bgra4444)]
    [InlineData(SurfaceFormat.Bgr565)]
    [InlineData(SurfaceFormat.Bgra5551)]
    public void Encode_ThenDecode_Again_HasNoDrift_ForEveryFormatWithoutCompression(SurfaceFormat format)
    {
        // The 5/6 bit quantiser rounds to nearest, which inverts the decoder's bit replication, so decoding a canvas
        // and encoding the result again returns the same pixels: an export / edit / import cycle cannot drift.
        var src = Noise(16, 16, 31, opaque: true);
        var first = Decoded(Encode(src, 16, 16, format), 16, 16);
        var second = Decoded(Encode(first, 16, 16, format), 16, 16);
        Assert.Equal(first, second);
        var third = Decoded(Encode(second, 16, 16, format), 16, 16);
        Assert.Equal(first, third);
    }

    [Theory]
    [InlineData(WzPngFormat.Format513, SurfaceFormat.Bgr565)]
    [InlineData(WzPngFormat.Format257, SurfaceFormat.Bgra5551)]
    [InlineData(WzPngFormat.Format1, SurfaceFormat.Bgra4444)]
    public void DecodeThenEncode_ReturnsTheSameValue_ForEvery16BitValue(WzPngFormat wz, SurfaceFormat surface)
    {
        // A 256x256 image whose pixels are all 65536 possible 16 bit values (so every 4, 5 and 6 bit channel value and
        // both alpha bits occur in every channel). Decode, encode and decode again must give identical pixels.
        const int size = 256;
        var raw = new byte[size * size * 2];
        for (int v = 0; v < 65536; v++)
        {
            raw[v * 2] = (byte)v;
            raw[v * 2 + 1] = (byte)(v >> 8);
        }
        var decoded = Decode(wz, size, size, raw);
        var again = Decoded(Encode(decoded, size, size, surface), size, size);
        Assert.Equal(decoded, again);

        // The same with a width that is not a multiple of four, so the scalar tail of the encoder runs as well.
        const int w = 253, h = 259;
        var part = raw.AsSpan(0, w * h * 2).ToArray();
        var decodedPart = Decode(wz, w, h, part);
        Assert.Equal(decodedPart, Decoded(Encode(decodedPart, w, h, surface), w, h));
    }

    [Theory]
    [InlineData(256, 1)]   // every pixel goes through the vector path of the encoder
    [InlineData(1, 256)]   // every pixel goes through the scalar path
    public void Quantiser_RoundsToNearest_ForEveryChannelValue(int width, int height)
    {
        // Pixel i has the value i in all three colour channels (and alpha 255).
        var src = Pattern(width, height, (x, y) => { byte v = (byte)(x + y); return (v, v, v, 255); });
        foreach (var (format, greenMax) in new[] { (SurfaceFormat.Bgr565, 63), (SurfaceFormat.Bgra5551, 31) })
        {
            var px = Decoded(Encode(src, width, height, format), width, height);
            for (int i = 0; i < 256; i++)
            {
                Assert.Equal(Expand5(Quantize(i, 31)), px[i * 4]);
                Assert.Equal(greenMax == 63 ? Expand6(Quantize(i, 63)) : Expand5(Quantize(i, 31)), px[i * 4 + 1]);
                Assert.Equal(Expand5(Quantize(i, 31)), px[i * 4 + 2]);
            }
        }
    }

    [Fact]
    public void Quantiser_PreviouslyDriftingValues_NowStay()
    {
        // 5 bit value 1 expands to 8 and used to encode back to 0; 5 expands to 41 and used to encode back to 4.
        foreach (int five in new[] { 1, 5 })
        {
            byte expanded = Expand5(five);
            var src = Pattern(4, 1, (x, y) => (expanded, expanded, expanded, 255));
            var px = Decoded(Encode(src, 4, 1, SurfaceFormat.Bgr565), 4, 1);
            Assert.Equal(expanded, px[0]);
            Assert.Equal(expanded, px[2]);
        }
    }

    #endregion
}
