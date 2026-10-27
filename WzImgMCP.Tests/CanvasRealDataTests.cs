using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Imaging;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using Xunit.Abstractions;

namespace WzImgMCP.Tests;

/// <summary>
/// Tests on canvases of real client files. They need WZIMG_TEST_WZ_PATH to point at a directory that contains
/// String.wz and the canvas-bearing files named below (copies of the v83 GMS files); each test skips when its
/// file is missing.
/// </summary>
public class CanvasRealDataTests
{
    private readonly ITestOutputHelper _output;

    public CanvasRealDataTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Files => new[] { new object[] { "Morph.wz" }, new object[] { "Ui.wz" }, new object[] { "Etc.wz" } };

    private static string? Find(string file)
    {
        var dir = Environment.GetEnvironmentVariable(WzTestData.EnvVar);
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var path = Path.Combine(dir, file);
        return File.Exists(path) ? path : null;
    }

    private static IEnumerable<(string path, WzCanvasProperty canvas)> Canvases(WzObject root, string path)
    {
        if (root is WzImage image)
        {
            if (!image.ParseImage()) yield break;
            foreach (var p in image.WzProperties)
                foreach (var c in Canvases(p, path + "/" + p.Name)) yield return c;
        }
        else if (root is WzCanvasProperty canvas)
        {
            yield return (path, canvas);
            foreach (var p in canvas.WzProperties)
                foreach (var c in Canvases(p, path + "/" + p.Name)) yield return c;
        }
        else if (root is WzImageProperty prop && prop.WzProperties != null)
        {
            foreach (var p in prop.WzProperties)
                foreach (var c in Canvases(p, path + "/" + p.Name)) yield return c;
        }
    }

    private static IEnumerable<(string path, WzCanvasProperty canvas)> AllCanvases(WzDirectory dir, string path)
    {
        foreach (var image in dir.WzImages)
            foreach (var c in Canvases(image, path + "/" + image.Name)) yield return c;
        foreach (var sub in dir.WzDirectories)
            foreach (var c in AllCanvases(sub, path + "/" + sub.Name)) yield return c;
    }

    [SkippableTheory]
    [MemberData(nameof(Files))]
    public void Every_canvas_decodes_and_survives_an_8888_re_encode(string file)
    {
        var wzPath = Find(file);
        Skip.If(wzPath == null, $"{file} is not in {WzTestData.EnvVar}");

        using var wz = new WzFile(wzPath!, MapleLib.WzLib.WzMapleVersion.GMS);
        Assert.Equal(WzFileParseStatus.Success, wz.ParseWzFile());

        var formats = new SortedDictionary<string, int>();
        int total = 0, undecodable = 0;
        foreach (var (path, canvas) in AllCanvases(wz.WzDirectory, ""))
        {
            total++;
            var png = canvas.PngProperty;
            if (png == null) { undecodable++; continue; }
            formats[png.Format.ToString()] = formats.GetValueOrDefault(png.Format.ToString()) + 1;

            byte[]? pixels;
            int w = 0, h = 0;
            try { pixels = canvas.GetLinkedPixelsBgra32(out w, out h); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"{file}{path}: decode failed: {ex.Message}"); }
            if (pixels == null) { undecodable++; continue; }

            // Re-encode as exact BGRA8888 into a scratch property and compare.
            var copy = new WzPngProperty();
            copy.SetPixelsBgra32(pixels, w, h, Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra32);
            Assert.Equal(pixels, copy.GetPixelsBgra32(out _, out _));
        }

