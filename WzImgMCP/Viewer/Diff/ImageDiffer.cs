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
/// <item>Caps: <c>MaxChanges</c> (then <c>truncated</c>), a cap on the estimated size of the listed JSON (then
/// <c>truncated</c> and a warning), a wall-clock budget (then <c>truncated</c> and a warning), a per-request number of
/// pixel decodes and a per-request budget of decoded bytes. Paths and child names are cut (<see cref="DiffOptions.MaxNameChars"/>,
/// <see cref="DiffOptions.MaxPathChars"/>) and lone surrogates are replaced by U+FFFD in every string that is output.</item>
/// <item>A read failure of one value or picture (damaged length, short data) is reported for that node
/// (<c>detail "unverified"</c> / <c>pixels_unverified</c>), never as a failure of the whole diff. An
/// <see cref="OutOfMemoryException"/> is NOT caught here: the caller answers 413 and empties the cache.</item>
/// <item>The output is sorted by path (ordinal) and is the same for the same input.</item>
/// </list>
/// An instance is for one comparison.
/// </summary>
public sealed class ImageDiffer
{
    private readonly DiffOptions _options;
    // For a file that exists on one side only the other field is null; it is then never read (every level has an empty list on that side).
    private readonly LoadedImage _old;
    private readonly LoadedImage _new;
    private readonly CancellationToken _cancellation;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<DiffChange> _changes = new();
    private readonly List<CanvasChange> _canvases = new();
    private bool _truncated;
    private bool _timeUp;
    private int _added, _removed, _changed, _recompressed, _hiddenColour;
    private int _decodes, _budgetSkipped, _bytesSkipped, _undecodable, _linkedCanvases, _unreadable;
    private long _decodedBytes;
    private long _responseBytes;
    private bool _sizeCapped;
    private bool _changesCapped, _canvasesCapped;   // which list hit MaxChanges, for the warning

    /// <summary>A node's path as it is output, and whether any name in it or the whole path was cut.</summary>
    /// <param name="Addressable">True when <see cref="Text"/> can be sent to <c>/v1/canvas</c> as a node unchanged (see <see cref="NodeGrammar"/>).</param>
    private readonly record struct NodePath(string Text, bool Cut, bool Addressable = true)
    {
        public static readonly NodePath Root = new("", false, true);

        /// <summary>The node for this path: the path itself when it is addressable, else null.</summary>
        public string? Node => Addressable ? Text : null;

        public NodePath Child(string name, bool duplicated, int k)
        {
            var (shown, nameCut) = PropertyValues.NameForOutput(name);
            var text = (Text.Length == 0 ? "" : Text + "/") + shown + (duplicated ? "#" + k : "");
            bool cut = Cut || nameCut;
            // addressable: every name is valid in a node as written (so nothing was replaced, cut, or contains '/' or '#'), and the whole fits a node
            bool addressable = Addressable && !nameCut && NodeGrammar.IsAddressableName(shown) && text.Length <= NodeGrammar.MaxNodeChars;
            if (text.Length > DiffOptions.MaxPathChars)
            {
                int length = DiffOptions.MaxPathChars;
                if (char.IsHighSurrogate(text[length - 1])) length--;
                text = text.Substring(0, length);
                cut = true;
                addressable = false;
            }
            return new NodePath(text, cut, addressable);
        }
    }

    private sealed record Level(IReadOnlyList<WzImageProperty> Old, IReadOnlyList<WzImageProperty> New, NodePath Prefix);

    /// <summary>The data of a value could not be read: a damaged length, a short stream, an invalid format.</summary>
    private static bool IsDataError(Exception ex)
        => ex is InvalidDataException or ArgumentException or NotSupportedException or OverflowException
            or IndexOutOfRangeException or InvalidOperationException or IOException;   // IOException includes EndOfStreamException

    private ImageDiffer(LoadedImage? old, LoadedImage? @new, DiffOptions options, CancellationToken cancellation)
    {
        _old = old!;
        _new = @new!;
        _options = options;
        _cancellation = cancellation;
    }

    public static ImageDiffResult Diff(LoadedImage old, LoadedImage @new, DiffOptions options, CancellationToken cancellation)
        => new ImageDiffer(old, @new, options, cancellation).Run();

    /// <summary>
    /// A file that exists on one side only: the whole image is walked as an added subtree (<paramref name="added"/>: the file is only
    /// in the person's copy) or a removed one (only in canon). The same rows, pictures and caps as for a subtree that is added or
    /// removed inside a changed file; no pixel work.
    /// </summary>
    public static ImageDiffResult DiffOneSide(LoadedImage image, bool added, DiffOptions options, CancellationToken cancellation)
        => added ? new ImageDiffer(null, image, options, cancellation).Run()
                 : new ImageDiffer(image, null, options, cancellation).Run();

