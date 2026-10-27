using MapleLib.Helpers;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Imaging;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// format=auto (and no format on import_png) must store lossless bgra8888, never DXT or a 16 bit format, and the
/// pre Big Bang switch (WZIMG_PRE_BIG_BANG, default on) must keep DXT out of MapleLib's automatic choice.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class AutoFormatTests : IClassFixture<TestFixture>, IDisposable
{
    private const string Cat = "Character";
    private const string Img = "Test.img";

    private readonly TestFixture _fixture;
    private readonly ModifyTools _modify;
    private readonly string? _previousPreBb = Environment.GetEnvironmentVariable(ServerSettings.PreBigBangEnvVar);
    private readonly bool _previousFlag = ImageFormatDetector.UsePreBigBangImageFormats;

    public AutoFormatTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        _modify = new ModifyTools(fixture.Session);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ServerSettings.PreBigBangEnvVar, _previousPreBb);
        ImageFormatDetector.UsePreBigBangImageFormats = _previousFlag;
    }

    private static byte[] SoftAlpha(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = (byte)(i * 3); px[i * 4 + 1] = (byte)(i * 5); px[i * 4 + 2] = (byte)(i * 11);
            px[i * 4 + 3] = (byte)((i * 37) % 256);        // many different alpha values, none of them only 0 / 255
        }
        return px;
    }

    private static byte[] GrayWithAlpha(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte g = (byte)((x * 255) / (w - 1));
                px[i] = px[i + 1] = px[i + 2] = g;
                px[i + 3] = (byte)(y % 2 == 0 ? 255 : 128 + (x % 64));
            }
        return px;
    }

    private static byte[] ManyColours(int w, int h)
    {
        var px = new byte[w * h * 4];
        new Random(123).NextBytes(px);
        for (int i = 0; i < w * h; i++) px[i * 4 + 3] = 255;     // opaque, far more than 4096 colours
        return px;
    }

    private static string Png(byte[] bgra, int w, int h)
    {
        var canvas = new WzCanvasProperty("c") { PngProperty = new WzPngProperty() };
        canvas.PngProperty.SetPixelsBgra32(bgra, w, h, SurfaceFormat.Bgra32);
        return Convert.ToBase64String(CanvasImaging.EncodeCanvas(canvas, ExportImageFormat.Png, out _, out _)!);
    }

    private WzCanvasProperty Canvas(string path) => (WzCanvasProperty)_fixture.Session.GetImage(Cat, Img).GetFromPath(path)!;

    public static IEnumerable<object[]> Images()
    {
        yield return new object[] { "soft alpha", 40, 24 };
        yield return new object[] { "gray alpha", 64, 64 };
        yield return new object[] { "many colours", 48, 48 };
    }

    private static byte[] Make(string kind, int w, int h) => kind switch
    {
        "soft alpha" => SoftAlpha(w, h),
        "gray alpha" => GrayWithAlpha(w, h),
        _ => ManyColours(w, h)
    };

    [Theory]
    [MemberData(nameof(Images))]
    public void ImportPng_WithoutFormatOrWithAuto_StoresBgra8888_AndReadsBackExactly(string kind, int w, int h)
    {
        var src = Make(kind, w, h);
        var b64 = Png(src, w, h);
        int n = 0;
        foreach (var format in new string?[] { null, "auto", "AUTO", "" })
        {
            var name = $"auto_{kind.Replace(' ', '_')}_{n++}";
            MarkdownTestHelper.AssertSuccess(_modify.ImportPng(Cat, Img, "", name, b64, null, null, null, format));
            var canvas = Canvas(name);
            Assert.Equal(WzPngFormat.Format2, canvas.PngProperty.Format);
            Assert.Equal(src, canvas.GetLinkedPixelsBgra32(out int rw, out int rh));
            Assert.Equal((w, h), (rw, rh));
        }
    }

    [Theory]
    [MemberData(nameof(Images))]
    public void SetCanvasBitmap_WithAuto_StoresBgra8888_EvenOnACanvasThatWasLossy(string kind, int w, int h)
    {
        var src = Make(kind, w, h);
        var b64 = Png(src, w, h);
        var name = "autoSet_" + kind.Replace(' ', '_');
        MarkdownTestHelper.AssertSuccess(_modify.ImportPng(Cat, Img, "", name, Png(SoftAlpha(8, 8), 8, 8), null, null, null, "bgra4444"));
        Assert.Equal(WzPngFormat.Format1, Canvas(name).PngProperty.Format);

        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, name, b64, null, "auto"));
        var canvas = Canvas(name);
        Assert.Equal(WzPngFormat.Format2, canvas.PngProperty.Format);
        Assert.Equal(src, canvas.GetLinkedPixelsBgra32(out _, out _));
    }

    [Fact]
    public void LossyFormats_StayAvailableWhenRequestedExplicitly()
    {
        var b64 = Png(GrayWithAlpha(64, 64), 64, 64);
        foreach (var (format, expected) in new[] { ("dxt3", WzPngFormat.Format3), ("dxt5", WzPngFormat.Format2050),
                     ("bgra4444", WzPngFormat.Format1), ("bgr565", WzPngFormat.Format513), ("bgra5551", WzPngFormat.Format257) })
        {
            MarkdownTestHelper.AssertSuccess(_modify.ImportPng(Cat, Img, "", "explicit_" + format, b64, null, null, null, format));
            Assert.Equal(expected, Canvas("explicit_" + format).PngProperty.Format);
        }
    }

    [Fact]
    public void PreBigBang_IsOnByDefault_AndKeepsDxtOutOfTheAutomaticChoice()
    {
        Environment.SetEnvironmentVariable(ServerSettings.PreBigBangEnvVar, null);
        ImageFormatDetector.UsePreBigBangImageFormats = false;
        ServerSettings.Apply();
        Assert.True(ImageFormatDetector.UsePreBigBangImageFormats);

        foreach (var kind in new[] { "soft alpha", "gray alpha", "many colours" })
        {
            var px = Make(kind, 64, 64);
            var chosen = ImageFormatDetector.DetermineTextureFormat(px, 64, 64);
            Assert.NotEqual(SurfaceFormat.Dxt3, chosen);
            Assert.NotEqual(SurfaceFormat.Dxt5, chosen);
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("", true)]
    [InlineData("nonsense", true)]
    public void PreBigBang_CanBeSwitchedWithTheEnvironmentVariable(string value, bool expected)
    {
        Environment.SetEnvironmentVariable(ServerSettings.PreBigBangEnvVar, value);
        ServerSettings.Apply();
        Assert.Equal(expected, ImageFormatDetector.UsePreBigBangImageFormats);
    }

    [Fact]
    public void PreBigBangOff_LetsTheDetectorPickDxtAgain()
    {
        Environment.SetEnvironmentVariable(ServerSettings.PreBigBangEnvVar, "0");
        ServerSettings.Apply();
        var chosen = ImageFormatDetector.DetermineTextureFormat(GrayWithAlpha(64, 64), 64, 64);
        Assert.Equal(SurfaceFormat.Dxt3, chosen);
    }
}
