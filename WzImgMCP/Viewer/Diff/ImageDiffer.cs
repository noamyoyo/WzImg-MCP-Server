using System.Diagnostics;
using System.Security.Cryptography;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Viewer.Diff;

/// <summary>
/// Compares ONE parsed old image with ONE parsed new image (both from <see cref="ViewerImageLoader"/>) and returns
/// what changed. Ideas from the study of WzComparerR2's comparer (merge by name, type-strict values, staged canvas
/// compare); none of its code is used.
///
/// <list type="bullet">
/// <item>Children are matched by name, ordinal, per level. The order of siblings is ignored. A name that occurs more than
/// once among siblings pairs the k-th with the k-th and is reported as <c>name#k</c>.</item>
/// <item>A node of a different type is one change (<c>detail "type"</c>) and is not walked further.</item>
/// <item>A node that exists on one side only is ONE change; when it has children the change carries their count and the
/// first 20 names instead of listing every descendant.</item>
/// <item>Values: see <see cref="PropertyValues"/>. Canvases go through the generic walk (origin, delay, ... are child
/// nodes) plus a canvas stage (<see cref="CompareCanvasPixels"/>).</item>
/// <item>Caps: <c>MaxChanges</c> (then <c>truncated</c>), a wall-clock budget (then <c>truncated</c> and a warning), a
/// per-request number of pixel decodes.</item>
/// <item>The output is sorted by path (ordinal) and is the same for the same input.</item>
/// </list>
/// An instance is for one comparison.
/// </summary>
public sealed class ImageDiffer
{
    private readonly DiffOptions _options;
    private readonly LoadedImage _old;
    private readonly LoadedImage _new;
    private readonly CancellationToken _cancellation;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<DiffChange> _changes = new();
    private readonly List<CanvasChange> _canvases = new();
    private bool _truncated;
    private bool _timeUp;
    private int _added, _removed, _changed, _recompressed;
    private int _decodes, _budgetSkipped, _undecodable, _linkedCanvases;

    private ImageDiffer(LoadedImage old, LoadedImage @new, DiffOptions options, CancellationToken cancellation)
    {
        _old = old;
        _new = @new;
        _options = options;
        _cancellation = cancellation;
    }

    public static ImageDiffResult Diff(LoadedImage old, LoadedImage @new, DiffOptions options, CancellationToken cancellation)
        => new ImageDiffer(old, @new, options, cancellation).Run();

    private ImageDiffResult Run()
    {
        var pending = new Stack<(IReadOnlyList<WzImageProperty> Old, IReadOnlyList<WzImageProperty> New, string Prefix)>();
        pending.Push((Top(_old), Top(_new), ""));
        while (pending.Count > 0 && !_truncated && !_timeUp)
        {
            var (o, n, prefix) = pending.Pop();
            var deeper = CompareLevel(o, n, prefix);
            for (int i = deeper.Count - 1; i >= 0; i--)   // so the smallest name is expanded first
                pending.Push(deeper[i]);
        }

        var warnings = new List<string>();
        if (_timeUp) warnings.Add($"the comparison stopped after {_options.Budget.TotalSeconds:0.##} s; the list is incomplete");
        if (_truncated && !_timeUp) warnings.Add($"more than {_options.MaxChanges} changes; the list is cut");
        if (_budgetSkipped > 0) warnings.Add($"{_budgetSkipped} canvas pair(s) that differ in compressed bytes were not decoded (limit {_options.MaxCanvasDecodes} per request); reported as pixels_unverified");
        if (_undecodable > 0) warnings.Add($"{_undecodable} canvas pair(s) could not be verified (too large, damaged or without pixel data); reported as pixels_unverified");
        if (_linkedCanvases > 0) warnings.Add($"{_linkedCanvases} linked canvas(es) (_inlink/_outlink) were compared by their link text only; pixels behind links are not compared");

        var changes = _changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        var canvases = _canvases.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        int canvasesChanged = canvases.Count(c => c.Change != "recompressed");
        return new ImageDiffResult(changes, canvases, new DiffSummary(_added, _removed, _changed, canvasesChanged, _recompressed),
            _truncated || _timeUp, warnings);
    }

    private static IReadOnlyList<WzImageProperty> Top(LoadedImage image) => (IReadOnlyList<WzImageProperty>)image.Image.WzProperties;

