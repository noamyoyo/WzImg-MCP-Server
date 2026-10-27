using System.IO.Compression;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using ModelContextProtocol.Protocol.Types;
using WzImgMCP.Imaging;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// PNG decoding and encoding, the import limits, the path guard on image files, and the view_canvas image block.
/// None of these tests uses GDI+. They change WZIMG_ALLOWED_ROOT, so they run in the non-parallel collection.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class CanvasImagingTests : IClassFixture<TestFixture>, IDisposable
{
    private readonly TestFixture _fixture;
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _outside = Directory.CreateTempSubdirectory("canvas-outside-").FullName;
    private readonly ImageTools _image;
    private readonly ModifyTools _modify;
    private readonly ExportTools _export;

    private const string Cat = "Character";
    private const string Img = "Test.img";

    public CanvasImagingTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, fixture.TestDataPath);
        _image = new ImageTools(fixture.Session);
        _modify = new ModifyTools(fixture.Session);
        _export = new ExportTools(fixture.Session);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_outside, true); } catch { }
    }

    #region helpers

    private static byte[] Noise(int width, int height, int seed)
    {
        var buf = new byte[width * height * 4];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private static WzCanvasProperty CanvasOf(byte[] bgra, int width, int height, SurfaceFormat format = SurfaceFormat.Bgra32)
    {
        var canvas = new WzCanvasProperty("c") { PngProperty = new WzPngProperty() };
        canvas.PngProperty.SetPixelsBgra32(bgra, width, height, format);
        return canvas;
    }

    private static byte[] PngOf(byte[] bgra, int width, int height)
    {
        var png = CanvasImaging.EncodeCanvas(CanvasOf(bgra, width, height), ExportImageFormat.Png, out int w, out int h);
        Assert.NotNull(png);
        Assert.Equal((width, height), (w, h));
        return png!;
    }

    /// <summary>Decodes PNG bytes to BGRA32 through the production import path (forced exact BGRA8888).</summary>
    private static byte[] DecodePng(byte[] png, out int width, out int height)
    {
        var scratch = new WzPngProperty();
        CanvasImaging.SetCanvasFromPng(scratch, png, "bgra8888", out width, out height);
        return scratch.GetPixelsBgra32(out _, out _)!;
    }

    private static uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        void Be(uint v) => s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        Be((uint)data.Length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        uint crc = 0xFFFFFFFF;
        foreach (var b in typed) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        Be(~crc);
    }

    /// <summary>Builds a PNG file by hand: <paramref name="scanlines"/> includes the filter byte of every row.</summary>
    private static byte[] MakePng(int width, int height, byte bitDepth, byte colorType, byte[] scanlines, byte[]? palette = null)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        WriteChunk(ms, "IHDR", ihdr);
        if (palette != null) WriteChunk(ms, "PLTE", palette);
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(scanlines);
        WriteChunk(ms, "IDAT", z.ToArray());
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] HeaderOnly(uint width, uint height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' });
        foreach (var v in new[] { width, height })
            ms.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        ms.Write(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
        return ms.ToArray();
    }

    private string WriteInRoot(string name, byte[] data)
    {
        var path = Path.Combine(_fixture.TestDataPath, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string WriteOutside(string name, byte[] data)
    {
        var path = Path.Combine(_outside, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private byte[] PixelsOf(string path, out int w, out int h, WzSessionManager? session = null)
    {
        var canvas = (WzCanvasProperty)(session ?? _fixture.Session).GetImage(Cat, Img).GetFromPath(path)!;
        return canvas.GetLinkedPixelsBgra32(out w, out h)!;
    }

    private static string Base64(byte[] b) => Convert.ToBase64String(b);

    #endregion

    #region header checks and limits

    [Fact]
    public void Header_AcceptsTheLimit_AndReportsTheSize()
    {
        Assert.Equal((4096, 4096), CanvasImaging.ReadAndCheckPngHeader(HeaderOnly(4096, 4096)));
        Assert.Equal((1, 7), CanvasImaging.ReadAndCheckPngHeader(HeaderOnly(1, 7)));
    }

    [Theory]
    [InlineData(4097u, 1u)]
    [InlineData(1u, 4097u)]
    [InlineData(65536u, 65536u)]
    [InlineData(0xFFFFFFFFu, 1u)]
    public void Header_RefusesImagesAboveTheDimensionLimit(uint w, uint h)
    {
        var ex = Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadAndCheckPngHeader(HeaderOnly(w, h)));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Header_RefusesZeroSize()
    {
        Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadAndCheckPngHeader(HeaderOnly(0, 10)));
        Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadAndCheckPngHeader(HeaderOnly(10, 0)));
    }

    [Fact]
    public void Header_RefusesDataThatIsNotAPng()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9 };
        foreach (var data in new[] { jpeg, System.Text.Encoding.ASCII.GetBytes("this is not an image at all, just text"), Array.Empty<byte>(), new byte[3] })
        {
            var ex = Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadAndCheckPngHeader(data));
            Assert.Contains("not a PNG", ex.Message);
        }
    }

    [Fact]
    public void Header_RefusesPayloadsOverTwentyMegabytes()
    {
        var big = new byte[(int)CanvasImaging.MaxImportBytes + 1];
        var ex = Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadAndCheckPngHeader(big));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void ReadPngInput_NeedsExactlyOneSource()
    {
        Assert.Throws<ArgumentException>(() => CanvasImaging.ReadPngInput(null, null));
        Assert.Throws<ArgumentException>(() => CanvasImaging.ReadPngInput("", "  "));
        Assert.Throws<ArgumentException>(() => CanvasImaging.ReadPngInput("AAAA", "x.png"));
    }

    [Fact]
    public void ReadPngInput_RefusesOversizedBase64BeforeDecoding_AndBadBase64()
    {
        var tooLong = new string('A', (int)(CanvasImaging.MaxImportBytes / 3 * 4) + 100);
        Assert.Contains("too large", Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadPngInput(tooLong, null)).Message);
        Assert.Contains("not valid base64", Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadPngInput("%%%not base64%%%", null)).Message);
    }

    [Fact]
    public void ReadPngInput_ChecksThePathGuardAndTheFileSize()
    {
        var inside = WriteInRoot("ok.png", PngOf(Noise(2, 2, 1), 2, 2));
        Assert.True(CanvasImaging.ReadPngInput(null, inside).Length > 8);
        Assert.Throws<FileNotFoundException>(() => CanvasImaging.ReadPngInput(null, Path.Combine(_fixture.TestDataPath, "missing.png")));

        var outside = WriteOutside("out.png", PngOf(Noise(2, 2, 1), 2, 2));
        var ex = Assert.Throws<PathNotAllowedException>(() => CanvasImaging.ReadPngInput(null, outside));
        Assert.Contains("outside allowed root", ex.Message);
        Assert.Throws<PathNotAllowedException>(() => CanvasImaging.ReadPngInput(null, "../../etc/passwd"));

        var huge = WriteInRoot("huge.png", new byte[(int)CanvasImaging.MaxImportBytes + 1]);
        Assert.Contains("too large", Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadPngInput(null, huge)).Message);
    }

    #endregion

    #region PNG decode / encode

    [Fact]
    public void Png_RoundTrip_IsExactForBgra8888_IncludingColourUnderTransparentPixels()
    {
        var src = Noise(37, 23, 5); // all channel values, alpha 0..255
        for (int i = 0; i < 20; i++) src[i * 4 + 3] = 0; // transparent pixels that still carry colour
        var back = DecodePng(PngOf(src, 37, 23), out int w, out int h);
        Assert.Equal((37, 23), (w, h));
        Assert.Equal(src, back);
    }

    [Theory]
    [InlineData("bgra4444", SurfaceFormat.Bgra4444)]
    [InlineData("bgr565", SurfaceFormat.Bgr565)]
    [InlineData("rgb565", SurfaceFormat.Bgr565)]
    [InlineData("1555", SurfaceFormat.Bgra5551)]
    [InlineData("bgra5551", SurfaceFormat.Bgra5551)]
    [InlineData("dxt3", SurfaceFormat.Dxt3)]
    [InlineData("dxt5", SurfaceFormat.Dxt5)]
    [InlineData("bgra8888", SurfaceFormat.Bgra32)]
    public void ImportWithFormat_GivesTheSamePixelsAsEncodingDirectly(string name, SurfaceFormat surface)
    {
        var src = Noise(32, 16, 9);
        var png = PngOf(src, 32, 16);

        var viaPng = new WzPngProperty();
        CanvasImaging.SetCanvasFromPng(viaPng, png, name, out _, out _);

        var direct = new WzPngProperty();
        direct.SetPixelsBgra32(src, 32, 16, surface);
        Assert.Equal(direct.GetPixelsBgra32(out _, out _), viaPng.GetPixelsBgra32(out _, out _));
        Assert.Equal(direct.Format, viaPng.Format);
    }

    [Fact]
    public void Import_UnknownFormatName_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CanvasImaging.ParseFormat("jpeg2000"));
        Assert.Contains("Unknown format", ex.Message);
        Assert.Null(CanvasImaging.ParseFormat(null));
        Assert.Null(CanvasImaging.ParseFormat("AUTO"));
    }

    [Fact]
    public void Decode_HandlesTheCommonPngColourTypes()
    {
        // 8 bit grayscale 2x1: values 0x10 and 0xF0.
        var gray = DecodePng(MakePng(2, 1, 8, 0, new byte[] { 0, 0x10, 0xF0 }), out _, out _);
        Assert.Equal(new byte[] { 0x10, 0x10, 0x10, 255, 0xF0, 0xF0, 0xF0, 255 }, gray);

        // 8 bit RGB 1x1: R=1 G=2 B=3, no alpha channel means opaque.
        var rgb = DecodePng(MakePng(1, 1, 8, 2, new byte[] { 0, 1, 2, 3 }), out _, out _);
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, rgb);

        // 8 bit gray + alpha 1x1.
        var ga = DecodePng(MakePng(1, 1, 8, 4, new byte[] { 0, 0x80, 0x40 }), out _, out _);
        Assert.Equal(new byte[] { 0x80, 0x80, 0x80, 0x40 }, ga);

        // 8 bit RGBA 1x1 with a non-opaque alpha: straight alpha must come back unchanged.
        var rgba = DecodePng(MakePng(1, 1, 8, 6, new byte[] { 0, 10, 20, 30, 40 }), out _, out _);
        Assert.Equal(new byte[] { 30, 20, 10, 40 }, rgba);

        // 8 bit palette 2x1 (index 1, then 0).
        var pal = DecodePng(MakePng(2, 1, 8, 3, new byte[] { 0, 1, 0 }, palette: new byte[] { 255, 0, 0, 0, 0, 255 }), out _, out _);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, pal);

        // 4 bit palette, 3x1, packed two indices per byte.
        var pal4 = DecodePng(MakePng(3, 1, 4, 3, new byte[] { 0, 0x01, 0x20 }, palette: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }), out _, out _);
        Assert.Equal(new byte[] { 3, 2, 1, 255, 6, 5, 4, 255, 9, 8, 7, 255 }, pal4);

        // 16 bit RGB 1x1: the result is within one level of the high byte.
        var rgb16 = DecodePng(MakePng(1, 1, 16, 2, new byte[] { 0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC }), out _, out _);
        Assert.InRange(rgb16[0], 0x9A, 0x9B);
        Assert.InRange(rgb16[1], 0x56, 0x57);
        Assert.InRange(rgb16[2], 0x12, 0x13);
        Assert.Equal(255, rgb16[3]);
    }

    [Fact]
    public void Decode_RefusesATruncatedPng_WhoseHeaderClaimsALargeImage()
    {
        // The header says 4096x4096 (allowed) but the data holds two scanlines: it must fail, not hang or crash.
        var png = MakePng(4096, 4096, 8, 6, new byte[2 * (1 + 4096 * 4)]);
        var scratch = new WzPngProperty();
        Assert.Throws<ImageRejectedException>(() => CanvasImaging.SetCanvasFromPng(scratch, png, null, out _, out _));
    }

    [Fact]
    public void Decode_RefusesCorruptedPngData()
    {
        var png = PngOf(Noise(16, 16, 3), 16, 16);
        var corrupt = (byte[])png.Clone();
        for (int i = 40; i < corrupt.Length - 20; i++) corrupt[i] ^= 0x5A;
        var scratch = new WzPngProperty();
        Assert.ThrowsAny<Exception>(() => CanvasImaging.SetCanvasFromPng(scratch, corrupt, null, out _, out _));
    }

    [Fact]
    public void Import_AcceptsA4096By4096Png_AndRoundTripsIt()
    {
        // A flat image compresses to a few kilobytes, but decodes to 64 MB: the largest allowed import.
        var src = new byte[4096 * 4096 * 4];
        for (int i = 0; i < src.Length; i += 4) { src[i] = 10; src[i + 1] = 20; src[i + 2] = 30; src[i + 3] = 255; }
        var png = PngOf(src, 4096, 4096);
        Assert.True(png.Length < 1_000_000);

        var target = new WzPngProperty();
        CanvasImaging.SetCanvasFromPng(target, png, "bgra8888", out int w, out int h);
        Assert.Equal((4096, 4096), (w, h));
        var back = target.GetPixelsBgra32(out _, out _)!;
        Assert.True(back.AsSpan().SequenceEqual(src));
    }

    [Fact]
    public void Import_RefusesA4097Wide_AndAnOversizedPng_BeforeDecoding()
    {
        var wide = PngOf(new byte[4097 * 4], 4097, 1);
        var scratch = new WzPngProperty();
        var ex = Assert.Throws<ImageRejectedException>(() => CanvasImaging.SetCanvasFromPng(scratch, wide, null, out _, out _));
        Assert.Contains("4097x1", ex.Message);
    }

    #endregion

    #region view

    [Fact]
    public void View_SmallImage_IsReturnedUnscaledAndPixelExact()
    {
        var src = Noise(40, 30, 77);
        var canvas = CanvasOf(src, 40, 30);
        var png = CanvasImaging.EncodeCanvasForView(canvas, 2048, out int ow, out int oh, out int w, out int h, out bool scaled)!;
        Assert.False(scaled);
        Assert.Equal((40, 30, 40, 30), (ow, oh, w, h));
        Assert.Equal(src, DecodePng(png, out _, out _));
    }

    [Theory]
    [InlineData(3000, 100, 2048, 68)]
    [InlineData(100, 3000, 68, 2048)]
    [InlineData(2049, 2049, 2048, 2048)]
    [InlineData(2048, 10, 2048, 10)]
    public void View_ScalesDownToTheSizeCap(int width, int height, int expectedW, int expectedH)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 255; px[i + 3] = 255; } // opaque red
        var png = CanvasImaging.EncodeCanvasForView(CanvasOf(px, width, height), 2048, out int ow, out int oh, out int w, out int h, out bool scaled)!;
        Assert.Equal((width, height), (ow, oh));
        Assert.Equal((expectedW, expectedH), (w, h));
        Assert.Equal(width > 2048 || height > 2048, scaled);

        Assert.Equal((expectedW, expectedH), CanvasImaging.ReadAndCheckPngHeader(png));
        var back = DecodePng(png, out int bw, out int bh);
        Assert.Equal((expectedW, expectedH), (bw, bh));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, back.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, back.AsSpan(back.Length - 4, 4).ToArray());
    }

    [Fact]
    public void View_HonoursASmallerMaxSize_AndClampsALargerOne()
    {
        var canvas = CanvasOf(Noise(64, 32, 4), 64, 32);
        CanvasImaging.EncodeCanvasForView(canvas, 16, out _, out _, out int w, out int h, out bool scaled);
        Assert.True(scaled);
        Assert.Equal((16, 8), (w, h));

        CanvasImaging.EncodeCanvasForView(canvas, 100000, out _, out _, out w, out h, out scaled);
        Assert.False(scaled);
        Assert.Equal((64, 32), (w, h));

        var big = CanvasOf(new byte[3000 * 2 * 4], 3000, 2);
        CanvasImaging.EncodeCanvasForView(big, 100000, out _, out _, out w, out h, out scaled);
        Assert.True(scaled); // the cap stays 2048 whatever the caller asks for
        Assert.Equal(2048, w);
    }

    [Fact]
    public void ViewCanvas_ReturnsATextBlockAndAnImageBlock()
    {
        CallToolResponse response = _image.ViewCanvas(Cat, Img, "testCanvas");
        Assert.NotEqual(true, response.IsError);
        Assert.Equal(2, response.Content.Count);
        Assert.Equal("text", response.Content[0].Type);
        Assert.Contains("32x32", response.Content[0].Text);
        Assert.Contains("origin (16, 16)", response.Content[0].Text);

        var image = response.Content[1];
        Assert.Equal("image", image.Type);
        Assert.Equal("image/png", image.MimeType);
        var bytes = Convert.FromBase64String(image.Data!);
        Assert.Equal((32, 32), CanvasImaging.ReadAndCheckPngHeader(bytes));
        var px = DecodePng(bytes, out _, out _);
        for (int i = 0; i < px.Length; i += 4)
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, px.AsSpan(i, 4).ToArray()); // the fixture canvas is solid red
    }

    [Fact]
    public void ViewCanvas_ScalesAndSaysSo()
    {
        var response = _image.ViewCanvas(Cat, Img, "testCanvas", maxSize: 8);
        Assert.Equal(2, response.Content.Count);
        Assert.Contains("Scaled down to 8x8", response.Content[0].Text);
        Assert.Contains("original size is 32x32", response.Content[0].Text);
        Assert.Equal((8, 8), CanvasImaging.ReadAndCheckPngHeader(Convert.FromBase64String(response.Content[1].Data!)));
    }

    [Fact]
    public void ViewCanvas_ReportsErrorsAsErrorResults()
    {
        var missing = _image.ViewCanvas(Cat, Img, "no/such/canvas");
        Assert.True(missing.IsError);
        Assert.Single(missing.Content);
        Assert.Contains("not found", missing.Content[0].Text);

        var notCanvas = _image.ViewCanvas(Cat, Img, "testString");
        Assert.True(notCanvas.IsError);

        var uninitialised = new ImageTools(new WzSessionManager()).ViewCanvas(Cat, Img, "testCanvas");
        Assert.True(uninitialised.IsError);
        Assert.Contains("No data source", uninitialised.Content[0].Text);
    }

    #endregion

    #region tools

    [Fact]
    public void ExportPng_WritesAPngFile_ThatMatchesView()
    {
        var target = Path.Combine(_fixture.TestDataPath, "out", "export.png");
        var result = _export.ExportPng(Cat, Img, "stand/2", target);
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("32", MarkdownTestHelper.GetFirstValue(result, "width"));
        var file = File.ReadAllBytes(target);

        var view = _image.ViewCanvas(Cat, Img, "stand/2");
        Assert.Equal(DecodePng(file, out _, out _), DecodePng(Convert.FromBase64String(view.Content[1].Data!), out _, out _));
        Assert.Equal(PixelsOf("stand/2", out _, out _), DecodePng(file, out _, out _));
    }

    [Fact]
    public void ExportPng_RefusesAnOutputPathOutsideTheAllowedRoot()
    {
        var result = _export.ExportPng(Cat, Img, "testCanvas", Path.Combine(_outside, "x.png"));
        MarkdownTestHelper.AssertFailure(result);
        Assert.Contains("outside allowed root", result);
        Assert.False(File.Exists(Path.Combine(_outside, "x.png")));
    }

    [Fact]
    public void ImportPng_FromAFile_CreatesACanvas_ThatReadsBackExactly()
    {
        var src = Noise(31, 17, 123);
        var file = WriteInRoot("import-a.png", PngOf(src, 31, 17));

        var result = _modify.ImportPng(Cat, Img, "", "importedFromFile", null, 3, 4, file, "bgra8888");
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("31", MarkdownTestHelper.GetFirstValue(result, "width"));
        Assert.Equal("17", MarkdownTestHelper.GetFirstValue(result, "height"));

        Assert.Equal(src, PixelsOf("importedFromFile", out int w, out int h));
        Assert.Equal((31, 17), (w, h));
        Assert.Contains("origin (3, 4)", _image.ViewCanvas(Cat, Img, "importedFromFile").Content[0].Text);
    }

    [Fact]
    public void ImportPng_FromBase64_StillWorks()
    {
        var src = Noise(8, 8, 124);
        var result = _modify.ImportPng(Cat, Img, "", "importedFromBase64", Base64(PngOf(src, 8, 8)), null, null, null, "bgra8888");
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal(src, PixelsOf("importedFromBase64", out _, out _));
    }

    [Fact]
    public void ImportPng_AutoFormat_ChoosesAFormat_AndKeepsTheSize()
    {
        var src = Noise(31, 17, 125);
        var result = _modify.ImportPng(Cat, Img, "", "importedAuto", Base64(PngOf(src, 31, 17)));
        MarkdownTestHelper.AssertSuccess(result);
        PixelsOf("importedAuto", out int w, out int h);
        Assert.Equal((31, 17), (w, h));
    }

    [Fact]
    public void ImportPng_Refusals_AreClearAndLeaveNothingBehind()
    {
        var valid = PngOf(Noise(4, 4, 1), 4, 4);
        var outside = WriteOutside("outside.png", valid);
        var notPng = WriteInRoot("not-a-png.png", System.Text.Encoding.ASCII.GetBytes("GIF89a....not a png"));
        var tooWide = WriteInRoot("too-wide.png", PngOf(new byte[4097 * 4], 4097, 1));
        var tooBig = WriteInRoot("too-big.png", new byte[(int)CanvasImaging.MaxImportBytes + 1]);
        var inside = WriteInRoot("fine.png", valid);

        string Import(string name, string? b64, string? path) => _modify.ImportPng(Cat, Img, "", name, b64, null, null, path);

        var r = Import("refused1", null, outside);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("outside allowed root", r);

        r = Import("refused2", null, "../../../../etc/passwd");
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("outside allowed root", r);

        r = Import("refused3", null, notPng);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("not a PNG", r);

        r = Import("refused4", null, tooWide);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("too large", r);
        Assert.Contains("4097x1", r);

        r = Import("refused5", null, tooBig);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("too large", r);

        r = Import("refused6", Base64(valid), inside);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("exactly one", r);

        r = Import("refused7", null, null);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("exactly one", r);

        r = Import("refused8", "not base64 !!", null);
        MarkdownTestHelper.AssertFailure(r);

        r = Import("refused9", Base64(System.Text.Encoding.ASCII.GetBytes("plain text, not an image")), null);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("not a PNG", r);

        r = _modify.ImportPng(Cat, Img, "", "refused10", null, null, null, inside, "dxt5"); // 4x4 is fine for DXT
        MarkdownTestHelper.AssertSuccess(r);
        _modify.DeleteProperty(Cat, Img, "refused10");

        var odd = WriteInRoot("odd.png", PngOf(Noise(5, 3, 2), 5, 3));
        r = _modify.ImportPng(Cat, Img, "", "refused11", null, null, null, odd, "dxt5");
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("multiples of 4", r);

        r = _modify.ImportPng(Cat, Img, "", "refused12", null, null, null, inside, "nonsense");
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("Unknown format", r);

        // None of the refused imports may have created a canvas.
        var list = _image.ListCanvasInImage(Cat, Img, 3);
        Assert.DoesNotContain("refused", list);
    }

    [Fact]
    public void SetCanvasBitmap_ReplacesTheImage_AndTheNewPixelsSurviveSaveAndReload()
    {
        var src = Noise(21, 11, 999);
        var file = WriteInRoot("replace.png", PngOf(src, 21, 11));
        var before = _image.ViewCanvas(Cat, Img, "stand/0");

        var result = _modify.SetCanvasBitmap(Cat, Img, "stand/0", null, file, "bgra8888");
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Contains("32x32", result);   // old size
        Assert.Contains("21x11", result);   // new size
        Assert.NotEqual(before.Content[1].Data, _image.ViewCanvas(Cat, Img, "stand/0").Content[1].Data);
        Assert.Equal(src, PixelsOf("stand/0", out _, out _));

        var untouchedBefore = PixelsOf("stand/1", out _, out _);
        MarkdownTestHelper.AssertSuccess(_modify.SaveImage(Cat, Img));
        using var reloaded = new WzSessionManager();
        reloaded.InitDataSource(_fixture.TestDataPath);
        Assert.Equal(src, PixelsOf("stand/0", out int w, out int h, reloaded));
        Assert.Equal((21, 11), (w, h));
        // an untouched frame decodes to the same pixels after the save and reload
        Assert.Equal(untouchedBefore, PixelsOf("stand/1", out _, out _, reloaded));
    }

    [Fact]
    public void SetCanvasBitmap_AcceptsAOnePixelPng_AndRefusesBadInput()
    {
        // Unlike the upstream test CanvasAndPersistenceOps_Work (which accepts an error), this one insists on success.
        var onePx = Base64(MakePng(1, 1, 8, 4, new byte[] { 0, 0x80, 0xFF }));
        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "stand/1", onePx));
        PixelsOf("stand/1", out int w, out int h);
        Assert.Equal((1, 1), (w, h));

        var outside = WriteOutside("set-outside.png", PngOf(Noise(2, 2, 1), 2, 2));
        var r = _modify.SetCanvasBitmap(Cat, Img, "stand/1", null, outside);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("outside allowed root", r);
        PixelsOf("stand/1", out w, out h);
        Assert.Equal((1, 1), (w, h)); // a refused call changes nothing

        r = _modify.SetCanvasBitmap(Cat, Img, "stand/1", Base64(new byte[40]));
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("not a PNG", r);

        r = _modify.SetCanvasBitmap(Cat, Img, "does/not/exist", onePx);
        MarkdownTestHelper.AssertFailure(r);
    }

    [Fact]
    public void SetCanvasBitmap_RefusesTheUpstreamTestPng_BecauseItsChecksumsAreWrong()
    {
        // The 1x1 PNG in the upstream ModifyToolsTests has a wrong IDAT CRC and a zlib stream without its Adler-32.
        // libpng (used by SkiaSharp) rejects such a file; the upstream test accepts either outcome.
        const string badPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7+J1gAAAAASUVORK5CYII=";
        var r = _modify.SetCanvasBitmap(Cat, Img, "stand/1", badPng);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("could not be decoded", r);
    }

    [Fact]
    public void BatchExportImages_WritesOneFilePerCanvas_AsPngOrJpeg()
    {
        var batch = new BatchTools(_fixture.Session);
        foreach (var (format, magic) in new[] { ("png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }), ("jpg", new byte[] { 0xFF, 0xD8, 0xFF }) })
        {
            var dir = Path.Combine(_fixture.TestDataPath, "batch-" + format);
            var result = batch.BatchExportImages("Character", dir, format);
            MarkdownTestHelper.AssertSuccess(result);

            // Test.img has testCanvas and the three frames stand/0..2 (other tests may add more canvases).
            var files = Directory.GetFiles(Path.Combine(dir, "Character", "Test"), "*." + format);
            var exported = MarkdownTestHelper.GetFirstInt(result, "exported_count");
            Assert.True(files.Length >= 4, $"expected at least 4 {format} files, found {files.Length}");
            Assert.Equal(exported, files.Length); // no file was overwritten by another canvas
            Assert.Contains(files, f => Path.GetFileName(f) == "stand_0." + format);
            Assert.All(files, f => Assert.True(File.ReadAllBytes(f).AsSpan().StartsWith(magic)));
        }
    }

    [Fact]
    public void CopyProperty_CopiesACanvasWithItsPixels()
    {
        var src = Noise(9, 6, 55);
        MarkdownTestHelper.AssertSuccess(_modify.ImportPng(Cat, Img, "", "copySource", Base64(PngOf(src, 9, 6)), null, null, null, "bgra8888"));
        MarkdownTestHelper.AssertSuccess(_modify.CopyProperty(Cat, Img, "copySource", Cat, Img, "info", "copyTarget"));
        Assert.Equal(src, PixelsOf("info/copyTarget", out int w, out int h));
        Assert.Equal((9, 6), (w, h));
    }

    [Fact]
    public void GetCanvasBitmap_ReturnsAValidPng()
    {
        var result = _image.GetCanvasBitmap(Cat, Img, "testCanvas");
        MarkdownTestHelper.AssertSuccess(result);
        var b64 = MarkdownTestHelper.GetFirstValue(result, "base64_png")!;
        Assert.Equal((32, 32), CanvasImaging.ReadAndCheckPngHeader(Convert.FromBase64String(b64)));
    }

    [Theory]
    [InlineData("bgra8888", WzPngFormat.Format2, true)]
    [InlineData("bgra4444", WzPngFormat.Format1, true)]
    [InlineData("bgra5551", WzPngFormat.Format257, true)]
    [InlineData("bgr565", WzPngFormat.Format513, true)]
    [InlineData("dxt3", WzPngFormat.Format1026, false)]
    [InlineData("dxt5", WzPngFormat.Format2050, false)]
    public void SetCanvasBitmap_KeepsTheCanvasFormat_SoExportEditImportDoesNotDrift(string fmt, WzPngFormat expected, bool exactCycle)
    {
        var name = "keep_" + fmt;
        var src = Noise(32, 16, 77 + fmt.Length);
        MarkdownTestHelper.AssertSuccess(_modify.ImportPng(Cat, Img, "", name, Base64(PngOf(src, 32, 16)), null, null, null, fmt));
        var canvas = (WzCanvasProperty)_fixture.Session.GetImage(Cat, Img).GetFromPath(name)!;
        Assert.Equal(expected, canvas.PngProperty.Format);
        var first = PixelsOf(name, out _, out _);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            var png = CanvasImaging.EncodeCanvas(canvas, ExportImageFormat.Png, out _, out _)!;
            var result = _modify.SetCanvasBitmap(Cat, Img, name, Base64(png)); // no format: keep the current one
            MarkdownTestHelper.AssertSuccess(result);
            Assert.Equal(expected, canvas.PngProperty.Format);
            Assert.Contains($"({expected})", result);
            if (exactCycle)
                Assert.Equal(first, PixelsOf(name, out _, out _)); // nothing drifts, however often the cycle runs
        }
    }

    [Fact]
    public void SetCanvasBitmap_FormatParameter_OverridesTheCurrentFormat_AndAutoMeansBgra8888()
    {
        var src = Noise(8, 8, 5);
        MarkdownTestHelper.AssertSuccess(_modify.ImportPng(Cat, Img, "", "override565", Base64(PngOf(src, 8, 8)), null, null, null, "bgr565"));
        var canvas = (WzCanvasProperty)_fixture.Session.GetImage(Cat, Img).GetFromPath("override565")!;
        Assert.Equal(WzPngFormat.Format513, canvas.PngProperty.Format);

        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "override565", Base64(PngOf(src, 8, 8)), null, "bgra4444"));
        Assert.Equal(WzPngFormat.Format1, canvas.PngProperty.Format);

        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "override565", Base64(PngOf(src, 8, 8)), null, "auto"));
        Assert.Equal(WzPngFormat.Format2, canvas.PngProperty.Format);
        Assert.Equal(src, PixelsOf("override565", out _, out _));
    }

    [Fact]
    public void SetCanvasBitmap_OnACanvasWithoutPixels_UsesBgra8888()
    {
        var image = _fixture.Session.GetImage(Cat, Img);
        image.AddProperty(new WzCanvasProperty("noPixelProperty"));
        image.AddProperty(new WzCanvasProperty("emptyPixelProperty") { PngProperty = new WzPngProperty() });
        var src = Noise(5, 7, 6);
        foreach (var name in new[] { "noPixelProperty", "emptyPixelProperty" })
        {
            MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, name, Base64(PngOf(src, 5, 7))));
            var canvas = (WzCanvasProperty)image.GetFromPath(name)!;
            Assert.Equal(WzPngFormat.Format2, canvas.PngProperty.Format);
            Assert.Equal(src, PixelsOf(name, out _, out _));
        }
    }

    [Fact]
    public void SetCanvasBitmap_OnADxtCanvas_FallsBackToBgra8888WithAWarning_WhenTheSizeIsNotAMultipleOfFour()
    {
        MarkdownTestHelper.AssertSuccess(_modify.ImportPng(Cat, Img, "", "dxtKeep", Base64(PngOf(Noise(8, 8, 1), 8, 8)), null, null, null, "dxt5"));
        var odd = Noise(5, 3, 2);
        var result = _modify.SetCanvasBitmap(Cat, Img, "dxtKeep", Base64(PngOf(odd, 5, 3)));
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Contains("multiples of 4", MarkdownTestHelper.GetFirstValue(result, "warning"));
        var canvas = (WzCanvasProperty)_fixture.Session.GetImage(Cat, Img).GetFromPath("dxtKeep")!;
        Assert.Equal(WzPngFormat.Format2, canvas.PngProperty.Format);
        Assert.Equal(odd, PixelsOf("dxtKeep", out _, out _));
    }

    [Fact]
    public void LinkedPixels_ALinkToACanvasWithoutPixels_GivesNoImage_LikeWindows()
    {
        var image = _fixture.Session.GetImage(Cat, Img);
        var own = Noise(6, 6, 41);
        var target = new WzCanvasProperty("linkTargetNoPixels");            // no pixel property at all
        var good = CanvasOf(Noise(3, 3, 42), 3, 3);
        good.Name = "linkTargetGood";
        image.AddProperty(target);
        image.AddProperty(good);

        WzCanvasProperty Linker(string name, string link)
        {
            var c = CanvasOf(own, 6, 6);
            c.Name = name;
            c.AddProperty(new WzStringProperty("_inlink", link));
            image.AddProperty(c);
            return c;
        }

        // A link that leads to a canvas without pixel data has no image: it must not fall back to the linker's own pixels.
        Assert.Null(Linker("linkerNoPixels", "linkTargetNoPixels").GetLinkedPixelsBgra32(out int w, out int h));
        Assert.Equal((0, 0), (w, h));
        Assert.False(CanvasImaging.HasDecodableImage(Linker("linkerNoPixels2", "linkTargetNoPixels")));
        Assert.Null(CanvasImaging.EncodeCanvas(Linker("linkerNoPixels3", "linkTargetNoPixels"), ExportImageFormat.Png, out _, out _));

        // A link to a canvas with pixels returns those pixels; a link that does not resolve falls back to the canvas itself.
        Assert.Equal(good.GetLinkedPixelsBgra32(out _, out _), Linker("linkerGood", "linkTargetGood").GetLinkedPixelsBgra32(out w, out h));
        Assert.Equal((3, 3), (w, h));
        Assert.Equal(own, Linker("linkerBroken", "does/not/exist").GetLinkedPixelsBgra32(out w, out h));
        Assert.Equal((6, 6), (w, h));

        // And without any link a canvas without pixels simply has none.
        Assert.Null(new WzCanvasProperty("plain").GetLinkedPixelsBgra32(out _, out _));
    }

    #endregion
}
