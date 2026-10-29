using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Core;
using WzImgMCP.Server;
using WzImgMCP.Utils;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for analysis and statistics
/// </summary>
[McpServerToolType]
public class AnalysisTools
{
    private readonly WzSessionManager _session;

    public AnalysisTools(WzSessionManager session)
    {
        _session = session;
    }

    [McpServerTool(Name = "get_statistics"), Description("Get overall statistics for the data source")]
    public string GetStatistics()
    {
        if (!_session.IsInitialized)
        {
            return new StatisticsResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var ds = _session.DataSource;
            var stats = ds.GetStats();
            var categories = ds.GetCategories().ToList();

            // Counts come from the category index and the cache: no image is opened to answer this.
            var cached = _session.ImgSource.GetCachedImages();
            var categoryStats = new List<CategoryStats>();
            int totalImages = 0;
            int totalParsed = 0;

            foreach (var cat in categories)
            {
                int imageCount = ds.GetImagePathsInCategory(cat).Count;
                int parsedCount = cached.Count(c => c.Image.Parsed && string.Equals(c.Category, cat, StringComparison.OrdinalIgnoreCase));
                totalImages += imageCount;
                totalParsed += parsedCount;

                categoryStats.Add(new CategoryStats
                {
                    Name = cat,
                    ImageCount = imageCount,
                    ParsedCount = parsedCount
                });
            }

            return new StatisticsResult
            {
                Success = true,
                CategoryCount = categories.Count,
                TotalImageCount = totalImages,
                ParsedImageCount = totalParsed,
                CacheHitCount = stats.CacheHitCount,
                CacheMissCount = stats.CacheMissCount,
                CacheHitRatio = stats.CacheHitRatio,
                DiskReadCount = stats.DiskReadCount,
                MemoryUsageBytes = stats.MemoryUsageBytes,
                Categories = categoryStats
            };
        }
        catch (Exception ex)
        {
            return new StatisticsResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "get_category_summary"), Description("Summary of a category from the file system, without opening any image: image count, total file size, subdirectories and one page of images with their file size. detailed=true also counts properties, canvases and sounds (opens every image of the page; refused for a category above WZIMG_SCAN_MAX_IMAGES images).")]
    public string GetCategorySummary(
        [Description("Category name")] string category,
        [Description("Also count properties, canvases and sounds per image (opens the images; default: false)")] bool detailed = false,
        [Description("Maximum images to list (default: 200, max: 1000)")] int limit = Paging.DefaultLimit,
        [Description("Number of images to skip (default: 0)")] int offset = 0)
    {
        if (!_session.IsInitialized)
        {
            return new CategorySummaryResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var ds = _session.DataSource;
            var source = _session.ImgSource;
            (limit, offset) = Paging.Normalize(limit, offset);

            var paths = ds.GetImagePathsInCategory(category);
            var subdirs = ds.GetSubdirectories(category).ToList();
            var page = paths.Skip(offset).Take(limit).ToList();

            long totalSize = 0;
            foreach (var path in paths)
                totalSize += Math.Max(0, source.GetImageFileSize(category, path));

            var imageSummaries = page.Select(p => new ImageSummary
            {
                Name = p.Replace('\\', '/'),
                SizeBytes = Math.Max(0, source.GetImageFileSize(category, p))
            }).ToList();

            int? propertyCount = null, canvasCount = null, soundCount = null;
            ScanOutcome? scan = null;
            if (detailed)
            {
                // Opens every image of the page: refused when the category is too big, and run under the time budget.
                var plan = ScanGuard.PlanScan(_session, new[] { category },
                    "Call get_category_summary without detailed (file sizes only), or analyse one image with validate_image.");
                var pagePlan = new ScanPlan { Items = plan.Items.Skip(offset).Take(limit).ToList() };
                var byName = imageSummaries.ToDictionary(i => i.Name, StringComparer.Ordinal);
                int props = 0, canvases = 0, sounds = 0;

                scan = ScanGuard.Run(_session, pagePlan, (img, cat, name) =>
                {
                    var summary = byName[name];
                    summary.PropertyCount = CountPropertiesRecursive(img, 0, 5);
                    summary.CanvasCount = CountCanvasesRecursive(img, 0, 10);
                    summary.SoundCount = CountSoundsRecursive(img, 0, 10);
                    props += summary.PropertyCount.Value;
                    canvases += summary.CanvasCount.Value;
                    sounds += summary.SoundCount.Value;
                    return false;
                });
                propertyCount = props;
                canvasCount = canvases;
                soundCount = sounds;
            }

            return new CategorySummaryResult
            {
                Success = true,
                Category = category,
                ImageCount = paths.Count,
                TotalSizeBytes = totalSize,
                SubdirectoryCount = subdirs.Count,
                Subdirectories = subdirs.Take(Paging.DefaultLimit).ToList(),
                TotalPropertyCount = propertyCount,
                TotalCanvasCount = canvasCount,
                TotalSoundCount = soundCount,
                Returned = imageSummaries.Count,
                Offset = offset,
                Limit = limit,
                HasMore = offset + imageSummaries.Count < paths.Count,
                NextOffset = Paging.NextOffset(offset, imageSummaries.Count, paths.Count),
                Images = imageSummaries,
                Truncated = scan?.BudgetExhausted ?? false,
                ScannedImages = scan?.Scanned,
                TotalImages = scan?.Total
            };
        }
        catch (Exception ex)
        {
            return new CategorySummaryResult { Success = false, Error = ex.Message, ErrorCode = ToolErrorException.CodeOf(ex) };
        }
    }

