using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// The tools that write many files (batch_export_images, export_all_images, export_all_sounds) must never write or
/// create anything outside WZIMG_ALLOWED_ROOT, whatever the format, category or canvas names contain.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ExportPathSafetyTests : IClassFixture<TestFixture>, IDisposable
{
    private const string Cat = "Character";
    private const string Img = "Test.img";

    private readonly TestFixture _fixture;
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _sandbox = Directory.CreateTempSubdirectory("export-safety-").FullName;
    private readonly string _root;
    private readonly string _outside;
    private readonly BatchTools _batch;
    private readonly ExportTools _export;

    public ExportPathSafetyTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        _root = Directory.CreateDirectory(Path.Combine(_sandbox, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_sandbox, "outside")).FullName;
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
        _batch = new BatchTools(fixture.Session);
        _export = new ExportTools(fixture.Session);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_sandbox, true); } catch { }
    }

    private static WzCanvasProperty Canvas(string name)
    {
        var canvas = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 7);
        canvas.PngProperty.SetPixelsBgra32(px, 4, 4, SurfaceFormat.Bgra32);
        return canvas;
    }

    /// <summary>Every file and directory below the sandbox except the allowed root itself must not exist.</summary>
    private void AssertNothingOutsideRoot()
    {
        Assert.Empty(Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories));
        var strays = Directory.GetFileSystemEntries(_sandbox, "*", SearchOption.TopDirectoryOnly)
            .Where(e => e != _root && e != _outside).ToList();
        Assert.Empty(strays);
        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            Assert.StartsWith(_root + Path.DirectorySeparatorChar, file);
    }

    private const string SymlinkSkipReason = "symbolic links cannot be created here (platform or account does not allow them)";

    private static bool TrySymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    [Theory]
    [InlineData("png/../../../../../../../../../../../../tmp/evil")]
    [InlineData("png/../x")]
    [InlineData("../png")]
    [InlineData("gif")]
    [InlineData("")]
    [InlineData("png\0")]
    public void BatchExportImages_RefusesAnythingButPngJpgJpegAsFormat(string format)
    {
        var dir = Path.Combine(_root, "out");
        var r = _batch.BatchExportImages(Cat, dir, format);
        MarkdownTestHelper.AssertFailure(r);
        Assert.False(Directory.Exists(dir), "the output directory must not even be created");
        AssertNothingOutsideRoot();
    }

    [Fact]
    public void BatchExportImages_FormatTraversalAimedAtAnAbsolutePathOutsideTheRoot_WritesNothing()
    {
        var format = "png/" + string.Concat(Enumerable.Repeat("../", 12)) + _outside.TrimStart('/') + "/evil";
        var r = _batch.BatchExportImages(Cat, Path.Combine(_root, "out"), format);
        MarkdownTestHelper.AssertFailure(r);
        AssertNothingOutsideRoot();
    }

    [Theory]
    [InlineData("PNG", "png")]
    [InlineData("Jpg", "jpg")]
    [InlineData("JPEG", "jpg")]
    [InlineData(" png ", "png")]
    public void BatchExportImages_AcceptsFormatsCaseInsensitively_AndNormalisesTheExtension(string format, string extension)
    {
        var dir = Path.Combine(_root, "out-" + extension);
        MarkdownTestHelper.AssertSuccess(_batch.BatchExportImages(Cat, dir, format));
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.EndsWith("." + extension, f));
        AssertNothingOutsideRoot();
    }

    [Theory]
    [InlineData("ABSOLUTE")]      // replaced by an absolute path inside the test
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("Character/../../x")]
    [InlineData("Character\\..\\x")]
    [InlineData("C:evil")]
    [InlineData("Character,../x")]
    [InlineData("Char\u0001acter")]
    public void BatchExportImages_RefusesCategoriesThatAreNotASinglePlainName(string category)
    {
        if (category == "ABSOLUTE") category = Path.Combine(_outside, "x");
        var dir = Path.Combine(_root, "out");
        var r = _batch.BatchExportImages(category, dir);
        MarkdownTestHelper.AssertFailure(r);
        AssertNothingOutsideRoot();
        Assert.False(Directory.Exists(Path.Combine(_root, "x")));
        Assert.False(Directory.Exists(Path.Combine(_sandbox, "x")));
    }

    [SkippableFact]
    public void BatchExportImages_RefusesASymlinkInsideTheRootThatLeadsOutside()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        Skip.IfNot(TrySymlink(Path.Combine(dir, Cat), _outside), SymlinkSkipReason);

        var r = _batch.BatchExportImages(Cat, dir);
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("outside allowed root", r);
        Assert.Empty(Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public void BatchExportImages_RefusesASymlinkedImageDirectory()
    {
        var categoryDir = Directory.CreateDirectory(Path.Combine(_root, "out", Cat)).FullName;
        Skip.IfNot(TrySymlink(Path.Combine(categoryDir, "Test"), _outside), SymlinkSkipReason);

        MarkdownTestHelper.AssertFailure(_batch.BatchExportImages(Cat, Path.Combine(_root, "out")));
        Assert.Empty(Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void BatchExportImages_CanvasNamesWithDotsAndSeparators_StayInsideAndStayUnique()
    {
        var image = _fixture.Session.GetImage(Cat, Img);
        var sub = new WzSubProperty("evilNames");
        foreach (var name in new[] { "../../../escape", "..\\..\\escape2", "a/../b", "/abs/path", "dup/x", "dup_x", "C:\\win", "ctl\u0001name", ".." })
            sub.AddProperty(Canvas(name));
        image.AddProperty(sub);

        var dir = Path.Combine(_root, "out");
        var r = _batch.BatchExportImages(Cat, dir);
        MarkdownTestHelper.AssertSuccess(r);

        var files = Directory.GetFiles(Path.Combine(dir, Cat, "Test"), "evilNames_*", SearchOption.TopDirectoryOnly);
        Assert.Equal(9, files.Length);                       // no canvas overwrote another one
        Assert.Equal(files.Length, files.Select(f => f.ToLowerInvariant()).Distinct().Count());
        Assert.All(files, f => Assert.DoesNotContain("..", Path.GetFileName(f)));
        AssertNothingOutsideRoot();
        Assert.Equal(MarkdownTestHelper.GetFirstInt(r, "exported_count"), Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void ExportAllImages_CanvasNamesWithDotsAndSeparators_StayInsideAndStayUnique()
    {
        var image = _fixture.Session.GetImage(Cat, Img);
        var sub = new WzSubProperty("evilAll");
        foreach (var name in new[] { "../../../escape", "..\\..\\escape2", "dup/x", "dup_x", "C:\\win" })
            sub.AddProperty(Canvas(name));
        image.AddProperty(sub);

        var dir = Path.Combine(_root, "all");
        var r = _export.ExportAllImages(Cat, Img, dir, "evilAll");
        MarkdownTestHelper.AssertSuccess(r);
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.Equal(5, files.Length);
        Assert.All(files, f => Assert.Equal(dir, Path.GetDirectoryName(f)));
        AssertNothingOutsideRoot();
    }

    [Fact]
    public void ExportAllImages_RefusesAnOutputDirectoryOutsideTheRoot()
    {
        MarkdownTestHelper.AssertFailure(_export.ExportAllImages(Cat, Img, Path.Combine(_outside, "x")));
        MarkdownTestHelper.AssertFailure(_export.ExportAllImages(Cat, Img, Path.Combine(_root, "..", "outside", "y")));
        Assert.Empty(Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public void ExportAllImages_RefusesAnOutputDirectorySymlinkedOutside()
    {
        var link = Path.Combine(_root, "link");
        Skip.IfNot(TrySymlink(link, _outside), SymlinkSkipReason);
        MarkdownTestHelper.AssertFailure(_export.ExportAllImages(Cat, Img, link));
        Assert.Empty(Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public void ExportAllImages_RefusesAFileSymlinkedOutside()
    {
        // The output file itself is a symlink to a file outside the root: writing through it must be refused.
        var dir = Directory.CreateDirectory(Path.Combine(_root, "linkfile")).FullName;
        var victim = Path.Combine(_outside, "victim.png");
        File.WriteAllText(victim, "keep");
        var fileLinkCreated = true;
        try { File.CreateSymbolicLink(Path.Combine(dir, "testCanvas.png"), victim); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { fileLinkCreated = false; }
        Skip.IfNot(fileLinkCreated, SymlinkSkipReason);

        MarkdownTestHelper.AssertFailure(_export.ExportAllImages(Cat, Img, dir));
        Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Theory]
    [InlineData("Character", true)]
    [InlineData("Map", true)]
    [InlineData("a.b", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a..b", false)]
    [InlineData("/tmp", false)]
    [InlineData("C:", false)]
    [InlineData("x\0y", false)]
    [InlineData("x\ny", false)]
    public void PathGuard_IsSafeComponent(string name, bool expected)
        => Assert.Equal(expected, PathGuard.IsSafeComponent(name, out _));

    [Theory]
    [InlineData("a/b", "a_b")]
    [InlineData("../../x", "______x")]
    [InlineData("C:\\win", "C__win")]
    [InlineData("", "unnamed")]
    [InlineData("..", "__")]
    [InlineData(".", "unnamed")]
    public void PathGuard_SanitizeFileName_ProducesOneSafeName(string input, string expected)
    {
        var s = PathGuard.SanitizeFileName(input);
        Assert.True(PathGuard.IsSafeComponent(s, out var why), why);
        Assert.Equal(expected, s);
    }
}
