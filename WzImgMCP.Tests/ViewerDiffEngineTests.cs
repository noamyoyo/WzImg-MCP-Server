#if LINUX_BUILD
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;
using WzImgMCP.Viewer.Diff;

namespace WzImgMCP.Tests;

/// <summary>The diff engine on pairs of synthetic images (one old, one new) written with the library's own writer.</summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerDiffEngineTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly ViewerTree _tree = new();
    private readonly ViewerImageLoader _loader;
    private readonly ViewerPathResolver _resolver;
    private int _counter;

    public ViewerDiffEngineTests()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        var options = _tree.Options();
        var fs = new PosixViewerFileSystem();
        _loader = new ViewerImageLoader(options, fs, new ViewerImageCache(options.CacheBytes), () => { });
        _resolver = new ViewerPathResolver(options.Roots, fs);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _tree.Dispose();
    }

    private ImageDiffResult Diff(Action<WzImage> oldImage, Action<WzImage> newImage, DiffOptions? options = null)
    {
        // a new file name per call: the cache cannot tell a rewrite with the same size and mtime from the old file
        var name = $"X{Interlocked.Increment(ref _counter)}.img";
        ImgFixture.Write(_tree.Root("canon"), name, ImgFixture.Build(oldImage));
        ImgFixture.Write(_tree.Root("owner"), name, ImgFixture.Build(newImage));
        using var a = _loader.Load(_resolver.Resolve("canon", name), CancellationToken.None);
        using var b = _loader.Load(_resolver.Resolve("owner", name), CancellationToken.None);
        return ImageDiffer.Diff(a.Loaded, b.Loaded, options ?? new DiffOptions(), CancellationToken.None);
    }

    /// <summary>AddProperty refuses a second property of the same name; a file can still hold one, so go round it.</summary>
    private static void Put(WzImage img, WzImageProperty p) => img.WzProperties.Add(p);

    private static DiffChange Only(ImageDiffResult r)
    {
        Assert.False(r.Truncated);
        return Assert.Single(r.Changes);
    }

    // ---- values

    [Fact]
    public void SameContent_NoChanges_AnyOrder()
    {
        var r = Diff(
            img => { img.AddProperty(new WzIntProperty("a", 1)); img.AddProperty(new WzStringProperty("b", "x")); img.AddProperty(new WzVectorProperty("v", 1, 2)); },
            img => { img.AddProperty(new WzVectorProperty("v", 1, 2)); img.AddProperty(new WzStringProperty("b", "x")); img.AddProperty(new WzIntProperty("a", 1)); });
        Assert.Empty(r.Changes);
        Assert.Empty(r.Warnings);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void String_ChangeIsOrdinal()
    {
        var c = Only(Diff(img => img.AddProperty(new WzStringProperty("s", "Maple")), img => img.AddProperty(new WzStringProperty("s", "maple"))));
        Assert.Equal(("s", "changed", "string", "Maple", "maple"), (c.Path, c.Kind, c.Type, c.Before, c.After));
    }

    [Fact]
    public void Integers_AreExact_AndTypeStrict()
    {
        var r = Diff(
            img => { img.AddProperty(new WzIntProperty("i", 1)); img.AddProperty(new WzShortProperty("sh", 5)); img.AddProperty(new WzLongProperty("l", 5)); img.AddProperty(new WzIntProperty("same", 7)); },
            img => { img.AddProperty(new WzIntProperty("i", 2)); img.AddProperty(new WzIntProperty("sh", 5)); img.AddProperty(new WzLongProperty("l", 6)); img.AddProperty(new WzIntProperty("same", 7)); });
        Assert.Equal(new[] { "i", "l", "sh" }, r.Changes.Select(c => c.Path));
        Assert.Equal("type", r.Changes.Single(c => c.Path == "sh").Detail);   // short -> int is a change even though 5 == 5
        Assert.Equal("(short) 5", r.Changes.Single(c => c.Path == "sh").Before);
        Assert.Equal("(int) 5", r.Changes.Single(c => c.Path == "sh").After);
        Assert.Equal("int", r.Changes.Single(c => c.Path == "sh").Type);
        Assert.Equal(("5", "6"), (r.Changes.Single(c => c.Path == "l").Before, r.Changes.Single(c => c.Path == "l").After));
    }

    [Fact]
    public void Floats_BitExact_NaNEqualsNaN_NegativeZeroDiffers_InvariantFormat()
    {
        var r = Diff(
            img =>
            {
                img.AddProperty(new WzFloatProperty("same", 1.5f)); img.AddProperty(new WzFloatProperty("nan", float.NaN)); img.AddProperty(new WzFloatProperty("zero", 0f));
                img.AddProperty(new WzDoubleProperty("dsame", 0.1)); img.AddProperty(new WzDoubleProperty("dnan", double.NaN)); img.AddProperty(new WzDoubleProperty("dzero", 0.0));
                img.AddProperty(new WzFloatProperty("tiny", 1.0f));
            },
            img =>
            {
                img.AddProperty(new WzFloatProperty("same", 1.5f)); img.AddProperty(new WzFloatProperty("nan", float.NaN)); img.AddProperty(new WzFloatProperty("zero", -0f));
                img.AddProperty(new WzDoubleProperty("dsame", 0.1)); img.AddProperty(new WzDoubleProperty("dnan", double.NaN)); img.AddProperty(new WzDoubleProperty("dzero", -0.0));
                img.AddProperty(new WzFloatProperty("tiny", 1.0000001f));
            });
        // (a float written as -0 is stored as 0 by the file format, a double keeps its sign: see also the direct test below)
        Assert.Equal(new[] { "dzero", "tiny" }, r.Changes.Select(c => c.Path));
        Assert.Equal(("0", "-0"), (r.Changes[0].Before, r.Changes[0].After));
        Assert.Equal(("1", "1.0000001"), (r.Changes[1].Before, r.Changes[1].After));
    }

    [Fact]
    public void FloatEquality_IsBitExact_WithNaNEqualNaN_AndNegativeZeroDifferent()
    {
        Assert.True(PropertyValues.FloatEquals(1.5f, 1.5f));
        Assert.True(PropertyValues.FloatEquals(float.NaN, BitConverter.Int32BitsToSingle(0x7FC00001)));   // two different NaNs
        Assert.False(PropertyValues.FloatEquals(0f, -0f));
        Assert.False(PropertyValues.FloatEquals(1.0f, 1.0000001f));
        Assert.True(PropertyValues.DoubleEquals(double.NaN, -double.NaN));
        Assert.False(PropertyValues.DoubleEquals(0.0, -0.0));
        Assert.False(PropertyValues.DoubleEquals(0.1, Math.BitIncrement(0.1)));
    }

    [Fact]
    public void FloatText_DoesNotDependOnTheCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var c = Only(Diff(img => img.AddProperty(new WzDoubleProperty("d", 1.5)), img => img.AddProperty(new WzDoubleProperty("d", 2.25))));
            Assert.Equal(("1.5", "2.25"), (c.Before, c.After));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Vector_Uol_Convex()
    {
        var r = Diff(
            img =>
            {
                img.AddProperty(new WzVectorProperty("v", 1, 2)); img.AddProperty(new WzVectorProperty("vsame", 3, 4));
                img.AddProperty(new WzUOLProperty("u", "../a")); img.AddProperty(new WzUOLProperty("usame", "../b"));
                var cx = new WzConvexProperty("c"); cx.AddProperty(new WzVectorProperty("0", 1, 1)); cx.AddProperty(new WzVectorProperty("1", 2, 2)); img.AddProperty(cx);
                var cs = new WzConvexProperty("csame"); cs.AddProperty(new WzVectorProperty("0", 1, 1)); img.AddProperty(cs);
            },
            img =>
            {
                img.AddProperty(new WzVectorProperty("v", 1, 3)); img.AddProperty(new WzVectorProperty("vsame", 3, 4));
                img.AddProperty(new WzUOLProperty("u", "../z")); img.AddProperty(new WzUOLProperty("usame", "../b"));
                var cx = new WzConvexProperty("c"); cx.AddProperty(new WzVectorProperty("0", 1, 1)); cx.AddProperty(new WzVectorProperty("1", 2, 9)); img.AddProperty(cx);
                var cs = new WzConvexProperty("csame"); cs.AddProperty(new WzVectorProperty("0", 1, 1)); img.AddProperty(cs);
            });
        Assert.Equal(new[] { "c", "u", "v" }, r.Changes.Select(c => c.Path));
        Assert.Equal(("(1, 2)", "(1, 3)"), (r.Changes[2].Before, r.Changes[2].After));
        Assert.Equal(("../a", "../z"), (r.Changes[1].Before, r.Changes[1].After));   // the link text, never resolved
        Assert.Equal(("[(1, 1) (2, 2)]", "[(1, 1) (2, 9)]"), (r.Changes[0].Before, r.Changes[0].After));
        Assert.All(r.Changes, c => Assert.Equal("changed", c.Kind));
    }

    [Fact]
    public void Sound_ComparedByDurationLengthAndBytes()
    {
        var dir = Directory.CreateTempSubdirectory("viewer-wav-").FullName;
        try
        {
            var r = Diff(
                img => { img.AddProperty(ImgFixture.Sound("a", dir, 10)); img.AddProperty(ImgFixture.Sound("same", dir, 20)); },
                img => { img.AddProperty(ImgFixture.Sound("a", dir, 11)); img.AddProperty(ImgFixture.Sound("same", dir, 20)); });   // same length, other bytes
            var c = Only(r);
            Assert.Equal(("a", "sound"), (c.Path, c.Type));
            Assert.StartsWith("sound ", c.Before);
            Assert.NotEqual(c.Before, c.After);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NullAndSub_ChildrenAreWalked()
    {
        var r = Diff(
            img => { img.AddProperty(new WzNullProperty("n")); var s = new WzSubProperty("s"); s.AddProperty(new WzIntProperty("x", 1)); img.AddProperty(s); },
            img => { img.AddProperty(new WzNullProperty("n")); var s = new WzSubProperty("s"); s.AddProperty(new WzIntProperty("x", 2)); img.AddProperty(s); });
        var c = Only(r);
        Assert.Equal("s/x", c.Path);
    }

    [Fact]
    public void TypeChange_IsOneChange_AndNotWalkedFurther()
    {
        var r = Diff(
            img => { var s = new WzSubProperty("p"); s.AddProperty(new WzIntProperty("a", 1)); s.AddProperty(new WzIntProperty("b", 2)); img.AddProperty(s); },
            img => img.AddProperty(new WzIntProperty("p", 5)));
        var c = Only(r);
        Assert.Equal(("p", "changed", "int", "type"), (c.Path, c.Kind, c.Type, c.Detail));
        Assert.Equal(2, c.DescendantCount);
        Assert.StartsWith("(sub)", c.Before);
        Assert.Equal("(int) 5", c.After);
    }

    [Fact]
    public void LongValues_AreCutAt200Characters_WithAFlag()
    {
        var r = Diff(img => img.AddProperty(new WzStringProperty("s", new string('a', 500))), img => img.AddProperty(new WzStringProperty("s", new string('a', 300) + "b")));
        var c = Only(r);
        Assert.Equal(200, c.Before!.Length);
        Assert.Equal(200, c.After!.Length);
        Assert.True(c.BeforeTruncated);
        Assert.True(c.AfterTruncated);

        var short1 = Only(Diff(img => img.AddProperty(new WzStringProperty("s", "a")), img => img.AddProperty(new WzStringProperty("s", "b"))));
        Assert.Null(short1.BeforeTruncated);
    }

    [Fact]
    public void LongValue_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 199) + "\U0001F600" + "tail";   // the emoji straddles character 200
        var c = Only(Diff(img => img.AddProperty(new WzStringProperty("s", "x")), img => img.AddProperty(new WzStringProperty("s", text))));
        Assert.Equal(199, c.After!.Length);
        Assert.True(c.AfterTruncated);
    }

    // ---- structure

    [Fact]
    public void Duplicates_PairTheKthWithTheKth_AndAreNamedNameHashK()
    {
        var r = Diff(
            img => { Put(img, new WzIntProperty("x", 1)); Put(img, new WzIntProperty("x", 2)); Put(img, new WzIntProperty("y", 9)); },
            img => { Put(img, new WzIntProperty("x", 1)); Put(img, new WzIntProperty("x", 3)); Put(img, new WzIntProperty("x", 4)); Put(img, new WzIntProperty("y", 9)); });
        Assert.Equal(new[] { ("x#1", "changed"), ("x#2", "added") }, r.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal(("2", "3"), (r.Changes[0].Before, r.Changes[0].After));
        Assert.Equal("4", r.Changes[1].After);
    }

    [Fact]
    public void Duplicates_RemovedExtra()
    {
        var r = Diff(
            img => { Put(img, new WzIntProperty("x", 1)); Put(img, new WzIntProperty("x", 2)); },
            img => Put(img, new WzIntProperty("x", 1)));
        var c = Only(r);
        Assert.Equal(("x#1", "removed", "2"), (c.Path, c.Kind, c.Before));
    }

    [Fact]
    public void AddedSubtree_IsOneChange_WithCountAndTheFirst20Names()
    {
        var r = Diff(
            img => img.AddProperty(new WzIntProperty("keep", 1)),
            img =>
            {
                img.AddProperty(new WzIntProperty("keep", 1));
                var big = new WzSubProperty("big");
                for (int i = 0; i < 30; i++)
                {
                    var child = new WzSubProperty($"c{i:00}");
                    child.AddProperty(new WzIntProperty("v", i));
                    big.AddProperty(child);
                }
                img.AddProperty(big);
            });
        var c = Only(r);
        Assert.Equal(("big", "added", "sub"), (c.Path, c.Kind, c.Type));
        Assert.Equal(60, c.DescendantCount);   // 30 children and their 30 values
        Assert.Equal(20, c.Children!.Count);
        Assert.Equal("c00", c.Children[0]);
        Assert.Equal("c19", c.Children[19]);
        Assert.Null(c.Before);
        Assert.Equal(1, r.Summary.Added);
    }

    [Fact]
    public void RemovedSubtree_IsOneChange()
    {
        var r = Diff(
            img => { var s = new WzSubProperty("gone"); s.AddProperty(new WzIntProperty("a", 1)); s.AddProperty(new WzIntProperty("b", 2)); img.AddProperty(s); },
            img => { });
        var c = Only(r);
        Assert.Equal(("gone", "removed", 2), (c.Path, c.Kind, c.DescendantCount));
        Assert.Equal(1, r.Summary.Removed);
    }

    [Fact]
    public void Summary_CountsKinds()
    {
        var r = Diff(
            img => { img.AddProperty(new WzIntProperty("a", 1)); img.AddProperty(new WzIntProperty("gone", 1)); },
            img => { img.AddProperty(new WzIntProperty("a", 2)); img.AddProperty(new WzIntProperty("new", 1)); });
        Assert.Equal(new DiffSummary(1, 1, 1, 0, 0), r.Summary);
    }

    // ---- caps

    [Fact]
    public void MaxChanges_CutsTheList_AndSaysSo()
    {
        var r = Diff(
            img => { for (int i = 0; i < 50; i++) img.AddProperty(new WzIntProperty($"n{i:00}", i)); },
            img => { for (int i = 0; i < 50; i++) img.AddProperty(new WzIntProperty($"n{i:00}", i + 1000)); },
            new DiffOptions { MaxChanges = 10 });
        Assert.True(r.Truncated);
        Assert.Equal(10, r.Changes.Count);
        Assert.Contains(r.Warnings, w => w.Contains("more than 10 changes"));
        Assert.Equal("n00", r.Changes[0].Path);   // the first ones in path order
        Assert.Equal("n09", r.Changes[9].Path);
    }

    [Fact]
    public void Exactly_MaxChanges_IsNotTruncated()
    {
        var r = Diff(
            img => { for (int i = 0; i < 10; i++) img.AddProperty(new WzIntProperty($"n{i}", i)); },
            img => { for (int i = 0; i < 10; i++) img.AddProperty(new WzIntProperty($"n{i}", i + 1)); },
            new DiffOptions { MaxChanges = 10 });
        Assert.False(r.Truncated);
        Assert.Equal(10, r.Changes.Count);
    }

    [Fact]
    public void WallClockBudget_StopsTheComparison_FlaggedTruncated()
    {
        var r = Diff(
            img => { for (int i = 0; i < 200; i++) img.AddProperty(new WzIntProperty($"n{i:000}", i)); },
            img => { for (int i = 0; i < 200; i++) img.AddProperty(new WzIntProperty($"n{i:000}", i + 1)); },
            new DiffOptions { Budget = TimeSpan.Zero });
        Assert.True(r.Truncated);
        Assert.True(r.Changes.Count < 200);
        Assert.Contains(r.Warnings, w => w.Contains("stopped after"));
    }

    [Fact]
    public void Cancellation_Throws()
    {
        ImgFixture.Write(_tree.Root("canon"), "X.img", ImgFixture.Build(img => img.AddProperty(new WzIntProperty("a", 1))));
        ImgFixture.Write(_tree.Root("owner"), "X.img", ImgFixture.Build(img => img.AddProperty(new WzIntProperty("a", 2))));
        using var a = _loader.Load(_resolver.Resolve("canon", "X.img"), CancellationToken.None);
        using var b = _loader.Load(_resolver.Resolve("owner", "X.img"), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ImageDiffer.Diff(a.Loaded, b.Loaded, new DiffOptions(), cts.Token));
    }

    [Fact]
    public void Output_IsDeterministic_AndSortedByPathOrdinal()
    {
        void Fill(WzImage img, int offset)
        {
            foreach (var name in new[] { "b", "a-b", "a", "a0", "B", "c" })
                Put(img, new WzIntProperty(name, offset));
            var s = new WzSubProperty("a");   // a duplicate name with children
            s.AddProperty(new WzIntProperty("z", offset));
            s.AddProperty(new WzIntProperty("b", offset));
            Put(img, s);
        }
        var first = Diff(img => Fill(img, 1), img => Fill(img, 2));
        var second = Diff(img => Fill(img, 1), img => Fill(img, 2));
        var j1 = JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var j2 = JsonSerializer.Serialize(second, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(j1, j2);

        var paths = first.Changes.Select(c => c.Path).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal), paths);
        Assert.Contains("a#0", paths);
        Assert.Contains("a#1/z", paths);
    }

    // ---- canvases

    private static Func<int, int, (byte, byte, byte, byte)> Solid(byte shade) => (x, y) => (shade, shade, shade, 255);

    [Fact]
    public void Canvas_Identical_NoChange_NoDecode()
    {
        var r = Diff(img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))), img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))),
            new DiffOptions { MaxCanvasDecodes = 0 });   // would warn if stage 2 were needed
        Assert.Empty(r.Changes);
        Assert.Empty(r.Canvases);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Canvas_PixelChange_IsReported_OnlyForThatNode()
    {
        var r = Diff(
            img => { img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))); img.AddProperty(ImgFixture.PixelCanvas("d", 8, 8, Solid(50))); },
            img => { img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(200))); img.AddProperty(ImgFixture.PixelCanvas("d", 8, 8, Solid(50))); });
        var c = Only(r);
        Assert.Equal(("c", "canvas", "pixels"), (c.Path, c.Type, c.Detail));
        var canvas = Assert.Single(r.Canvases);
        Assert.Equal(("c", "pixels"), (canvas.Path, canvas.Change));
        Assert.Equal(new CanvasInfo(8, 8, 2), canvas.Before);
        Assert.Equal(1, r.Summary.CanvasesChanged);
    }

    [Fact]
    public void Canvas_OnlyColourOfTransparentPixelsDiffers_IsHiddenColour_NotAVisibleChange()
    {
        // alpha 0 everywhere in the left half; the colour hidden there differs, the visible half is the same
        Func<byte, Func<int, int, (byte, byte, byte, byte)>> make = hidden => (x, y) => x < 4 ? (hidden, hidden, hidden, (byte)0) : ((byte)90, (byte)90, (byte)90, (byte)255);
        var r = Diff(img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, make(7))), img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, make(99))));
        Assert.Empty(r.Changes);
        var canvas = Assert.Single(r.Canvases);
        Assert.Equal("hidden_color", canvas.Change);
        Assert.Equal(0, r.Summary.CanvasesRecompressed);
        Assert.Equal(1, r.Summary.CanvasesHiddenColour);
        Assert.Equal(0, r.Summary.CanvasesChanged);
        Assert.Equal("identical_visible", r.Semantic);   // F10: not a plain "identical"
        Assert.Contains(r.Warnings, w => w.Contains("hidden_color"));
    }

    [Fact]
    public void Canvas_SizeChange_AndFormatChange()
    {
        var size = Diff(img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))), img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 9, Solid(10))));
        Assert.Equal("size", Only(size).Detail);
        Assert.Equal(new CanvasInfo(8, 9, 2), size.Canvases[0].After);

        var format = Diff(
            img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(16), SurfaceFormat.Bgra32)),
            img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(16), SurfaceFormat.Bgra4444)));
        Assert.Equal("format", Only(format).Detail);
        Assert.NotEqual(format.Canvases[0].Before!.Format, format.Canvases[0].After!.Format);
    }

    [Fact]
    public void Canvas_DecodeBudget_ExceededGivesPixelsUnverified_AndAWarning()
    {
        var r = Diff(
            img => { for (int i = 0; i < 3; i++) img.AddProperty(ImgFixture.PixelCanvas($"c{i}", 8, 8, Solid(10))); },
            img => { for (int i = 0; i < 3; i++) img.AddProperty(ImgFixture.PixelCanvas($"c{i}", 8, 8, Solid(200))); },
            new DiffOptions { MaxCanvasDecodes = 1 });
        Assert.Equal(new[] { "pixels", "pixels_unverified", "pixels_unverified" }, r.Changes.Select(c => c.Detail));
        Assert.Contains(r.Warnings, w => w.StartsWith("2 canvas pair(s)"));
    }

    [Fact]
    public void Canvas_OriginAndDelay_ComeFromTheGenericWalk()
    {
        WzCanvasProperty Make(int ox, int delay)
        {
            var c = ImgFixture.PixelCanvas("c", 8, 8, Solid(10));
            c.AddProperty(new WzVectorProperty("origin", ox, 2));
            c.AddProperty(new WzIntProperty("delay", delay));
            return c;
        }
        var r = Diff(img => img.AddProperty(Make(1, 100)), img => img.AddProperty(Make(5, 100)));
        var c = Only(r);
        Assert.Equal(("c/origin", "vector"), (c.Path, c.Type));
        Assert.Empty(r.Canvases);   // the pixels did not change
    }

    [Fact]
    public void Canvas_Linked_ComparedByLinkTextOnly_WithAWarning()
    {
        WzCanvasProperty Linked(string text)
        {
            var c = ImgFixture.PixelCanvas("c", 8, 8, Solid(10));
            c.AddProperty(new WzStringProperty("_outlink", text));
            return c;
        }
        var same = Diff(img => img.AddProperty(Linked("Mob/A.img/1")), img => img.AddProperty(Linked("Mob/A.img/1")));
        Assert.Empty(same.Changes);
        Assert.Contains(same.Warnings, w => w.Contains("linked canvas"));

        var other = Diff(img => img.AddProperty(Linked("Mob/A.img/1")), img => img.AddProperty(Linked("Mob/B.img/1")));
        var c = Only(other);
        Assert.Equal("c/_outlink", c.Path);
        Assert.Equal(("Mob/A.img/1", "Mob/B.img/1"), (c.Before, c.After));

        var oneSide = Diff(img => img.AddProperty(Linked("Mob/A.img/1")), img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))));
        Assert.Contains(oneSide.Changes, x => x.Path == "c" && x.Detail == "link");
    }

    [Fact]
    public void Canvas_AddedAndRemoved_AreListedWithTheirInfo()
    {
        var r = Diff(
            img => img.AddProperty(ImgFixture.PixelCanvas("gone", 4, 5, Solid(10))),
            img =>
            {
                var s = new WzSubProperty("anim");
                s.AddProperty(ImgFixture.PixelCanvas("0", 6, 7, Solid(10)));
                s.AddProperty(ImgFixture.PixelCanvas("1", 6, 7, Solid(20)));
                img.AddProperty(s);
            });
        Assert.Equal(new[] { ("anim/0", "added"), ("anim/1", "added"), ("gone", "removed") }, r.Canvases.Select(c => (c.Path, c.Change)));
        Assert.Equal(new CanvasInfo(6, 7, 2), r.Canvases[0].After);
        Assert.Null(r.Canvases[0].Before);
        Assert.Equal(new CanvasInfo(4, 5, 2), r.Canvases[2].Before);
        Assert.Equal(2, r.Changes.Count);   // the added subtree is ONE change, the removed canvas another
    }

    [Fact]
    public void Canvas_Pixels_Equal_WhenReSavedWithTheLibrary_NoDecodeNeeded()
    {
        // a canvas written twice from the same pixels has the same compressed bytes
        var r = Diff(img => img.AddProperty(ImgFixture.PixelCanvas("c", 16, 16, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)255))),
                     img => img.AddProperty(ImgFixture.PixelCanvas("c", 16, 16, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)255))),
                     new DiffOptions { MaxCanvasDecodes = 0 });
        Assert.Empty(r.Changes);
        Assert.Empty(r.Warnings);
    }

    // ---- review findings

    private static WzImage MemoryImage(Action<WzImage> fill)
    {
        var img = new WzImage("mem.img") { Changed = true };
        fill(img);
        return img;
    }

    private static ImageDiffResult DiffMemory(Action<WzImage> oldImage, Action<WzImage> newImage, DiffOptions? options = null)
    {
        var a = new LoadedImage(new ImageKey("canon", "mem.img", 1, 1), MemoryImage(oldImage), "BMS", 0, 1, 1, "a");
        var b = new LoadedImage(new ImageKey("owner", "mem.img", 1, 1), MemoryImage(newImage), "BMS", 0, 1, 1, "b");
        return ImageDiffer.Diff(a, b, options ?? new DiffOptions(), CancellationToken.None);
    }

    private static WzSubProperty Anim(string name, int frames, byte shade = 10)
    {
        var s = new WzSubProperty(name);
        for (int i = 0; i < frames; i++) s.AddProperty(ImgFixture.PixelCanvas(i.ToString(), 4, 4, Solid((byte)(shade + i))));
        return s;
    }

    // F3

    [Fact]
    public void F3_SubReplacedByString_ListsTheRemovedCanvases_AndCountsThem()
    {
        var r = Diff(img => img.AddProperty(Anim("stand", 3)), img => img.AddProperty(new WzStringProperty("stand", "gone")));
        Assert.Equal("type", Only(r).Detail);
        Assert.Equal(new[] { ("stand/0", "removed"), ("stand/1", "removed"), ("stand/2", "removed") }, r.Canvases.Select(c => (c.Path, c.Change)));
        Assert.Equal(3, r.Summary.CanvasesChanged);
        Assert.NotNull(r.Canvases[0].Before);
        Assert.Null(r.Canvases[0].After);
    }

    [Fact]
    public void F3_StringReplacedBySub_ListsTheAddedCanvases()
    {
        var r = Diff(img => img.AddProperty(new WzStringProperty("stand", "x")), img => img.AddProperty(Anim("stand", 2)));
        Assert.Equal(new[] { ("stand/0", "added"), ("stand/1", "added") }, r.Canvases.Select(c => (c.Path, c.Change)));
        Assert.Equal(2, r.Summary.CanvasesChanged);
    }

    [Fact]
    public void F3_CanvasReplacedByInt_AndSubReplacedByCanvas()
    {
        var a = Diff(img => img.AddProperty(ImgFixture.PixelCanvas("c", 4, 4, Solid(10))), img => img.AddProperty(new WzIntProperty("c", 1)));
        Assert.Equal(("c", "removed"), (a.Canvases.Single().Path, a.Canvases.Single().Change));

        var b = Diff(img => img.AddProperty(Anim("c", 2)), img => img.AddProperty(ImgFixture.PixelCanvas("c", 4, 4, Solid(10))));
        Assert.Equal(new[] { ("c", "added"), ("c/0", "removed"), ("c/1", "removed") }, b.Canvases.Select(c => (c.Path, c.Change)));
        Assert.Equal(3, b.Summary.CanvasesChanged);
    }

    // F4

    [Fact]
    public void F4_Sound_SameDataOtherWaveFormat_IsAChange()
    {
        var dir = Directory.CreateTempSubdirectory("viewer-wav-").FullName;
        try
        {
            var r = Diff(
                img => img.AddProperty(ImgFixture.Sound("a", dir, 10, sampleRate: 8000)),
                img => img.AddProperty(ImgFixture.Sound("a", dir, 10, sampleRate: 22050)));   // identical sample bytes, another sample rate
            var c = Only(r);
            Assert.Equal(("a", "sound"), (c.Path, c.Type));
            Assert.NotEqual(c.Before, c.After);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void F4_Video_BytesAreCompared()
    {
        var same = Diff(img => img.AddProperty(new WzVideoProperty("v", 1, new byte[] { 1, 2, 3, 4 })), img => img.AddProperty(new WzVideoProperty("v", 1, new byte[] { 1, 2, 3, 4 })));
        Assert.Empty(same.Changes);

        var bytes = Diff(img => img.AddProperty(new WzVideoProperty("v", 1, new byte[] { 1, 2, 3, 4 })), img => img.AddProperty(new WzVideoProperty("v", 1, new byte[] { 1, 2, 3, 5 })));
        var c = Only(bytes);
        Assert.Equal(("v", "video"), (c.Path, c.Type));
        Assert.StartsWith("video ", c.Before);
        Assert.NotEqual(c.Before, c.After);

        var kind = Diff(img => img.AddProperty(new WzVideoProperty("v", 1, new byte[] { 1, 2, 3, 4 })), img => img.AddProperty(new WzVideoProperty("v", 2, new byte[] { 1, 2, 3, 4 })));
        Assert.Equal("v", Only(kind).Path);
    }

    private static WzConvexProperty Convex(string name, params WzImageProperty[] children)
    {
        var c = new WzConvexProperty(name);
        foreach (var child in children) c.AddProperty(child);
        return c;
    }

    [Fact]
    public void F4_Convex_NonVectorAndExtraChildrenAreVisible()
    {
        var baseline = Convex("hull", new WzVectorProperty("0", 1, 2), new WzVectorProperty("1", 3, 4));

        var same = Diff(img => img.AddProperty(baseline.DeepClone()), img => img.AddProperty(Convex("hull", new WzVectorProperty("0", 1, 2), new WzVectorProperty("1", 3, 4))));
        Assert.Empty(same.Changes);

        var extra = Diff(img => img.AddProperty(baseline.DeepClone()),
            img => img.AddProperty(Convex("hull", new WzVectorProperty("0", 1, 2), new WzVectorProperty("1", 3, 4), new WzSubProperty("2"))));
        Assert.Equal("hull", Only(extra).Path);
        Assert.Equal("[(1, 2) (3, 4) hull:sub]", Only(extra).After);   // the file format names every convex child after the convex itself

        WzSubProperty WithInt(int v) { var sub = new WzSubProperty("1"); sub.AddProperty(new WzIntProperty("x", v)); return sub; }
        var nonVector = Diff(img => img.AddProperty(Convex("hull", new WzVectorProperty("0", 1, 2), WithInt(5))),
            img => img.AddProperty(Convex("hull", new WzVectorProperty("0", 1, 2), WithInt(6))));
        Assert.Equal("hull", Only(nonVector).Path);   // a non-vector child changed below: invisible before

        var order = Diff(img => img.AddProperty(baseline.DeepClone()), img => img.AddProperty(Convex("hull", new WzVectorProperty("0", 3, 4), new WzVectorProperty("1", 1, 2))));
        Assert.Equal("hull", Only(order).Path);
    }

    // F6

    [Fact]
    public void F6_LongNamesAndPaths_AreCut_AndFlagged()
    {
        var longName = new string('n', 500);
        var r = Diff(img => img.AddProperty(new WzIntProperty(longName, 1)), img => img.AddProperty(new WzIntProperty(longName, 2)));
        var c = Only(r);
        Assert.Equal(DiffOptions.MaxNameChars, c.Path.Length);
        Assert.True(c.PathTruncated);

        // a subtree with long child names: the list of names is cut too
        var added = Diff(img => { }, img =>
        {
            var s = new WzSubProperty("s");
            s.AddProperty(new WzIntProperty(new string('k', 400), 1));
            img.AddProperty(s);
        });
        var change = Only(added);
        Assert.All(change.Children!, n => Assert.True(n.Length <= DiffOptions.MaxNameChars));
        Assert.Null(change.PathTruncated);

        // a deep path of long names is cut as a whole
        Action<WzImage> deepWith(int leaf) => img =>
        {
            var top = new WzSubProperty(new string('a', 90));
            var parent = top;
            for (int i = 0; i < 30; i++)
            {
                var child = new WzSubProperty(new string((char)('b' + i % 20), 90));
                parent.AddProperty(child);
                parent = child;
            }
            parent.AddProperty(new WzIntProperty("leaf", leaf));
            img.AddProperty(top);
        };
        var deep = DiffMemory(deepWith(1), deepWith(2));
        var leaf = Assert.Single(deep.Changes);
        Assert.Equal(DiffOptions.MaxPathChars, leaf.Path.Length);
        Assert.True(leaf.PathTruncated);
    }

    [Fact]
    public void F6_ResponseSizeCap_CutsTheList_AndFlagsIt()
    {
        var options = new DiffOptions { MaxResponseBytes = 4000 };
        var r = Diff(img => { }, img => { for (int i = 0; i < 200; i++) img.AddProperty(new WzStringProperty($"name{i:000}", new string('v', 150))); }, options);
        Assert.True(r.Truncated);
        Assert.InRange(r.Changes.Count, 1, 199);
        Assert.Contains(r.Warnings, w => w.Contains("response size limit"));
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.Length < options.MaxResponseBytes, $"{json.Length} bytes against a cap of {options.MaxResponseBytes}");
    }

    [Fact]
    public void F6_SizeEstimate_IsAnUpperBound_ForNonAsciiText()
    {
        var options = new DiffOptions { MaxResponseBytes = 20000 };
        var r = Diff(img => { }, img => { for (int i = 0; i < 300; i++) img.AddProperty(new WzStringProperty($"\u540d{i:000}", new string('\u5b57', 150))); }, options);
        Assert.True(r.Truncated);
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.Length < options.MaxResponseBytes, $"{json.Length} bytes against a cap of {options.MaxResponseBytes}");
    }

    // F7

    [Fact]
    public void F7_LoneSurrogates_AreReplaced_SoTheResultCanBeSerialised()
    {
        var bad = "x\uD800y";   // a lone high surrogate
        var r = DiffMemory(
            img => { img.AddProperty(new WzStringProperty("v" + bad, bad + "1")); img.AddProperty(new WzIntProperty("k\uDC00", 1)); },
            img => { img.AddProperty(new WzStringProperty("v" + bad, bad + "2")); img.AddProperty(new WzIntProperty("k\uDC00", 2));
                     var s = new WzSubProperty("s\uD800"); s.AddProperty(new WzIntProperty("c\uD801", 1)); img.AddProperty(s); });
        Assert.NotEmpty(r.Changes);
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web));   // threw before the fix
        Assert.DoesNotContain("\\ud800", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(r.Changes, c => c.Path == "v" + "x\uFFFDy" && c.Before == "x\uFFFDy1");
        Assert.Contains(r.Changes, c => c.Path == "k\uFFFD");
        Assert.Contains(r.Changes, c => c.Path == "s\uFFFD" && c.Children!.Contains("c\uFFFD"));
    }

    [Fact]
    public void F7_Clean()
    {
        // built in code: a lone surrogate cannot survive in an attribute argument (the metadata is UTF-8)
        var cases = new (string Input, string Expected)[]
        {
            ("plain", "plain"),
            ("a\uD800b", "a\uFFFDb"),
            ("a\uDC00b", "a\uFFFDb"),
            ("\uD800", "\uFFFD"),
            ("\uD83D\uDE00", "\uD83D\uDE00"),   // a valid pair is kept
            ("\uDE00\uD83D", "\uFFFD\uFFFD"),   // reversed
        };
        foreach (var (input, expected) in cases)
            Assert.Equal(expected, PropertyValues.Clean(input));
    }

    [Fact]
    public void F7_Cut_NeverLeavesALoneSurrogateAtTheEnd()
    {
        var text = new string('a', DiffOptions.MaxValueChars - 1) + "\uD83D\uDE00tail";
        var (cut, truncated) = PropertyValues.Cut(text);
        Assert.True(truncated);
        Assert.Equal(DiffOptions.MaxValueChars - 1, cut!.Length);
        JsonSerializer.Serialize(cut);
    }

    // F8

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(EndOfStreamException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(InvalidDataException))]
    public void F8_ReadFailureOfOneCanvas_IsPixelsUnverified_NotAFailedDiff(Type exception)
    {
        Action<string> boom = where => { if (where == "stage1") throw (Exception)Activator.CreateInstance(exception)!; };
        var r = Diff(img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))), img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(200))),
            new DiffOptions { Probe = boom });
        var c = Only(r);
        Assert.Equal("pixels_unverified", c.Detail);
        Assert.Contains(r.Warnings, w => w.Contains("could not be verified"));
        Assert.Equal("different", r.Semantic);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(InvalidOperationException))]
    public void F8_ReadFailureWhileDecoding_IsPixelsUnverified(Type exception)
    {
        Action<string> boom = where => { if (where == "decode") throw (Exception)Activator.CreateInstance(exception)!; };
        var r = Diff(img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))), img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(200))),
            new DiffOptions { Probe = boom });
        Assert.Equal("pixels_unverified", Only(r).Detail);
    }

    [Fact]
    public void F8_ReadFailureOfASoundValue_IsAnUnverifiedChange_AndTheRestIsStillCompared()
    {
        var dir = Directory.CreateTempSubdirectory("viewer-wav-").FullName;
        try
        {
            Action<string> boom = where => { if (where == "value") throw new EndOfStreamException(); };
            var r = Diff(
                img => { img.AddProperty(ImgFixture.Sound("a", dir, 10)); img.AddProperty(new WzIntProperty("n", 1)); },
                img => { img.AddProperty(ImgFixture.Sound("a", dir, 10)); img.AddProperty(new WzIntProperty("n", 2)); },
                new DiffOptions { Probe = boom });
            // the probe fires for every value; both nodes are reported as unverified, the diff itself completes
            Assert.All(r.Changes, c => Assert.Equal("unverified", c.Detail));
            Assert.Contains(r.Changes, c => c.Path == "a");
            Assert.Contains(r.Warnings, w => w.Contains("could not be read"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void F8_OutOfMemory_IsNotSwallowedByTheDiffer()
    {
        Action<string> boom = _ => throw new OutOfMemoryException();
        Assert.Throws<OutOfMemoryException>(() => Diff(
            img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(10))), img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, Solid(200))),
            new DiffOptions { Probe = boom }));
    }

    // F9

    [Fact]
    public void F9_DecodedBytesBudget_StopsFurtherDecodes()
    {
        Func<byte, Action<WzImage>> fill = shade => img => { for (int i = 0; i < 4; i++) img.AddProperty(ImgFixture.PixelCanvas($"c{i}", 8, 8, Solid((byte)(shade + i)))); };
        // each 8x8 pair costs 2 x 256 bytes; room for two pairs
        var r = Diff(fill(10), fill(100), new DiffOptions { MaxDecodedBytes = 2 * 2 * 8 * 8 * 4 });
        Assert.Equal(new[] { "pixels", "pixels", "pixels_unverified", "pixels_unverified" }, r.Changes.Select(c => c.Detail));
        Assert.Contains(r.Warnings, w => w.Contains("decoded-bytes budget"));
        Assert.Equal("different", r.Semantic);
    }

    [Fact]
    public void F9_DiffCanvasCap_IsBelowThePictureEndpointCap()
        => Assert.True(DiffOptions.MaxDiffCanvasDecodedBytes < DiffOptions.MaxCanvasDecodedBytes);

    // F10

    private static WzCanvasProperty Recompress(WzCanvasProperty source)
    {
        var png = source.PngProperty!;
        var zipped = png.GetCompressedBytes(true)!;
        using var inflated = new MemoryStream();
        using (var input = new System.IO.Compression.ZLibStream(new MemoryStream(zipped), System.IO.Compression.CompressionMode.Decompress))
            input.CopyTo(inflated);
        using var output = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.NoCompression, leaveOpen: true))
            z.Write(inflated.ToArray());
        var copy = new WzCanvasProperty(source.Name) { PngProperty = new WzPngProperty() };
        copy.PngProperty.SetCompressedBytes(output.ToArray(), png.Width, png.Height, png.Format);
        foreach (var child in source.WzProperties) copy.AddProperty(child.DeepClone());
        return copy;
    }

    [Fact]
    public void F10_ReallyRecompressed_SamePixelsOtherBytes_IsIdentical()
    {
        var original = ImgFixture.PixelCanvas("c", 16, 16, (x, y) => ((byte)(x * 9), (byte)(y * 7), (byte)(x ^ y), (byte)255));
        var r = Diff(img => img.AddProperty(original.DeepClone()), img => img.AddProperty(Recompress(original)));
        Assert.Empty(r.Changes);
        var canvas = Assert.Single(r.Canvases);
        Assert.Equal("recompressed", canvas.Change);
        Assert.Equal(1, r.Summary.CanvasesRecompressed);
        Assert.Equal("identical", r.Semantic);
    }

    [Fact]
    public void F10_LinkedCanvases_CompareEqualByTextOnly_AreIdenticalVisibleWithAWarning()
    {
        WzCanvasProperty Linked()
        {
            var c = ImgFixture.PixelCanvas("c", 8, 8, Solid(10));
            c.AddProperty(new WzStringProperty("_outlink", "Mob/A.img/1"));
            return c;
        }
        var r = Diff(img => img.AddProperty(Linked()), img => img.AddProperty(Linked()));
        Assert.Empty(r.Changes);
        Assert.Equal("identical_visible", r.Semantic);
        Assert.Contains(r.Warnings, w => w.Contains("linked canvas"));
    }

    [Fact]
    public void F10_TheRule()
    {
        Assert.Equal("identical", Diff(img => img.AddProperty(new WzIntProperty("a", 1)), img => img.AddProperty(new WzIntProperty("a", 1))).Semantic);
        Assert.Equal("different", Diff(img => img.AddProperty(new WzIntProperty("a", 1)), img => img.AddProperty(new WzIntProperty("a", 2))).Semantic);
        var truncated = Diff(img => { }, img => { for (int i = 0; i < 5; i++) img.AddProperty(new WzIntProperty($"n{i}", i)); }, new DiffOptions { MaxChanges = 2 });
        Assert.Equal("different", truncated.Semantic);
    }
}
#endif
