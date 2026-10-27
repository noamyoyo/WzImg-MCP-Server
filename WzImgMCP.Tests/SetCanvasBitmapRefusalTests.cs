using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Imaging;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>set_canvas_bitmap: a refused call changes nothing, and canvases that are links say so.</summary>
[Collection("AllowedRootEnv")]
public sealed class SetCanvasBitmapRefusalTests : IClassFixture<TestFixture>, IDisposable
{
    private const string Cat = "Character";
    private const string Img = "Test.img";

    private readonly TestFixture _fixture;
    private readonly ModifyTools _modify;
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);

    public SetCanvasBitmapRefusalTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, fixture.TestDataPath);
        _modify = new ModifyTools(fixture.Session);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);

    private static string GoodPng(int w = 4, int h = 4)
    {
        var canvas = new WzCanvasProperty("c") { PngProperty = new WzPngProperty() };
        var px = new byte[w * h * 4];
        new Random(5).NextBytes(px);
        canvas.PngProperty.SetPixelsBgra32(px, w, h, SurfaceFormat.Bgra32);
        return Convert.ToBase64String(CanvasImaging.EncodeCanvas(canvas, ExportImageFormat.Png, out _, out _)!);
    }

    private static string CorruptPng()
    {
        var bytes = Convert.FromBase64String(GoodPng());
        for (int i = bytes.Length / 2; i < bytes.Length - 16; i++) bytes[i] ^= 0xA5;   // keep the header, break the data
        return Convert.ToBase64String(bytes);
    }

    private WzCanvasProperty AddCanvas(string name)
    {
        var canvas = new WzCanvasProperty(name);
        _fixture.Session.GetImage(Cat, Img).AddProperty(canvas);
        return canvas;
    }

    [Theory]
    [InlineData("corrupt", null)]
    [InlineData("good", "jpeg2000")]
    [InlineData("notbase64", null)]
    public void SetCanvasBitmap_Refused_LeavesACanvasWithoutPixelsWithoutAPixelProperty(string kind, string? format)
    {
        var canvas = AddCanvas("noPixels_" + kind);
        Assert.Null(canvas.PngProperty);
        var png = kind switch { "corrupt" => CorruptPng(), "good" => GoodPng(), _ => "!!!not base64!!!" };

        var r = _modify.SetCanvasBitmap(Cat, Img, canvas.Name, png, null, format);

        MarkdownTestHelper.AssertFailure(r);
        Assert.Null(canvas.PngProperty);
    }

    [Fact]
    public void SetCanvasBitmap_Refused_ByTheSizeLimit_LeavesNoPixelProperty()
    {
        var canvas = AddCanvas("noPixels_huge");
        // A header that claims 5000 x 5000 pixels.
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x13, 0x88, 0, 0, 0x13, 0x88, 8, 6, 0, 0, 0, 0, 0, 0, 0 };
        MarkdownTestHelper.AssertFailure(_modify.SetCanvasBitmap(Cat, Img, canvas.Name, Convert.ToBase64String(header)));
        Assert.Null(canvas.PngProperty);
    }

    [Fact]
    public void SetCanvasBitmap_Refused_LeavesTheExistingPixelsAlone()
    {
        var canvas = (WzCanvasProperty)_fixture.Session.GetImage(Cat, Img).GetFromPath("testCanvas")!;
        var before = canvas.PngProperty.GetPixelsBgra32(out int w, out int h);
        var format = canvas.PngProperty.Format;

        MarkdownTestHelper.AssertFailure(_modify.SetCanvasBitmap(Cat, Img, "testCanvas", CorruptPng()));

        Assert.Equal(before, canvas.PngProperty.GetPixelsBgra32(out int w2, out int h2));
        Assert.Equal((w, h), (w2, h2));
        Assert.Equal(format, canvas.PngProperty.Format);
    }

    [Fact]
    public void SetCanvasBitmap_OnACanvasWithoutPixels_CreatesThePixelPropertyWhenTheCallSucceeds()
    {
        var canvas = AddCanvas("noPixels_ok");
        MarkdownTestHelper.AssertSuccess(_modify.SetCanvasBitmap(Cat, Img, canvas.Name, GoodPng(6, 3)));
        Assert.NotNull(canvas.PngProperty);
        Assert.Equal((6, 3), (canvas.PngProperty.Width, canvas.PngProperty.Height));
        Assert.Equal(WzPngFormat.Format2, canvas.PngProperty.Format);
    }

    [Theory]
    [InlineData("_inlink", "testCanvas")]
    [InlineData("_outlink", "Map/Back/somewhere.img/back/0")]
    public void SetCanvasBitmap_OnALinkedCanvas_Succeeds_ButWarnsThatTheLinkTargetIsStillShown(string linkName, string target)
    {
        var canvas = AddCanvas("linked_" + linkName.TrimStart('_'));
        canvas.AddProperty(new WzStringProperty(linkName, target));

        var r = _modify.SetCanvasBitmap(Cat, Img, canvas.Name, GoodPng(5, 5));

        MarkdownTestHelper.AssertSuccess(r);
        var warning = MarkdownTestHelper.GetFirstValue(r, "warning");
        Assert.NotNull(warning);
        Assert.Contains("link", warning);
        Assert.Contains(linkName, warning);
        Assert.Contains(target, warning);
        Assert.Contains("link target", warning);
        Assert.Equal((5, 5), (canvas.PngProperty.Width, canvas.PngProperty.Height));   // the pixels were stored
    }

    [Fact]
    public void SetCanvasBitmap_OnAPlainCanvas_HasNoLinkWarning()
    {
        var canvas = AddCanvas("notLinked");
        var r = _modify.SetCanvasBitmap(Cat, Img, canvas.Name, GoodPng());
        MarkdownTestHelper.AssertSuccess(r);
        Assert.Null(MarkdownTestHelper.GetFirstValue(r, "warning"));
    }
}
