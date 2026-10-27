using MapleLib.Helpers;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Imaging;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// set_canvas_bitmap without a format keeps the pixel format the canvas has, exactly: the same WZ format number where the
/// new image allows it, and a warning that names the change where it does not.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class KeepFormatTests : IClassFixture<TestFixture>, IDisposable
{
    private const string Cat = "Character";
    private const string Img = "Test.img";

    private readonly TestFixture _fixture;
    private readonly bool _previousPreBigBang = ImageFormatDetector.UsePreBigBangImageFormats;
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly ModifyTools _modify;

    public KeepFormatTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _modify = new ModifyTools(fixture.Session);
    }

    public void Dispose()
    {
        ImageFormatDetector.UsePreBigBangImageFormats = _previousPreBigBang;
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
    }

    private static byte[] Noise(int w, int h, int seed)
    {
        var buf = new byte[w * h * 4];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    /// <summary>Opaque grey pixels: red = green = blue.</summary>
    private static byte[] Grey(int w, int h)
    {
        var buf = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte v = (byte)(i * 5);
            buf[i * 4] = v; buf[i * 4 + 1] = v; buf[i * 4 + 2] = v; buf[i * 4 + 3] = 255;
        }
        return buf;
    }

    /// <summary>Opaque pixels with one colour per 16x16 block (so Format517 can store them).</summary>
    private static byte[] BlockColours(int w, int h)
    {
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int block = (y / 16) * 7 + (x / 16);
                int o = (y * w + x) * 4;
                buf[o] = (byte)(block * 40 + 8); buf[o + 1] = (byte)(255 - block * 30); buf[o + 2] = (byte)(block * 17); buf[o + 3] = 255;
            }
        return buf;
    }

    private static string PngBase64(byte[] bgra, int w, int h)
    {
        var canvas = new WzCanvasProperty("c") { PngProperty = new WzPngProperty() };
        canvas.PngProperty.SetPixelsBgra32(bgra, w, h, SurfaceFormat.Bgra32);
        return Convert.ToBase64String(CanvasImaging.EncodeCanvas(canvas, ExportImageFormat.Png, out _, out _)!);
    }

    private WzCanvasProperty AddCanvas(string name, byte[] bgra, int w, int h, SurfaceFormat surface, bool? dxt3Grayscale = null)
    {
        var canvas = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
        canvas.PngProperty.SetPixelsBgra32(bgra, w, h, surface, dxt3Grayscale);
        _fixture.Session.GetImage(Cat, Img).AddProperty(canvas);
        return canvas;
    }

    [Fact]
    public void Format3_StaysFormat3_EvenForAnImageThatIsNotGrey()
    {
        var canvas = AddCanvas("keep3", Grey(16, 16), 16, 16, SurfaceFormat.Dxt3, dxt3Grayscale: true);
        Assert.Equal(WzPngFormat.Format3, canvas.PngProperty.Format);

        // Re-importing the exported (lossy) pixels, and importing a colour image: the format tag is kept.
        var exported = canvas.PngProperty.GetPixelsBgra32(out var w, out var h)!;
        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "keep3", PngBase64(exported, w, h)));
        Assert.Equal(WzPngFormat.Format3, canvas.PngProperty.Format);

        var result = _modify.SetCanvasBitmap(Cat, Img, "keep3", PngBase64(Noise(16, 16, 3), 16, 16));
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal(WzPngFormat.Format3, canvas.PngProperty.Format);
        Assert.Contains("(Format3)", result);
    }

    [Fact]
    public void Format1026_StaysFormat1026_EvenForAGreyImage()
    {
        var canvas = AddCanvas("keep1026", Noise(16, 16, 4), 16, 16, SurfaceFormat.Dxt3, dxt3Grayscale: false);
        Assert.Equal(WzPngFormat.Format1026, canvas.PngProperty.Format);

        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "keep1026", PngBase64(Grey(16, 16), 16, 16)));
        Assert.Equal(WzPngFormat.Format1026, canvas.PngProperty.Format);
    }

    [Fact]
    public void ExplicitDxt3_StillLetsTheEncoderPickTheTag()
    {
        var canvas = AddCanvas("explicit3", Noise(16, 16, 5), 16, 16, SurfaceFormat.Dxt3, dxt3Grayscale: true);
        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "explicit3", PngBase64(Noise(16, 16, 6), 16, 16), null, "dxt3"));
        Assert.Equal(WzPngFormat.Format1026, canvas.PngProperty.Format);          // colour content: not a grey thumbnail
        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "explicit3", PngBase64(Grey(16, 16), 16, 16), null, "dxt3"));
        Assert.Equal(WzPngFormat.Format3, canvas.PngProperty.Format);             // grey content
    }

    [Fact]
    public void Format517_StaysFormat517_WhenTheNewImageIsOneColourPer16x16Block()
    {
        var canvas = AddCanvas("keep517", BlockColours(32, 32), 32, 32, SurfaceFormat.Bgr565);
        Assert.Equal(WzPngFormat.Format517, canvas.PngProperty.Format);

        var result = _modify.SetCanvasBitmap(Cat, Img, "keep517", PngBase64(BlockColours(48, 32), 48, 32));
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal(WzPngFormat.Format517, canvas.PngProperty.Format);
        Assert.True(string.IsNullOrEmpty(MarkdownTestHelper.GetFirstValue(result, "warning")), result);
    }

    [Theory]
    [InlineData(32, 32, true)]     // multiple of 16, but every pixel differs
    [InlineData(20, 20, false)]    // not a multiple of 16
    public void Format517_BecomesFormat513WithAWarning_WhenTheNewImageCannotBeStoredAs517(int w, int h, bool useNoise)
    {
        var canvas = AddCanvas($"drop517_{w}", BlockColours(32, 32), 32, 32, SurfaceFormat.Bgr565);
        Assert.Equal(WzPngFormat.Format517, canvas.PngProperty.Format);

        var image = useNoise ? Noise(w, h, 7) : BlockColours(w, h);
        var result = _modify.SetCanvasBitmap(Cat, Img, $"drop517_{w}", PngBase64(image, w, h));
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal(WzPngFormat.Format513, canvas.PngProperty.Format);
        var warning = MarkdownTestHelper.GetFirstValue(result, "warning");
        Assert.Contains("Format517", warning);
        Assert.Contains("Format513", warning);
    }

    [Theory]
    [InlineData(SurfaceFormat.Dxt3, WzPngFormat.Format1026)]
    [InlineData(SurfaceFormat.Dxt5, WzPngFormat.Format2050)]
    public void ADxtCanvas_BecomesBgra8888WithAWarning_WhenPreBigBangIsOn(SurfaceFormat surface, WzPngFormat expectedBefore)
    {
        ImageFormatDetector.UsePreBigBangImageFormats = false;
        var name = "preBB_" + surface;
        var canvas = AddCanvas(name, Noise(16, 16, 8), 16, 16, surface);
        Assert.Equal(expectedBefore, canvas.PngProperty.Format);

        ImageFormatDetector.UsePreBigBangImageFormats = true;
        var src = Noise(16, 16, 9);
        var result = _modify.SetCanvasBitmap(Cat, Img, name, PngBase64(src, 16, 16));
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal(WzPngFormat.Format2, canvas.PngProperty.Format);
        Assert.Equal(src, canvas.GetLinkedPixelsBgra32(out _, out _));
        Assert.Contains("WZIMG_PRE_BIG_BANG", MarkdownTestHelper.GetFirstValue(result, "warning"));

        // DXT stays available on request.
        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, name, PngBase64(src, 16, 16), null, "dxt5"));
        Assert.Equal(WzPngFormat.Format2050, canvas.PngProperty.Format);
    }

    [Fact]
    public void ADxtCanvas_KeepsDxt_WhenPreBigBangIsOff()
    {
        ImageFormatDetector.UsePreBigBangImageFormats = false;
        var canvas = AddCanvas("postBB", Noise(16, 16, 10), 16, 16, SurfaceFormat.Dxt5);
        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, "postBB", PngBase64(Noise(16, 16, 11), 16, 16)));
        Assert.Equal(WzPngFormat.Format2050, canvas.PngProperty.Format);
    }
}
