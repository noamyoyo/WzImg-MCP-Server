using System.Text.Json.Serialization;

namespace WzImgMCP.Viewer.Diff;

/// <summary>One side of a file diff: whether the file is there, how big, its hash, and whether it equals the recorded base.</summary>
public sealed record DiffFileInfo(
    bool Exists,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SizeBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256,
    bool? MatchesBase);

/// <summary>The answer of <c>/v1/diff</c>.</summary>
public sealed record DiffResponse(
    string State,
    string Semantic,
    DiffFileInfo Before,
    DiffFileInfo Now,
    DiffSummary Summary,
    IReadOnlyList<DiffChange> Changes,
    IReadOnlyList<CanvasChange> Canvases,
    bool Truncated,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Always <c>"advisory"</c>: <c>before/now.matchesBase</c> come from <c>.base.json</c> in the person's own root, which that
    /// person's editor can write. The panel must not treat it as proof of "not edited" or use it to skip review; <c>state</c>
    /// and the sha256 values (compared with canon's) decide.
    /// </summary>
    public string BaseTrust { get; init; } = "advisory";
}

/// <summary>
/// Diffs a person's copy of one image against the canon copy. States: <c>unchanged</c> (same bytes), <c>changed</c>,
/// <c>added</c> (only the person has it), <c>removed</c> (only canon has it). Semantic:
/// <list type="bullet">
/// <item><c>identical</c>: the bytes are equal, or every value, every picture (to the last pixel) and every link text is equal ("re-saved");</item>
/// <item><c>identical_visible</c>: nothing is listed and nothing visible differs, BUT a caveat applies: the colour under fully
/// transparent pixels differs (<c>hidden_color</c> in <c>canvases</c>), or linked canvases (_inlink/_outlink) were compared by
/// their link text only. Carries warnings; the panel must not show it as a plain "identical" tick;</item>
/// <item><c>different</c>: anything is listed, or the list is cut.</item>
/// </list>
/// </summary>
public sealed class ViewerDiffService
{
    private readonly ViewerImageLoader _loader;
    private readonly ViewerBaseMap _bases;
    private DiffOptions _options;

    public ViewerDiffService(ViewerImageLoader loader, ViewerBaseMap bases, ViewerOptions options)
    {
        _loader = loader;
        _bases = bases;
        _options = DiffOptions.From(options);
    }

    /// <summary>Test seam, see <see cref="DiffOptions.Probe"/>.</summary>
    public Action<string>? Probe { set => _options = _options with { Probe = value }; }

    /// <summary>Synchronous; run it through <see cref="ViewerWorkRunner"/>. A null side means the file does not exist there.</summary>
    public DiffResponse Diff(string person, ResolvedImage? canon, ResolvedImage? mine, CancellationToken cancellationToken)
    {
        if (canon == null && mine == null)
            throw new ViewerApiException(404, "not_found", "image not found");

        ImageLease? oldLease = null, newLease = null;
        try
        {
            if (canon != null) oldLease = _loader.Load(canon, cancellationToken);
            if (mine != null) newLease = _loader.Load(mine, cancellationToken);

            var path = (mine ?? canon)!.RelativePath;
            var baseHash = _bases.Get(person, path);
            var before = Info(oldLease, baseHash);
            var now = Info(newLease, baseHash);

            if (oldLease == null || newLease == null)
                return new DiffResponse(oldLease == null ? "added" : "removed", "different", before, now,
                    new DiffSummary(0, 0, 0, 0, 0), Array.Empty<DiffChange>(), Array.Empty<CanvasChange>(), false,
                    new[] { "the file exists on one side only; its contents are not compared" });

            if (oldLease.Loaded.Sha256 == newLease.Loaded.Sha256)
                return new DiffResponse("unchanged", "identical", before, now, new DiffSummary(0, 0, 0, 0, 0),
                    Array.Empty<DiffChange>(), Array.Empty<CanvasChange>(), false, Array.Empty<string>());

            ImageDiffResult result;
            try
            {
                result = ImageDiffer.Diff(oldLease.Loaded, newLease.Loaded, _options, cancellationToken);
            }
            catch (OutOfMemoryException)   // also InsufficientMemoryException
            {
                _loader.Cache.Clear();   // give memory back before the next request, like the loader does
                throw new ViewerApiException(413, "too_large", "the image needs more memory than the viewer allows");
            }
            return new DiffResponse("changed", result.Semantic, before, now, result.Summary, result.Changes, result.Canvases, result.Truncated, result.Warnings);
        }
        finally
        {
            oldLease?.Dispose();
            newLease?.Dispose();
        }
    }

    private static DiffFileInfo Info(ImageLease? lease, string? baseHash)
        => lease == null
            ? new DiffFileInfo(false, null, null, null)
            : new DiffFileInfo(true, lease.Loaded.FileBytes, lease.Loaded.Sha256, baseHash == null ? null : baseHash == lease.Loaded.Sha256);
}
