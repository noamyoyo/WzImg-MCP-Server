using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Core;
using WzImgMCP.Server;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using WzImgMCP.Utils;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for batch operations and WZ conversion
/// </summary>
[McpServerToolType]
public class BatchTools
{
    private readonly WzSessionManager _session;

    public BatchTools(WzSessionManager session)
    {
        _session = session;
    }

    [McpServerTool(Name = "extract_to_img"), Description("Extract WZ files to IMG filesystem format")]
    public string ExtractToImg(
        [Description("Path to WZ file or directory containing WZ files")] string wzPath,
        [Description("Output directory for IMG filesystem")] string outputDir,
        [Description("Version key for WZ decryption (empty for auto-detect)")] string? versionKey = null,
        [Description("Create version manifest")] bool createManifest = true,
        [Description("Categories to extract (comma-separated, optional). For a single WZ file this is inferred from the file name.")] string? categories = null,
        [Description("Version id for the extracted IMG filesystem manifest")] string? versionId = null,
        [Description("Display name for the extracted IMG filesystem manifest")] string? displayName = null,
        [Description("Resolve _inlink/_outlink canvas references during extraction")] bool resolveLinks = false)
    {
        _session.Touch();   // a real tool call, although it needs no open data source
        try
        {
            wzPath = PathGuard.Resolve(wzPath);
            outputDir = PathGuard.Resolve(outputDir);
            if (!Directory.Exists(wzPath) && !File.Exists(wzPath))
            {
                return new ExtractResult { Success = false, Error = $"Path not found: {wzPath}" };
            }

            // The category names become file and directory names (input {category}.wz, output below outputDir):
            // refuse anything but plain names before a directory is created.
            var categoriesToExtract = GetExtractionCategories(wzPath, categories);
            RequireSafeCategories(categoriesToExtract);

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var mapleStoryPath = File.Exists(wzPath)
                ? Path.GetDirectoryName(Path.GetFullPath(wzPath)) ?? Directory.GetCurrentDirectory()
                : Path.GetFullPath(wzPath);
            var encryption = ResolveEncryption(versionKey, wzPath);
            var effectiveVersionId = string.IsNullOrWhiteSpace(versionId)
                ? $"extracted_{DateTime.UtcNow:yyyyMMddHHmmss}"
                : versionId.Trim();
            var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? $"Extracted from {Path.GetFileName(mapleStoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}"
                : displayName.Trim();

            var extractor = new WzExtractionService();
            var result = categoriesToExtract.Count > 0
                ? extractor.ExtractAsync(
                    mapleStoryPath,
                    outputDir,
                    effectiveVersionId,
                    effectiveDisplayName,
                    encryption,
                    categoriesToExtract,
                    resolveLinks).GetAwaiter().GetResult()
                : extractor.ExtractAsync(
                    mapleStoryPath,
                    outputDir,
                    effectiveVersionId,
                    effectiveDisplayName,
                    encryption,
                    resolveLinks).GetAwaiter().GetResult();

            var errors = result.CategoriesExtracted.Values
                .SelectMany(c => c.Errors.Select(e => $"{c.CategoryName}: {e}"))
                .ToList();

            return new ExtractResult
            {
                Success = result.Success && errors.Count == 0,
                Error = result.ErrorMessage ?? (errors.Count > 0 ? "Extraction completed with category errors" : null),
                OutputDirectory = outputDir,
                CategoriesExtracted = result.CategoriesExtracted.Count,
                ImagesExtracted = result.TotalImagesExtracted,
                TotalSize = result.TotalSize,
                DurationSeconds = result.Duration.TotalSeconds,
                ManifestCreated = createManifest && File.Exists(Path.Combine(outputDir, "manifest.json")),
                ExtractedCategories = result.CategoriesExtracted.Keys.OrderBy(c => c).ToList(),
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            return new ExtractResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "pack_to_wz"), Description("Pack IMG filesystem back to WZ files")]
    public string PackToWz(
        [Description("Path to IMG filesystem directory")] string imgPath,
        [Description("Output directory for WZ files")] string outputDir,
        [Description("WZ version to create")] int wzVersion = 83,
        [Description("Category to pack (optional - packs all if not specified)")] string? category = null,
        [Description("WZ encryption/version key (GMS, EMS, BMS, CLASSIC; empty uses manifest/default)")] string? versionKey = null,
        [Description("Save as 64-bit WZ format")] bool saveAs64Bit = false,
        [Description("Separate canvas data for 64-bit WZ format")] bool separateCanvas = false)
    {
        _session.Touch();   // a real tool call, although it needs no open data source
        try
        {
            imgPath = PathGuard.Resolve(imgPath);
            outputDir = PathGuard.Resolve(outputDir);
            if (!Directory.Exists(imgPath))
            {
                return new PackResult { Success = false, Error = $"Directory not found: {imgPath}" };
            }

            // The category names become output file and directory names ({category}.wz, Data/{category}):
            // only plain names are accepted, and every output path is resolved through the path guard
            // (symlinks followed) before a directory is created or the packer writes anything. GuardPackOutputs also
            // refuses symbolic links found inside Data/<category> and Data/<category>/_Canvas that lead outside the
            // root; the packer's own writes (MapleLib NoFollowFile) never write through a link either way.
            var categoriesToPack = GetPackingCategories(imgPath, category);
            RequireSafeCategories(categoriesToPack);
            if (categoriesToPack.Count == 0)
            {
                return new PackResult { Success = false, Error = $"No IMG categories found in {imgPath}" };
            }

            GuardPackOutputs(outputDir, categoriesToPack);

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var existingWzFiles = Directory.EnumerateFiles(outputDir, "*.wz", SearchOption.TopDirectoryOnly)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var packer = new WzPackingService();
            var result = packer.PackCategoriesAsync(
                imgPath,
                outputDir,
                categoriesToPack,
                saveAs64Bit,
                overridePatchVersion: ToPatchVersion(wzVersion),
                separateCanvas: separateCanvas,
                overrideEncryption: ParseEncryption(versionKey)).GetAwaiter().GetResult();

            var createdFiles = Directory.EnumerateFiles(outputDir, "*.wz", SearchOption.TopDirectoryOnly)
                .Where(path => !existingWzFiles.Contains(path))
                .OrderBy(path => path)
                .ToList();
            if (createdFiles.Count == 0)
            {
                createdFiles = result.CategoriesPacked.Values
                    .Select(r => r.OutputFilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path)
                    .ToList()!;
            }

            var errors = result.CategoriesPacked.Values
                .SelectMany(c => c.Errors.Select(e => $"{c.CategoryName}: {e}"))
                .ToList();

            return new PackResult
            {
                Success = result.Success && createdFiles.Count > 0 && errors.Count == 0,
                Error = result.ErrorMessage ?? (createdFiles.Count == 0 ? "Packing completed without creating any WZ files" : null),
                OutputDirectory = outputDir,
                FilesCreated = createdFiles.Count,
                TotalSize = createdFiles.Count > 0 ? createdFiles.Sum(path => new FileInfo(path).Length) : result.TotalOutputSize,
                ImagesPacked = result.TotalImagesPacked,
                DurationSeconds = result.Duration.TotalSeconds,
                PackedCategories = result.CategoriesPacked.Keys.OrderBy(c => c).ToList(),
                CreatedFiles = createdFiles,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            return new PackResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "batch_export_images"), Description("Export all images from multiple categories. Refused when the categories hold more than WZIMG_BULK_MAX_IMAGES (default 2000) images, unless force is set.")]
    public string BatchExportImages(
        [Description("Categories to export (comma-separated, or 'all')")] string categories,
        [Description("Output directory")] string outputDir,
        [Description("Output format (png, jpg)")] string format = "png",
        [Description("Maximum images to export")] int maxImages = 1000,
        [Description("Export even when the categories hold more images than WZIMG_BULK_MAX_IMAGES (default: false)")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!_session.BeginToolCall())
        {
            return new BatchExportImagesResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            // Validate everything the caller controls before anything is created on disk.
            var extension = NormalizeImageFormat(format);
            outputDir = PathGuard.Resolve(outputDir);
            var ds = _session.DataSource;
            List<string> categoryList;

            if (categories.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                categoryList = ds.GetCategories().ToList();
            }
            else
            {
                categoryList = categories.Split(',').Select(c => c.Trim()).ToList();
            }

            foreach (var category in categoryList)
                PathGuard.RequireSafeComponent(category, "category");

            // The images are read one after the other and not kept (see ScanGuard.Run), but a call that goes through
            // thousands of them is asked for twice.
            ScanGuard.RequireWithinBulkCap(_session, categoryList, force,
                "To continue, call batch_export_images again with the argument force=true to export anyway, or with the argument categories='<fewer names>' (maxImages does not help: every image is opened to find its canvases).");

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var exported = new List<string>();
            var failed = new List<string>();
            int totalExported = 0;

            foreach (var category in categoryList)
            {
                if (totalExported >= maxImages) break;

                // Directories are created lazily, after the final file path passed the path guard.
                var categoryDir = Path.Combine(outputDir, category);

                // One directory per image: images in different sub directories can share a file name, and different
                // names can sanitise to the same one, so the directory names are made unique per category.
                var imageDirNames = PathGuard.NewNameSet();

                var plan = ScanGuard.PlanCategories(_session, new[] { category }, int.MaxValue, "", "", force: true);
                var scan = ScanGuard.Run(_session, plan, (img, cat, name) =>
                {
                    if (totalExported >= maxImages) return true;
                    if (_session.MemoryGuard.IsOverSoftLimit())
                    {
                        failed.Add($"stopped before {category}/{name}: the managed heap is above WZIMG_MEMORY_SOFT_LIMIT_MB");
                        return true;
                    }

                    try
                    {
                        var imageDir = Path.Combine(categoryDir, PathGuard.UniqueName(
                            PathGuard.SanitizeFileName(Path.GetFileNameWithoutExtension(img.Name), "image"), imageDirNames));
                        ExportCanvasesFromImage(img, imageDir, extension, exported, failed, ref totalExported, maxImages);
                    }
                    catch (Exception ex) when (ex is PathNotAllowedException or OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed.Add($"{category}/{name}: {ex.Message}");
                    }

                    return totalExported >= maxImages;
                }, applyBudget: false, cancellationToken: cancellationToken);

                if (scan.Failed > 0)
                    failed.Add($"{category}: {scan.Failed} image(s) could not be read");
            }

            return new BatchExportImagesResult
            {
                Success = true,
                OutputDirectory = outputDir,
                ExportedCount = exported.Count,
                FailedCount = failed.Count,
                Truncated = totalExported >= maxImages,
                SampleExported = exported.Take(20).ToList(),
                Failed = failed.Take(20).ToList()
            };
        }
        catch (Exception ex)
        {
            return new BatchExportImagesResult { Success = false, Error = ex.Message, ErrorCode = ToolErrorException.CodeOf(ex) };
        }
    }

    [McpServerTool(Name = "batch_search"), Description("Search across multiple categories. Every image of every listed category is opened, so the categories together may hold at most WZIMG_SCAN_MAX_IMAGES (default 150) images, otherwise the call is refused with scope_too_wide; the default categories='all' therefore only works on a small data set, give categories='<comma separated names>' instead. Stops early with truncated=true when the time budget (WZIMG_SCAN_BUDGET_SECONDS) ends.")]
    public string BatchSearch(
        [Description("Search pattern (supports wildcards)")] string pattern,
        [Description("Categories to search (comma-separated, or 'all')")] string categories = "all",
        [Description("Search type: name, value, or both")] string searchType = "name",
        [Description("Maximum results (default: 30)")] int maxResults = 30,
        CancellationToken cancellationToken = default)
    {
        if (!_session.BeginToolCall())
        {
            return new BatchSearchResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var ds = _session.DataSource;
            IEnumerable<string> categoryList;

            if (categories.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                categoryList = ds.GetCategories();
            }
            else
            {
                categoryList = categories.Split(',').Select(c => c.Trim());
            }

            var results = new List<BatchSearchMatch>();
            var regex = WildcardToRegex(pattern);

            var plan = ScanGuard.PlanScan(_session, categoryList.ToList(),
                $"To continue, call batch_search again with the argument categories='<comma separated names>' naming only categories whose images add up to at most {_session.ScanLimits.MaxImages} (for example categories='Mob'); the default categories='all' is not accepted on a data set this big. " +
                "To search one image use search_by_name or search_by_value with the arguments category and image; list_images_in_category(category='<name>') shows how many images a category has.");
            var scan = ScanGuard.Run(_session, plan, (img, category, name) =>
            {
                try
                {
                    SearchInImage(img, category, name, regex, searchType, results, maxResults, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Skip images that can't be searched
                }

                return results.Count >= maxResults;
            }, cancellationToken: cancellationToken);

            return new BatchSearchResult
            {
                Success = true,
                Pattern = pattern,
                SearchType = searchType,
                ResultCount = results.Count,
                Truncated = results.Count >= maxResults || scan.BudgetExhausted,
                TruncatedReason = scan.BudgetExhausted ? "time_budget" : results.Count >= maxResults ? "max_results" : null,
                ScannedImages = scan.Scanned,
                TotalImages = scan.Total,
                Results = results
            };
        }
        catch (Exception ex)
        {
            return new BatchSearchResult { Success = false, Error = ex.Message, ErrorCode = ToolErrorException.CodeOf(ex) };
        }
    }

    private void ExportCanvasesFromImage(MapleLib.WzLib.WzImage img, string outputDir,
        string format, List<string> exported, List<string> failed, ref int count, int max)
    {
        if (count >= max) return;

        // One name set per image: canvases whose sanitised names collide get a numeric suffix instead of overwriting each other.
        var usedNames = PathGuard.NewNameSet();
        foreach (var prop in img.WzProperties ?? Enumerable.Empty<MapleLib.WzLib.WzImageProperty>())
        {
            ExportCanvasesRecursive(prop, "", outputDir, format, exported, failed, ref count, max, usedNames);
        }
    }

    /// <summary>Accepts png, jpg and jpeg (any case) and returns the file extension to use ("png" or "jpg").</summary>
    internal static string NormalizeImageFormat(string? format)
    {
        switch ((format ?? "").Trim().ToLowerInvariant())
        {
            case "png": return "png";
            case "jpg":
            case "jpeg": return "jpg";
            default:
                throw new ArgumentException($"Unsupported format '{format}': use png, jpg or jpeg");
        }
    }

    private void ExportCanvasesRecursive(MapleLib.WzLib.WzImageProperty prop, string path,
        string outputDir, string format, List<string> exported, List<string> failed, ref int count, int max,
        HashSet<string> usedNames)
    {
        if (count >= max) return;

        if (prop is MapleLib.WzLib.WzProperties.WzCanvasProperty canvas)
        {
            try
            {
                var isJpeg = format == "jpg";
                var encoded = WzImgMCP.Imaging.CanvasImaging.EncodeCanvas(canvas,
                    isJpeg ? WzImgMCP.Imaging.ExportImageFormat.Jpeg : WzImgMCP.Imaging.ExportImageFormat.Png, out _, out _);
                if (encoded != null)
                {
                    // path is the parent path here, so add the canvas name or all frames of one animation overwrite each other.
                    // Canvas names come from the WZ file: make them one safe file name, keep them unique, and let the
                    // path guard check the final path (symlinks resolved) before anything is created or written.
                    var fileName = PathGuard.UniqueName(
                        PathGuard.SanitizeFileName(string.IsNullOrEmpty(path) ? prop.Name : $"{path}/{prop.Name}", "canvas"), usedNames);
                    var filePath = PathGuard.WriteFileGuarded(outputDir, $"{fileName}.{format}", encoded);
                    exported.Add(filePath);
                    count++;
                }
            }
            catch (PathNotAllowedException) { throw; }
            catch (Exception ex)
            {
                failed.Add($"{path}: {ex.Message}");
            }
        }

        var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}/{prop.Name}";
        foreach (var child in prop.WzProperties ?? Enumerable.Empty<MapleLib.WzLib.WzImageProperty>())
        {
            ExportCanvasesRecursive(child, childPath, outputDir, format, exported, failed, ref count, max, usedNames);
        }
    }

    private void SearchInImage(MapleLib.WzLib.WzImage img, string category, string imageName,
        System.Text.RegularExpressions.Regex regex, string searchType, List<BatchSearchMatch> results, int max, CancellationToken ct)
    {
        foreach (var prop in img.WzProperties ?? Enumerable.Empty<MapleLib.WzLib.WzImageProperty>())
        {
            SearchInProperty(prop, category, imageName, "", regex, searchType, results, max, ct);
        }
    }

    private void SearchInProperty(MapleLib.WzLib.WzImageProperty prop, string category, string imageName,
        string path, System.Text.RegularExpressions.Regex regex, string searchType, List<BatchSearchMatch> results, int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (results.Count >= max) return;

        var currentPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}/{prop.Name}";
        var matched = false;

        // Check name
        if (searchType == "name" || searchType == "both")
        {
            if (regex.IsMatch(prop.Name))
            {
                matched = true;
            }
        }

        // Check value
        if (!matched && (searchType == "value" || searchType == "both"))
        {
            var valueStr = GetPropertyValueString(prop);
            if (valueStr != null && regex.IsMatch(valueStr))
            {
                matched = true;
            }
        }

        if (matched)
        {
            results.Add(new BatchSearchMatch
            {
                Category = category,
                Image = imageName,
                Path = currentPath,
                Name = prop.Name,
                Type = prop.PropertyType.ToString(),
                Value = GetPropertyValueString(prop)
            });
        }

        // Recurse
        foreach (var child in prop.WzProperties ?? Enumerable.Empty<MapleLib.WzLib.WzImageProperty>())
        {
            SearchInProperty(child, category, imageName, currentPath, regex, searchType, results, max, ct);
        }
    }

    private static string? GetPropertyValueString(MapleLib.WzLib.WzImageProperty prop)
    {
        return prop switch
        {
            MapleLib.WzLib.WzProperties.WzStringProperty s => s.Value,
            MapleLib.WzLib.WzProperties.WzIntProperty i => i.Value.ToString(),
            MapleLib.WzLib.WzProperties.WzShortProperty sh => sh.Value.ToString(),
            MapleLib.WzLib.WzProperties.WzLongProperty l => l.Value.ToString(),
            MapleLib.WzLib.WzProperties.WzFloatProperty f => f.Value.ToString(),
            MapleLib.WzLib.WzProperties.WzDoubleProperty d => d.Value.ToString(),
            MapleLib.WzLib.WzProperties.WzUOLProperty u => u.Value,
            _ => null
        };
    }

    private static System.Text.RegularExpressions.Regex WildcardToRegex(string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return new System.Text.RegularExpressions.Regex(regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static List<string> GetExtractionCategories(string wzPath, string? categories)
    {
        var parsed = ParseCategoryList(categories);
        if (parsed.Count > 0)
        {
            return parsed;
        }

        if (!File.Exists(wzPath))
        {
            return parsed;
        }

        var inferred = Path.GetFileNameWithoutExtension(wzPath);
        return string.IsNullOrWhiteSpace(inferred) ? parsed : new List<string> { inferred };
    }

    private static List<string> GetPackingCategories(string imgPath, string? category)
    {
        var parsed = ParseCategoryList(category);
        if (parsed.Count > 0)
        {
            return parsed;
        }

        return Directory.EnumerateDirectories(imgPath)
            .Where(dir => WzPackingService.EnumeratePackableImageFiles(dir).Any())
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name)
            .ToList();
    }

    /// <summary>Throws <see cref="PathNotAllowedException"/> unless every category is one plain name (no separator, "..", drive prefix).</summary>
    internal static void RequireSafeCategories(IEnumerable<string> categories)
    {
        foreach (var category in categories)
            PathGuard.RequireSafeComponent(category, "category");
    }

    /// <summary>
    /// Resolves the paths the packer writes below <paramref name="outputDir"/> through the path guard (symlinks followed),
    /// before anything is created: Category.wz, Category, Data, Data/Category, Data.wz and List.wz, and every symbolic link
    /// found inside Data/Category and Data/Category/_Canvas (Category.ini, Category_NNN.wz, List_000.wz, Canvas.ini,
    /// _Canvas_NNN.wz), so a link planted there that leads outside the allowed root is refused with a clear message before
    /// anything is written. A link that stays inside the root is not refused here: the packer's own write (NoFollowFile)
    /// refuses it later, so files written before that point (for example Category.ini) can remain. This is a check of the state now; the packer's own writes (MapleLib NoFollowFile) never
    /// write through a link at the file they write, whatever happens after the check.
    /// </summary>
    internal static void GuardPackOutputs(string outputDir, IEnumerable<string> categories)
    {
        foreach (var fixedName in new[] { "Data.wz", "List.wz", "Data" })
            PathGuard.Resolve(Path.Combine(outputDir, fixedName));
        foreach (var category in categories)
        {
            PathGuard.Resolve(Path.Combine(outputDir, $"{category}.wz"));
            PathGuard.Resolve(Path.Combine(outputDir, category));
            var categoryData = Path.Combine(outputDir, "Data", category);
            PathGuard.Resolve(categoryData);
            foreach (var directory in new[] { categoryData, Path.Combine(categoryData, "_Canvas") })
            {
                var resolved = PathGuard.Resolve(directory);
                if (!Directory.Exists(resolved))
                    continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(resolved))
                {
                    if (new FileInfo(entry).LinkTarget != null)
                        PathGuard.Resolve(entry);
                }
            }
        }
    }

    private static List<string> ParseCategoryList(string? categories)
    {
        if (string.IsNullOrWhiteSpace(categories) || categories.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        return categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WzMapleVersion ResolveEncryption(string? versionKey, string wzPath)
    {
        var parsed = ParseEncryption(versionKey);
        if (parsed.HasValue)
        {
            return parsed.Value;
        }

        var candidateWz = File.Exists(wzPath)
            ? wzPath
            : Directory.EnumerateFiles(wzPath, "*.wz", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(candidateWz))
        {
            try
            {
                return WzTool.DetectMapleVersion(candidateWz, out _);
            }
            catch
            {
                // Fall through to the common GMS default.
            }
        }

        return WzMapleVersion.GMS;
    }

    private static WzMapleVersion? ParseEncryption(string? versionKey)
    {
        if (string.IsNullOrWhiteSpace(versionKey) ||
            versionKey.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Enum.TryParse<WzMapleVersion>(versionKey.Trim(), true, out var parsed) &&
            parsed != WzMapleVersion.UNKNOWN &&
            parsed != WzMapleVersion.GENERATE &&
            parsed != WzMapleVersion.GETFROMZLZ)
        {
            return parsed;
        }

        throw new ArgumentException($"Unsupported WZ version key '{versionKey}'. Use GMS, EMS, BMS, CLASSIC, CUSTOM, or empty/auto.");
    }

    private static short ToPatchVersion(int wzVersion)
    {
        if (wzVersion < short.MinValue || wzVersion > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(wzVersion), "WZ version must fit in a signed 16-bit patch version.");
        }

        return (short)wzVersion;
    }
}

// Result types

public class ExtractResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputDirectory { get; set; }
    public int CategoriesExtracted { get; set; }
    public int ImagesExtracted { get; set; }
    public long TotalSize { get; set; }
    public double DurationSeconds { get; set; }
    public bool ManifestCreated { get; set; }
    public List<string>? ExtractedCategories { get; set; }
    public List<string>? Errors { get; set; }
}

public class PackResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputDirectory { get; set; }
    public int FilesCreated { get; set; }
    public long TotalSize { get; set; }
    public int ImagesPacked { get; set; }
    public double DurationSeconds { get; set; }
    public List<string>? PackedCategories { get; set; }
    public List<string>? CreatedFiles { get; set; }
    public List<string>? Errors { get; set; }
}

public class BatchExportImagesResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? OutputDirectory { get; set; }
    public int ExportedCount { get; set; }
    public int FailedCount { get; set; }
    public bool Truncated { get; set; }
    public List<string>? SampleExported { get; set; }
    public List<string>? Failed { get; set; }
}

public class BatchSearchResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? Pattern { get; set; }
    public string? SearchType { get; set; }
    public int ResultCount { get; set; }
    public bool Truncated { get; set; }
    public string? TruncatedReason { get; set; }
    public int ScannedImages { get; set; }
    public int TotalImages { get; set; }
    public List<BatchSearchMatch>? Results { get; set; }
}

public class BatchSearchMatch
{
    public required string Category { get; set; }
    public required string Image { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Value { get; set; }
}