    private bool Tick()
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_clock.Elapsed > _options.Budget)
            _timeUp = true;
        return !_timeUp;
    }

    /// <summary>Compares the children of one pair of containers; returns the pairs whose children must be compared next.</summary>
    private List<(IReadOnlyList<WzImageProperty> Old, IReadOnlyList<WzImageProperty> New, string Prefix)> CompareLevel(
        IReadOnlyList<WzImageProperty> oldChildren, IReadOnlyList<WzImageProperty> newChildren, string prefix)
    {
        var deeper = new List<(IReadOnlyList<WzImageProperty>, IReadOnlyList<WzImageProperty>, string)>();
        var oldGroups = Group(oldChildren);
        var newGroups = Group(newChildren);
        var names = new SortedSet<string>(oldGroups.Keys, StringComparer.Ordinal);
        names.UnionWith(newGroups.Keys);

        foreach (var name in names)
        {
            oldGroups.TryGetValue(name, out var lo);
            newGroups.TryGetValue(name, out var ln);
            int oc = lo?.Count ?? 0, nc = ln?.Count ?? 0;
            bool duplicated = oc > 1 || nc > 1;

            for (int k = 0; k < Math.Max(oc, nc); k++)
            {
                if (!Tick() || _truncated)
                    return deeper;
                var path = (prefix.Length == 0 ? "" : prefix + "/") + name + (duplicated ? "#" + k : "");

                if (k >= oc) AddOnlyOneSide(path, ln![k], "added", _new);
                else if (k >= nc) AddOnlyOneSide(path, lo![k], "removed", _old);
                else ComparePair(path, lo![k], ln![k], deeper);
            }
        }
        return deeper;
    }

    private static Dictionary<string, List<WzImageProperty>> Group(IReadOnlyList<WzImageProperty> children)
    {
        var groups = new Dictionary<string, List<WzImageProperty>>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            var name = child.Name ?? "";
            if (!groups.TryGetValue(name, out var list))
                groups[name] = list = new List<WzImageProperty>();
            list.Add(child);   // file order is kept, so the k-th duplicate is the k-th in the file
        }
        return groups;
    }

    private void ComparePair(string path, WzImageProperty a, WzImageProperty b,
        List<(IReadOnlyList<WzImageProperty>, IReadOnlyList<WzImageProperty>, string)> deeper)
    {
        string ta = PropertyValues.TypeOf(a), tb = PropertyValues.TypeOf(b);
        if (ta != tb)
        {
            var (before, bt) = PropertyValues.Describe(a, _old);
            var (after, at) = PropertyValues.Describe(b, _new);
            int below = CountDescendants(a) + CountDescendants(b);
            AddChange(new DiffChange
            {
                Path = path, Kind = "changed", Type = tb,
                Before = $"({ta}) {before}".TrimEnd(), After = $"({tb}) {after}".TrimEnd(),
                BeforeTruncated = bt ? true : null, AfterTruncated = at ? true : null,
                Detail = "type", DescendantCount = below > 0 ? below : null,
            });
            return;
        }

        if (a is WzCanvasProperty ca && b is WzCanvasProperty cb)
            CompareCanvasPixels(path, ca, cb);
        else if (!PropertyValues.ValueEquals(a, b, _old, _new))
        {
            var (before, bt) = PropertyValues.Describe(a, _old);
            var (after, at) = PropertyValues.Describe(b, _new);
            AddChange(new DiffChange
            {
                Path = path, Kind = "changed", Type = ta, Before = before, After = after,
                BeforeTruncated = bt ? true : null, AfterTruncated = at ? true : null,
            });
        }

        var childrenA = PropertyValues.Children(a);
        var childrenB = PropertyValues.Children(b);
        if (childrenA.Count > 0 || childrenB.Count > 0)
            deeper.Add((childrenA, childrenB, path));
    }

    private void AddChange(DiffChange change)
    {
        if (_changes.Count >= _options.MaxChanges)
        {
            _truncated = true;
            return;
        }
        _changes.Add(change);
        switch (change.Kind)
        {
            case "added": _added++; break;
            case "removed": _removed++; break;
            default: _changed++; break;
        }
    }

    // ---- nodes on one side only

    private void AddOnlyOneSide(string path, WzImageProperty p, string kind, LoadedImage owner)
    {
        var type = PropertyValues.TypeOf(p);
        var (text, truncated) = PropertyValues.Describe(p, owner);
        var children = PropertyValues.Children(p);
        int below = children.Count == 0 ? 0 : CountDescendants(p);
        var names = children.Count == 0 ? null
            : (IReadOnlyList<string>)children.Select(c => c.Name ?? "").OrderBy(n => n, StringComparer.Ordinal).Take(DiffOptions.MaxSubtreeNames).ToList();
        bool added = kind == "added";

        AddChange(new DiffChange
        {
            Path = path, Kind = kind, Type = type,
            Before = added ? null : text, After = added ? text : null,
            BeforeTruncated = !added && truncated ? true : null, AfterTruncated = added && truncated ? true : null,
            DescendantCount = children.Count == 0 ? null : below,
            Children = names,
        });

        // The canvases in the subtree are listed (without pixel work), so the panel can show the new or removed pictures.
        foreach (var (canvasPath, canvas) in CanvasesIn(p, path))
        {
            if (_canvases.Count >= _options.MaxChanges) { _truncated = true; break; }
            var info = InfoOf(canvas);
            _canvases.Add(new CanvasChange(canvasPath, kind, added ? null : info, added ? info : null));
        }
    }

    private static IEnumerable<(string Path, WzCanvasProperty Canvas)> CanvasesIn(WzImageProperty root, string rootPath)
    {
        var stack = new Stack<(WzImageProperty, string)>();
        stack.Push((root, rootPath));
        while (stack.Count > 0)
        {
            var (p, path) = stack.Pop();
            if (p is WzCanvasProperty c)
                yield return (path, c);
            var groups = Group(PropertyValues.Children(p));
            foreach (var name in groups.Keys.OrderByDescending(n => n, StringComparer.Ordinal))   // popped in ascending order
            {
                var list = groups[name];
                for (int k = list.Count - 1; k >= 0; k--)
                    stack.Push((list[k], path + "/" + name + (list.Count > 1 ? "#" + k : "")));
            }
        }
    }

    private static int CountDescendants(WzImageProperty p)
    {
        int count = 0;
        var stack = new Stack<WzImageProperty>();
        foreach (var c in PropertyValues.Children(p)) stack.Push(c);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            count++;
            foreach (var c in PropertyValues.Children(node)) stack.Push(c);
        }
        return count;
    }

    // ---- canvases

    private static CanvasInfo? InfoOf(WzCanvasProperty c)
        => c.PngProperty == null ? null : new CanvasInfo(c.PngProperty.Width, c.PngProperty.Height, (int)c.PngProperty.Format);

    private static bool IsLinked(WzCanvasProperty c)
        => c[WzCanvasProperty.InlinkPropertyName] != null || c[WzCanvasProperty.OutlinkPropertyName] != null;

    /// <summary>
    /// Stage 0: size and format from the headers. Stage 1: SHA-256 of the compressed bytes; equal means unchanged.
    /// Stage 2 (limited): decode both to BGRA, clear the colour of fully transparent pixels, hash; equal means only
    /// recompressed (listed, not a change). A linked canvas is compared by its link text (the generic walk); pixels
    /// behind links are never read here.
    /// </summary>
    private void CompareCanvasPixels(string path, WzCanvasProperty a, WzCanvasProperty b)
    {
        var before = InfoOf(a);
        var after = InfoOf(b);
        bool linkedA = IsLinked(a), linkedB = IsLinked(b);

        if (linkedA || linkedB)
        {
            _linkedCanvases++;
            if (linkedA != linkedB)
                ReportCanvas(path, "link", before, after, "canvas");   // one side is a link, the other holds pixels
            return;
        }

        if (before == null || after == null)
        {
            if (before != after)
                ReportCanvas(path, "pixels_unverified", before, after, "canvas");
            return;
        }

        if (before.Width != after.Width || before.Height != after.Height)
        {
            ReportCanvas(path, "size", before, after, "canvas");
            return;
        }
        if (before.Format != after.Format)
        {
            ReportCanvas(path, "format", before, after, "canvas");
            return;
        }

        // stage 1
        string hashA = CompressedHash(a, _old), hashB = CompressedHash(b, _new);
        if (hashA == hashB)
            return;

        // stage 2
        if (_decodes >= _options.MaxCanvasDecodes)
        {
            _budgetSkipped++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
            return;
        }
        if (before.Width > DiffOptions.MaxCanvasSide || before.Height > DiffOptions.MaxCanvasSide
            || (long)before.Width * before.Height * 4 > DiffOptions.MaxCanvasDecodedBytes)
        {
            _undecodable++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
            return;
        }

        _decodes++;
        string? pixelsA, pixelsB;
        try
        {
            pixelsA = PixelHash(a, _old);        // the old image first, one buffer at a time, released at once
            _cancellation.ThrowIfCancellationRequested();
            pixelsB = PixelHash(b, _new);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            pixelsA = pixelsB = null;
        }
        catch (OutOfMemoryException)
        {
            throw new ViewerApiException(413, "too_large", "the image needs more memory than the viewer allows");
        }

        if (pixelsA == null || pixelsB == null)
        {
            _undecodable++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
        }
        else if (pixelsA == pixelsB)
        {
            _recompressed++;
            if (_canvases.Count < _options.MaxChanges)
                _canvases.Add(new CanvasChange(path, "recompressed", before, after));
            else
                _truncated = true;
        }
        else
            ReportCanvas(path, "pixels", before, after, "canvas");
    }

    private void ReportCanvas(string path, string detail, CanvasInfo? before, CanvasInfo? after, string type)
    {
        if (_canvases.Count < _options.MaxChanges)
            _canvases.Add(new CanvasChange(path, detail, before, after));
        else
            _truncated = true;
        AddChange(new DiffChange
        {
            Path = path, Kind = "changed", Type = type,
            Before = before == null ? "canvas (no image)" : $"canvas {before.Width}x{before.Height} format {before.Format}",
            After = after == null ? "canvas (no image)" : $"canvas {after.Width}x{after.Height} format {after.Format}",
            Detail = detail,
        });
    }

    private static string CompressedHash(WzCanvasProperty c, LoadedImage owner)
    {
        byte[]? bytes;
        lock (owner.Sync) bytes = c.PngProperty!.GetCompressedBytes(false);
        return PropertyValues.Hash(bytes);
    }

    /// <summary>Decodes one canvas to BGRA, zeroes the colour where alpha is 0, hashes; the buffer is dropped on return. Null when there is no pixel data.</summary>
    private static string? PixelHash(WzCanvasProperty c, LoadedImage owner)
    {
        byte[]? pixels;
        lock (owner.Sync) pixels = c.PngProperty!.GetPixelsBgra32(out _, out _);
        if (pixels == null)
            return null;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
        }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
}