    private ImageDiffResult Run()
    {
        var pending = new Stack<Level>();
        pending.Push(new Level(Top(_old), Top(_new), NodePath.Root));
        while (pending.Count > 0 && !_truncated && !_timeUp)
        {
            var level = pending.Pop();
            var deeper = CompareLevel(level.Old, level.New, level.Prefix);
            for (int i = deeper.Count - 1; i >= 0; i--)   // so the smallest name is expanded first
                pending.Push(deeper[i]);
        }

        var warnings = new List<string>();
        if (_timeUp) warnings.Add($"the comparison stopped after {_options.Budget.TotalSeconds:0.##} s; the list is incomplete");
        if (_sizeCapped) warnings.Add($"the listed changes reached the response size limit ({_options.MaxResponseBytes / 1024} KiB); the list is cut");
        if (_changesCapped && !_timeUp && !_sizeCapped) warnings.Add($"more than {_options.MaxChanges} changes; the list is cut");
        if (_canvasesCapped && !_timeUp && !_sizeCapped) warnings.Add($"more than {_options.MaxChanges} pictures; the list is cut and later entries are not listed");
        if (_budgetSkipped > 0) warnings.Add($"{_budgetSkipped} canvas pair(s) that differ in compressed bytes were not decoded (limit {_options.MaxCanvasDecodes} per request); reported as pixels_unverified");
        if (_bytesSkipped > 0) warnings.Add($"{_bytesSkipped} canvas pair(s) were not decoded: the decoded-bytes budget of {_options.MaxDecodedBytes / (1024 * 1024)} MiB per request was used up; reported as pixels_unverified");
        if (_undecodable > 0) warnings.Add($"{_undecodable} canvas pair(s) could not be verified (too large, damaged or without pixel data); reported as pixels_unverified");
        if (_unreadable > 0) warnings.Add($"{_unreadable} value(s) could not be read (damaged data); reported as unverified");
        if (_hiddenColour > 0) warnings.Add($"{_hiddenColour} canvas(es) have equal visible pixels but a different colour under fully transparent pixels (hidden_color)");
        if (_linkedCanvases > 0) warnings.Add($"{_linkedCanvases} linked canvas(es) (_inlink/_outlink) were compared by their link text only; pixels behind links are not compared");

        var changes = _changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        var canvases = _canvases.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        int canvasesChanged = canvases.Count(c => c.Change is not ("recompressed" or "hidden_color"));
        return new ImageDiffResult(changes, canvases, new DiffSummary(_added, _removed, _changed, canvasesChanged, _recompressed, _hiddenColour),
            _truncated || _timeUp, warnings)
        {
            HiddenColourCanvases = _hiddenColour,
            LinkedCanvases = _linkedCanvases,
        };
    }

    private static IReadOnlyList<WzImageProperty> Top(LoadedImage? image)
        => image == null ? Array.Empty<WzImageProperty>() : (IReadOnlyList<WzImageProperty>)image.Image.WzProperties;

