using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Core;
using WzImgMCP.Server;
using WzImgMCP.Utils;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for file and data source operations
/// </summary>
[McpServerToolType]
public class FileTools : ToolBase
{
    public FileTools(WzSessionManager session) : base(session) { }

    [McpServerTool(Name = "init_data_source"), Description("Initialize an IMG filesystem data source from a directory path")]
    public string InitDataSource(
        [Description("Path to the extracted IMG filesystem directory (containing manifest.json)")] string basePath)
    {
        return ExecuteRaw(() =>
        {
            basePath = PathGuard.Resolve(basePath);
            var info = Session.InitDataSource(basePath);
            return new DataSourceData
            {
                Path = info.Path,
                Name = info.Name,
                Version = info.Version,
                DisplayName = info.DisplayName,
                Categories = info.Categories,
                IsPreBB = info.IsPreBB,
                Is64Bit = info.Is64Bit
            };
        });
    }

    [McpServerTool(Name = "scan_img_directories"), Description("Scan a directory for available IMG filesystem data sources")]
    public string ScanImgDirectories(
        [Description("Directory path to scan")] string path,
        [Description("Scan subdirectories recursively (default: true)")] bool recursive = true)
    {
        return ExecuteRaw(() =>
        {
            path = PathGuard.Resolve(path);
            var sources = Session.ScanForDataSources(path, recursive);
            return new ScanData
            {
                DataSources = sources.Select(s => new DataSourceEntry
                {
                    Path = s.Path,
                    Version = s.Version,
                    DisplayName = s.DisplayName,
                    SizeBytes = s.Size,
                    SizeFormatted = FormatSize(s.Size)
                }).ToList()
            };
        });
    }

    [McpServerTool(Name = "get_data_source_info"), Description("Get information about the currently loaded data source")]
    public string GetDataSourceInfo()
    {
        return Execute(() =>
        {
            var ds = Session.DataSource;
            var versionInfo = Session.VersionInfo;
            var stats = ds.GetStats();

            return new DataSourceInfoData
            {
                Name = ds.Name,
                Version = versionInfo?.Version,
                DisplayName = versionInfo?.DisplayName,
                SourceRegion = versionInfo?.SourceRegion,
                IsPreBB = versionInfo?.IsPreBB ?? false,
                Is64Bit = versionInfo?.Is64Bit ?? false,
                CategoryCount = stats.CategoryCount,
                ImageCount = stats.ImageCount,
                CachedImageCount = stats.CachedImageCount,
                CacheHitRatio = stats.CacheHitRatio,
                AutoInitialized = Session.AutoInitialized ? "true" : "false"
            };
        });
    }

    [McpServerTool(Name = "list_categories"), Description("List all available categories in the current data source")]
    public string ListCategories()
    {
        return Execute(() =>
        {
            var ds = Session.DataSource;
            var categories = ds.GetCategories().ToList();

            return new CategoryListData
            {
                Categories = categories.Select(c => new CategoryEntry
                {
                    Name = c,
                    Exists = ds.CategoryExists(c)
                }).ToList()
            };
        });
    }

    [McpServerTool(Name = "list_images_in_category"), Description("List the .img files of a category, one page at a time (no image is opened). Sorted by name; use offset/nextOffset to read on.")]
    public string ListImagesInCategory(
        [Description("Category name (e.g., 'Map', 'Mob', 'Npc')")] string category,
        [Description("Subdirectory within the category (optional)")] string? subdirectory = null,
        [Description("Maximum images to return (default: 200, max: 1000)")] int limit = Paging.DefaultLimit,
        [Description("Number of images to skip (default: 0)")] int offset = 0)
    {
        return Execute(() =>
        {
            var ds = Session.DataSource;
            (limit, offset) = Paging.Normalize(limit, offset);
            List<ImageEntry> page;
            int total;

            if (string.IsNullOrEmpty(subdirectory))
            {
                // Names only: the paths come from the category index, no image is loaded or parsed.
                var paths = ds.GetImagePathsInCategory(category);
                total = paths.Count;
                page = paths.Skip(offset).Take(limit).Select(p =>
                {
                    var relative = p.Replace('\\', '/');
                    return new ImageEntry { Name = relative[(relative.LastIndexOf('/') + 1)..], FullPath = $"{category}/{relative}" };
                }).ToList();
            }
            else
            {
                var names = ds.GetImageNamesInDirectory(category, subdirectory).OrderBy(n => n, StringComparer.Ordinal).ToList();
                total = names.Count;
                page = names.Skip(offset).Take(limit).Select(n => new ImageEntry { Name = n, FullPath = $"{category}/{subdirectory}/{n}" }).ToList();
            }

            return new ImageListData
            {
                Category = category,
                Subdirectory = subdirectory,
                Total = total,
                Returned = page.Count,
                Offset = offset,
                Limit = limit,
                HasMore = offset + page.Count < total,
                NextOffset = Paging.NextOffset(offset, page.Count, total),
                Images = page
            };
        });
    }

