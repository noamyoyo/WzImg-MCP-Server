using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tests;

/// <summary>
/// The cache limit counts <see cref="ImgFileSystemManager.EstimateWzImageSize"/>. It used to be 2x to 5x below the real managed
/// heap, so a "512 MB" cache held 1.1 GB and more. The first test loads many images and compares the estimate with the heap.
/// Runs in the non-parallel collection, so no other test allocates while the heap is measured.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class EstimatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wzimg-estimate-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>An image shaped like the real ones: sub properties, ints, strings, vectors and canvases with a vector and a delay.</summary>
    private static WzImage BuildImage(string name, int groups)
    {
        var img = new WzImage(name) { Changed = true };
        for (int g = 0; g < groups; g++)
        {
            var group = new WzSubProperty($"group{g}");
            group.AddProperty(new WzIntProperty("id", g));
            group.AddProperty(new WzStringProperty("name", $"a name that is {g} long enough"));
            group.AddProperty(new WzVectorProperty("pos", new WzIntProperty("X", g), new WzIntProperty("Y", -g)));
            group.AddProperty(new WzFloatProperty("rate", g / 3f));
            group.AddProperty(new WzUOLProperty("link", "../name"));
            var frame = new WzSubProperty("frame");
            for (int f = 0; f < 3; f++)
            {
                frame.AddProperty(new WzIntProperty($"delay{f}", 100));
                frame.AddProperty(new WzStringProperty($"s{f}", "x"));
            }
            group.AddProperty(frame);
            img.AddProperty(group);
        }
        return img;
    }

    private static void Save(WzImage img, string file)
    {
        using var fs = File.Create(file);
        using var writer = new WzBinaryWriter(fs, WzTool.GetIvByMapleVersion(WzMapleVersion.BMS));
        img.SaveImage(writer, true);
    }

    [Fact]
    public void Estimate_FollowsTheRealManagedHeap()
    {
        // 150 images with 220 sub properties each: some 30 MB of heap, far above what the runtime does in the background.
        Directory.CreateDirectory(Path.Combine(_root, "Mob"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"version\":\"1\"}");
        for (int i = 0; i < 150; i++)
            Save(BuildImage($"i{i:000}.img", 40), Path.Combine(_root, "Mob", $"i{i:000}.img"));

        var config = new HaCreatorConfig();
        config.Cache.MaxMemoryCacheMB = 4000;     // nothing is evicted
        using var manager = new ImgFileSystemManager(_root, config);
        manager.Initialize();

        long Heap()
        {
            for (int i = 0; i < 3; i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
            return GC.GetTotalMemory(true);
        }

        long before = Heap();
        var loaded = new List<WzImage>();
        long estimate = 0;
        foreach (var path in manager.GetImagePathsInCategory("Mob"))
        {
            var image = manager.LoadImage("Mob", path)!;
            loaded.Add(image);
            estimate += ImgFileSystemManager.EstimateWzImageSize(image);
        }
        long grown = Heap() - before;
        GC.KeepAlive(loaded);

        double factor = (double)grown / estimate;
        Assert.InRange(factor, 0.7, 1.4);                          // the old estimate was 2x to 5x too low (factor 2 to 5)
        Assert.Equal(estimate, manager.GetStats().MemoryUsageBytes);   // and that is what the cache counts
    }

    [Fact]
    public void Estimate_CountsEveryKindOfProperty_AndStringLength()
    {
        long empty = ImgFileSystemManager.EstimateWzImageSize(new WzImage("e.img") { Changed = true, Parsed = true });
        Assert.InRange(empty, 1024, 64 * 1024);

        var oneString = new WzImage("s.img") { Changed = true, Parsed = true };
        oneString.AddProperty(new WzStringProperty("n", "abc"));
        var longString = new WzImage("l.img") { Changed = true, Parsed = true };
        longString.AddProperty(new WzStringProperty("n", new string('x', 1003)));

        long small = ImgFileSystemManager.EstimateWzImageSize(oneString);
        long big = ImgFileSystemManager.EstimateWzImageSize(longString);
        Assert.True(small > empty + 100);                          // a property costs far more than its characters
        Assert.Equal(2 * 1000, big - small);                       // a character is two bytes (UTF-16)

        var container = new WzImage("c.img") { Changed = true, Parsed = true };
        container.AddProperty(new WzSubProperty("sub"));
        var leaf = new WzImage("f.img") { Changed = true, Parsed = true };
        leaf.AddProperty(new WzIntProperty("sub", 1));
        Assert.True(ImgFileSystemManager.EstimateWzImageSize(container) > ImgFileSystemManager.EstimateWzImageSize(leaf));   // a container owns a collection

        var linked = new WzImage("u.img") { Changed = true, Parsed = true };
        var uol = new WzUOLProperty("link", "does/not/exist");
        linked.AddProperty(uol);
        Assert.True(ImgFileSystemManager.EstimateWzImageSize(linked) > empty);   // a link is counted, not followed

        Assert.Equal(0, ImgFileSystemManager.EstimateWzImageSize(null!));
    }

    [Fact]
    public void Estimate_OfAnUnparsedImage_IsOnlyItsReader()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Mob"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"version\":\"1\"}");
        Save(BuildImage("a.img", 20), Path.Combine(_root, "Mob", "a.img"));
        using var manager = new ImgFileSystemManager(_root);
        manager.Initialize();

        var image = manager.LoadImage("Mob", "a.img")!;
        long parsed = ImgFileSystemManager.EstimateWzImageSize(image);
        image.UnparseImage();

        long unparsed = ImgFileSystemManager.EstimateWzImageSize(image);
        Assert.InRange(unparsed, 1024, 16 * 1024);
        Assert.True(unparsed < parsed / 3);
    }
}