    [McpServerTool(Name = "find_broken_uols"), Description("Find UOL references that don't resolve to valid targets. Give image (the path of one image inside its category) to check one image, or category to check every image of it (only when it has at most WZIMG_SCAN_MAX_IMAGES, default 150, images). One of the two is required.")]
    public string FindBrokenUols(
        [Description("Category to check; every image of it is opened, so it may have at most 150 images (required unless image is given)")] string? category = null,
        [Description("Path of one image inside the category, as list_images_in_category shows it; checks only that image, whatever the size of the category")] string? image = null,
        [Description("Maximum results to return (default: 30)")] int maxResults = 30)
    {
        if (!_session.IsInitialized)
        {
            return new BrokenUolsResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var brokenUols = new List<BrokenUol>();

            var plan = ScanGuard.PlanSearch(_session, category, image, "find_broken_uols");
            var scan = ScanGuard.Run(_session, plan, (img, cat, name) =>
            {
                FindBrokenUolsRecursive(img, cat, name, "", brokenUols, maxResults);
                return brokenUols.Count >= maxResults;
            });

            return new BrokenUolsResult
            {
                Success = true,
                Count = brokenUols.Count,
                Truncated = brokenUols.Count >= maxResults || scan.BudgetExhausted,
                TruncatedReason = scan.BudgetExhausted ? "time_budget" : brokenUols.Count >= maxResults ? "max_results" : null,
                ScannedImages = scan.Scanned,
                TotalImages = scan.Total,
                BrokenUols = brokenUols
            };
        }
        catch (Exception ex)
        {
            return new BrokenUolsResult { Success = false, Error = ex.Message, ErrorCode = ToolErrorException.CodeOf(ex) };
        }
    }

