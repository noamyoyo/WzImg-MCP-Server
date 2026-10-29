using System.Diagnostics;
using MapleLib.Img;
using MapleLib.WzLib;

namespace WzImgMCP.Server;

/// <summary>An error with a stable code a client can act on (for example <c>scope_too_wide</c>); the code also starts the message.</summary>
public sealed class ToolErrorException : Exception
{
    public string Code { get; }

    public ToolErrorException(string code, string message) : base($"{code}: {message}")
    {
        Code = code;
    }

    /// <summary>The code of a typed error, otherwise null.</summary>
    public static string? CodeOf(Exception ex) => (ex as ToolErrorException)?.Code;
}

/// <summary>
/// How many images a tool may open in one call. <see cref="MaxImages"/> is the limit of a scan (a search or analysis over the
/// images of a category), <see cref="Budget"/> its time budget (zero: none), <see cref="BulkMaxImages"/> the sanity cap of the
/// tools that work on a whole category on purpose (preload, export); <c>force</c> lifts that cap.
/// </summary>
public sealed record ScanLimits(int MaxImages, TimeSpan Budget, int BulkMaxImages);

/// <summary>What a scan is going to open: (category, path) pairs and the name to show for each.</summary>
public sealed class ScanPlan
{
    public required List<ScanItem> Items { get; init; }
    public int Total => Items.Count;
}

public readonly record struct ScanItem(string Category, string Path, string? Display);

/// <summary>How a scan ended.</summary>
public sealed class ScanOutcome
{
    /// <summary>Images that were opened and handed to the visitor.</summary>
    public int Scanned { get; init; }
    /// <summary>Images the plan held.</summary>
    public int Total { get; init; }
    /// <summary>Images that could not be read or parsed (skipped).</summary>
    public int Failed { get; init; }
    /// <summary>The visitor asked to stop (enough results).</summary>
    public bool Stopped { get; init; }
    /// <summary>The time budget ended the scan.</summary>
    public bool BudgetExhausted { get; init; }
    /// <summary>Why the result may be partial: "time_budget" or "max_results", otherwise null.</summary>
    public string? TruncatedReason => BudgetExhausted ? "time_budget" : Stopped ? "max_results" : null;
}

/// <summary>
/// The one place that decides how a tool may touch many images. A scan first asks for its plan (the images it would open,
/// from the category index, nothing is opened) and is refused with <c>scope_too_wide</c> when that is more than the limit;
/// then it runs under a time budget and returns what it found so far when the budget ends. Images the scan has to load are not
/// put in the cache and are disposed right after the visitor returned, so a scan leaves the cache as it found it; an image that
/// is cached (all images with unsaved edits are) is used as it is and left alone.
/// </summary>
public static class ScanGuard
{
    public const string ScopeTooWide = "scope_too_wide";
    public const string CategoryRequired = "category_required";

    /// <summary>
    /// The plan of a search: with <paramref name="image"/> that image (in <paramref name="category"/>, or in every category
    /// that has it), otherwise every image of <paramref name="category"/>, which is then required and limited.
    /// </summary>
    public static ScanPlan PlanSearch(WzSessionManager session, string? category, string? image, string tool)
    {
        var source = session.ImgSource;
        if (!string.IsNullOrEmpty(image))
        {
            var categories = string.IsNullOrEmpty(category) ? source.GetCategories().ToList() : new List<string> { category };
            return new ScanPlan
            {
                Items = categories.Where(c => source.ImageExists(c, image)).Select(c => new ScanItem(c, image, null)).ToList()
            };
        }

        int max = session.ScanLimits.MaxImages;
        if (string.IsNullOrEmpty(category))
            throw new ToolErrorException(CategoryRequired,
                $"{tool} needs the argument 'category' or the argument 'image'. " +
                $"Add category='<name>' to look through every image of one category (allowed when the category has at most {max} images), " +
                $"or add image='<path of the image inside its category>' (with or without category) to look at one image. " +
                $"{DescribeCategories(source, max)} " +
                "list_images_in_category(category='<name>') lists the image paths of a category, one page at a time.");

        var plan = PlanCategories(session, new[] { category }, max, ServerSettingsNames.ScanMax, SearchAdvice(source, category, tool, max));
        return plan;
    }

    private static string SearchAdvice(MapleLib.Img.ImgFileSystemDataSource source, string category, string tool, int max)
    {
        var paths = source.GetImagePathsInCategory(category);
        string example = paths.Count > 0 ? paths[0].Replace('\\', '/') : "<path>";
        return $"To continue, call {tool} again with the argument image='<path>' (keep category='{category}'), for example image='{example}', " +
               $"to look at one image; list_images_in_category(category='{category}', limit=200, offset=0) lists the image paths, 200 per page " +
               $"(next_offset gives the next page). Or search a category with at most {max} images.";
    }

    /// <summary>The categories with their image counts, split into those a whole-category scan accepts and those it refuses.</summary>
    private static string DescribeCategories(MapleLib.Img.ImgFileSystemDataSource source, int max)
    {
        var counts = source.GetCategories().Select(c => (Name: c, Count: source.GetImagePathsInCategory(c).Count)).ToList();
        string Format(IEnumerable<(string Name, int Count)> list)
        {
            var items = list.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            return items.Count == 0 ? "none" : string.Join(", ", items.Take(12).Select(c => $"{c.Name} ({c.Count})")) + (items.Count > 12 ? ", ..." : "");
        }
        return $"Categories with at most {max} images (can be given whole): {Format(counts.Where(c => c.Count <= max))}. " +
               $"Larger (give an image, or use list_images_in_category): {Format(counts.Where(c => c.Count > max))}.";
    }

