using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// Names-only listing, paging, the scan guard (limit, time budget, release after the scan) and the tools that must not parse
/// a category. "Nothing was parsed" is checked through the manager's disk read counter and the cache count.
/// </summary>
[Collection("AllowedRootEnv")]   // batch_export_images resolves paths through the path guard, which reads WZIMG_ALLOWED_ROOT
public sealed class ScanGuardTests : IDisposable
{
    private readonly ScanTestTree _tree = new(30);
    private readonly WzSessionManager _session;

    public ScanGuardTests()
    {
        _session = _tree.NewSession(new ScanLimits(MaxImages: 100, Budget: TimeSpan.FromSeconds(60), BulkMaxImages: 2000));
    }

    public void Dispose()
    {
        _session.Dispose();
        _tree.Dispose();
    }

    private DataSourceStats Stats => _session.DataSource.GetStats();
    private static string Value(string markdown, string key) => MarkdownTestHelper.GetFirstValue(markdown, key) ?? "";

    private void AssertNothingOpened()
    {
        Assert.Equal(0, Stats.DiskReadCount);
        Assert.Equal(0, Stats.CachedImageCount);
    }

    // ---- list_images_in_category: names only, paged

    [Fact]
    public void ListImages_ParsesNothing()
    {
        var result = new FileTools(_session).ListImagesInCategory(ScanTestTree.Category);

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("30", Value(result, "total"));
        Assert.Equal("30", Value(result, "returned"));
        AssertNothingOpened();
    }

    [Fact]
    public void ListImages_PagesInStableNameOrder()
    {
        var tools = new FileTools(_session);
        var seen = new List<string>();
        int offset = 0;
        for (int pages = 0; pages < 10; pages++)
        {
            var page = tools.ListImagesInCategory(ScanTestTree.Category, limit: 7, offset: offset);
            MarkdownTestHelper.AssertSuccess(page);
            Assert.Equal("30", Value(page, "total"));
            seen.AddRange(MarkdownTestHelper.GetAllValues(page, "full_path"));

            var next = MarkdownTestHelper.GetFirstValue(page, "next_offset");
            if (next == null)
            {
                Assert.DoesNotContain("has_more", page);   // false is not printed
                break;
            }
            Assert.Contains("- has_more: true", page);
            offset = int.Parse(next);
            Assert.Equal(offset, seen.Count);
        }

        Assert.Equal(_tree.RelativePaths.Select(p => $"{ScanTestTree.Category}/{p}").ToList(), seen);   // all, once, ordinal order
        AssertNothingOpened();
    }

    [Fact]
    public void ListImages_LastPageAndBeyondTheEnd()
    {
        var tools = new FileTools(_session);

        var last = tools.ListImagesInCategory(ScanTestTree.Category, limit: 10, offset: 25);
        Assert.Equal("5", Value(last, "returned"));
        Assert.Null(MarkdownTestHelper.GetFirstValue(last, "next_offset"));

        var beyond = tools.ListImagesInCategory(ScanTestTree.Category, offset: 500);
        MarkdownTestHelper.AssertSuccess(beyond);
        Assert.Equal("0", Value(beyond, "returned"));
        Assert.Equal("30", Value(beyond, "total"));
    }

    [Fact]
    public void ListImages_LimitIsClampedToTheMaximum_AndDefaultsTo200()
    {
        var tools = new FileTools(_session);
        Assert.Equal("1000", Value(tools.ListImagesInCategory(ScanTestTree.Category, limit: 50_000), "limit"));
        Assert.Equal("200", Value(tools.ListImagesInCategory(ScanTestTree.Category), "limit"));
        Assert.Equal("1", Value(tools.ListImagesInCategory(ScanTestTree.Category, limit: 0), "limit"));
    }

    [Fact]
    public void ListImages_NestedImagesGetTheirRealPath()
    {
        var result = new FileTools(_session).ListImagesInCategory(ScanTestTree.Category);
        Assert.Contains("Mob/Sub/s004.img", result);
        Assert.Contains("- name: s004.img", result);
    }

