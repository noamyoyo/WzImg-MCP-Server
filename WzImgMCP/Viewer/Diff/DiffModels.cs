using System.Text.Json.Serialization;

namespace WzImgMCP.Viewer.Diff;

/// <summary>One difference between two images. <c>Kind</c> is "added", "removed" or "changed".</summary>
public sealed class DiffChange
{
    /// <summary>Names joined by '/'; a name that occurs more than once among siblings is written name#k (k from 0).</summary>
    public required string Path { get; init; }
    public required string Kind { get; init; }
    /// <summary>The property type: string, int, short, long, float, double, vector, uol, convex, sound, sub, canvas, null, ...</summary>
    public required string Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Before { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? After { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BeforeTruncated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AfterTruncated { get; init; }
    /// <summary>True when the path was cut (a name over 100 characters, or the whole path over 1000); it may then not be found in the image.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? PathTruncated { get; init; }
    /// <summary>Why: "type" for a type change; "unverified" for a value whose data could not be read; for a canvas "size", "format", "pixels", "pixels_unverified" or "link".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Detail { get; init; }
    /// <summary>For a whole added or removed subtree (reported as one change): how many nodes it holds below it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DescendantCount { get; init; }
    /// <summary>For a whole added or removed subtree: the first 20 names of its direct children.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Children { get; init; }
}

public sealed record CanvasInfo(int Width, int Height, int Format);

/// <summary>
/// One canvas that differs. <c>Change</c>: "size", "format", "pixels", "pixels_unverified", "link", "added", "removed",
/// "hidden_color" (the visible pixels are equal; only the colour of fully transparent pixels differs), or
/// "recompressed" (other bytes, the very same pixels: not a change).
/// </summary>
public sealed record CanvasChange(string Path, string Change, CanvasInfo? Before, CanvasInfo? After);

public sealed record DiffSummary(int Added, int Removed, int Changed, int CanvasesChanged, int CanvasesRecompressed, int CanvasesHiddenColour = 0);

public sealed record ImageDiffResult(
    IReadOnlyList<DiffChange> Changes,
    IReadOnlyList<CanvasChange> Canvases,
    DiffSummary Summary,
    bool Truncated,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Canvases whose visible pixels are equal but whose colour under alpha 0 differs.</summary>
    public int HiddenColourCanvases { get; init; }

    /// <summary>Linked canvases (_inlink/_outlink) that were compared by their link text only.</summary>
    public int LinkedCanvases { get; init; }

    /// <summary>
    /// <c>different</c> when anything is listed or the list is cut. With nothing listed: <c>identical_visible</c> when a
    /// caveat applies (hidden colour differs, or linked canvases were not compared through their links), else
    /// <c>identical</c>: every value, every picture and every link text is equal.
    /// </summary>
    public string Semantic => Changes.Count > 0 || Truncated ? "different"
        : HiddenColourCanvases > 0 || LinkedCanvases > 0 ? "identical_visible" : "identical";
}

public sealed record DiffOptions
{
    public int MaxChanges { get; init; } = ViewerOptions.DefaultMaxChanges;
    public int MaxCanvasDecodes { get; init; } = ViewerOptions.DefaultMaxCanvasDecodes;
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(ViewerOptions.DefaultDiffBudgetSeconds);
    public long MaxDecodedBytes { get; init; } = ViewerOptions.DefaultMaxDecodedMb * 1024L * 1024L;
    public long MaxResponseBytes { get; init; } = ViewerOptions.DefaultMaxDiffResponseKb * 1024L;

    /// <summary>Test seam: called with "stage1", "decode" or "value" just before the data of a canvas or value is read (may throw, for instance an IOException or an OutOfMemoryException).</summary>
    public Action<string>? Probe { get; init; }

    /// <summary>Longest value text kept in a change; longer is cut and flagged.</summary>
    public const int MaxValueChars = 200;
    /// <summary>Longest node name kept in a path or a child list; longer is cut.</summary>
    public const int MaxNameChars = 100;
    /// <summary>Longest path kept in a change; longer is cut and flagged.</summary>
    public const int MaxPathChars = 1000;
    /// <summary>Child names listed for a collapsed added or removed subtree.</summary>
    public const int MaxSubtreeNames = 20;
    /// <summary>Canvas limits from the plan: pixels per side and decoded bytes.</summary>
    public const int MaxCanvasSide = 32768;
    public const long MaxCanvasDecodedBytes = 256L * 1024 * 1024;
    /// <summary>
    /// The largest canvas a DIFF decodes (width x height x 4). The library holds the raw and the BGRA buffer at once (about
    /// twice this), and two decodes can run at the same time, so the diff stays well below the 256 MiB the picture endpoint allows.
    /// </summary>
    public const long MaxDiffCanvasDecodedBytes = 64L * 1024 * 1024;

    public static DiffOptions From(ViewerOptions o) => new()
    {
        MaxChanges = o.MaxChanges, MaxCanvasDecodes = o.MaxCanvasDecodes, Budget = o.DiffBudget,
        MaxDecodedBytes = o.MaxDecodedBytes, MaxResponseBytes = o.MaxDiffResponseBytes,
    };
}