    [McpServerTool(Name = "compare_properties"), Description("Compare two property trees and find differences")]
    public string CompareProperties(
        [Description("First category")] string category1,
        [Description("First image")] string image1,
        [Description("First path")] string path1,
        [Description("Second category")] string category2,
        [Description("Second image")] string image2,
        [Description("Second path")] string path2,
        [Description("Maximum depth to compare")] int maxDepth = 5)
    {
        if (!_session.IsInitialized)
        {
            return new CompareResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var img1 = _session.GetImage(category1, image1);
            var img2 = _session.GetImage(category2, image2);

            var prop1 = img1.GetFromPath(path1);
            var prop2 = img2.GetFromPath(path2);

            if (prop1 == null)
            {
                return new CompareResult { Success = false, Error = $"Property not found: {path1}" };
            }
            if (prop2 == null)
            {
                return new CompareResult { Success = false, Error = $"Property not found: {path2}" };
            }

            var differences = new List<PropertyDifference>();
            CompareRecursive(prop1, prop2, "", differences, 0, maxDepth);

            return new CompareResult
            {
                Success = true,
                Path1 = $"{category1}/{image1}/{path1}",
                Path2 = $"{category2}/{image2}/{path2}",
                DifferenceCount = differences.Count,
                Differences = differences
            };
        }
        catch (Exception ex)
        {
            return new CompareResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "get_version_info"), Description("Get version information for the current data source")]
    public string GetVersionInfo()
    {
        if (!_session.IsInitialized)
        {
            return new VersionInfoResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var versionInfo = _session.VersionInfo;
            var ds = _session.DataSource;

            return new VersionInfoResult
            {
                Success = true,
                Name = ds.Name,
                Version = versionInfo?.Version ?? "unknown",
                DisplayName = versionInfo?.DisplayName,
                SourceRegion = versionInfo?.SourceRegion,
                IsPreBB = versionInfo?.IsPreBB ?? false,
                Is64Bit = versionInfo?.Is64Bit ?? false,
                Features = null // Reserved for future use
            };
        }
        catch (Exception ex)
        {
            return new VersionInfoResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "validate_image"), Description("Validate an image structure for common issues")]
    public string ValidateImage(
        [Description("Category name")] string category,
        [Description("Image name")] string image)
    {
        if (!_session.IsInitialized)
        {
            return new ValidationResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var img = _session.GetImage(category, image);
            var issues = new List<ValidationIssue>();
            var stats = new ValidationStats();

            ValidateRecursive(img, "", issues, stats, 0, 20);

            return new ValidationResult
            {
                Success = true,
                Category = category,
                Image = image,
                IsValid = issues.Count == 0,
                IssueCount = issues.Count,
                Stats = stats,
                Issues = issues
            };
        }
        catch (Exception ex)
        {
            return new ValidationResult { Success = false, Error = ex.Message };
        }
    }