    [Fact]
    public void ListImages_SubdirectoryIsPagedToo()
    {
        var tools = new FileTools(_session);
        var page = tools.ListImagesInCategory(ScanTestTree.Category, "Sub", limit: 4, offset: 0);

        MarkdownTestHelper.AssertSuccess(page);
        Assert.Equal("6", Value(page, "total"));
        Assert.Equal("4", Value(page, "returned"));
        Assert.Equal("4", Value(page, "next_offset"));
        Assert.Contains("- has_more: true", page);
        Assert.Equal("s004", MarkdownTestHelper.GetAllValues(page, "name")[0]);

        var rest = tools.ListImagesInCategory(ScanTestTree.Category, "Sub", limit: 4, offset: 4);
        Assert.Equal("2", Value(rest, "returned"));
        Assert.Null(MarkdownTestHelper.GetFirstValue(rest, "next_offset"));
        AssertNothingOpened();
    }

    // ---- the scan guard

    [Fact]
    public void Search_WithoutCategoryAndImage_IsRefused_AndOpensNothing()
    {
        var result = new NavigationTools(_session).SearchByName("needle*");

        MarkdownTestHelper.AssertFailure(result);
        Assert.Equal("category_required", Value(result, "error_code"));
        var message = Value(result, "error");
        Assert.Contains("search_by_name needs the argument 'category' or the argument 'image'", message);
        Assert.Contains("category='<name>'", message);
        Assert.Contains("image='<path of the image inside its category>'", message);
        Assert.Contains("mob (30)", message);                                   // the categories with their image counts
        Assert.Contains("at most 100 images", message);                         // the limit of this session
        AssertNothingOpened();
    }

    [Fact]
    public void Search_OfATooBigCategory_IsRefusedWithScopeTooWide_AndOpensNothing()
    {
        _session.ScanLimits = _session.ScanLimits with { MaxImages = 10 };
        var tools = new NavigationTools(_session);

        foreach (var result in new[] { tools.SearchByName("needle*", ScanTestTree.Category), tools.SearchByValue("needle", category: ScanTestTree.Category) })
        {
            MarkdownTestHelper.AssertFailure(result);
            Assert.Equal("scope_too_wide", Value(result, "error_code"));
            var message = Value(result, "error");
            Assert.StartsWith("scope_too_wide:", message);
            Assert.Contains("30 images", message);
            Assert.Contains("limit of 10", message);
            Assert.Contains("WZIMG_SCAN_MAX_IMAGES", message);
            // An AI client must be able to act on it: which argument to add, with a real example, and where to find more.
            Assert.Contains("category 'Mob' has 30 images", message);
            Assert.Contains("the argument image='<path>' (keep category='Mob')", message);
            Assert.Contains($"image='{_tree.RelativePaths[0]}'", message);   // a real image of the category as the example
            Assert.Contains("list_images_in_category(category='Mob', limit=200, offset=0)", message);
        }
        Assert.Contains("search_by_name", Value(tools.SearchByName("needle*", ScanTestTree.Category), "error"));
        Assert.Contains("search_by_value", Value(tools.SearchByValue("needle", category: ScanTestTree.Category), "error"));
        AssertNothingOpened();
    }

    [Fact]
    public void Search_ByImage_WorksWhateverTheSizeOfTheCategory()
    {
        _session.ScanLimits = _session.ScanLimits with { MaxImages = 1 };

        var result = new NavigationTools(_session).SearchByValue("needle-7", category: ScanTestTree.Category, image: "m007.img");

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("1", Value(result, "scanned_images"));
        Assert.Equal("1", Value(result, "total_images"));
        Assert.Contains("info/name", result);
    }

