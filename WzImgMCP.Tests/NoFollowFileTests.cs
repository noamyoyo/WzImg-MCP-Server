using MapleLib.Helpers;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tests;

/// <summary>
/// MapleLib writes (WzFile.SaveToDisk, ListFileParser.SaveToDisk and the helper they use) never follow a symbolic link
/// at the target: the data goes to a new random-named file in the same directory, which is renamed over the target.
/// </summary>
public sealed class NoFollowFileTests : IDisposable
{
    private const string Keep = "victim, must stay as it is";
    private readonly string _dir = Directory.CreateTempSubdirectory("nofollow-lib-").FullName;
    private readonly string _victim;

    public NoFollowFileTests()
    {
        _victim = Path.Combine(_dir, "victim.txt");
        File.WriteAllText(_victim, Keep);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static bool TrySymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    private static WzFile SmallWzFile()
    {
        var wz = new WzFile(83, WzMapleVersion.GMS) { Name = "Small.wz" };
        var img = new WzImage("a.img") { Changed = true };
        img.AddProperty(new WzStringProperty("name", "value"));
        wz.WzDirectory.AddImage(img);
        return wz;
    }

    private string[] Entries() => Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    private void AssertVictimUntouched() => Assert.Equal(Keep, File.ReadAllText(_victim));

    // ---- the helper

    [Fact]
    public void WriteAllText_CreatesAFile_AndLeavesNoTemporaryFileBehind()
    {
        var path = Path.Combine(_dir, "new.ini");
        NoFollowFile.WriteAllText(path, "LastWzIndex|0");
        Assert.Equal("LastWzIndex|0", File.ReadAllText(path));
        Assert.Equal(new[] { "new.ini", "victim.txt" }, Entries());
    }

    [Fact]
    public void WriteAllText_ReplacesAnExistingRegularFile()
    {
        var path = Path.Combine(_dir, "old.ini");
        File.WriteAllText(path, "a much longer old content than the new one");
        NoFollowFile.WriteAllText(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(new[] { "old.ini", "victim.txt" }, Entries());
    }

    [Fact]
    public void Write_WhenTheWriterFails_RemovesTheTemporaryFile_AndKeepsTheOldContent()
    {
        var path = Path.Combine(_dir, "keep.bin");
        File.WriteAllText(path, "old");
        Assert.Throws<InvalidOperationException>(() => NoFollowFile.Write(path, stream =>
        {
            stream.WriteByte(1);
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(new[] { "keep.bin", "victim.txt" }, Entries());
    }

    [SkippableFact]
    public void WriteAllText_RefusesASymlinkAtTheTarget_AndDoesNotWriteThroughIt()
    {
        var link = Path.Combine(_dir, "Map.ini");
        Skip.IfNot(TrySymlink(link, _victim), "symbolic links cannot be created here");
        Assert.Throws<IOException>(() => NoFollowFile.WriteAllText(link, "LastWzIndex|-1"));
        AssertVictimUntouched();
        Assert.Equal(new[] { "Map.ini", "victim.txt" }, Entries());
    }

    [SkippableFact]
    public void WriteAllText_RefusesADanglingSymlink_AndDoesNotCreateItsTarget()
    {
        var target = Path.Combine(_dir, "not-yet.txt");
        var link = Path.Combine(_dir, "dangling.ini");
        Skip.IfNot(TrySymlink(link, target), "symbolic links cannot be created here");
        Assert.Throws<IOException>(() => NoFollowFile.WriteAllText(link, "x"));
        Assert.False(File.Exists(target));
    }

    // ---- the writers that use it

    [SkippableFact]
    public void WzFileSaveToDisk_RefusesASymlinkAtTheTarget()
    {
        var link = Path.Combine(_dir, "Map.wz");
        Skip.IfNot(TrySymlink(link, _victim), "symbolic links cannot be created here");
        using var wz = SmallWzFile();
        Assert.ThrowsAny<IOException>(() => wz.SaveToDisk(link, false, WzMapleVersion.GMS));
        AssertVictimUntouched();
        Assert.Equal(new[] { "Map.wz", "victim.txt" }, Entries());
    }

    [SkippableFact]
    public void WzFileSaveToDisk_IgnoresASymlinkNamedLikeTheOldScratchFile_AndLeavesNoScratchFile()
    {
        var path = Path.Combine(_dir, "Map.wz");
        Skip.IfNot(TrySymlink(Path.Combine(_dir, "Map.TEMP"), _victim), "symbolic links cannot be created here");
        using var wz = SmallWzFile();
        foreach (var as64 in new[] { false, true })
        {
            wz.SaveToDisk(path, as64, WzMapleVersion.GMS);
            AssertVictimUntouched();
            Assert.True(new FileInfo(path).Length > 0);
        }
        Assert.Equal(new[] { "Map.TEMP", "Map.wz", "victim.txt" }, Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WzFileSaveToDisk_WritesAFileThatCanBeReadBack_AndLeavesNothingElse(bool as64)
    {
        var path = Path.Combine(_dir, "Small.wz");
        using (var wz = SmallWzFile())
            wz.SaveToDisk(path, as64, WzMapleVersion.GMS);
        Assert.Equal(new[] { "Small.wz", "victim.txt" }, Entries());

        using var read = new WzFile(path, WzMapleVersion.GMS);
        Assert.Equal(WzFileParseStatus.Success, read.ParseWzFile());
        Assert.Equal("value", ((WzStringProperty)read.WzDirectory.GetImageByName("a.img")!["name"]!).Value);
    }

    [SkippableFact]
    public void ListFileSaveToDisk_RefusesASymlinkAtTheTarget()
    {
        var link = Path.Combine(_dir, "List.wz");
        Skip.IfNot(TrySymlink(link, _victim), "symbolic links cannot be created here");
        Assert.Throws<IOException>(() => ListFileParser.SaveToDisk(link, WzMapleVersion.GMS, new List<string> { "a", "b" }));
        AssertVictimUntouched();
        Assert.Equal(new[] { "List.wz", "victim.txt" }, Entries());
    }

    [Fact]
    public void ListFileSaveToDisk_WritesAFileThatCanBeParsedBack()
    {
        var path = Path.Combine(_dir, "List.wz");
        ListFileParser.SaveToDisk(path, WzMapleVersion.GMS, new List<string> { "Map/a.img", "Map/b.img" });
        Assert.Equal(new[] { "List.wz", "victim.txt" }, Entries());
        Assert.Equal(new[] { "Map/a.img", "Map/b.img" }, ListFileParser.ParseListFile(path, WzMapleVersion.GMS).ToArray());
    }
}
