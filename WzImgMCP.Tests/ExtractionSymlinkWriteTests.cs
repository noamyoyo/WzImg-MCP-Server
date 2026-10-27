using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tests;

/// <summary>
/// The other MapleLib writers the tools can reach (extract_to_img output, the manifest.json that opening a data source
/// can rewrite, the category index) do not write through a symbolic link at their target either.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ExtractionSymlinkWriteTests : IDisposable
{
    private const string Keep = "victim, must stay as it is";
    private readonly string _sandbox = Directory.CreateTempSubdirectory("extract-symlink-").FullName;
    private readonly string _victim;

    public ExtractionSymlinkWriteTests()
    {
        _victim = Path.Combine(_sandbox, "victim.txt");
        File.WriteAllText(_victim, Keep);
    }

    public void Dispose() { try { Directory.Delete(_sandbox, true); } catch { } }

    private static bool TrySymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    private void AssertVictimUntouched() => Assert.Equal(Keep, File.ReadAllText(_victim));

    [SkippableFact]
    public void SerializeImage_RefusesASymlinkAtTheTarget_AndWritesAPlainFileOtherwise()
    {
        var img = new WzImage("a.img") { Changed = true };
        img.AddProperty(new WzStringProperty("name", "value"));
        var serializer = new WzImgSerializer(WzTool.GetIvByMapleVersion(WzMapleVersion.GMS));

        var plain = Path.Combine(_sandbox, "plain.img");
        serializer.SerializeImage(img, plain);
        Assert.True(new FileInfo(plain).Length > 0);

        var link = Path.Combine(_sandbox, "linked.img");
        Skip.IfNot(TrySymlink(link, _victim), "symbolic links cannot be created here");
        Assert.Throws<IOException>(() => serializer.SerializeImage(img, link));
        AssertVictimUntouched();
        Assert.Equal(new[] { "linked.img", "plain.img", "victim.txt" }, Directory.GetFileSystemEntries(_sandbox).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [SkippableFact]
    public void CategoryIndexSave_RefusesASymlinkAtTheTarget()
    {
        var link = Path.Combine(_sandbox, "index.json");
        Skip.IfNot(TrySymlink(link, _victim), "symbolic links cannot be created here");
        Assert.Throws<IOException>(() => new CategoryIndex().Save(link));
        AssertVictimUntouched();
    }

    [SkippableFact]
    public void SaveVersionManifest_RefusesASymlinkAtTheTarget()
    {
        Skip.IfNot(TrySymlink(Path.Combine(_sandbox, "manifest.json"), _victim), "symbolic links cannot be created here");
        Assert.Throws<IOException>(() => new VersionManager(_sandbox).SaveVersionManifest(new VersionInfo { DirectoryPath = _sandbox }));
        AssertVictimUntouched();
    }

    [SkippableTheory]
    [InlineData("manifest.json")]
    [InlineData("String/Pet.img")]
    public void ExtractToImg_NeverWritesThroughASymlinkInTheOutputTree(string link)
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        var outDir = Path.Combine(_sandbox, "extracted");
        var linkPath = Path.Combine(outDir, link.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Skip.IfNot(TrySymlink(linkPath, _victim), "symbolic links cannot be created here");

        WzTestData.Extract(Path.Combine(WzTestData.WzDir!, "String.wz"), outDir, "GMS");

        AssertVictimUntouched();
        Assert.Equal(_victim, new FileInfo(linkPath).LinkTarget);   // still the link: it was not replaced by a file either
    }
}
