using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Core;
using WzImgMCP.Server;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for image lifecycle management (parsing/unparsing)
/// </summary>
[McpServerToolType]
public class LifecycleTools
{
    private readonly WzSessionManager _session;

    public LifecycleTools(WzSessionManager session)
    {
        _session = session;
    }

    [McpServerTool(Name = "parse_image"), Description("Parse an image (load properties into memory)")]
    public string ParseImage(
        [Description("Category name")] string category,
        [Description("Image name")] string image)
    {
        if (!_session.BeginToolCall())
        {
            return new ParseResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var count = _session.ParseImage(category, image);
            return new ParseResult
            {
                Success = true,
                Category = category,
                Image = image,
                PropertyCount = count
            };
        }
        catch (Exception ex)
        {
            return new ParseResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "unparse_image"), Description("Unparse an image (free memory)")]
    public string UnparseImage(
        [Description("Category name")] string category,
        [Description("Image name")] string image)
    {
        if (!_session.BeginToolCall())
        {
            return new UnparseResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            _session.UnparseImage(category, image);
            return new UnparseResult
            {
                Success = true,
                Category = category,
                Image = image
            };
        }
        catch (Exception ex)
        {
            return new UnparseResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "is_image_parsed"), Description("Check if an image is currently parsed")]
    public string IsImageParsed(
        [Description("Category name")] string category,
        [Description("Image name")] string image)
    {
        if (!_session.BeginToolCall())
        {
            return new IsParsedResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var parsed = _session.IsImageParsed(category, image);
            return new IsParsedResult
            {
                Success = true,
                Category = category,
                Image = image,
                IsParsed = parsed
            };
        }
        catch (Exception ex)
        {
            return new IsParsedResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "get_parsed_images"), Description("List all currently parsed images")]
    public string GetParsedImages()
    {
        if (!_session.BeginToolCall())
        {
            return new ParsedImagesResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var images = _session.GetParsedImages();
            return new ParsedImagesResult
            {
                Success = true,
                Count = images.Count,
                Images = images
            };
        }
        catch (Exception ex)
        {
            return new ParsedImagesResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "preload_category"), Description("Preload the images of a category into the cache. Stops when the cache is full (WZIMG_CACHE_MB, default 400) or the managed heap is above WZIMG_MEMORY_SOFT_LIMIT_MB; refused above WZIMG_BULK_MAX_IMAGES (default 2000) images unless force is set.")]
    public string PreloadCategory(
        [Description("Category name")] string category,
        [Description("Preload even when the category holds more images than WZIMG_BULK_MAX_IMAGES (default: false)")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!_session.BeginToolCall())
        {
            return new PreloadResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var ds = _session.DataSource;
            var source = _session.ImgSource;
            ScanGuard.RequireWithinBulkCap(_session, new[] { category }, force,
                "To continue, call preload_category again with the argument force=true to preload anyway (the cache holds only what fits in WZIMG_CACHE_MB, so the call stops when it is full). Preloading is never required: every tool loads an image when it needs it.");

            var paths = ds.GetImagePathsInCategory(category);
            int before = CountCachedParsed(source, category);

            // Load until the cache is (nearly) full: going on would only push out the images loaded a moment ago.
            bool cacheFull = false, memoryLimit = false;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _session.Touch();
                if (_session.MemoryGuard.IsOverSoftLimit())
                {
                    memoryLimit = true;
                    break;
                }

                var stats = ds.GetStats();
                if (stats.CacheLimitBytes > 0 && stats.MemoryUsageBytes >= stats.CacheLimitBytes - stats.CacheLimitBytes / 20)
                {
                    cacheFull = true;
                    break;
                }

                ds.GetImage(category, path);

                // An image that was read from disk and did not make the cache grow pushed another one out: it is full.
                var afterLoad = ds.GetStats();
                if (afterLoad.DiskReadCount > stats.DiskReadCount && afterLoad.CachedImageCount <= stats.CachedImageCount)
                {
                    cacheFull = true;
                    break;
                }
            }

            int after = CountCachedParsed(source, category);
            return new PreloadResult
            {
                Success = true,
                Category = category,
                LoadedCount = after,
                NewlyLoaded = Math.Max(0, after - before),
                TotalImages = paths.Count,
                StoppedAtCacheLimit = cacheFull,
                StoppedAtMemoryLimit = memoryLimit
            };
        }
        catch (Exception ex)
        {
            return new PreloadResult { Success = false, Error = ex.Message, ErrorCode = ToolErrorException.CodeOf(ex) };
        }
    }

    private static int CountCachedParsed(MapleLib.Img.ImgFileSystemDataSource source, string category)
        => source.GetCachedImages().Count(c => c.Image.Parsed && string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));

    [McpServerTool(Name = "unload_category"), Description("Unload the cached images of a category (free memory). Images that are not cached are not touched.")]
    public string UnloadCategory(
        [Description("Category name")] string category)
    {
        if (!_session.BeginToolCall())
        {
            return new UnloadResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var source = _session.ImgSource;
            int unloadedCount = 0;

            // Only the images that are in the cache can hold memory: nothing is loaded to be unloaded.
            var cached = source.GetCachedImages()
                .Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Unparsing a changed image would discard the edits (and a later save would write it empty),
            // so refuse the whole call before touching anything.
            var changed = cached.Where(c => c.Image.Changed).Select(c => c.Image.Name).ToList();
            if (changed.Count > 0)
            {
                var shown = string.Join(", ", changed.Take(5)) + (changed.Count > 5 ? ", ..." : "");
                return new UnloadResult
                {
                    Success = false,
                    Error = $"{WzSessionManager.UnsavedChangesMessage} ({changed.Count} changed image(s): {shown})"
                };
            }

            int skipped = 0;
            foreach (var entry in cached)
            {
                // Under the lock of the image, and the flag is read again under it: an edit tool that started after the check
                // above has flagged the image by now, and its edit must not be unparsed away.
                lock (_session.GateOf(entry.Image))
                {
                    if (entry.Image.Changed)
                    {
                        skipped++;
                        continue;
                    }
                    if (entry.Image.Parsed)
                    {
                        entry.Image.UnparseImage();
                        unloadedCount++;
                    }
                    // Out of the cache as well: an unparsed image left there would keep counting at its old size.
                    source.Manager.RemoveFromCache(entry.Image);
                }
            }

            return new UnloadResult
            {
                Success = true,
                Category = category,
                UnloadedCount = unloadedCount,
                SkippedEditedCount = skipped
            };
        }
        catch (Exception ex)
        {
            return new UnloadResult { Success = false, Error = ex.Message };
        }
    }
}

// Result types

public class ParseResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public string? Image { get; set; }
    public int PropertyCount { get; set; }
}

public class UnparseResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public string? Image { get; set; }
}

public class IsParsedResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public string? Image { get; set; }
    public bool IsParsed { get; set; }
}

public class ParsedImagesResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }
    public List<ParsedImageInfo>? Images { get; set; }
}

public class PreloadResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? Category { get; set; }
    /// <summary>Images of the category that are parsed and in the cache now.</summary>
    public int LoadedCount { get; set; }
    public int NewlyLoaded { get; set; }
    public int TotalImages { get; set; }
    /// <summary>True when the call stopped early because the cache is full (false is left out of the text).</summary>
    public bool StoppedAtCacheLimit { get; set; }
    /// <summary>True when the call stopped early because the managed heap is above WZIMG_MEMORY_SOFT_LIMIT_MB (false is left out of the text).</summary>
    public bool StoppedAtMemoryLimit { get; set; }
}

public class UnloadResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public int UnloadedCount { get; set; }
    /// <summary>Images that were edited after the call began and were therefore left alone .</summary>
    public int SkippedEditedCount { get; set; }
}



