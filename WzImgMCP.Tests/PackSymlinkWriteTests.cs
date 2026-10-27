using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// Every file the packer writes (the .wz files, the scratch file next to each of them, the .ini files, the _Canvas files,
/// List.wz) must never be written through a symbolic link planted in the output tree: the file the link points to, outside
/// the allowed root, stays byte for byte as it was. Checked on the packer itself (no path guard: the writes must not
/// follow a link by themselves) and through pack_to_wz (the guard refuses the link before anything is written).
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackSymlinkWriteTests : IDisposable
{
    private const string Keep = "outside file, must stay as it is";

    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _sandbox = Directory.CreateTempSubdirectory("pack-symlink-").FullName;
    private readonly string _root;
    private readonly string _outside;
    private readonly string _img;
    private readonly string _victim;

    public PackSymlinkWriteTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_sandbox, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_sandbox, "outside")).FullName;
        _img = Directory.CreateDirectory(Path.Combine(_root, "img")).FullName;
        _victim = Path.Combine(_outside, "victim.bin");
        File.WriteAllText(_victim, Keep);
        WriteMapImage(Path.Combine(_img, "Map"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_sandbox, true); } catch { }
    }

    /// <summary>One IMG file with a string and a 200 x 200 canvas (160 kB: large enough for separateCanvas to move it to _Canvas).</summary>
    private static void WriteMapImage(string categoryDir)
    {
        Directory.CreateDirectory(categoryDir);
        var img = new WzImage("Big.img") { Changed = true };
        img.AddProperty(new WzStringProperty("name", "value"));
        var canvas = new WzCanvasProperty("canvas") { PngProperty = new WzPngProperty() };
        var px = new byte[200 * 200 * 4];
        new Random(5).NextBytes(px);
        canvas.PngProperty.SetPixelsBgra32(px, 200, 200, SurfaceFormat.Bgra32);
        img.AddProperty(canvas);
        new WzImgSerializer(WzTool.GetIvByMapleVersion(WzMapleVersion.GMS)).SerializeImage(img, Path.Combine(categoryDir, "Big.img"));
    }

    private static bool TrySymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    /// <summary>Plants the link (creating its directory first), and returns false when links cannot be created here.</summary>
    private bool Plant(string outDir, string relative)
    {
        var link = Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        return TrySymlink(link, _victim);
    }

    private void AssertVictimUntouched()
    {
        Assert.Equal(Keep, File.ReadAllText(_victim));
        Assert.Equal(new[] { "victim.bin" }, Directory.GetFileSystemEntries(_outside, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
    }

    /// <summary>saveAs64Bit, separateCanvas, the path of the planted link relative to the output directory.</summary>
    public static IEnumerable<object[]> Links() => new[]
    {
        new object[] { false, false, "Map.wz" },
        new object[] { false, false, "Map.TEMP" },                       // the scratch file SaveToDisk used to create (32 bit)
        new object[] { true, false, "Data/Map/Map.ini" },                // LastWzIndex|n
        new object[] { true, false, "Data/Map/Map_000.wz" },
        new object[] { true, false, "Data/Map/Map_000.TEMP" },           // the scratch file SaveToDisk used to create (64 bit)
        new object[] { true, true, "Data/Map/_Canvas/_Canvas_000.wz" },
        new object[] { true, true, "Data/Map/_Canvas/_Canvas_000.TEMP" },
        new object[] { true, true, "Data/Map/_Canvas/Canvas.ini" },
    };

    [SkippableTheory]
    [MemberData(nameof(Links))]
    public async Task Packer_NeverWritesThroughASymlinkInTheOutputTree(bool saveAs64Bit, bool separateCanvas, string link)
    {
        var outDir = Path.Combine(_sandbox, "packer-out");
        Skip.IfNot(Plant(outDir, link), "symbolic links cannot be created here");

        var result = await new WzPackingService().PackCategoryAsync(
            _img, outDir, "Map", WzMapleVersion.GMS, 83, saveAs64Bit, CancellationToken.None, null, separateCanvas);

        AssertVictimUntouched();
        if (!link.EndsWith(".TEMP"))   // a file name the packer writes: the write is refused and reported
            Assert.True(!result.Success || result.Errors.Count > 0, "the refused write must be reported");
    }

    [SkippableTheory]
    [MemberData(nameof(Links))]
    public void PackToWz_NeverWritesThroughASymlinkInTheOutputTree(bool saveAs64Bit, bool separateCanvas, string link)
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
        var outDir = Path.Combine(_root, "tool-out");
        Skip.IfNot(Plant(outDir, link), "symbolic links cannot be created here");

        var r = new BatchTools(new WzSessionManager()).PackToWz(_img, outDir, 83, "Map", "GMS", saveAs64Bit, separateCanvas);

        AssertVictimUntouched();
        if (!link.EndsWith(".TEMP"))
        {
            // refused up front: no .wz, .ini or scratch file was written
            MarkdownTestHelper.AssertFailure(r);
            Assert.Contains("outside allowed root", r);
            Assert.Empty(Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Where(f => new FileInfo(f).LinkTarget == null));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PackToWz_PositiveControl_WithoutLinksTheSameTreeIsPacked(bool saveAs64Bit, bool separateCanvas)
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
        var outDir = Path.Combine(_root, "plain-out");

        var r = new BatchTools(new WzSessionManager()).PackToWz(_img, outDir, 83, "Map", "GMS", saveAs64Bit, separateCanvas);

        // (pack_to_wz counts only top-level .wz files as created, so in 64 bit mode its own success flag is not
        // meaningful here; what matters is exactly which files exist afterwards)
        if (!saveAs64Bit) MarkdownTestHelper.AssertSuccess(r);
        var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(outDir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (!saveAs64Bit) Assert.Equal(new[] { "Map.wz" }, files);
        else if (!separateCanvas) Assert.Equal(new[] { "Data/Map/Map.ini", "Data/Map/Map_000.wz" }, files);
        else Assert.Equal(new[] { "Data/Map/Map.ini", "Data/Map/Map_000.wz", "Data/Map/_Canvas/Canvas.ini", "Data/Map/_Canvas/_Canvas_000.wz" }, files);
    }
}