    [Fact]
    public void Search_UnderTheLimit_ScansEverything_AndLeavesTheCacheAsItWas()
    {
        var result = new NavigationTools(_session).SearchByName("name", ScanTestTree.Category, maxResults: 200);

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("30", Value(result, "scanned_images"));
        Assert.Equal("30", Value(result, "total_images"));
        Assert.Equal("30", Value(result, "total_found"));
        Assert.DoesNotContain("truncated", result);
        Assert.Contains("Sub/s004.img", result);            // the image is named by its path in the category
        Assert.Equal(30, Stats.DiskReadCount);              // every image was read ...
        Assert.Equal(0, Stats.CachedImageCount);            // ... and none was left in the cache
        Assert.Equal(0, Stats.CacheMissCount + Stats.CacheHitCount);
    }

    [Fact]
    public void Search_StopsAtMaxResults_AndSaysSo()
    {
        var result = new NavigationTools(_session).SearchByName("name", ScanTestTree.Category, maxResults: 3);

        Assert.Equal("3", Value(result, "total_found"));
        Assert.Equal("true", Value(result, "truncated"));
        Assert.Equal("max_results", Value(result, "truncated_reason"));
        Assert.Equal("3", Value(result, "scanned_images"));   // it stopped reading as soon as it had enough
        Assert.Equal("30", Value(result, "total_images"));
    }

    [Fact]
    public void Search_EndsWithPartialResults_WhenTheTimeBudgetIsUsedUp()
    {
        // A budget of one tick is over before the second image: the first one is always scanned.
        _session.ScanLimits = _session.ScanLimits with { Budget = TimeSpan.FromTicks(1) };

        var result = new NavigationTools(_session).SearchByName("name", ScanTestTree.Category, maxResults: 200);

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("true", Value(result, "truncated"));
        Assert.Equal("time_budget", Value(result, "truncated_reason"));
        Assert.Equal("1", Value(result, "scanned_images"));
        Assert.Equal("30", Value(result, "total_images"));
        Assert.Equal("1", Value(result, "total_found"));       // the partial result is returned
        Assert.Equal(0, Stats.CachedImageCount);
    }

    [Fact]
    public void Scan_UsesACachedImageAsItIs_AndNeverDisposesOrEvictsIt()
    {
        var edited = _session.GetImage(ScanTestTree.Category, "m001.img");
        edited.AddProperty(new WzIntProperty("onlyInMemory", 1));
        edited.Changed = true;                               // pinned: an unsaved edit
        int readsBefore = Stats.DiskReadCount;

        var result = new NavigationTools(_session).SearchByName("onlyInMemory", ScanTestTree.Category);

        Assert.Contains("onlyInMemory", result);             // the scan saw the edit, not the file on disk
        Assert.Equal(readsBefore + 29, Stats.DiskReadCount); // the other 29 were read from disk
        Assert.Equal(1, Stats.CachedImageCount);             // only the edited one is cached
        Assert.Same(edited, _session.DataSource.GetImage(ScanTestTree.Category, "m001.img"));
        Assert.True(edited.Changed);
        Assert.NotNull(edited.Name);                         // not disposed
        Assert.NotNull(edited["onlyInMemory"]);
    }

    [Fact]
    public void Scan_NeverUnparsesACachedImage()
    {
        var image = _session.GetImage(ScanTestTree.Category, "m002.img");
        image.UnparseImage();
        Assert.False(image.Parsed);

        new NavigationTools(_session).SearchByName("name", ScanTestTree.Category, maxResults: 200);

        // It may be being edited by another request right now, and unparsing an edited image loses the edit.
        Assert.True(image.Parsed);
        Assert.Same(image, _session.DataSource.GetImage(ScanTestTree.Category, "m002.img"));
        Assert.Equal(1, Stats.CachedImageCount);
    }

