using System.Security.Cryptography;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// save_image writes to the file the image was loaded from, whatever spelling of the name the caller uses, never
/// creates folders or recreates removed files, reports failed writes, and images with unsaved edits are not evicted.
/// Every test gets its own temp tree.
/// </summary>
public sealed class SaveImageHardeningTests : IDisposable
{
    private const string Nested = "Map0/000000000.img";
    private readonly TestFixture _fixture = new();
    private ModifyTools? _tools;

    private string Root => _fixture.TestDataPath;
    private string MapFile(string sub, string name = "000000000.img") => Path.Combine(Root, "Map", sub, name);

    /// <summary>Opens the tree (call after any file layout changes that must be visible to the first scan).</summary>
    private ModifyTools Tools()
    {
        if (_tools == null)
        {
            _fixture.InitializeDataSource();
            _tools = new ModifyTools(_fixture.Session);
        }
        return _tools;
    }

    public void Dispose() => _fixture.Dispose();

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Reads a string property from the file on disk with a separate session (no shared cache).</summary>
    private string? ReadFromDisk(string category, string image, string property = "testString")
    {
        using var other = new WzSessionManager();
        other.InitDataSource(Root);
        var img = other.GetImage(category, image);
        return (img[property] as WzStringProperty)?.Value;
    }

    private static string Names(string dir) => string.Join(",", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(x => x));

    [Fact]
    public void SameFileNameInTwoSubdirectories_OnlyTheEditedOneChanges()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Map", "Map1"));
        File.Copy(MapFile("Map0"), MapFile("Map1"));
        var map1Before = Hash(MapFile("Map1"));
        var tools = Tools();

        MarkdownTestHelper.AssertSuccess(tools.SetString("Map", "Map0/000000000.img", "testString", "only map0"));
        MarkdownTestHelper.AssertSuccess(tools.SaveImage("Map", "Map0/000000000.img"));

