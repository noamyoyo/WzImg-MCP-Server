using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// pack_to_wz and extract_to_img build output (and input) file names from the category argument. Nothing may be
/// created or written outside WZIMG_ALLOWED_ROOT, whatever the category or the output directory contains.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackPathSafetyTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _sandbox = Directory.CreateTempSubdirectory("pack-safety-").FullName;
    private readonly string _root;
    private readonly string _outside;
    private readonly string _img;
    private readonly BatchTools _batch = new(new WzSessionManager());

    public PackPathSafetyTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_sandbox, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_sandbox, "outside")).FullName;
        _img = Directory.CreateDirectory(Path.Combine(_root, "img")).FullName;
        Directory.CreateDirectory(Path.Combine(_img, "Map"));
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_sandbox, true); } catch { }
    }

    private static bool TrySymlink(string link, string target, bool isDirectory)
    {
        try
        {
            if (isDirectory) Directory.CreateSymbolicLink(link, target); else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    /// <summary>Nothing in the sandbox except the allowed root and the (empty) outside directory, and nothing in outside.</summary>
    private void AssertNothingOutsideRoot()
    {
        Assert.Empty(Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories));
        var strays = Directory.GetFileSystemEntries(_sandbox, "*", SearchOption.TopDirectoryOnly)
            .Where(e => e != _root && e != _outside).ToList();
        Assert.Empty(strays);
    }

    [Theory]
    [InlineData("../img/Map")]          // the review's example: output dir /data, category escapes to a sibling of the root
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("Map,../x")]            // a second entry of the list
    [InlineData("Map, ../../outside/Map")]
    [InlineData("C:\\temp\\x")]
    [InlineData("ABSOLUTE")]            // replaced by an absolute path below
    public void PackToWz_RefusesACategoryThatIsNotOnePlainName(string category)
    {
        if (category == "ABSOLUTE") category = Path.Combine(_outside, "Map");
        foreach (var saveAs64Bit in new[] { false, true })
        {
            var r = _batch.PackToWz(_img, _root, 83, category, "GMS", saveAs64Bit);
            MarkdownTestHelper.AssertFailure(r);
            Assert.Contains("category", r);
            AssertNothingOutsideRoot();
            Assert.False(File.Exists(Path.Combine(_root, "Map.wz")));
            Assert.False(Directory.Exists(Path.Combine(_root, "Data")));
        }
    }

    [Fact]
    public void PackToWz_RefusesATraversalCategory_ToANewOutputDirectory_WithoutCreatingIt()
    {
        var outDir = Path.Combine(_root, "not-yet");
        MarkdownTestHelper.AssertFailure(_batch.PackToWz(_img, outDir, 83, "../../outside/Map", "GMS", true));
        Assert.False(Directory.Exists(outDir));
        AssertNothingOutsideRoot();
    }

    [Fact]
    public void PackToWz_RefusesATraversalCategory_EvenWithoutAnAllowedRoot()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        var outDir = Path.Combine(_sandbox, "unrestricted-out");
        MarkdownTestHelper.AssertFailure(_batch.PackToWz(_img, outDir, 83, "../x", "GMS"));
        Assert.False(Directory.Exists(outDir));
        Assert.False(Directory.Exists(Path.Combine(_sandbox, "x")));
    }

    [Fact]
    public void PackToWz_RefusesAnOutputDirectoryOutsideTheRoot()
    {
        MarkdownTestHelper.AssertFailure(_batch.PackToWz(_img, Path.Combine(_outside, "o"), 83, "Map", "GMS"));
        AssertNothingOutsideRoot();
    }

    [Fact]
    public void PackToWz_RefusesASymlinkedOutputDirectory()
    {
        var link = Path.Combine(_root, "link");
        Skip.IfNot(TrySymlink(link, _outside, true), "symbolic links cannot be created here");
        MarkdownTestHelper.AssertFailure(_batch.PackToWz(_img, link, 83, "Map", "GMS"));
        AssertNothingOutsideRoot();
    }

    [Fact]
    public void PackToWz_RefusesASymlinkedWzFileInTheOutputDirectory()
    {
        var outDir = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var victim = Path.Combine(_outside, "victim.wz");
        File.WriteAllText(victim, "keep");
        Skip.IfNot(TrySymlink(Path.Combine(outDir, "Map.wz"), victim, false), "symbolic links cannot be created here");

        MarkdownTestHelper.AssertFailure(_batch.PackToWz(_img, outDir, 83, "Map", "GMS"));
        Assert.Equal("keep", File.ReadAllText(victim));
        Assert.Equal(new[] { "victim.wz" }, Directory.GetFileSystemEntries(_outside).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void PackToWz_RefusesASymlinkedDataDirectory_In64BitMode()
    {
        var outDir = Directory.CreateDirectory(Path.Combine(_root, "out64")).FullName;
        Skip.IfNot(TrySymlink(Path.Combine(outDir, "Data"), _outside, true), "symbolic links cannot be created here");

        MarkdownTestHelper.AssertFailure(_batch.PackToWz(_img, outDir, 83, "Map", "GMS", true));
        AssertNothingOutsideRoot();
    }

    [Fact]
    public void PackToWz_RefusesASymlinkedCategoryDirectoryBelowData_In64BitMode()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "out64b", "Data")).FullName;
        Skip.IfNot(TrySymlink(Path.Combine(data, "Map"), _outside, true), "symbolic links cannot be created here");

        MarkdownTestHelper.AssertFailure(_batch.PackToWz(_img, Path.Combine(_root, "out64b"), 83, "Map", "GMS", true));
        AssertNothingOutsideRoot();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("Map,../../outside/Map")]
    [InlineData("..")]
    public void ExtractToImg_RefusesACategoryThatIsNotOnePlainName(string categories)
    {
        var wzDir = Directory.CreateDirectory(Path.Combine(_root, "wz")).FullName;
        File.WriteAllText(Path.Combine(_sandbox, "x.wz"), "not a wz");
        var outDir = Path.Combine(_root, "extracted");

        var r = _batch.ExtractToImg(wzDir, outDir, "GMS", true, categories, "v83");
        MarkdownTestHelper.AssertFailure(r);
        Assert.Contains("category", r);
        Assert.False(Directory.Exists(outDir));
        Assert.Empty(Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("C:\\x")]
    [InlineData("")]
    [InlineData("..")]
    public async Task Packer_RefusesACategoryThatIsNotOnePlainName_OnItsOwn(string category)
    {
        var outDir = Path.Combine(_root, "packer-out");
        var result = await new WzPackingService().PackCategoryAsync(
            _img, outDir, category, WzMapleVersion.GMS, 83, saveAs64Bit: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.False(Directory.Exists(outDir));
        AssertNothingOutsideRoot();
    }
}
