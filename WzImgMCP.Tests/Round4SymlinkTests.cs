using System.Collections.Concurrent;
using System.Reflection;
using MapleLib.Helpers;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tests;

/// <summary>
/// Fourth review round: a symbolic link planted in a directory of the tree (not only at the file that is written)
/// must not carry a write out of it. Covers the check helper, the data-source save and its backup copy, and the
/// extractor's List directory.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class Round4SymlinkTests : IDisposable
{
    private const string Keep = "victim, must stay as it is";
    private readonly string _previousRoot = Environment.GetEnvironmentVariable("WZIMG_ALLOWED_ROOT") ?? "";
    private readonly string _sandbox = Directory.CreateTempSubdirectory("round4-symlink-").FullName;
    private readonly string _outside;
    private readonly string _victim;

    public Round4SymlinkTests()
    {
        Environment.SetEnvironmentVariable("WZIMG_ALLOWED_ROOT", null);     // backups are a sibling of the version directory
        _outside = Directory.CreateDirectory(Path.Combine(_sandbox, "outside")).FullName;
        _victim = Path.Combine(_sandbox, "victim.txt");
        File.WriteAllText(_victim, Keep);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WZIMG_ALLOWED_ROOT", string.IsNullOrEmpty(_previousRoot) ? null : _previousRoot);
        try { Directory.Delete(_sandbox, true); } catch { }
    }

    private static bool TryDirLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    private static bool TryFileLink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    private void AssertVictimUntouched() => Assert.Equal(Keep, File.ReadAllText(_victim));
    private void AssertOutsideEmpty() => Assert.Empty(Directory.GetFileSystemEntries(_outside));

    private static WzImage NewImage(string value)
    {
        var img = new WzImage("x.img") { Changed = true };
        img.AddProperty(new WzStringProperty("name", value));
        return img;
    }

    // ---- NoFollowFile.RefuseSymbolicLinksBelow --------------------------------------------------------------

    [Fact]
    public void RefuseSymbolicLinksBelow_AcceptsPlainAndMissingParts()
    {
        var root = Directory.CreateDirectory(Path.Combine(_sandbox, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(root, "a", "b", "file.img"));
        NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(root, "not", "there", "yet.img"));
        NoFollowFile.RefuseSymbolicLinksBelow(root, root);
    }

    [Fact]
    public void RefuseSymbolicLinksBelow_RefusesAPathThatIsNotBelowTheRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_sandbox, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(_sandbox, "rootx"));
        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(_sandbox, "rootx", "f.img")));
        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(root, "..", "outside", "f.img")));
    }

    [SkippableFact]
    public void RefuseSymbolicLinksBelow_RefusesALinkAnywhereOnTheWay()
    {
        var root = Directory.CreateDirectory(Path.Combine(_sandbox, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Skip.IfNot(TryDirLink(Path.Combine(root, "dirlink"), _outside), "symbolic links cannot be created here");
        Assert.True(TryFileLink(Path.Combine(root, "real", "filelink.img"), _victim));
        Assert.True(TryFileLink(Path.Combine(root, "real", "dangling.img"), Path.Combine(_sandbox, "nothing-here")));

        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(root, "dirlink")));
        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(root, "dirlink", "deeper", "f.img")));
        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(root, "real", "filelink.img")));
        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(root, Path.Combine(root, "real", "dangling.img")));
    }

    [SkippableFact]
    public void RefuseSymbolicLinksBelow_RefusesARootThatIsALink()
    {
        var link = Path.Combine(_sandbox, "rootlink");
        Skip.IfNot(TryDirLink(link, _outside), "symbolic links cannot be created here");
        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(link, Path.Combine(link, "f.img")));
    }

    // ---- ImgFileSystemManager.SaveImageToFile ---------------------------------------------------------------

    private ImgFileSystemManager NewManager(out string version)
    {
        version = Directory.CreateDirectory(Path.Combine(_sandbox, "v83")).FullName;
        Directory.CreateDirectory(Path.Combine(version, "Map"));
        return new ImgFileSystemManager(version);
    }

    [Fact]
    public void SaveImage_PlainTree_SavesAndBacksUp()
    {
        var manager = NewManager(out var version);
        var target = Path.Combine(version, "Map", "x.img");
        Assert.True(manager.SaveImageToFile(NewImage("one"), target));
        Assert.True(manager.SaveImageToFile(NewImage("two"), target));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_sandbox, "Backups"), "*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public void SaveImage_RefusesACategoryDirectoryThatIsALink()
    {
        var manager = NewManager(out var version);
        Directory.Delete(Path.Combine(version, "Map"));
        Skip.IfNot(TryDirLink(Path.Combine(version, "Map"), _outside), "symbolic links cannot be created here");

        Assert.False(manager.SaveImageToFile(NewImage("x"), Path.Combine(version, "Map", "x.img")));
        AssertOutsideEmpty();
    }

    [SkippableFact]
    public void SaveImage_RefusesALinkAtTheImageFile_AndDoesNotCopyItsTargetIntoTheBackups()
    {
        var manager = NewManager(out var version);
        Skip.IfNot(TryFileLink(Path.Combine(version, "Map", "y.img"), _victim), "symbolic links cannot be created here");

        Assert.False(manager.SaveImageToFile(NewImage("x"), Path.Combine(version, "Map", "y.img")));
        AssertVictimUntouched();
        Assert.True(new FileInfo(Path.Combine(version, "Map", "y.img")).LinkTarget != null);     // not replaced by a file
        Assert.False(Directory.Exists(Path.Combine(_sandbox, "Backups")));
    }

    [SkippableFact]
    public void SaveImage_RefusesABackupsDirectoryThatIsALink_AndKeepsTheImage()
    {
        var manager = NewManager(out var version);
        var target = Path.Combine(version, "Map", "x.img");
        Assert.True(manager.SaveImageToFile(NewImage("one"), target));
        var before = File.ReadAllBytes(target);
        Skip.IfNot(TryDirLink(Path.Combine(_sandbox, "Backups"), _outside), "symbolic links cannot be created here");

        Assert.False(manager.SaveImageToFile(NewImage("two"), target));
        AssertOutsideEmpty();
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(Path.Combine(version, "Map"), "*.tmp"));
    }

    [SkippableFact]
    public void SaveImage_DoesNotAcceptADirectoryThatDiffersFromTheVersionDirectoryOnlyInCase()
    {
        Skip.If(OperatingSystem.IsWindows(), "paths are compared ignoring case on Windows");
        var manager = NewManager(out var version);
        var sibling = Path.Combine(_sandbox, "V83");                 // a different directory on a case-sensitive file system
        Skip.IfNot(TryDirLink(sibling, _outside), "symbolic links cannot be created here");
        Directory.CreateDirectory(Path.Combine(_outside, "Map"));

        Assert.Throws<IOException>(() => NoFollowFile.RefuseSymbolicLinksBelow(version, Path.Combine(sibling, "Map", "x.img")));
        Assert.False(manager.SaveImageToFile(NewImage("x"), Path.Combine(sibling, "Map", "x.img")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_outside, "Map")));
    }

    [SkippableFact]
    public void SaveImage_RefusesADanglingLinkAtACategoryDirectory()
    {
        var manager = NewManager(out var version);
        Directory.Delete(Path.Combine(version, "Map"));
        Skip.IfNot(TryDirLink(Path.Combine(version, "Map"), Path.Combine(_sandbox, "nothing-here")), "symbolic links cannot be created here");

        Assert.False(manager.SaveImageToFile(NewImage("x"), Path.Combine(version, "Map", "x.img")));
        AssertOutsideEmpty();
    }

    [SkippableFact]
    public void SaveImage_WithAllowedRoot_RefusesALinkInsideTheBackupsDirectory()
    {
        var manager = NewManager(out var version);
        Environment.SetEnvironmentVariable("WZIMG_ALLOWED_ROOT", _sandbox);      // backups are <version>/Backups now
        var target = Path.Combine(version, "Map", "x.img");
        Assert.True(manager.SaveImageToFile(NewImage("one"), target));
        Assert.True(manager.SaveImageToFile(NewImage("two"), target));            // makes Backups/IMG/v83/Map
        var inner = Path.Combine(version, "Backups", "IMG", "v83", "Map");
        Assert.True(Directory.Exists(inner));
        Directory.Delete(inner, true);
        Skip.IfNot(TryDirLink(inner, _outside), "symbolic links cannot be created here");
        var before = File.ReadAllBytes(target);

        Assert.False(manager.SaveImageToFile(NewImage("three"), target));
        AssertOutsideEmpty();
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    // ---- WzExtractionService: List/List.json ----------------------------------------------------------------

    private static void WriteListJson(string outputVersionPath)
    {
        var method = typeof(WzExtractionService).GetMethod("WriteFilteredListJson", BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("WriteFilteredListJson not found");
        try
        {
            method.Invoke(new WzExtractionService(), new object[] { outputVersionPath, new HashSet<string> { "a.img" }, new ConcurrentDictionary<string, byte>() });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    [SkippableFact]
    public void ListJson_IsNotWrittenThroughADanglingListLink()
    {
        var outDir = Directory.CreateDirectory(Path.Combine(_sandbox, "extracted")).FullName;
        var gone = Path.Combine(_sandbox, "nothing-here");
        Skip.IfNot(TryDirLink(Path.Combine(outDir, "List"), gone), "symbolic links cannot be created here");

        Assert.Throws<IOException>(() => WriteListJson(outDir));
        Assert.False(Directory.Exists(gone));
        Assert.False(File.Exists(gone));
    }

    [Fact]
    public void ListJson_IsWrittenInAPlainTree()
    {
        var outDir = Directory.CreateDirectory(Path.Combine(_sandbox, "extracted")).FullName;
        WriteListJson(outDir);
        var file = Path.Combine(outDir, "List", "List.json");
        Assert.True(File.Exists(file));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("ListWz", doc.RootElement.GetProperty("Type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("OriginalCount").GetInt32());
        Assert.Equal("a.img", doc.RootElement.GetProperty("Entries")[0].GetString());
        Assert.Equal("a.img", doc.RootElement.GetProperty("RemainingEntries")[0].GetString());
    }

    [SkippableFact]
    public void ListJson_IsNotWrittenThroughAListDirectoryThatIsALink()
    {
        var outDir = Directory.CreateDirectory(Path.Combine(_sandbox, "extracted")).FullName;
        Skip.IfNot(TryDirLink(Path.Combine(outDir, "List"), _outside), "symbolic links cannot be created here");

        Assert.Throws<IOException>(() => WriteListJson(outDir));
        AssertOutsideEmpty();
    }
}