        Assert.Equal(map1Before, Hash(MapFile("Map1")));
        Assert.Equal("only map0", ReadFromDisk("Map", "Map0/000000000.img"));
        Assert.Equal("Hello World", ReadFromDisk("Map", "Map1/000000000.img"));
        Assert.False(File.Exists(Path.Combine(Root, "Map", "000000000.img")));
    }

    [Theory]
    [InlineData("map0/000000000.img")]          // other capitalisation of the folder
    [InlineData("MAP0/000000000.IMG")]
    [InlineData("Map0/000000000")]              // no .img
    [InlineData("map0/000000000")]
    [InlineData("Map0/../Map0/000000000.img")]  // dot dot spelling
    [InlineData("Map0/./000000000.img")]
    public void SavingUnderAnotherSpelling_WritesTheOriginalFile_AndKeepsTheEdit(string saveName)
    {
        var tools = Tools();
        var mapDirBefore = Names(Path.Combine(Root, "Map"));
        var catBefore = Names(Root);

        MarkdownTestHelper.AssertSuccess(tools.SetString("Map", Nested, "testString", "edited " + saveName));
        MarkdownTestHelper.AssertSuccess(tools.SaveImage("Map", saveName));

        Assert.Equal(mapDirBefore, Names(Path.Combine(Root, "Map")));   // no new directory such as "map0"
        Assert.Equal(catBefore, Names(Root));
        Assert.Equal("edited " + saveName, ReadFromDisk("Map", Nested));
    }

    [Theory]
    [InlineData("Map0/000000000")]
    [InlineData("map0/000000000.img")]
    [InlineData("Map0/../Map0/000000000.img")]
    public void EditingUnderAnotherSpelling_AndSavingNormally_KeepsTheEdit(string editName)
    {
        var tools = Tools();
        _fixture.Session.GetImage("Map", Nested);   // load it once under its real name: other spellings then find it in the cache
        MarkdownTestHelper.AssertSuccess(tools.SetString("Map", editName, "testString", "via " + editName));
        MarkdownTestHelper.AssertSuccess(tools.SaveImage("Map", Nested));
        Assert.Equal("via " + editName, ReadFromDisk("Map", Nested));
    }

    [Fact]
    public void AllSpellingsShareOneCachedImage_AndSaveLeavesNoStaleEntry()
    {
        Tools();
        var session = _fixture.Session;
        var a = session.GetImage("Map", Nested);
        Assert.Same(a, session.GetImage("Map", "map0/000000000.img"));
        Assert.Same(a, session.GetImage("Map", "Map0/000000000"));
        Assert.Same(a, session.GetImage("Map", "Map0/../Map0/000000000.img"));

        var cachedBefore = session.DataSource.GetStats().CachedImageCount;
        a.AddProperty(new WzIntProperty("extra", 5));
        a.Changed = true;
        MarkdownTestHelper.AssertSuccess(new ModifyTools(session).SaveImage("Map", "Map0/000000000"));

        Assert.Equal(cachedBefore - 1, session.DataSource.GetStats().CachedImageCount);   // every key of the object is gone
        var fresh = session.GetImage("Map", Nested);
        Assert.NotSame(a, fresh);
        Assert.Equal(5, (fresh["extra"] as WzIntProperty)?.Value);
    }

    [Fact]
    public void NameEscapingTheVersionRoot_IsAnError_AndWritesNothing()
    {
        var tools = Tools();
        var parent = Path.GetDirectoryName(Root)!;
        var parentBefore = Names(parent);
        var rootBefore = Names(Root);

        var result = tools.SaveImage("Map", "../../outside.img");

        MarkdownTestHelper.AssertFailure(result);
        Assert.Equal(parentBefore, Names(parent));
        Assert.Equal(rootBefore, Names(Root));
    }

    [Fact]
    public void FileRemovedWhileLoaded_IsRefused_NothingIsRecreated()
    {
        var tools = Tools();
        MarkdownTestHelper.AssertSuccess(tools.SetString("Map", Nested, "testString", "orphan"));
        File.Delete(MapFile("Map0"));

        var result = tools.SaveImage("Map", Nested);

        MarkdownTestHelper.AssertFailure(result);
        Assert.Contains("no longer exists", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(MapFile("Map0")));
        Assert.False(File.Exists(Path.Combine(Root, "Map", "000000000.img")));
    }

    [Fact]
    public void FolderRemovedWhileLoaded_IsRefused_NoFolderIsCreated()
    {
        var tools = Tools();
        MarkdownTestHelper.AssertSuccess(tools.SetString("Map", Nested, "testString", "orphan"));
        Directory.Delete(Path.Combine(Root, "Map", "Map0"), recursive: true);

        MarkdownTestHelper.AssertFailure(tools.SaveImage("Map", Nested));
        Assert.False(Directory.Exists(Path.Combine(Root, "Map", "Map0")));
        Assert.False(Directory.Exists(Path.Combine(Root, "Map", "map0")));
    }

    [SkippableFact]
    public void ImageFileThatIsASymbolicLink_FailsTheSave_AndLeavesBothFilesUnchanged()
    {
        var target = Path.Combine(Root, "Map", "target.bin");
        File.Copy(MapFile("Map0"), target);
        File.Delete(MapFile("Map0"));
        try { File.CreateSymbolicLink(MapFile("Map0"), target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Skip.If(true, "symbolic links cannot be created here"); }
        var before = Hash(target);
        var tools = Tools();

        MarkdownTestHelper.AssertSuccess(tools.SetString("Map", Nested, "testString", "via link"));
        var result = tools.SaveImage("Map", Nested);

        MarkdownTestHelper.AssertFailure(result);
        Assert.Equal(before, Hash(target));
        Assert.True(new FileInfo(MapFile("Map0")).LinkTarget != null);   // still a link, not replaced by a file
    }

    [SkippableFact]
    public void ReadOnlyDirectory_FailsTheSave_AndLeavesTheFileUnchanged()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "uses POSIX permissions");
        var dir = Path.Combine(Root, "Map", "Map0");
        var before = Hash(MapFile("Map0"));
        var tools = Tools();
        MarkdownTestHelper.AssertSuccess(tools.SetString("Map", Nested, "testString", "ro"));
        var original = File.GetUnixFileMode(dir);
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            // root (and some CI containers) can write anyway: then there is nothing to test
            try { File.WriteAllText(Path.Combine(dir, "probe"), "x"); File.Delete(Path.Combine(dir, "probe")); Skip.If(true, "directory permissions are not enforced for this user"); }
            catch (UnauthorizedAccessException) { }

            var result = tools.SaveImage("Map", Nested);

            MarkdownTestHelper.AssertFailure(result);
            Assert.Equal(before, Hash(MapFile("Map0")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { File.SetUnixFileMode(dir, original); }
    }

    [Fact]
    public void MarkImageUpdated_WritesTheNestedFile_WithTheEdit()
    {
        Tools();
        var session = _fixture.Session;
        var img = session.GetImage("Map", Nested);
        var before = Hash(MapFile("Map0"));
        img.AddProperty(new WzIntProperty("markedInt", 7));
        img.Changed = true;

        session.DataSource.MarkImageUpdated("Map", img);

        Assert.False(File.Exists(Path.Combine(Root, "Map", "000000000.img")));
        Assert.NotEqual(before, Hash(MapFile("Map0")));                       // the file really changed
        using var other = new WzSessionManager();
        other.InitDataSource(Root);
        Assert.Equal(7, (other.GetImage("Map", Nested)["markedInt"] as WzIntProperty)?.Value);
    }

    [Fact]
    public void MarkImageUpdated_Throws_ForAnImageThatWasNotLoadedHere()
    {
        Tools();
        var stranger = new WzImage("stranger.img");
        Assert.Throws<InvalidOperationException>(() => _fixture.Session.DataSource.MarkImageUpdated("Map", stranger));
        Assert.Throws<ArgumentNullException>(() => _fixture.Session.DataSource.MarkImageUpdated("Map", null!));
    }

    [Fact]
    public void MarkImageUpdated_Throws_WhenTheFileIsGone()
    {
        Tools();
        var img = _fixture.Session.GetImage("Map", Nested);
        File.Delete(MapFile("Map0"));
        Assert.Throws<FileNotFoundException>(() => _fixture.Session.DataSource.MarkImageUpdated("Map", img));
        Assert.False(File.Exists(MapFile("Map0")));
    }

    [SkippableFact]
    public void MarkImageUpdated_Throws_WhenTheWriteFails()
    {
        var target = Path.Combine(Root, "Map", "target.bin");
        File.Copy(MapFile("Map0"), target);
        File.Delete(MapFile("Map0"));
        try { File.CreateSymbolicLink(MapFile("Map0"), target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Skip.If(true, "symbolic links cannot be created here"); }
        Tools();
        var img = _fixture.Session.GetImage("Map", Nested);
        img.Changed = true;
        Assert.Throws<IOException>(() => _fixture.Session.DataSource.MarkImageUpdated("Map", img));
    }

    [Fact]
    public void ImageWithUnsavedEdits_SurvivesClearCache()
    {
        var tools = Tools();
        MarkdownTestHelper.AssertSuccess(tools.SetString("Character", "Test.img", "testString", "unsaved"));
        var before = _fixture.Session.GetImage("Character", "Test.img");

        MarkdownTestHelper.AssertSuccess(new FileTools(_fixture.Session).ClearCache());

        var after = _fixture.Session.GetImage("Character", "Test.img");
        Assert.Same(before, after);
        Assert.Equal("unsaved", (after["testString"] as WzStringProperty)?.Value);
    }

    private sealed class Box { public int Id; public bool Dirty; }

    [Fact]
    public void LruCache_DoesNotEvictPinnedItems()
    {
        using var cache = new LRUCache<string, Box>(3, _ => 1, b => b.Dirty);
        var dirty = new Box { Id = 1, Dirty = true };
        cache.Add("dirty", dirty);                       // oldest
        cache.Add("b", new Box { Id = 2 });
        cache.Add("c", new Box { Id = 3 });

        cache.Add("d", new Box { Id = 4 });              // over the limit: evicts the oldest UNPINNED item ("b")

        Assert.True(cache.TryGet("dirty", out var kept));
        Assert.Same(dirty, kept);
        Assert.False(cache.ContainsKey("b"));
        Assert.True(cache.ContainsKey("c"));
        Assert.True(cache.ContainsKey("d"));
    }

    [Fact]
    public void LruCache_AllPinned_GrowsInsteadOfEvicting_AndClearUnpinnedKeepsThem()
    {
        using var cache = new LRUCache<string, Box>(2, _ => 1, b => b.Dirty);
        for (int i = 0; i < 5; i++)
            cache.Add("k" + i, new Box { Id = i, Dirty = true });
        Assert.Equal(5, cache.Count);                    // nothing lost, no endless loop

        cache.Add("clean", new Box { Id = 99 });
        cache.ClearUnpinned();
        Assert.Equal(5, cache.Count);
        Assert.False(cache.ContainsKey("clean"));
    }

    [Fact]
    public void LruCache_WithoutPinPredicate_BehavesAsBefore()
    {
        using var cache = new LRUCache<string, Box>(2, _ => 1);
        cache.Add("a", new Box { Dirty = true });
        cache.Add("b", new Box());
        cache.Add("c", new Box());
        Assert.False(cache.ContainsKey("a"));
        cache.ClearUnpinned();
        Assert.Equal(0, cache.Count);
    }
}