    /// <summary>The plan of a scan of whole categories, limited to WZIMG_SCAN_MAX_IMAGES images in total.</summary>
    public static ScanPlan PlanScan(WzSessionManager session, IReadOnlyList<string> categories, string alternative)
        => PlanCategories(session, categories, session.ScanLimits.MaxImages, ServerSettingsNames.ScanMax, alternative);

    /// <summary>
    /// The plan of a scan of whole categories. The total number of images may not exceed <paramref name="maxImages"/>
    /// (unless <paramref name="force"/>), otherwise <c>scope_too_wide</c> is thrown before any image is opened.
    /// </summary>
    public static ScanPlan PlanCategories(WzSessionManager session, IReadOnlyList<string> categories, int maxImages, string limitName, string alternative, bool force = false)
    {
        var source = session.ImgSource;
        var perCategory = new List<(string Category, IReadOnlyList<string> Paths)>();
        long total = 0;
        foreach (var category in categories)
        {
            var paths = source.GetImagePathsInCategory(category);
            perCategory.Add((category, paths));
            total += paths.Count;
        }

        if (!force && total > maxImages)
        {
            string scope = perCategory.Count == 1
                ? $"category '{perCategory[0].Category}' has {total} images"
                : $"the {perCategory.Count} categories hold {total} images ({string.Join(", ", perCategory.OrderByDescending(c => c.Paths.Count).Take(5).Select(c => $"{c.Category} {c.Paths.Count}"))}{(perCategory.Count > 5 ? ", ..." : "")})";
            throw new ToolErrorException(ScopeTooWide,
                $"{scope}, more than the limit of {maxImages} ({limitName}); nothing was opened. {alternative}");
        }

        var items = new List<ScanItem>((int)Math.Min(total, int.MaxValue));
        foreach (var (category, paths) in perCategory)
        {
            foreach (var path in paths)
                items.Add(new ScanItem(category, path, path.Replace('\\', '/')));
        }
        return new ScanPlan { Items = items };
    }

    /// <summary>
    /// Opens the images of a plan one after the other and hands each parsed image to <paramref name="visit"/>
    /// (image, category, name to show); the visitor returns true to stop. At least one image is always scanned; after that the
    /// time budget (when there is one) is checked before each image. An image that was not cached is disposed after its visit;
    /// a cached image is visited under its lock and left exactly as it is (never unparsed or disposed). A bulk tool that works through a
    /// whole category on purpose passes <paramref name="applyBudget"/> false.
    /// </summary>
    public static ScanOutcome Run(WzSessionManager session, ScanPlan plan, Func<WzImage, string, string, bool> visit, bool applyBudget = true)
    {
        var source = session.ImgSource;
        var budget = applyBudget ? session.ScanLimits.Budget : TimeSpan.Zero;
        var clock = Stopwatch.StartNew();
        int scanned = 0, failed = 0;
        bool stopped = false, budgetExhausted = false;

        foreach (var item in plan.Items)
        {
            if (budget > TimeSpan.Zero && scanned + failed > 0 && clock.Elapsed >= budget)
            {
                budgetExhausted = true;
                break;
            }

            session.Touch();   // a long scan is activity: the idle trim must not run under it
            WzImage? image = null;
            bool fromCache = false;
            try
            {
                image = source.OpenImageUncached(item.Category, item.Path, out fromCache);
                if (image == null)
                {
                    failed++;
                    continue;
                }

                string display = item.Display ?? image.Name ?? item.Path;
                if (fromCache)
                {
                    // A cached image is shared with the tools that edit it: look at it under its lock, so an edit and this scan never
                    // interleave, and never unparse it afterwards (it may be being edited now, and an unparse of an edited image
                    // loses the edit). A cached image that was unparsed stays parsed; the memory guard releases it with the others.
                    lock (session.GateOf(image))
                    {
                        if (!image.Parsed)
                            image.ParseImage();
                        scanned++;
                        stopped = visit(image, item.Category, display);
                    }
                }
                else
                {
                    // This scan's own copy: nobody else can see it.
                    if (!image.Parsed)
                        image.ParseImage();
                    scanned++;
                    stopped = visit(image, item.Category, display);
                }

                if (stopped)
                    break;
            }
            catch (ToolErrorException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                failed++;   // one unreadable image does not end the scan
            }
            finally
            {
                // Only the copies this scan loaded itself are disposed; a cached image is never touched.
                if (image != null && !fromCache)
                    image.Dispose();
            }
        }

        return new ScanOutcome
        {
            Scanned = scanned,
            Total = plan.Total,
            Failed = failed,
            Stopped = stopped,
            BudgetExhausted = budgetExhausted
        };
    }

    /// <summary>
    /// Refuses the work of a bulk tool (preload, export) on more than the sanity cap of images unless <c>force</c> is set.
    /// </summary>
    public static void RequireWithinBulkCap(WzSessionManager session, IReadOnlyList<string> categories, bool force, string alternative)
        => PlanCategories(session, categories, session.ScanLimits.BulkMaxImages, ServerSettingsNames.BulkMax, alternative, force);
}

/// <summary>Names used in messages.</summary>
internal static class ServerSettingsNames
{
    public const string ScanMax = WzImgMCP.Utils.ServerSettings.ScanMaxImagesEnvVar;
    public const string BulkMax = WzImgMCP.Utils.ServerSettings.BulkMaxImagesEnvVar;
}