    [McpServerTool(Name = "get_cache_stats"), Description("Get cache and memory statistics for the current data source")]
    public string GetCacheStats()
    {
        return Execute(() =>
        {
            var stats = Session.DataSource.GetStats();
            var trim = Session.MemoryGuard.LastTrim;
            return new CacheStatsData
            {
                CachedImageCount = stats.CachedImageCount,
                PinnedImageCount = stats.PinnedImageCount,
                CacheHitCount = stats.CacheHitCount,
                CacheMissCount = stats.CacheMissCount,
                CacheHitRatio = stats.CacheHitRatio,
                DiskReadCount = stats.DiskReadCount,
                MemoryUsageBytes = stats.MemoryUsageBytes,
                MemoryUsageFormatted = FormatSize(stats.MemoryUsageBytes),
                CacheLimitBytes = stats.CacheLimitBytes,
                ManagedHeapBytes = GC.GetTotalMemory(false),
                ProcessWorkingSetBytes = Environment.WorkingSet,
                TrimCount = Session.MemoryGuard.TrimCount,
                LastTrimUtc = trim?.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                LastTrimReason = trim?.Reason,
                LastTrimReleasedImages = trim?.ReleasedImages
            };
        }, countsAsActivity: false);   // a monitoring loop must not count as activity (the idle trim would never run)
    }

    [McpServerTool(Name = "clear_cache"), Description("Clear the loaded image cache")]
    public string ClearCache()
    {
        return Execute(() =>
        {
            var beforeStats = Session.DataSource.GetStats();
            Session.DataSource.ClearCache();
            var afterStats = Session.DataSource.GetStats();

            return new ClearCacheData
            {
                ClearedCount = beforeStats.CachedImageCount - afterStats.CachedImageCount
            };
        });
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}

// Data types (no Success/Error - that's in Result<T>)

public class DataSourceData
{
    public string? Path { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? DisplayName { get; init; }
    public List<string>? Categories { get; init; }
    public bool IsPreBB { get; init; }
    public bool Is64Bit { get; init; }
}

public class ScanData
{
    public required List<DataSourceEntry> DataSources { get; init; }
}

public class DataSourceEntry
{
    public required string Path { get; init; }
    public string? Version { get; init; }
    public string? DisplayName { get; init; }
    public long SizeBytes { get; init; }
    public string? SizeFormatted { get; init; }
}

public class DataSourceInfoData
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? DisplayName { get; init; }
    public string? SourceRegion { get; init; }
    public bool IsPreBB { get; init; }
    public bool Is64Bit { get; init; }
    public int CategoryCount { get; init; }
    public int ImageCount { get; init; }
    public int CachedImageCount { get; init; }
    public double CacheHitRatio { get; init; }
    /// <summary>"true" when the data folder was opened automatically (WZIMG_DEFAULT_DATA_PATH), "false" after init_data_source.
    /// A string, because the markdown formatter leaves out boolean false.</summary>
    public string AutoInitialized { get; init; } = "false";
}

public class CategoryListData
{
    public required List<CategoryEntry> Categories { get; init; }
}

public class CategoryEntry
{
    public required string Name { get; init; }
    public bool Exists { get; init; }
}

public class ImageListData
{
    public string? Category { get; init; }
    public string? Subdirectory { get; init; }
    /// <summary>All images of the category (or subdirectory), not only this page.</summary>
    public int Total { get; init; }
    public int Returned { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
    /// <summary>True when more images follow this page; false is left out of the text (as for every flag), then there is no nextOffset either.</summary>
    public bool HasMore { get; init; }
    /// <summary>The offset to ask for the next page; absent on the last page.</summary>
    public int? NextOffset { get; init; }
    public required List<ImageEntry> Images { get; init; }
}

public class ImageEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
}

public class CacheStatsData
{
    public int CachedImageCount { get; init; }
    /// <summary>Cached images with unsaved edits: never evicted or trimmed.</summary>
    public int PinnedImageCount { get; init; }
    public int CacheHitCount { get; init; }
    public int CacheMissCount { get; init; }
    public double CacheHitRatio { get; init; }
    public int DiskReadCount { get; init; }
    public long MemoryUsageBytes { get; init; }
    public string? MemoryUsageFormatted { get; init; }
    /// <summary>The size limit of the image cache (WZIMG_CACHE_MB), in the same estimated bytes as memory_usage_bytes.</summary>
    public long CacheLimitBytes { get; init; }
    /// <summary>Managed heap of the server (GC.GetTotalMemory), garbage not yet collected included.</summary>
    public long ManagedHeapBytes { get; init; }
    public long ProcessWorkingSetBytes { get; init; }
    public long TrimCount { get; init; }
    public string? LastTrimUtc { get; init; }
    public string? LastTrimReason { get; init; }
    public int? LastTrimReleasedImages { get; init; }
}

public class ClearCacheData
{
    public int ClearedCount { get; init; }
}

