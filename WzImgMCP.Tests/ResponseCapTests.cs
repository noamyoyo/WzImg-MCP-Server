using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Imaging;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>get_canvas_bitmap, resolve_canvas_link, get_animation_frames and view_canvas keep their image data within WZIMG_MAX_RESPONSE_BYTES.</summary>
[Collection("AllowedRootEnv")]
public sealed class ResponseCapTests : IClassFixture<TestFixture>, IDisposable
{
    private const string Cat = "Character";
    private const string Img = "Test.img";

    private readonly TestFixture _fixture;
    private readonly ImageTools _image;
    private readonly string? _previous = Environment.GetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar);

    public ResponseCapTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        _image = new ImageTools(fixture.Session);
        Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, _previous);

    private static WzCanvasProperty NoiseCanvas(string name, int w, int h, int seed)
    {
        var canvas = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
        var px = new byte[w * h * 4];
        new Random(seed).NextBytes(px);
        canvas.PngProperty.SetPixelsBgra32(px, w, h, SurfaceFormat.Bgra32);
        return canvas;
    }

    private WzCanvasProperty Add(WzCanvasProperty canvas)
    {
        _fixture.Session.GetImage(Cat, Img).AddProperty(canvas);
        return canvas;
    }

    private static void SetCap(long bytes) => Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, bytes.ToString());

    [Fact]
    public void Setting_DefaultsTo8MiB_AndTakesTheEnvironmentVariable()
    {
        Assert.Equal(8L * 1024 * 1024, ServerSettings.MaxResponseBytes);
        SetCap(12345);
        Assert.Equal(12345, ServerSettings.MaxResponseBytes);
        Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, "garbage");
        Assert.Equal(8L * 1024 * 1024, ServerSettings.MaxResponseBytes);
        Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, "-5");
        Assert.Equal(8L * 1024 * 1024, ServerSettings.MaxResponseBytes);
    }

    [Fact]
    public void GetCanvasBitmap_SmallCanvas_StillReturnsTheImage()
    {
        var r = _image.GetCanvasBitmap(Cat, Img, "testCanvas");
        MarkdownTestHelper.AssertSuccess(r);
        Assert.NotNull(MarkdownTestHelper.GetFirstValue(r, "base64_png"));
        Assert.DoesNotContain("Truncated", r);
    }

    [Fact]
    public void GetCanvasBitmap_AboveTheDefaultCap_OmitsTheImageAndSaysWhatToUseInstead()
    {
        Add(NoiseCanvas("noiseBig", 2000, 2000, 1));    // noise does not compress: about 16 MB of PNG, 22 million base64 characters
        var r = _image.GetCanvasBitmap(Cat, Img, "noiseBig");
        MarkdownTestHelper.AssertSuccess(r);
        Assert.Null(MarkdownTestHelper.GetFirstValue(r, "base64_png"));
        Assert.True(r.Length < 5000, $"the response must stay small, was {r.Length} characters");
        Assert.Contains("Truncated", r);
        Assert.Contains("view_canvas", r);
        Assert.Contains("export_png", r);
        Assert.Contains("2000", r);   // width and height are still reported
    }

    [Fact]
    public void GetCanvasBitmap_CapFromTheEnvironment_IsHonoured()
    {
        Add(NoiseCanvas("noiseSmall", 100, 100, 2));    // about 40 KB of PNG
        SetCap(100_000);
        Assert.NotNull(MarkdownTestHelper.GetFirstValue(_image.GetCanvasBitmap(Cat, Img, "noiseSmall"), "base64_png"));
        SetCap(10_000);
        var r = _image.GetCanvasBitmap(Cat, Img, "noiseSmall");
        MarkdownTestHelper.AssertSuccess(r);
        Assert.Null(MarkdownTestHelper.GetFirstValue(r, "base64_png"));
        Assert.Contains("Truncated", r);
    }

    [Fact]
    public void ResolveCanvasLink_AboveTheCap_OmitsTheImage_ForLinkedAndPlainCanvases()
    {
        var target = Add(NoiseCanvas("linkNoiseTarget", 100, 100, 3));
        var linker = new WzCanvasProperty("linkNoiseLinker");
        linker.AddProperty(new WzStringProperty("_inlink", "linkNoiseTarget"));
        Add(linker);
        SetCap(10_000);

        foreach (var name in new[] { "linkNoiseLinker", "linkNoiseTarget" })
        {
            var r = _image.ResolveCanvasLink(Cat, Img, name);
            MarkdownTestHelper.AssertSuccess(r);
            Assert.Null(MarkdownTestHelper.GetFirstValue(r, "base64_png"));
            Assert.Contains("Truncated", r);
        }

        SetCap(1_000_000);
        Assert.NotNull(MarkdownTestHelper.GetFirstValue(_image.ResolveCanvasLink(Cat, Img, "linkNoiseLinker"), "base64_png"));
    }

    [Fact]
    public void GetAnimationFrames_TotalCap_KeepsEarlyFramesAndOmitsTheRestWithANote()
    {
        var sub = new WzSubProperty("noiseAnim");
        for (int i = 0; i < 4; i++) sub.AddProperty(NoiseCanvas(i.ToString(), 100, 100, 10 + i));   // each about 53,000 base64 characters
        _fixture.Session.GetImage(Cat, Img).AddProperty(sub);

        SetCap(120_000);   // room for two frames, not for three
        var r = _image.GetAnimationFrames(Cat, Img, "noiseAnim", metadataOnly: false, limit: 4);
        MarkdownTestHelper.AssertSuccess(r);
        Assert.Equal(2, MarkdownTestHelper.GetAllValues(r, "base64_png").Count);
        Assert.Contains("Truncated", r);
        Assert.Contains("view_canvas", r);
        Assert.True(r.Length < 120_000 + 5_000, $"response was {r.Length} characters");

        SetCap(1_000_000);
        var all = _image.GetAnimationFrames(Cat, Img, "noiseAnim", metadataOnly: false, limit: 4);
        Assert.Equal(4, MarkdownTestHelper.GetAllValues(all, "base64_png").Count);
        Assert.DoesNotContain("Truncated", all);

        var meta = _image.GetAnimationFrames(Cat, Img, "noiseAnim", metadataOnly: true, limit: 4);
        Assert.Empty(MarkdownTestHelper.GetAllValues(meta, "base64_png"));
        Assert.DoesNotContain("Truncated", meta);
    }

    [Fact]
    public void ViewCanvas_ScalesDownFurtherToFitTheCap()
    {
        Add(NoiseCanvas("noiseView", 1000, 1000, 4));   // about 4 MB of PNG at full size
        SetCap(300_000);

        var response = _image.ViewCanvas(Cat, Img, "noiseView");
        Assert.NotEqual(true, response.IsError);
        var data = response.Content[1].Data!;
        Assert.True(data.Length <= 300_000, $"image block was {data.Length} base64 characters");
        var (w, h) = CanvasImaging.ReadAndCheckPngHeader(Convert.FromBase64String(data));
        Assert.True(w < 1000 && h < 1000);
        Assert.Contains("response size limit", response.Content[0].Text);
        Assert.Contains("original size is 1000x1000", response.Content[0].Text);
    }

    [Fact]
    public void ViewCanvas_RefusesWhenEvenATinyVersionDoesNotFit()
    {
        Add(NoiseCanvas("noiseTiny", 300, 300, 5));
        SetCap(64);
        var response = _image.ViewCanvas(Cat, Img, "noiseTiny");
        Assert.Equal(true, response.IsError);
        Assert.Contains("size limit", response.Content[0].Text);
        Assert.Contains("export_png", response.Content[0].Text);
    }

    [Fact]
    public void ViewCanvas_WithinTheCap_IsNotChanged()
    {
        var response = _image.ViewCanvas(Cat, Img, "testCanvas");
        Assert.NotEqual(true, response.IsError);
        Assert.DoesNotContain("response size limit", response.Content[0].Text);
        Assert.Equal((32, 32), CanvasImaging.ReadAndCheckPngHeader(Convert.FromBase64String(response.Content[1].Data!)));
    }
}