    private int CountPropertiesRecursive(WzObject obj, int depth, int maxDepth)
    {
        if (depth > maxDepth) return 0;

        int count = 0;
        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            count = 1;
            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                count += CountPropertiesRecursive(child, depth + 1, maxDepth);
            }
        }

        return count;
    }

    private int CountCanvasesRecursive(WzObject obj, int depth, int maxDepth)
    {
        if (depth > maxDepth) return 0;

        int count = 0;
        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            if (prop is WzCanvasProperty) count = 1;
            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                count += CountCanvasesRecursive(child, depth + 1, maxDepth);
            }
        }

        return count;
    }

    private int CountSoundsRecursive(WzObject obj, int depth, int maxDepth)
    {
        if (depth > maxDepth) return 0;

        int count = 0;
        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            if (prop is WzBinaryProperty) count = 1;
            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                count += CountSoundsRecursive(child, depth + 1, maxDepth);
            }
        }

        return count;
    }

    private void FindBrokenUolsRecursive(WzObject obj, string category, string imageName, string path,
        List<BrokenUol> results, int maxResults)
    {
        if (results.Count >= maxResults) return;

        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            if (prop is WzUOLProperty uol)
            {
                try
                {
                    var target = uol.LinkValue;
                    if (target == null)
                    {
                        results.Add(new BrokenUol
                        {
                            Category = category,
                            Image = imageName,
                            Path = path,
                            UolValue = uol.Value,
                            Reason = "Target not found"
                        });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new BrokenUol
                    {
                        Category = category,
                        Image = imageName,
                        Path = path,
                        UolValue = uol.Value,
                        Reason = ex.Message
                    });
                }
            }
            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                if (results.Count >= maxResults) return;
                var childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                FindBrokenUolsRecursive(child, category, imageName, childPath, results, maxResults);
            }
        }
    }

    private void CompareRecursive(WzObject obj1, WzObject obj2, string path,
        List<PropertyDifference> differences, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        // Compare types
        var type1 = obj1 is WzImageProperty p1 ? p1.PropertyType.ToString() : obj1.GetType().Name;
        var type2 = obj2 is WzImageProperty p2 ? p2.PropertyType.ToString() : obj2.GetType().Name;

        if (type1 != type2)
        {
            differences.Add(new PropertyDifference
            {
                Path = path,
                Type = "TypeMismatch",
                Value1 = type1,
                Value2 = type2
            });
            return;
        }

        // Compare values
        if (obj1 is WzImageProperty prop1 && obj2 is WzImageProperty prop2)
        {
            var val1 = WzDataConverter.GetPropertyValue(prop1);
            var val2 = WzDataConverter.GetPropertyValue(prop2);

            var str1 = val1?.ToString() ?? "null";
            var str2 = val2?.ToString() ?? "null";

            if (str1 != str2)
            {
                differences.Add(new PropertyDifference
                {
                    Path = path,
                    Type = "ValueDifference",
                    Value1 = str1,
                    Value2 = str2
                });
            }
        }

        // Compare children
        var children1 = GetChildren(obj1).ToDictionary(c => c.Name);
        var children2 = GetChildren(obj2).ToDictionary(c => c.Name);

        foreach (var name in children1.Keys.Union(children2.Keys))
        {
            var childPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

            if (!children1.ContainsKey(name))
            {
                differences.Add(new PropertyDifference
                {
                    Path = childPath,
                    Type = "OnlyInSecond",
                    Value2 = children2[name] is WzImageProperty cp ? cp.PropertyType.ToString() : "?"
                });
            }
            else if (!children2.ContainsKey(name))
            {
                differences.Add(new PropertyDifference
                {
                    Path = childPath,
                    Type = "OnlyInFirst",
                    Value1 = children1[name] is WzImageProperty cp ? cp.PropertyType.ToString() : "?"
                });
            }
            else
            {
                CompareRecursive(children1[name], children2[name], childPath, differences, depth + 1, maxDepth);
            }
        }
    }

    private void ValidateRecursive(WzObject obj, string path, List<ValidationIssue> issues,
        ValidationStats stats, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            stats.TotalProperties++;
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            stats.TotalProperties++;

            switch (prop)
            {
                case WzCanvasProperty canvas:
                    stats.CanvasCount++;
                    try
                    {
                        if (!WzImgMCP.Imaging.CanvasImaging.HasDecodableImage(canvas))
                        {
                            issues.Add(new ValidationIssue
                            {
                                Path = path,
                                Severity = "Warning",
                                Message = "Canvas has null bitmap"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        issues.Add(new ValidationIssue
                        {
                            Path = path,
                            Severity = "Error",
                            Message = $"Failed to load canvas: {ex.Message}"
                        });
                    }
                    break;

                case WzBinaryProperty sound:
                    stats.SoundCount++;
                    if (sound.Length == 0)
                    {
                        issues.Add(new ValidationIssue
                        {
                            Path = path,
                            Severity = "Warning",
                            Message = "Sound has zero length"
                        });
                    }
                    break;

                case WzUOLProperty uol:
                    stats.UolCount++;
                    try
                    {
                        var target = uol.LinkValue;
                        if (target == null)
                        {
                            issues.Add(new ValidationIssue
                            {
                                Path = path,
                                Severity = "Error",
                                Message = $"Broken UOL reference: {uol.Value}"
                            });
                        }
                    }
                    catch
                    {
                        issues.Add(new ValidationIssue
                        {
                            Path = path,
                            Severity = "Error",
                            Message = $"Failed to resolve UOL: {uol.Value}"
                        });
                    }
                    break;

                case WzStringProperty str:
                    stats.StringCount++;
                    break;

                case WzIntProperty:
                case WzShortProperty:
                case WzLongProperty:
                    stats.IntCount++;
                    break;

                case WzFloatProperty:
                case WzDoubleProperty:
                    stats.FloatCount++;
                    break;

                case WzVectorProperty:
                    stats.VectorCount++;
                    break;

                case WzSubProperty:
                    stats.SubPropertyCount++;
                    break;
            }

            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                var childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                ValidateRecursive(child, childPath, issues, stats, depth + 1, maxDepth);
            }
        }
    }

    private static IEnumerable<WzObject> GetChildren(WzObject obj)
    {
        if (obj is WzImage img)
        {
            return img.WzProperties?.Cast<WzObject>() ?? Enumerable.Empty<WzObject>();
        }
        else if (obj is WzImageProperty prop && prop.WzProperties != null)
        {
            return prop.WzProperties.Cast<WzObject>();
        }
        return Enumerable.Empty<WzObject>();
    }
}

