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
    /// <summary>Why: "type" for a type change; for a canvas "size", "format", "pixels", "pixels_unverified" or "link".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Detail { get; init; }
    /// <summary>For a whole added or removed subtree (reported as one change): how many nodes it holds below it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DescendantCount { get; init; }
    /// <summary>For a whole added or removed subtree: the first 20 names of its direct children.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Children { get; init; }
}

public sealed record CanvasInfo(int Width, int Height, int Format);

/// <summary>
/// One canvas that differs. <c>Change</c>: "size", "format", "pixels", "pixels_unverified", "link", "added", "removed",
/// or "recompressed" (other bytes, same pixels: not a change).
/// </summary>
public sealed record CanvasChange(string Path, string Change, CanvasInfo? Before, CanvasInfo? After);

public sealed record DiffSummary(int Added, int Removed, int Changed, int CanvasesChanged, int CanvasesRecompressed);

public sealed record ImageDiffResult(
    IReadOnlyList<DiffChange> Changes,
    IReadOnlyList<CanvasChange> Canvases,
    DiffSummary Summary,
    bool Truncated,
    IReadOnlyList<string> Warnings);

public sealed record DiffOptions
{
    public int MaxChanges { get; init; } = ViewerOptions.DefaultMaxChanges;
    public int MaxCanvasDecodes { get; init; } = ViewerOptions.DefaultMaxCanvasDecodes;
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(ViewerOptions.DefaultDiffBudgetSeconds);

    /// <summary>Longest value text kept in a change; longer is cut and flagged.</summary>
    public const int MaxValueChars = 200;
    /// <summary>Child names listed for a collapsed added or removed subtree.</summary>
    public const int MaxSubtreeNames = 20;
    /// <summary>Canvas limits from the plan: pixels per side and decoded bytes.</summary>
    public const int MaxCanvasSide = 32768;
    public const long MaxCanvasDecodedBytes = 256L * 1024 * 1024;

    public static DiffOptions From(ViewerOptions o) => new() { MaxChanges = o.MaxChanges, MaxCanvasDecodes = o.MaxCanvasDecodes, Budget = o.DiffBudget };
}
