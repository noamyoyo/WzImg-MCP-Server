using MapleLib.Img;
using MapleLib.WzLib;
using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// The memory guard trims the cache of unpinned images when the heap is above the soft limit or the server has been idle,
/// and never an image with unsaved edits. A fake clock, a fake heap size and a counting collection replace the real ones, so
/// nothing sleeps and nothing allocates.
/// </summary>
[Collection("AllowedRootEnv")]   // the real collections measure the heap of the whole process: nothing else may run meanwhile
public sealed class MemoryGuardTests : IDisposable
{
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;                          // in TimestampFrequency units
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, (long)span.TotalMilliseconds);
    }

    private static readonly MemoryGuardSettings Defaults = new(SoftLimitBytes: 1000, IdleTrim: TimeSpan.FromSeconds(300));

    private readonly ScanTestTree _tree = new(10);
    private readonly ImgFileSystemDataSource _source;
    private readonly FakeClock _clock = new();
    private long _heap = 10;
    private int _collections;
    private readonly List<string> _log = new();

    public MemoryGuardTests()
    {
        _source = new ImgFileSystemDataSource(_tree.Path);
    }

    public void Dispose()
    {
        _source.Dispose();
        _tree.Dispose();
    }

    private MemoryGuard NewGuard(MemoryGuardSettings? settings = null, Action? collect = null)
    {
        // The timer is not started: the ticks are driven by hand.
        return new MemoryGuard(() => _source, settings ?? Defaults, _clock, () => Interlocked.Read(ref _heap), collect ?? (() => _collections++));
    }

    /// <summary>Loads all ten images; the ones named are edited and therefore pinned.</summary>
    private List<WzImage> LoadAll(params string[] edited)
    {
        var all = new List<WzImage>();
        foreach (var path in _source.GetImagePathsInCategory(ScanTestTree.Category))
        {
            var image = _source.GetImage(ScanTestTree.Category, path)!;
            if (edited.Contains(Path.GetFileName(path)))
                image.Changed = true;
            all.Add(image);
        }
        return all;
    }

    [Fact]
    public void BelowTheLimitAndNotIdle_DoesNothing()
    {
        LoadAll();
        var guard = NewGuard();

        _clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Null(guard.Tick());

        Assert.Equal(10, _source.GetStats().CachedImageCount);
        Assert.Equal(0, _collections);
        Assert.Null(guard.LastTrim);
    }

    [Fact]
    public void AboveTheSoftLimit_ReleasesUnpinnedImages_AndKeepsEditedOnes()
    {
        var images = LoadAll("m001.img", "m002.img");
        var guard = NewGuard();
        _heap = 5000;

        var trim = guard.Tick();

        Assert.NotNull(trim);
        Assert.Equal("memory_soft_limit", trim!.Reason);
        Assert.Equal(8, trim.ReleasedImages);
        Assert.Equal(2, trim.PinnedImages);
        Assert.Equal(2, _source.GetStats().CachedImageCount);
        Assert.Equal(2, _source.GetStats().PinnedImageCount);
        Assert.Equal(1, _collections);

        // the edited images are still the cached objects, with their edits
        foreach (var name in new[] { "m001.img", "m002.img" })
        {
            var image = images.First(i => i.Name == name);
            Assert.Same(image, _source.GetImage(ScanTestTree.Category, name));
            Assert.True(image.Changed);
        }
        Assert.Same(trim, guard.LastTrim);
        Assert.Equal(1, guard.TrimCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), trim.At);
    }

    [Fact]
    public void SoftLimitTrims_ThatFreeLittle_BackOffExponentiallyUpToTenMinutes()
    {
        LoadAll("m001.img");
        var guard = NewGuard(Defaults with { IdleTrim = TimeSpan.Zero });   // only the soft limit is looked at here
        _heap = 5000;                                         // the fake collection frees nothing: the heap stays above the limit

        Assert.NotNull(guard.Tick());                         // t = 0
        Assert.Equal(TimeSpan.FromSeconds(120), guard.CurrentSoftInterval);
        foreach (var expectedNext in new[] { 240, 480, 600, 600, 600 })
        {
            var wait = guard.CurrentSoftInterval;
            _clock.Advance(wait - TimeSpan.FromSeconds(1));
            Assert.Null(guard.Tick());                        // one second too soon
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.NotNull(guard.Tick());
            Assert.Equal(TimeSpan.FromSeconds(expectedNext), guard.CurrentSoftInterval);
        }
        Assert.Equal(6, _collections);

        _heap = 10;                                           // back below the limit: nothing more to do
        _clock.Advance(TimeSpan.FromSeconds(1200));
        Assert.Null(guard.Tick());
    }

    [Fact]
    public void ATrimThatFreesMore_ResetsTheSpacing_AndSoDoesAHeapBelowTheLimit()
    {
        LoadAll();
        bool freeing = false;
        var guard = NewGuard(Defaults with { IdleTrim = TimeSpan.Zero }, collect: () => { _collections++; if (freeing) _heap = 3000; });   // 5000 -> 3000 is 40% freed
        _heap = 5000;

        Assert.NotNull(guard.Tick());                         // frees nothing: 120 s
        _clock.Advance(TimeSpan.FromSeconds(120));
        Assert.NotNull(guard.Tick());                         // frees nothing: 240 s
        Assert.Equal(TimeSpan.FromSeconds(240), guard.CurrentSoftInterval);

        _heap = 10;
        Assert.Null(guard.Tick());                            // below the limit again
        Assert.Equal(TimeSpan.FromSeconds(60), guard.CurrentSoftInterval);

        _heap = 5000;
        freeing = true;
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.NotNull(guard.Tick());                         // after only 60 s, and it frees 40%
        Assert.Equal(TimeSpan.FromSeconds(60), guard.CurrentSoftInterval);
        _heap = 5000;                                         // the heap grows back
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.NotNull(guard.Tick());                         // so the next one is not delayed
    }

    [Fact]
    public void IsOverSoftLimit_UsesTheTimersReading_AndNeverMeasuresOrCollects()
    {
        LoadAll();
        int heapReads = 0;
        var guard = new MemoryGuard(() => _source, Defaults, _clock, () => { heapReads++; return Interlocked.Read(ref _heap); }, () => _collections++);

        _heap = 5000;
        Assert.False(guard.IsOverSoftLimit());                // before the first tick there is no reading
        Assert.Equal(0, heapReads);

        guard.Tick();                                         // reads the heap (5000: over) and trims
        int readsAfterTick = heapReads;
        int collectionsAfterTick = _collections;
        for (int i = 0; i < 100; i++)
            Assert.True(guard.IsOverSoftLimit());             // what preload and export ask before every image

        Assert.Equal(readsAfterTick, heapReads);              // no measurement of its own
        Assert.Equal(collectionsAfterTick, _collections);     // and no collection

        _heap = 10;
        _clock.Advance(TimeSpan.FromSeconds(30));
        guard.Tick();
        Assert.False(guard.IsOverSoftLimit());

        var off = new MemoryGuard(() => _source, Defaults with { SoftLimitBytes = 0 }, _clock, () => 5000, () => { });
        off.Tick();
        Assert.False(off.IsOverSoftLimit());
    }

    [Fact]
    public void TheCollection_IsNotAggressiveByDefault_AndTheSettingTurnsItOn()
    {
        Assert.False(Defaults.AggressiveCollection);
        Assert.Equal(GCCollectionMode.Forced, MemoryGuard.ModeFor(Defaults.AggressiveCollection));
        Assert.Equal(GCCollectionMode.Aggressive, MemoryGuard.ModeFor(true));

        Assert.False(WzImgMCP.Utils.ServerSettings.MemoryGuardFromEnvironment().AggressiveCollection);
        Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.GcAggressiveEnvVar, "1");
        try { Assert.True(WzImgMCP.Utils.ServerSettings.MemoryGuardFromEnvironment().AggressiveCollection); }
        finally { Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.GcAggressiveEnvVar, null); }
    }

    [Fact]
    public void Idle_TrimsOnce_UntilTheNextToolCall()
    {
        LoadAll("m003.img");
        var guard = NewGuard();

        _clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Null(guard.Tick());                            // not yet idle long enough

        _clock.Advance(TimeSpan.FromSeconds(2));
        var trim = guard.Tick();
        Assert.NotNull(trim);
        Assert.Equal("idle", trim!.Reason);
        Assert.Equal(9, trim.ReleasedImages);
        Assert.Equal(1, _source.GetStats().CachedImageCount);

        _clock.Advance(TimeSpan.FromSeconds(1000));
        Assert.Null(guard.Tick());                            // once per quiet period
        Assert.Equal(1, _collections);

        guard.Touch();                                        // a tool call
        _clock.Advance(TimeSpan.FromSeconds(200));
        Assert.Null(guard.Tick());                            // the quiet period starts again
        _clock.Advance(TimeSpan.FromSeconds(101));
        Assert.NotNull(guard.Tick());
        Assert.Equal(2, _collections);
    }

    [Fact]
    public void ToolCalls_KeepTheGuardFromTrimming()
    {
        LoadAll();
        var guard = NewGuard();

        for (int i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(200));
            guard.Touch();
            Assert.Null(guard.Tick());
        }
        Assert.Equal(10, _source.GetStats().CachedImageCount);
    }

    [Fact]
    public void ZeroSwitchesATriggerOff()
    {
        LoadAll();
        var guard = NewGuard(new MemoryGuardSettings(SoftLimitBytes: 0, IdleTrim: TimeSpan.Zero));
        _heap = long.MaxValue / 2;
        _clock.Advance(TimeSpan.FromDays(10));

        Assert.Null(guard.Tick());
        Assert.Equal(10, _source.GetStats().CachedImageCount);
    }

    [Fact]
    public void TheSoftLimitTrimDoesNotUseUpTheIdleTrim()
    {
        LoadAll();
        var guard = NewGuard();
        _heap = 5000;
        Assert.Equal("memory_soft_limit", guard.Tick()!.Reason);

        _heap = 10;
        _clock.Advance(TimeSpan.FromSeconds(400));
        Assert.Equal("idle", guard.Tick()!.Reason);
    }

    [Fact]
    public void TwoTicksNeverRunAtOnce()
    {
        LoadAll();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var guard = NewGuard(collect: () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        });
        _heap = 5000;

        var first = Task.Run(() => guard.Tick());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        // the first trim is still collecting: every other tick returns at once and does nothing
        var others = Enumerable.Range(0, 8).Select(_ => Task.Run(() => guard.Tick())).ToArray();
        Task.WaitAll(others);
        Assert.All(others, t => Assert.Null(t.Result));

        release.Set();
        Assert.NotNull(first.Result);
        Assert.Equal(1, guard.TrimCount);
    }

    [Fact]
    public void ATrimWhileImagesAreLoaded_IsSafe()
    {
        LoadAll("m000.img");
        var guard = NewGuard();
        _heap = 5000;
        var stop = new CancellationTokenSource();

        var loader = Task.Run(() =>
        {
            var paths = _source.GetImagePathsInCategory(ScanTestTree.Category);
            while (!stop.IsCancellationRequested)
                foreach (var path in paths)
                    _ = _source.GetImage(ScanTestTree.Category, path);
        });
        for (int i = 0; i < 200; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            guard.Tick();
        }
        stop.Cancel();
        loader.Wait();

        Assert.True(_source.GetImage(ScanTestTree.Category, "m000.img")!.Changed);   // still the edited one
        Assert.Equal(1, _source.GetStats().PinnedImageCount);
    }

    [Fact]
    public void ATrimIsLoggedAsOneLineOfCounts()
    {
        LoadAll("m001.img");
        using var guard = new MemoryGuard(() => _source, Defaults with { Interval = TimeSpan.FromHours(1) }, _clock, () => 5000, () => { });
        guard.Start(_log.Add);                                // the timer will not fire during the test

        guard.Tick();

        var line = Assert.Single(_log);
        Assert.Contains("released 9 cached image(s)", line);
        Assert.Contains("kept 1 with unsaved edits", line);
        Assert.Contains("next soft limit trim not before 120 s", line);   // the fake collection freed nothing: the spacing doubled
        Assert.DoesNotContain(_tree.Path, line);
    }

    [Fact]
    public void TheSessionReportsTheLastTrimInGetCacheStats()
    {
        using var session = _tree.NewSession();
        var guard = new MemoryGuard(() => session.CurrentDataSource, Defaults, _clock, () => Interlocked.Read(ref _heap), () => { });
        session.UseMemoryGuard(guard);
        session.GetImage(ScanTestTree.Category, "m001.img");
        session.GetImage(ScanTestTree.Category, "m002.img").Changed = true;
        _heap = 5000;

        Assert.NotNull(guard.Tick());
        var stats = new FileTools(session).GetCacheStats();

        Assert.Equal("1", MarkdownTestHelper.GetFirstValue(stats, "trim_count"));
        Assert.Equal("2026-10-29T12:00:00Z", MarkdownTestHelper.GetFirstValue(stats, "last_trim_utc"));
        Assert.Equal("memory_soft_limit", MarkdownTestHelper.GetFirstValue(stats, "last_trim_reason"));
        Assert.Equal("1", MarkdownTestHelper.GetFirstValue(stats, "last_trim_released_images"));
        Assert.Equal("1", MarkdownTestHelper.GetFirstValue(stats, "pinned_image_count"));
        Assert.Equal("1", MarkdownTestHelper.GetFirstValue(stats, "cached_image_count"));
    }

    /// <summary>Holds 64 MB of large arrays; the test lets go of them by clearing the field (a debug build keeps locals alive for the whole method).</summary>
    private sealed class Ballast
    {
        public byte[][]? Arrays;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Allocate()
        {
            var arrays = new byte[4][];
            for (int i = 0; i < arrays.Length; i++)
            {
                arrays[i] = new byte[16 * 1024 * 1024];
                arrays[i][0] = 1;                             // touched: committed memory
            }
            Arrays = arrays;
        }
    }

    /// <summary>
    /// The real collection (no seam for it): the first heap reading sees the 64 MB, which the test lets go of at that moment, and the
    /// reading after the trim must be lower by most of it. Runs in the serial collection: a collection of the whole process heap is
    /// measured, so no other test may allocate or collect while it runs.
    /// </summary>
    private void RealCollectionShrinksTheHeap(bool aggressive)
    {
        for (int i = 0; i < 3; i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
        long baseline = GC.GetTotalMemory(true);              // what the process holds without the 64 MB
        var ballast = new Ballast();
        ballast.Allocate();
        int readings = 0;
        var settings = new MemoryGuardSettings(SoftLimitBytes: 1, IdleTrim: TimeSpan.Zero) { AggressiveCollection = aggressive };
        var guard = new MemoryGuard(() => null, settings, _clock, heapBytes: () =>
        {
            long heap = GC.GetTotalMemory(false);
            if (++readings == 1)
                ballast.Arrays = null;                        // from here on the arrays are garbage
            return heap;
        });

        var trim = guard.Tick();                              // the soft limit of one byte is always exceeded

        Assert.NotNull(trim);
        Assert.Equal(2, readings);
        Assert.True(trim!.HeapBefore >= baseline + 60L * 1024 * 1024, $"before the trim the heap held {trim.HeapBefore} bytes over a baseline of {baseline}");
        Assert.True(trim.HeapAfter <= baseline + 8L * 1024 * 1024,
            $"the trim left {(trim.HeapAfter - baseline) / 1048576.0:F1} MB above the baseline ({baseline} -> {trim.HeapBefore} -> {trim.HeapAfter})");
    }

    [Fact]
    public void TheRealCollection_ShrinksTheHeap()
    {
        RealCollectionShrinksTheHeap(aggressive: false);
    }

    [Fact]
    public void TheRealAggressiveCollection_ShrinksTheHeap()
    {
        RealCollectionShrinksTheHeap(aggressive: true);
    }

    [Fact]
    public void EnvironmentSettings_AreParsedStrictly()
    {
        var parse = WzImgMCP.Utils.ServerSettings.ParseInt;
        Assert.Equal(400, parse(null, 400, 16, 65536));
        Assert.Equal(400, parse("", 400, 16, 65536));
        Assert.Equal(400, parse("abc", 400, 16, 65536));
        Assert.Equal(400, parse("1.5", 400, 16, 65536));
        Assert.Equal(400, parse("-3", 400, 16, 65536));
        Assert.Equal(400, parse("15", 400, 16, 65536));        // below the minimum
        Assert.Equal(400, parse("70000", 400, 16, 65536));     // above the maximum
        Assert.Equal(16, parse("16", 400, 16, 65536));
        Assert.Equal(1024, parse(" 1024 ", 400, 16, 65536));
        Assert.Equal(0, parse("0", 300, 0, 100));              // 0 is allowed where the range starts at 0

        Assert.Equal(400, WzImgMCP.Utils.ServerSettings.DefaultCacheMb);
        Assert.Equal(150, WzImgMCP.Utils.ServerSettings.DefaultScanMaxImages);
        Assert.Equal(20, WzImgMCP.Utils.ServerSettings.DefaultScanBudgetSeconds);
        Assert.Equal(1536, WzImgMCP.Utils.ServerSettings.DefaultMemorySoftLimitMb);
        Assert.Equal(300, WzImgMCP.Utils.ServerSettings.DefaultIdleTrimSeconds);
    }
}