    private bool Tick()
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_clock.Elapsed > _options.Budget)
            _timeUp = true;
        return !_timeUp;
    }

    /// <summary>Compares the children of one pair of containers; returns the pairs whose children must be compared next.</summary>
    private List<Level> CompareLevel(IReadOnlyList<WzImageProperty> oldChildren, IReadOnlyList<WzImageProperty> newChildren, NodePath prefix)
    {
        var deeper = new List<Level>();
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
                var path = prefix.Child(name, duplicated, k);

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

    /// <summary>Describe, but a value whose data cannot be read is "(unreadable)" instead of an exception.</summary>
    private (string? Text, bool Truncated) SafeDescribe(WzImageProperty p, LoadedImage owner)
    {
        try { return PropertyValues.Describe(p, owner); }
        catch (Exception ex) when (IsDataError(ex))
        {
            _unreadable++;
            return ("(unreadable)", false);
        }
    }

    private void ComparePair(NodePath path, WzImageProperty a, WzImageProperty b, List<Level> deeper)
    {
        string ta = PropertyValues.TypeOf(a), tb = PropertyValues.TypeOf(b);
        if (ta != tb)
        {
            var (before, bt) = SafeDescribe(a, _old);
            var (after, at) = SafeDescribe(b, _new);
            int below = CountDescendants(a) + CountDescendants(b);
            AddChange(new DiffChange
            {
                Path = path.Text, PathTruncated = path.Cut ? true : null, Kind = "changed", Type = tb,
                Before = $"({ta}) {before}".TrimEnd(), After = $"({tb}) {after}".TrimEnd(),
                BeforeTruncated = bt ? true : null, AfterTruncated = at ? true : null,
                Detail = "type", DescendantCount = below > 0 ? below : null,
            });
            // The subtrees are not walked, but the pictures in them are listed, as for an added or removed subtree.
            ListCanvases(path, a, "removed", _old);
            ListCanvases(path, b, "added", _new);
            return;
        }

        if (a is WzCanvasProperty ca && b is WzCanvasProperty cb)
            CompareCanvasPixels(path, ca, cb);
        else
        {
            bool equal = true, unverified = false;
            try
            {
                _options.Probe?.Invoke("value");
                equal = PropertyValues.ValueEquals(a, b, _old, _new);
            }
            catch (Exception ex) when (IsDataError(ex)) { equal = false; unverified = true; _unreadable++; }

            if (!equal)
            {
                var (before, bt) = unverified ? ("(unreadable)", false) : SafeDescribe(a, _old);
                var (after, at) = unverified ? ("(unreadable)", false) : SafeDescribe(b, _new);
                AddChange(new DiffChange
                {
                    Path = path.Text, PathTruncated = path.Cut ? true : null, Kind = "changed", Type = ta, Before = before, After = after,
                    BeforeTruncated = bt ? true : null, AfterTruncated = at ? true : null,
                    Detail = unverified ? "unverified" : null,
                });
            }
        }

        var childrenA = PropertyValues.Children(a);
        var childrenB = PropertyValues.Children(b);
        if (childrenA.Count > 0 || childrenB.Count > 0)
            deeper.Add(new Level(childrenA, childrenB, path));
    }

    private bool AddChange(DiffChange change)
    {
        if (_changes.Count >= _options.MaxChanges || !Reserve(ChangeSize(change)))
        {
            _truncated = true;
            if (_changes.Count >= _options.MaxChanges) _changesCapped = true;
            return false;
        }
        _changes.Add(change);
        switch (change.Kind)
        {
            case "added": _added++; break;
            case "removed": _removed++; break;
            default: _changed++; break;
        }
        return true;
    }

    /// <summary>Adds a canvas entry unless the entry count or the response size cap is reached (then <c>truncated</c>).</summary>
    private void AddCanvas(CanvasChange canvas)
    {
        if (_canvases.Count >= _options.MaxChanges || !Reserve(CanvasSize(canvas)))
        {
            _truncated = true;
            if (_canvases.Count >= _options.MaxChanges) _canvasesCapped = true;
            return;
        }
        _canvases.Add(canvas);
    }

    /// <summary>Counts <paramref name="bytes"/> against the response cap; false (and the cap is flagged) when it would be exceeded.</summary>
    private bool Reserve(long bytes)
    {
        if (_responseBytes + bytes > _options.MaxResponseBytes)
        {
            _sizeCapped = true;
            return false;
        }
        _responseBytes += bytes;
        return true;
    }

    /// <summary>
    /// An upper bound of the JSON written for one change: every string at 6 bytes per character unless it is a plain ASCII
    /// character (the default encoder writes anything else as \uXXXX), plus 128 for the keys and punctuation.
    /// </summary>
    private static long ChangeSize(DiffChange c)
    {
        long size = 128 + JsonLength(c.Path) + JsonLength(c.Kind) + JsonLength(c.Type) + JsonLength(c.Before) + JsonLength(c.After) + JsonLength(c.Detail);
        if (c.Children != null)
            foreach (var name in c.Children) size += JsonLength(name) + 4;
        return size;
    }

    private static long CanvasSize(CanvasChange c) => 160 + JsonLength(c.Path) + JsonLength(c.Change);

    private static long JsonLength(string? s)
    {
        if (s == null) return 0;
        long n = 2;
        foreach (var ch in s)
            n += ch >= 0x20 && ch < 0x7f && ch is not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') ? 1 : 6;
        return n;
    }

    // ---- nodes on one side only

    private void AddOnlyOneSide(NodePath path, WzImageProperty p, string kind, LoadedImage owner)
    {
        var type = PropertyValues.TypeOf(p);
        var (text, truncated) = SafeDescribe(p, owner);
        var children = PropertyValues.Children(p);
        int below = children.Count == 0 ? 0 : CountDescendants(p);
        var names = children.Count == 0 ? null
            : (IReadOnlyList<string>)children.Select(c => c.Name ?? "").OrderBy(n => n, StringComparer.Ordinal).Take(DiffOptions.MaxSubtreeNames)
                .Select(n => PropertyValues.NameForOutput(n).Text).ToList();
        bool added = kind == "added";

        bool listed = AddChange(new DiffChange
        {
            Path = path.Text, PathTruncated = path.Cut ? true : null, Kind = kind, Type = type,
            Before = added ? null : text, After = added ? text : null,
            BeforeTruncated = !added && truncated ? true : null, AfterTruncated = added && truncated ? true : null,
            DescendantCount = children.Count == 0 ? null : below,
            Children = names,
        });

        // A small branch that only holds a few text values (a new item's name and description in String.wz) also gets one row per
        // value, so the panel can show "Cap/1004081/name = ...". The collapsed row above stays.
        if (listed && children.Count > 0 && below <= DiffOptions.MaxExpandNodes && TryCollectTextLeaves(p, path, out var leaves))
        {
            foreach (var (leafPath, leaf) in leaves.OrderBy(l => l.Path.Text, StringComparer.Ordinal))
            {
                var (leafText, leafCut) = SafeDescribe(leaf, owner);
                if (!AddChange(new DiffChange
                {
                    Path = leafPath.Text, PathTruncated = leafPath.Cut ? true : null, Kind = kind, Type = "string",
                    Before = added ? null : leafText, After = added ? leafText : null,
                    BeforeTruncated = !added && leafCut ? true : null, AfterTruncated = added && leafCut ? true : null,
                }))
                    break;
            }
        }

        ListCanvases(path, p, kind, owner);
    }

    /// <summary>
    /// True when everything below <paramref name="root"/> is at most <see cref="DiffOptions.MaxExpandLeaves"/> text values (and the
    /// containers between): no pictures, numbers, vectors or links. Those leaves are returned with their paths.
    /// </summary>
    private static bool TryCollectTextLeaves(WzImageProperty root, NodePath rootPath, out List<(NodePath Path, WzImageProperty Leaf)> leaves)
    {
        leaves = new List<(NodePath, WzImageProperty)>();
        var stack = new Stack<(WzImageProperty, NodePath)>();
        stack.Push((root, rootPath));
        while (stack.Count > 0)
        {
            var (node, path) = stack.Pop();
            if (node is WzCanvasProperty)   // a picture (even one holding only a link text such as _inlink) is never a text row
                return false;
            var children = PropertyValues.Children(node);
            if (children.Count == 0)
            {
                if (node == root || PropertyValues.TypeOf(node) != "string" || leaves.Count >= DiffOptions.MaxExpandLeaves)
                    return false;
                leaves.Add((path, node));
                continue;
            }
            var groups = Group(children);
            foreach (var name in groups.Keys)
            {
                var list = groups[name];
                for (int k = 0; k < list.Count; k++)
                    stack.Push((list[k], path.Child(name, list.Count > 1, k)));
            }
        }
        return leaves.Count > 0;
    }

    /// <summary>The canvases in a subtree that is added, removed or replaced are listed (without pixel work), so the panel can show those pictures.</summary>
    private void ListCanvases(NodePath path, WzImageProperty root, string kind, LoadedImage owner)
    {
        bool added = kind == "added";
        foreach (var (canvasPath, canvas) in CanvasesIn(root, path))
        {
            if (_canvases.Count >= _options.MaxChanges) { _truncated = true; _canvasesCapped = true; break; }
            var info = InfoOf(canvas);
            AddCanvas(new CanvasChange(canvasPath.Text, kind, added ? null : info, added ? info : null, canvasPath.Node));
            if (_sizeCapped) break;
        }
    }

    /// <summary>Called every <see cref="WalkTickNodes"/> nodes of a subtree walk: the time budget and the cancellation apply inside one big entry too. False when the time is up.</summary>
    private bool WalkTick()
    {
        if (_timeUp) return false;
        _options.Probe?.Invoke("walk");
        return Tick();
    }

    private const int WalkTickNodes = 4096;

    private IEnumerable<(NodePath Path, WzCanvasProperty Canvas)> CanvasesIn(WzImageProperty root, NodePath rootPath)
    {
        var stack = new Stack<(WzImageProperty, NodePath)>();
        stack.Push((root, rootPath));
        int visited = 0;
        while (stack.Count > 0)
        {
            if (++visited % WalkTickNodes == 0 && !WalkTick())
                yield break;
            var (p, path) = stack.Pop();
            if (p is WzCanvasProperty c)
                yield return (path, c);
            var groups = Group(PropertyValues.Children(p));
            foreach (var name in groups.Keys.OrderByDescending(n => n, StringComparer.Ordinal))   // popped in ascending order
            {
                var list = groups[name];
                for (int k = list.Count - 1; k >= 0; k--)
                    stack.Push((list[k], path.Child(name, list.Count > 1, k)));
            }
        }
    }

    /// <summary>The number of nodes below <paramref name="p"/>; when the time is up it stops (the count is then too low and the answer is flagged incomplete).</summary>
    private int CountDescendants(WzImageProperty p)
    {
        int count = 0;
        var stack = new Stack<WzImageProperty>();
        foreach (var c in PropertyValues.Children(p)) stack.Push(c);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            count++;
            if (count % WalkTickNodes == 0 && !WalkTick())
                break;
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
    /// Stage 2 (limited): decode both to BGRA and hash twice: the raw pixels, and the pixels with the colour of fully
    /// transparent pixels cleared. Raw equal means only recompressed (listed, not a change). Only the cleared hash
    /// equal means <c>hidden_color</c>: nothing visible differs, but the bytes under alpha 0 do (listed as a change, and
    /// the semantic result is <c>identical_visible</c>). A linked canvas is compared by its link text (the generic
    /// walk); pixels behind links are never read here.
    /// </summary>
    private void CompareCanvasPixels(NodePath path, WzCanvasProperty a, WzCanvasProperty b)
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

        // stage 1 (a read failure of the compressed data is a failed verification of this canvas, not of the diff)
        string hashA, hashB;
        try
        {
            _options.Probe?.Invoke("stage1");
            hashA = CompressedHash(a, _old);
            hashB = CompressedHash(b, _new);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            _undecodable++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
            return;
        }
        if (hashA == hashB)
            return;

        // stage 2
        if (_decodes >= _options.MaxCanvasDecodes)
        {
            _budgetSkipped++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
            return;
        }
        long decoded = (long)before.Width * before.Height * 4;
        if (before.Width <= 0 || before.Height <= 0 || before.Width > DiffOptions.MaxCanvasSide || before.Height > DiffOptions.MaxCanvasSide
            || decoded > _options.MaxCanvasBytes)
        {
            _undecodable++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
            return;
        }
        if (_decodedBytes + 2 * decoded > _options.MaxDecodedBytes)
        {
            _bytesSkipped++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
            return;
        }

        _decodes++;
        _decodedBytes += 2 * decoded;
        PixelHashes? pixelsA, pixelsB;
        try
        {
            _options.Probe?.Invoke("decode");
            pixelsA = PixelHash(a, _old);        // the old image first, one buffer at a time, released at once
            _cancellation.ThrowIfCancellationRequested();
            pixelsB = PixelHash(b, _new);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            pixelsA = pixelsB = null;
        }

        if (pixelsA == null || pixelsB == null)
        {
            _undecodable++;
            ReportCanvas(path, "pixels_unverified", before, after, "canvas");
        }
        else if (pixelsA.Raw == pixelsB.Raw)
        {
            _recompressed++;
            AddCanvas(new CanvasChange(path.Text, "recompressed", before, after, path.Node));
        }
        else if (pixelsA.Visible == pixelsB.Visible)
        {
            _hiddenColour++;   // listed in canvases only, like recompressed: it is not a visible change, but it is not "identical" either
            AddCanvas(new CanvasChange(path.Text, "hidden_color", before, after, path.Node));
        }
        else
            ReportCanvas(path, "pixels", before, after, "canvas");
    }

    private void ReportCanvas(NodePath path, string detail, CanvasInfo? before, CanvasInfo? after, string type)
    {
        AddCanvas(new CanvasChange(path.Text, detail, before, after, path.Node));
        AddChange(new DiffChange
        {
            Path = path.Text, PathTruncated = path.Cut ? true : null, Kind = "changed", Type = type,
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

    private sealed record PixelHashes(string Raw, string Visible);

    /// <summary>Decodes one canvas to BGRA and hashes it as it is (Raw) and with the colour of alpha-0 pixels zeroed (Visible); the buffer is dropped on return. Null when there is no pixel data.</summary>
    private static PixelHashes? PixelHash(WzCanvasProperty c, LoadedImage owner)
    {
        byte[]? pixels;
        lock (owner.Sync) pixels = c.PngProperty!.GetPixelsBgra32(out _, out _);
        if (pixels == null)
            return null;
        var raw = Convert.ToHexString(SHA256.HashData(pixels));
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
        }
        return new PixelHashes(raw, Convert.ToHexString(SHA256.HashData(pixels)));
    }
}