        _output.WriteLine($"{file}: {total} canvases, {undecodable} without image data; formats: " +
                          string.Join(", ", formats.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.True(total > 0);
        Assert.True(undecodable * 20 <= total, $"{undecodable} of {total} canvases could not be decoded");
    }

    [SkippableFact]
    public void Editing_a_real_canvas_saves_and_reloads_and_leaves_everything_else_alone()
    {
        var wzPath = Find("Morph.wz");
        Skip.If(wzPath == null, "Morph.wz is not in " + WzTestData.EnvVar);

        using var tree = new TempTree();
        MarkdownTestHelper.AssertSuccess(WzTestData.Extract(wzPath!, tree.Path, "GMS"));
        var before = Directory.GetFiles(tree.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));

        // First image directly under a category that holds at least two canvases.
        string category = "", imageName = "";
        List<string> canvasPaths = new();
        using (var probe = new WzSessionManager())
        {
            probe.InitDataSource(tree.Path);
            foreach (var file in Directory.GetFiles(tree.Path, "*.img", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var parts = Path.GetRelativePath(tree.Path, file).Split(Path.DirectorySeparatorChar);
                if (parts.Length != 2) continue;
                var found = Canvases(probe.GetImage(parts[0], parts[1]), "").Select(c => c.path.TrimStart('/')).ToList();
                if (found.Count < 2) continue;
                (category, imageName, canvasPaths) = (parts[0], parts[1], found);
                break;
            }
        }
        Assert.NotEmpty(canvasPaths);

        byte[] Pixels(WzSessionManager s, string path)
            => ((WzCanvasProperty)s.GetImage(category, imageName).GetFromPath(path)!).GetLinkedPixelsBgra32(out _, out _)!;

        string edited = canvasPaths[0], untouched = canvasPaths[1];
        var noise = new byte[17 * 9 * 4];
        new Random(5).NextBytes(noise);
        var source = new WzCanvasProperty("n") { PngProperty = new WzPngProperty() };
        source.PngProperty.SetPixelsBgra32(noise, 17, 9, Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra32);
        var pngFile = Path.Combine(tree.Path, "replacement.png");
        File.WriteAllBytes(pngFile, CanvasImaging.EncodeCanvas(source, ExportImageFormat.Png, out _, out _)!);

        byte[] untouchedBefore;
        using (var session = new WzSessionManager())
        {
            session.InitDataSource(tree.Path);
            untouchedBefore = Pixels(session, untouched);
            var tools = new ModifyTools(session);
            var env = Environment.GetEnvironmentVariable(WzImgMCP.Utils.PathGuard.EnvVar);
            try
            {
                Environment.SetEnvironmentVariable(WzImgMCP.Utils.PathGuard.EnvVar, tree.Path);
                MarkdownTestHelper.AssertSuccess(tools.SetCanvasBitmap(category, imageName, edited, null, pngFile, "bgra8888"));
                MarkdownTestHelper.AssertSuccess(tools.SaveImage(category, imageName));
            }
            finally
            {
                Environment.SetEnvironmentVariable(WzImgMCP.Utils.PathGuard.EnvVar, env);
            }
        }

        using var reloaded = new WzSessionManager();
        reloaded.InitDataSource(tree.Path);
        Assert.Equal(noise, Pixels(reloaded, edited));
        Assert.Equal(untouchedBefore, Pixels(reloaded, untouched));

        // Only the edited image file, its backup copy and the PNG this test added may differ from before.
        var changed = Directory.GetFiles(tree.Path, "*", SearchOption.AllDirectories)
            .Where(f => !before.ContainsKey(f) || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)).AsSpan().SequenceEqual(before[f]))
            .Select(f => Path.GetFileName(f)).OrderBy(f => f).ToList();
        Assert.Contains(imageName, changed);
        Assert.All(changed, f => Assert.True(f == imageName || f == "replacement.png" || f.Contains("_BAK_"), "unexpected change: " + f));
    }

    [SkippableFact]
    public void ListWzUsed_Setter_ReallyChangesTheLayout_AndKeepsFormatAndPixels()
    {
        var wzPath = Find("Morph.wz");
        Skip.If(wzPath == null, "Morph.wz is not in " + WzTestData.EnvVar);

        using var wz = new WzFile(wzPath!, MapleLib.WzLib.WzMapleVersion.GMS);
        Assert.Equal(WzFileParseStatus.Success, wz.ParseWzFile());

        int checkedCanvases = 0;
        foreach (var (path, canvas) in AllCanvases(wz.WzDirectory, ""))
        {
            var png = canvas.PngProperty;
            if (png == null) continue;
            var pixels = png.GetPixelsBgra32(out int w, out int h);
            if (pixels == null) continue;
            var format = png.Format;
            Assert.False(png.ListWzUsed);

            png.ListWzUsed = true;
            Assert.True(png.ListWzUsed);
            Assert.Equal(format, png.Format);
            Assert.Equal(pixels, png.GetPixelsBgra32(out _, out _));   // decoding through the list layout gives the same pixels
            Assert.True(png.ListWzUsed);                                 // and decoding does not undo the setting

            png.ListWzUsed = false;
            Assert.False(png.ListWzUsed);
            Assert.Equal(format, png.Format);
            Assert.Equal(pixels, png.GetPixelsBgra32(out _, out _));
            Assert.False(png.ListWzUsed);

            if (++checkedCanvases == 12) break;
        }
        Assert.True(checkedCanvases > 0);
    }

    [Fact]
    public void ListWzUsed_Setter_OnAPropertyWithoutPixels_JustSetsTheFlag()
    {
        var png = new WzPngProperty();
        png.ListWzUsed = true;
        Assert.True(png.ListWzUsed);
        png.ListWzUsed = false;
        Assert.False(png.ListWzUsed);
    }
}
