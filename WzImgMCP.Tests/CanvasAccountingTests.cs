using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;
using Xunit.Abstractions;

namespace WzImgMCP.Tests;

/// <summary>
/// What the cache counts for an image is measured once, when the image is added. A canvas bitmap or a sound that a later tool call
/// decodes and the image keeps would then be missing from the count. These tests measure which tools make a cached image grow.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class CanvasAccountingTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Directory.CreateTempSubdirectory("wzimg-canvas-").FullName;
    private string Out => Path.Combine(_root, "exported");   // inside the allowed root, created by the tools
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);

    public CanvasAccountingTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(Path.Combine(_root, "Map"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"version\":\"1\"}");
        var img = new WzImage("a.img") { Changed = true };
        var anim = new WzSubProperty("anim");
        for (int i = 0; i < 4; i++)
        {
            var canvas = new WzCanvasProperty(i.ToString()) { PngProperty = new WzPngProperty() };
            var pixels = new byte[256 * 256 * 4];
            new Random(i).NextBytes(pixels);
            canvas.PngProperty.SetPixelsBgra32(pixels, 256, 256, SurfaceFormat.Bgra32);
            canvas.AddProperty(new WzVectorProperty("origin", new WzIntProperty("X", 1), new WzIntProperty("Y", 2)));
            canvas.AddProperty(new WzIntProperty("delay", 100));
            anim.AddProperty(canvas);
        }
        img.AddProperty(anim);
        string wav = Path.Combine(_root, "tone.wav");
        File.WriteAllBytes(wav, Wav(22050));
        img.AddProperty(new WzBinaryProperty("snd", wav));
        using var fs = File.Create(Path.Combine(_root, "Map", "a.img"));
        using var writer = new WzBinaryWriter(fs, WzTool.GetIvByMapleVersion(WzMapleVersion.BMS));
        img.SaveImage(writer, true);
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
    }

    /// <summary>A mono 16 bit PCM WAV file of <paramref name="samples"/> samples (about 44 KB of data for 22,050).</summary>
    private static byte[] Wav(int samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(22050); w.Write(44100); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        var data = new byte[dataLen];
        new Random(7).NextBytes(data);
        w.Write(data);
        return ms.ToArray();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void ReadingACanvasOrASound_DoesNotMakeACachedImageGrow()
    {
        // The cache counts an image's estimated size once, when it is added. That is right only while the tools that decode a canvas
        // or read a sound do not leave the payload in the image. They read with saveInMemory=false, so nothing stays; this test
        // measures it for every canvas, export and sound tool and fails the day one of them starts to keep a payload (then the cache
        // would have to re-estimate the image after the call).
        using var session = new WzSessionManager();
        session.InitDataSource(_root);
        var image = session.GetImage("Map", "a.img");
        long start = ImgFileSystemManager.EstimateWzImageSize(image);
        Assert.True(start > 0);
        var images = new ImageTools(session);
        var export = new ExportTools(session);
        var audio = new AudioTools(session);

        void Step(string name, Func<string> call)
        {
            var result = call();
            MarkdownTestHelper.AssertSuccess(result);
            long growth = ImgFileSystemManager.EstimateWzImageSize(image) - start;
            _output.WriteLine($"{name}: {growth:+#;-#;0} bytes");
            Assert.True(growth == 0, $"{name} left {growth} bytes in the cached image");
        }

        Step("get_canvas_bitmap", () => images.GetCanvasBitmap("Map", "a.img", "anim/0"));
        Step("get_animation_frames", () => images.GetAnimationFrames("Map", "a.img", "anim", metadataOnly: false, limit: 4));
        Step("resolve_canvas_link", () => images.ResolveCanvasLink("Map", "a.img", "anim/2"));
        Step("export_png", () => export.ExportPng("Map", "a.img", "anim/3", Path.Combine(Out, "o.png")));
        Step("export_all_images", () => export.ExportAllImages("Map", "a.img", Path.Combine(Out, "all")));
        Step("get_sound_info", () => audio.GetSoundInfo("Map", "a.img", "snd"));
        Step("get_sound_data", () => audio.GetSoundData("Map", "a.img", "snd"));
        Step("export_mp3", () => export.ExportMp3("Map", "a.img", "snd", Path.Combine(Out, "s.mp3")));
        Step("export_all_sounds", () => export.ExportAllSounds("Map", "a.img", Path.Combine(Out, "snd")));

        var view = images.ViewCanvas("Map", "a.img", "anim/1");     // returns an image content block
        Assert.NotNull(view);
        Assert.Equal(0, ImgFileSystemManager.EstimateWzImageSize(image) - start);
    }

    [Fact]
    public void PositiveControl_AKeptPayloadIsSeenByTheEstimate()
    {
        // Without this the test above could pass for the wrong reason: the estimate does count a payload that an image keeps.
        using var session = new WzSessionManager();
        session.InitDataSource(_root);
        var image = session.GetImage("Map", "a.img");
        long start = ImgFileSystemManager.EstimateWzImageSize(image);
        var png = ((WzCanvasProperty)image.GetFromPath("anim/0")!).PngProperty;

        Assert.NotNull(png.GetCompressedBytes(saveInMemory: true));
        long afterBytes = ImgFileSystemManager.EstimateWzImageSize(image);
        Assert.True(afterBytes > start + 1000);                 // the compressed bytes are counted

        // The decoded System.Drawing bitmap (width x height x 4 bytes, also counted by the estimate) cannot exist in this build: it
        // needs GDI+, which the Linux build does not have, and the tools use the raw pixel API instead.
    }
}