    [Fact]
    public void BatchSearch_AndFindBrokenUols_AreGuardedToo()
    {
        _session.ScanLimits = _session.ScanLimits with { MaxImages = 10 };
        var batch = new BatchTools(_session);
        var analysis = new AnalysisTools(_session);

        var all = batch.BatchSearch("*name*");               // "all" categories
        Assert.Equal("scope_too_wide", Value(all, "error_code"));
        var one = batch.BatchSearch("*name*", ScanTestTree.Category);
        Assert.Equal("scope_too_wide", Value(one, "error_code"));
        var noScope = analysis.FindBrokenUols();
        Assert.Equal("category_required", Value(noScope, "error_code"));
        var tooWide = analysis.FindBrokenUols(ScanTestTree.Category);
        Assert.Equal("scope_too_wide", Value(tooWide, "error_code"));
        AssertNothingOpened();

        _session.ScanLimits = _session.ScanLimits with { MaxImages = 100 };
        var found = batch.BatchSearch("*name*", ScanTestTree.Category, maxResults: 100);
        MarkdownTestHelper.AssertSuccess(found);
        Assert.Equal("30", Value(found, "scanned_images"));
        var uols = analysis.FindBrokenUols(ScanTestTree.Category);
        MarkdownTestHelper.AssertSuccess(uols);
        Assert.Contains("no/such/target", uols);
        Assert.Equal(0, Stats.CachedImageCount);
    }

    // ---- get_category_summary, get_statistics, get_parsed_images: no parsing

    [Fact]
    public void CategorySummary_ParsesNothing_AndReportsSizes()
    {
        var result = new AnalysisTools(_session).GetCategorySummary(ScanTestTree.Category, limit: 100);

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("30", Value(result, "image_count"));
        long expectedTotal = _tree.RelativePaths.Sum(_tree.FileSize);
        Assert.Equal(expectedTotal.ToString(), Value(result, "total_size_bytes"));
        Assert.Equal(_tree.FileSize("m000.img").ToString(), MarkdownTestHelper.GetAllValues(result, "size_bytes")[_tree.RelativePaths.IndexOf("m000.img")]);
        Assert.DoesNotContain("total_property_count", result);
        Assert.DoesNotContain("canvas_count", result);
        AssertNothingOpened();
    }

    [Fact]
    public void CategorySummary_IsPaged()
    {
        var tools = new AnalysisTools(_session);
        var first = tools.GetCategorySummary(ScanTestTree.Category, limit: 12);
        Assert.Equal("12", Value(first, "returned"));
        Assert.Equal("12", Value(first, "next_offset"));
        Assert.Contains("- has_more: true", first);
        Assert.Equal("30", Value(first, "image_count"));      // the totals describe the whole category

        var last = tools.GetCategorySummary(ScanTestTree.Category, limit: 12, offset: 24);
        Assert.Equal("6", Value(last, "returned"));
        Assert.Null(MarkdownTestHelper.GetFirstValue(last, "next_offset"));
        Assert.Equal(_tree.RelativePaths[24], MarkdownTestHelper.GetAllValues(last, "name")[0]);
        AssertNothingOpened();
    }

    [Fact]
    public void CategorySummary_Detailed_IsRefusedAboveTheLimit_AndOtherwiseCountsWithoutCaching()
    {
        var tools = new AnalysisTools(_session);

        _session.ScanLimits = _session.ScanLimits with { MaxImages = 10 };
        var refused = tools.GetCategorySummary(ScanTestTree.Category, detailed: true);
        MarkdownTestHelper.AssertFailure(refused);
        Assert.Equal("scope_too_wide", Value(refused, "error_code"));
        AssertNothingOpened();

        _session.ScanLimits = _session.ScanLimits with { MaxImages = 100 };
        var detailed = tools.GetCategorySummary(ScanTestTree.Category, detailed: true);
        MarkdownTestHelper.AssertSuccess(detailed);
        Assert.Equal("30", Value(detailed, "scanned_images"));
        Assert.NotEqual("0", Value(detailed, "total_property_count"));
        Assert.Contains("property_count", detailed);
        Assert.Equal(30, Stats.DiskReadCount);
        Assert.Equal(0, Stats.CachedImageCount);
    }