// Result types

public class StatisticsResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int CategoryCount { get; set; }
    public int TotalImageCount { get; set; }
    public int ParsedImageCount { get; set; }
    public int CacheHitCount { get; set; }
    public int CacheMissCount { get; set; }
    public double CacheHitRatio { get; set; }
    public int DiskReadCount { get; set; }
    public long MemoryUsageBytes { get; set; }
    public List<CategoryStats>? Categories { get; set; }
}

public class CategoryStats
{
    public required string Name { get; set; }
    public int ImageCount { get; set; }
    public int ParsedCount { get; set; }
}

public class CategorySummaryResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? Category { get; set; }
    /// <summary>All images of the category.</summary>
    public int ImageCount { get; set; }
    /// <summary>File size of all images of the category together.</summary>
    public long TotalSizeBytes { get; set; }
    public int SubdirectoryCount { get; set; }
    public List<string>? Subdirectories { get; set; }
    /// <summary>The three totals count the images of this page and are only given with detailed=true.</summary>
    public int? TotalPropertyCount { get; set; }
    public int? TotalCanvasCount { get; set; }
    public int? TotalSoundCount { get; set; }
    public int Returned { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    /// <summary>True when more images follow this page (false is left out of the text).</summary>
    public bool HasMore { get; set; }
    public int? NextOffset { get; set; }
    public List<ImageSummary>? Images { get; set; }
    /// <summary>detailed=true only: the time budget ended the scan before every image of the page was counted.</summary>
    public bool Truncated { get; set; }
    public int? ScannedImages { get; set; }
    public int? TotalImages { get; set; }
}

public class ImageSummary
{
    /// <summary>Path of the image inside the category.</summary>
    public required string Name { get; set; }
    public long SizeBytes { get; set; }
    public int? PropertyCount { get; set; }
    public int? CanvasCount { get; set; }
    public int? SoundCount { get; set; }
}

public class BrokenUolsResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public int Count { get; set; }
    public bool Truncated { get; set; }
    public string? TruncatedReason { get; set; }
    public int ScannedImages { get; set; }
    public int TotalImages { get; set; }
    public List<BrokenUol>? BrokenUols { get; set; }
}

public class BrokenUol
{
    public required string Category { get; set; }
    public required string Image { get; set; }
    public required string Path { get; set; }
    public required string UolValue { get; set; }
    public required string Reason { get; set; }
}

public class CompareResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Path1 { get; set; }
    public string? Path2 { get; set; }
    public int DifferenceCount { get; set; }
    public List<PropertyDifference>? Differences { get; set; }
}

public class PropertyDifference
{
    public required string Path { get; set; }
    public required string Type { get; set; }
    public string? Value1 { get; set; }
    public string? Value2 { get; set; }
}

public class VersionInfoResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? DisplayName { get; set; }
    public string? SourceRegion { get; set; }
    public bool IsPreBB { get; set; }
    public bool Is64Bit { get; set; }
    public List<string>? Features { get; set; }
}

public class ValidationResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public string? Image { get; set; }
    public bool IsValid { get; set; }
    public int IssueCount { get; set; }
    public ValidationStats? Stats { get; set; }
    public List<ValidationIssue>? Issues { get; set; }
}

public class ValidationStats
{
    public int TotalProperties { get; set; }
    public int CanvasCount { get; set; }
    public int SoundCount { get; set; }
    public int UolCount { get; set; }
    public int StringCount { get; set; }
    public int IntCount { get; set; }
    public int FloatCount { get; set; }
    public int VectorCount { get; set; }
    public int SubPropertyCount { get; set; }
}

public class ValidationIssue
{
    public required string Path { get; set; }
    public required string Severity { get; set; }
    public required string Message { get; set; }
}



