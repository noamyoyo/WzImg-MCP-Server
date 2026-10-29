using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tests;

/// <summary>
/// An edit made while a save is running must not be lost, and two threads loading the same image must end up with
/// one object (an image that is already edited and pinned is never replaced by a freshly loaded copy).
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ImgManagerConcurrencyTests : IDisposable
{
    private const string Cat = "Map";
    private const string Name = "Map0/000000000.img";
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("WZIMG_ALLOWED_ROOT");
    private readonly TestFixture _fixture = new();
    private readonly ImgFileSystemDataSource _source;
    private ImgFileSystemManager Manager => _source.Manager;

    public ImgManagerConcurrencyTests()
    {
        Environment.SetEnvironmentVariable("WZIMG_ALLOWED_ROOT", null);     // backups are a sibling of the version directory
        _source = new ImgFileSystemDataSource(_fixture.TestDataPath);
    }

    public void Dispose()
    {
        _source.Dispose();
        _fixture.Dispose();
        Environment.SetEnvironmentVariable("WZIMG_ALLOWED_ROOT", _previousRoot);
    }

    private WzImage Load() => Manager.LoadImage(Cat, Name) ?? throw new InvalidOperationException("image not loaded");

    private bool OnDisk(string property)
    {
        using var other = new ImgFileSystemDataSource(_fixture.TestDataPath);
        return other.GetImage(Cat, Name)?[property] != null;
    }

    [Fact]
    public void EditDuringSave_KeepsTheImageChangedAndCached_AndTheNextSaveWritesIt()
    {
        var image = Load();
        image.AddProperty(new WzIntProperty("first", 1));
        Manager.AfterSerializeForTests = img => img.AddProperty(new WzIntProperty("raced", 2));   // another tool call edits mid-save

        Assert.True(Manager.SaveLoadedImage(image));

        Assert.True(image.Changed);                                    // the newer edit is still unsaved
        Assert.Same(image, Load());                                    // and the image was not evicted
        Assert.True(OnDisk("first"));
        Assert.False(OnDisk("raced"));

        Manager.AfterSerializeForTests = null;
        Assert.True(Manager.SaveLoadedImage(image));
        Assert.False(image.Changed);
        Assert.NotSame(image, Load());                                 // the usual behaviour: saved and evicted
        Assert.True(OnDisk("raced"));
    }

    [Fact]
    public void SaveWithoutRace_ClearsChanged_AndEvicts()
    {
        var image = Load();
        image.AddProperty(new WzIntProperty("first", 1));

        Assert.True(Manager.SaveLoadedImage(image));

        Assert.False(image.Changed);
        Assert.NotSame(image, Load());
        Assert.True(OnDisk("first"));
    }

    [Fact]
    public void FailedSave_LeavesTheImageChanged()
    {
        var image = Load();
        image.AddProperty(new WzIntProperty("first", 1));
        Manager.AfterSerializeForTests = _ => throw new IOException("disk full");

        Assert.False(Manager.SaveLoadedImage(image));

        Assert.True(image.Changed);
        Assert.Same(image, Load());
        Assert.False(OnDisk("first"));
    }

    [Fact]
    public void TryClearChanged_RefusesAfterANewEdit()
    {
        var image = new WzImage("x.img");
        image.Changed = true;
        int version = image.ChangeVersion;

        Assert.True(image.TryClearChanged(version));
        Assert.False(image.Changed);

        image.Changed = true;
        version = image.ChangeVersion;
        image.AddProperty(new WzIntProperty("late", 1));
        Assert.False(image.TryClearChanged(version));
        Assert.True(image.Changed);
    }

    [Fact]
    public async Task ConcurrentLoads_OfOneImage_ShareOneObjectAndOneDiskRead()
    {
        int readsBefore = Manager.GetStats().DiskReadCount;
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return Manager.LoadImage(Cat, Name);
        })).ToArray();
        start.Set();
        var images = await Task.WhenAll(tasks);

        Assert.All(images, i => Assert.Same(images[0], i));
        Assert.Equal(readsBefore + 1, Manager.GetStats().DiskReadCount);
    }

    private sealed class Item { public bool Pinned; }

    private static LRUCache<string, Item> NewCache() => new(1000, _ => 1, v => v.Pinned);

    [Fact]
    public void LruCache_Add_DoesNotReplaceAPinnedEntryWithAnotherObject()
    {
        using var cache = NewCache();
        var pinned = new Item { Pinned = true };
        cache.Add("k", pinned);

        var held = cache.Add("k", new Item());

        Assert.Same(pinned, held);
        Assert.True(cache.TryGet("k", out var current));
        Assert.Same(pinned, current);
        Assert.Same(pinned, cache.Add("k", pinned));        // the same object again is fine
    }

    [Fact]
    public void LruCache_Add_ReplacesAnUnpinnedEntry_AndRemoveUnpinnedSkipsPinned()
    {
        using var cache = NewCache();
        var first = new Item();
        cache.Add("k", first);
        var second = new Item();
        Assert.Same(second, cache.Add("k", second));
        Assert.True(cache.TryGet("k", out var current));
        Assert.Same(second, current);

        second.Pinned = true;
        Assert.False(cache.RemoveUnpinned("k"));
        Assert.True(cache.ContainsKey("k"));
        second.Pinned = false;
        Assert.True(cache.RemoveUnpinned("k"));
        Assert.False(cache.ContainsKey("k"));
    }
}