    [Fact]
    public void Statistics_AndParsedImages_DoNotLoadAnything()
    {
        var parsedBefore = new LifecycleTools(_session).GetParsedImages();
        Assert.Equal("0", Value(parsedBefore, "count"));

        var stats = new AnalysisTools(_session).GetStatistics();
        MarkdownTestHelper.AssertSuccess(stats);
        Assert.Equal("30", Value(stats, "total_image_count"));
        Assert.Equal("0", Value(stats, "parsed_image_count"));
        AssertNothingOpened();

        _session.GetImage(ScanTestTree.Category, "m003.img");
        var parsed = new LifecycleTools(_session).GetParsedImages();
        Assert.Equal("1", Value(parsed, "count"));
        Assert.Contains("m003.img", parsed);
        Assert.Equal("1", Value(new AnalysisTools(_session).GetStatistics(), "parsed_image_count"));
    }

    // ---- bulk tools: sanity cap, force, memory

    [Fact]
    public void Preload_IsRefusedAboveTheBulkCap_UnlessForced()
    {
        _session.ScanLimits = _session.ScanLimits with { BulkMaxImages = 10 };
        var tools = new LifecycleTools(_session);

        var refused = tools.PreloadCategory(ScanTestTree.Category);
        Assert.Equal("scope_too_wide", Value(refused, "error_code"));
        Assert.Contains("WZIMG_BULK_MAX_IMAGES", Value(refused, "error"));
        Assert.Contains("force", Value(refused, "error"));
        AssertNothingOpened();

        var forced = tools.PreloadCategory(ScanTestTree.Category, force: true);
        MarkdownTestHelper.AssertSuccess(forced);
        Assert.Equal("30", Value(forced, "loaded_count"));
        Assert.Equal("30", Value(forced, "newly_loaded"));
        Assert.Equal(30, Stats.CachedImageCount);
    }

    [Fact]
    public void Unload_OnlyTouchesCachedImages_AndRefusesUnsavedEdits()
    {
        var lifecycle = new LifecycleTools(_session);
        _session.GetImage(ScanTestTree.Category, "m000.img");
        _session.GetImage(ScanTestTree.Category, "m001.img");
        int reads = Stats.DiskReadCount;

        var edited = _session.GetImage(ScanTestTree.Category, "m001.img");
        edited.Changed = true;
        var refused = lifecycle.UnloadCategory(ScanTestTree.Category);
        MarkdownTestHelper.AssertFailure(refused);
        Assert.Contains("unsaved changes", refused);
        Assert.Equal(2, Stats.CachedImageCount);

        edited.Changed = false;
        var unloaded = lifecycle.UnloadCategory(ScanTestTree.Category);
        MarkdownTestHelper.AssertSuccess(unloaded);
        Assert.Equal("2", Value(unloaded, "unloaded_count"));
        Assert.Equal(0, Stats.CachedImageCount);
        Assert.Equal(reads, Stats.DiskReadCount);            // nothing was loaded to be unloaded
    }

    [Fact]
    public void BatchExport_IsRefusedAboveTheBulkCap_UnlessForced_AndKeepsNothingCached()
    {
        var outDir = Path.Combine(_tree.Path, "out");
        var tools = new BatchTools(_session);
        _session.ScanLimits = _session.ScanLimits with { BulkMaxImages = 10 };

        var refused = tools.BatchExportImages(ScanTestTree.Category, outDir);
        Assert.Equal("scope_too_wide", Value(refused, "error_code"));
        Assert.False(Directory.Exists(outDir));              // nothing was created
        AssertNothingOpened();

        var forced = tools.BatchExportImages(ScanTestTree.Category, outDir, force: true);
        MarkdownTestHelper.AssertSuccess(forced);
        Assert.Equal(30, Stats.DiskReadCount);
        Assert.Equal(0, Stats.CachedImageCount);
    }

