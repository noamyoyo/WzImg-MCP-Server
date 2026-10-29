using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using Xunit.Abstractions;

namespace WzImgMCP.Tests;

/// <summary>
/// The cache limit counts <see cref="ImgFileSystemManager.EstimateWzImageSize"/>. It used to be 2x to 5x below the real managed
/// heap, so a "512 MB" cache held 1.1 GB and more. The first test loads many images and compares the estimate with the heap.
/// Runs in the non-parallel collection, so no other test allocates while the heap is measured.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class EstimatorTests : IDisposable
{
    public const string CalibrationEnvVar = "WZIMG_CALIBRATION_DATA";

    private readonly ITestOutputHelper _output;
    private readonly string _root = Directory.CreateTempSubdirectory("wzimg-estimate-").FullName;

    public EstimatorTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>
    /// A map-like image: nested sub properties, ints, vectors, strings, links and canvases (a few pixels of noise each, so the
    /// canvas payload stays on disk after loading, as it does for the real files).
    /// </summary>
    private static WzImage BuildMapLike(string name, int layers)
    {
        var img = new WzImage(name) { Changed = true };
        var info = new WzSubProperty("info");
        info.AddProperty(new WzIntProperty("version", 10));
        info.AddProperty(new WzStringProperty("bgm", "Bgm00/FloralLife"));
        info.AddProperty(new WzVectorProperty("VRTopLeft", new WzIntProperty("X", -100), new WzIntProperty("Y", -50)));
        img.AddProperty(info);
        var rng = new Random(layers);
        for (int l = 0; l < layers; l++)
        {
            var layer = new WzSubProperty(l.ToString());
            var objs = new WzSubProperty("obj");
            for (int o = 0; o < 12; o++)
            {
                var obj = new WzSubProperty(o.ToString());
                obj.AddProperty(new WzStringProperty("oS", "Guide"));
                obj.AddProperty(new WzStringProperty("l0", "pillar"));
                obj.AddProperty(new WzStringProperty("l1", "a"));
                obj.AddProperty(new WzStringProperty("l2", "0"));
                foreach (var key in new[] { "x", "y", "z", "zM", "f", "r" })
                    obj.AddProperty(new WzIntProperty(key, rng.Next(-500, 500)));
                layer.AddProperty(obj);
                objs.AddProperty(new WzUOLProperty($"link{o}", $"../{o}"));
            }
            layer.AddProperty(objs);
            var tiles = new WzSubProperty("tile");
            for (int t = 0; t < 12; t++)
            {
                var tile = new WzSubProperty(t.ToString());
                tile.AddProperty(new WzStringProperty("u", "bsc"));
                tile.AddProperty(new WzStringProperty("no", "3"));
                tile.AddProperty(new WzIntProperty("x", rng.Next(500)));
                tile.AddProperty(new WzIntProperty("y", rng.Next(500)));
                tile.AddProperty(new WzIntProperty("zM", 0));
                tiles.AddProperty(tile);
            }
            layer.AddProperty(tiles);
            var anim = new WzSubProperty("anim");
            for (int f = 0; f < 4; f++)
            {
                var canvas = new WzCanvasProperty(f.ToString()) { PngProperty = new WzPngProperty() };
                var pixels = new byte[8 * 8 * 4];
                rng.NextBytes(pixels);
                canvas.PngProperty.SetPixelsBgra32(pixels, 8, 8, SurfaceFormat.Bgra32);
                canvas.AddProperty(new WzVectorProperty("origin", new WzIntProperty("X", f), new WzIntProperty("Y", f)));
                canvas.AddProperty(new WzIntProperty("delay", 120));
                anim.AddProperty(canvas);
            }
            layer.AddProperty(anim);
            img.AddProperty(layer);
        }
        return img;
    }

    /// <summary>A String.wz-like image: very many small string properties, three levels deep.</summary>
    private static WzImage BuildStringLike(string name, int entries)
    {
        var img = new WzImage(name) { Changed = true };
        for (int e = 0; e < entries; e++)
        {
            var entry = new WzSubProperty((1000000 + e).ToString());
            entry.AddProperty(new WzStringProperty("name", $"Item number {e}"));
            entry.AddProperty(new WzStringProperty("desc", $"A description of item {e} that runs on for a while, as they do."));
            img.AddProperty(entry);
        }
        return img;
    }

    private static long Heap()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
        return GC.GetTotalMemory(true);
    }

    /// <summary>Writes <paramref name="count"/> images, loads them all and returns (estimate, heap growth, growth / estimate).</summary>
    private (long Estimate, long Grown, double Factor, long Old) Measure(string category, int count, Func<int, WzImage> build)
    {
        Directory.CreateDirectory(Path.Combine(_root, category));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"version\":\"1\"}");
        for (int i = 0; i < count; i++)
            Save(build(i), Path.Combine(_root, category, $"i{i:000}.img"));

        var config = new HaCreatorConfig();
        config.Cache.MaxMemoryCacheMB = 4000;     // nothing is evicted
        using var manager = new ImgFileSystemManager(_root, config);
        manager.Initialize();

        long before = Heap();
        var loaded = new List<WzImage>();
        long estimate = 0, old = 0;
        foreach (var path in manager.GetImagePathsInCategory(category))
        {
            var image = manager.LoadImage(category, path)!;
            loaded.Add(image);
            estimate += ImgFileSystemManager.EstimateWzImageSize(image);
            old += OldEstimate(image);
        }
        long grown = Heap() - before;
        GC.KeepAlive(loaded);

        Assert.Equal(estimate, manager.GetStats().MemoryUsageBytes);   // and that is what the cache counts
        return (estimate, grown, (double)grown / estimate, old);
    }

    private void Report(string shape, int count, (long Estimate, long Grown, double Factor, long Old) m)
        => _output.WriteLine($"estimate factor [{shape}, {count} images]: heap growth {m.Grown / 1048576.0:F1} MB / estimate {m.Estimate / 1048576.0:F1} MB = {m.Factor:F2}" +
                             $" (the old estimate of 100 bytes per property: {m.Old / 1048576.0:F1} MB, factor {(double)m.Grown / m.Old:F2})");

    /// <summary>The estimate this repository had before the calibration: 1 KB per image, 100 bytes per property, characters of strings.</summary>
    private static long OldEstimate(WzImage image)
    {
        static long Of(WzImageProperty prop, int depth)
        {
            if (prop == null || depth > 50)
                return 0;
            long size = 100;
            if (prop is WzStringProperty str)
                size += (str.Value?.Length ?? 0) * 2;
            if (prop is WzUOLProperty uol)
                return size + (uol.Value?.Length ?? 0) * 2L;
            if (prop.WzProperties != null)
                foreach (var child in prop.WzProperties)
                    size += Of(child, depth + 1);
            return size;
        }

        long total = 1024;
        foreach (var prop in image.WzProperties)
            total += Of(prop, 0);
        return total;
    }

    // The old estimate (100 bytes per property) was 2x to 5x below the real heap, that is a factor of 2 to 5 here. The bounds
    // below are wide enough for runtime and platform differences and far from that.
    [Fact]
    public void Estimate_FollowsTheRealManagedHeap_OfAnAnimatedMapLikeTree()
    {
        var m = Measure("Map", 120, i => BuildMapLike($"i{i:000}.img", 12));
        Report("map-like", 120, m);
        Assert.InRange(m.Factor, 0.7, 1.4);
    }

    [Fact]
    public void Estimate_FollowsTheRealManagedHeap_OfAStringTree()
    {
        var m = Measure("String", 12, i => BuildStringLike($"i{i:000}.img", 3000));
        Report("string-like", 12, m);
        Assert.InRange(m.Factor, 0.7, 1.4);
    }

    [Fact]
    public void Estimate_FollowsTheRealManagedHeap_OfMixedSmallProperties()
    {
        var m = Measure("Mob", 150, i => BuildImage($"i{i:000}.img", 40));
        Report("mixed", 150, m);
        Assert.InRange(m.Factor, 0.7, 1.4);
    }

    /// <summary>
    /// OPT-IN calibration on real data: set WZIMG_CALIBRATION_DATA to an extracted IMG tree (the folder with manifest.json and one
    /// folder per category; the same layout init_data_source opens). The test loads up to WZIMG_CALIBRATION_IMAGES (default 300)
    /// images per category, and writes heap growth / estimate per category (1.00 = exact) to the test output
    /// (dotnet test --logger "console;verbosity=detailed"). It skips when the variable is not set, and it only fails when the
    /// estimate is off by more than a factor of 2 either way.
    /// </summary>
    [SkippableFact]
    public void Calibration_OnRealData_IsOptIn()
    {
        string? folder = Environment.GetEnvironmentVariable(CalibrationEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(folder), $"{CalibrationEnvVar} is not set (opt-in calibration on real data)");
        Skip.IfNot(Directory.Exists(folder), $"{CalibrationEnvVar} is not a folder: {folder}");
        int perCategory = int.TryParse(Environment.GetEnvironmentVariable("WZIMG_CALIBRATION_IMAGES"), out var n) && n > 0 ? n : 300;

        var config = new HaCreatorConfig();
        config.Cache.MaxMemoryCacheMB = 65536;     // nothing is evicted
        using var manager = new ImgFileSystemManager(folder!, config);
        manager.Initialize();

        var factors = new List<double>();
        foreach (var category in manager.GetCategories())
        {
            var paths = manager.GetImagePathsInCategory(category).Take(perCategory).ToList();
            if (paths.Count == 0)
                continue;

            long before = Heap();
            var loaded = new List<WzImage>();
            long estimate = 0;
            foreach (var path in paths)
            {
                var image = manager.LoadImage(category, path);
                if (image == null)
                    continue;
                loaded.Add(image);
                estimate += ImgFileSystemManager.EstimateWzImageSize(image);
            }
            long grown = Heap() - before;
            GC.KeepAlive(loaded);
            foreach (var image in loaded)
                manager.RemoveFromCache(image);
            loaded.Clear();
            if (estimate <= 0)
                continue;

            double factor = (double)grown / estimate;
            factors.Add(factor);
            _output.WriteLine($"calibration {category}: {paths.Count} images, heap growth {grown / 1048576.0:F1} MB / estimate {estimate / 1048576.0:F1} MB = {factor:F2}");
        }

        Assert.NotEmpty(factors);
        Assert.All(factors, f => Assert.InRange(f, 0.5, 2.0));
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