    [Fact]
    public void Preload_StopsWhenTheCacheIsFull_NotLoadingWhatWouldPushOutTheLast()
    {
        // 12 images of about 10 MB each by the estimate against the smallest accepted cache (16 MB).
        using var heavy = new ScanTestTree(12, propertiesPerImage: 6000);
        Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.CacheMbEnvVar, "16");
        try
        {
            using var small = heavy.NewSession();
            var result = new LifecycleTools(small).PreloadCategory(ScanTestTree.Category);

            MarkdownTestHelper.AssertSuccess(result);
            Assert.Equal("true", Value(result, "stopped_at_cache_limit"));
            Assert.Equal("12", Value(result, "total_images"));
            int loaded = int.Parse(Value(result, "loaded_count"));
            Assert.InRange(loaded, 1, 11);
            var stats = small.DataSource.GetStats();
            Assert.Equal(loaded, stats.CachedImageCount);
            Assert.True(stats.MemoryUsageBytes <= stats.CacheLimitBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.CacheMbEnvVar, null);
        }
    }

    private void UseHeapOf(long bytes)
    {
        var guard = new MemoryGuard(() => _session.CurrentDataSource, new MemoryGuardSettings(SoftLimitBytes: 1000, IdleTrim: TimeSpan.Zero),
            heapBytes: () => bytes, collect: () => { });
        _session.UseMemoryGuard(guard);
        guard.Tick();   // the guard answers from the reading of its last tick
    }

    [Fact]
    public void Preload_StopsWhenTheHeapIsAboveTheSoftLimit()
    {
        UseHeapOf(5000);

        var result = new LifecycleTools(_session).PreloadCategory(ScanTestTree.Category);

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("true", Value(result, "stopped_at_memory_limit"));
        Assert.Equal("0", Value(result, "newly_loaded"));
        Assert.Equal("30", Value(result, "total_images"));
        Assert.Equal(0, Stats.CachedImageCount);

        UseHeapOf(10);
        var again = new LifecycleTools(_session).PreloadCategory(ScanTestTree.Category);
        Assert.Equal("30", Value(again, "newly_loaded"));
        Assert.DoesNotContain("stopped_at_memory_limit", again);
    }

    [Fact]
    public void BatchExport_StopsWhenTheHeapIsAboveTheSoftLimit()
    {
        UseHeapOf(5000);
        var outDir = Path.Combine(_tree.Path, "out");

        var result = new BatchTools(_session).BatchExportImages(ScanTestTree.Category, outDir);

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Contains("WZIMG_MEMORY_SOFT_LIMIT_MB", result);
        Assert.Equal(1, Stats.DiskReadCount);                // the first image is opened, then the guard says stop
        Assert.Equal(0, Stats.CachedImageCount);
    }

    // ---- the cache limit and the new cache statistics

    [Fact]
    public void CacheLimit_ComesFromTheSetting_AndTheHaCreatorConfigRouteStillWorks()
    {
        Assert.Equal(ServerSettingsDefaultBytes(), Stats.CacheLimitBytes);

        var config = new HaCreatorConfig();
        config.Cache.MaxMemoryCacheMB = 20;
        using var source = new ImgFileSystemDataSource(_tree.Path, config);
        Assert.Equal(20L * 1024 * 1024, source.GetStats().CacheLimitBytes);
    }

    private static long ServerSettingsDefaultBytes() => WzImgMCP.Utils.ServerSettings.CacheMb * 1024L * 1024L;

    [Fact]
    public void CacheStats_ReportHeapWorkingSetPinnedAndTrimTime()
    {
        var image = _session.GetImage(ScanTestTree.Category, "m003.img");
        image.Changed = true;
        _session.GetImage(ScanTestTree.Category, "m005.img");

        var stats = new FileTools(_session).GetCacheStats();

        MarkdownTestHelper.AssertSuccess(stats);
        Assert.True(long.Parse(Value(stats, "managed_heap_bytes")) > 0);
        Assert.True(long.Parse(Value(stats, "process_working_set_bytes")) > 0);
        Assert.Equal("2", Value(stats, "cached_image_count"));
        Assert.Equal("1", Value(stats, "pinned_image_count"));
        Assert.Equal(ServerSettingsDefaultBytes().ToString(), Value(stats, "cache_limit_bytes"));
        Assert.Null(MarkdownTestHelper.GetFirstValue(stats, "last_trim_utc"));   // no trim yet
        Assert.Equal("0", Value(stats, "trim_count"));
    }
}
