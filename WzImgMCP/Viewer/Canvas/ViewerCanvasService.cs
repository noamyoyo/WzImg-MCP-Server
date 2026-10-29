#if LINUX_BUILD
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Imaging;

namespace WzImgMCP.Viewer.Canvas;

/// <summary>One frame of an animation, as <c>/v1/frames</c> lists it. No pixel data: the panel fetches each frame from <c>/v1/canvas</c>.</summary>
public sealed record FrameInfo(int Index, string Node, int Width, int Height, int OriginX, int OriginY, int? DelayMs);

public sealed record FramesResult(IReadOnlyList<FrameInfo> Frames, bool Truncated);

/// <summary>A rendered canvas: the PNG and what the headers say about it.</summary>
/// <param name="Width">Width of the canvas in the image (the origin is in these pixels).</param>
/// <param name="OutputWidth">Width of the PNG; smaller than <paramref name="Width"/> when it was scaled down.</param>
/// <param name="ETag">Null when the pixels come from another image (an <c>_outlink</c>): the file stamp alone cannot tell when they change.</param>
public sealed record CanvasRender(byte[] Png, int Width, int Height, int OutputWidth, int OutputHeight, int OriginX, int OriginY, int? DelayMs, string? ETag);

/// <summary>
/// The picture and frame endpoints (<c>/v1/canvas</c>, <c>/v1/frames</c>): finds a node in a cached parsed image, follows
/// UOL frames and canvas links, and renders one canvas as PNG.
///
/// Rules:
/// <list type="bullet">
/// <item><c>node</c> is a property path inside the image (see <see cref="NodeGrammar"/>): ASCII letters, digits, space, '_', '-', '.', '/' as separator, an optional '#k' on a segment for the k-th child of that name, at most 200 characters, no empty segment, no "." or ".." segment and no ".." anywhere.
/// A bad pattern is <c>bad_path</c> (400). A node that does not exist, or is not a canvas (or, for frames, not a container), is <c>not_found</c> (404): one answer, whatever the reason.</item>
/// <item>UOLs on the way and as the node itself are followed inside the same image. A canvas with <c>_inlink</c> is followed inside its image;
/// one with <c>_outlink</c> through <see cref="ViewerLinkContext"/>, so only inside the request's own source and through the same path rules and safe reader. No other link is ever read.</item>
/// <item>The tree and the shared reader are not thread safe: every access holds <see cref="LoadedImage.Sync"/> of the image the property belongs to, one image at a time (never two at once, so there is no lock order to get wrong), and never while another image is loaded or parsed,
/// and the lock is released before the pixels are scaled and encoded.</item>
/// <item>Decoded size above <see cref="ViewerOptions.MaxPictureBytes"/> (64 MiB by default, the same limit as the diff), or above 32768 pixels on a side, is <c>too_large</c> (413) and is checked from the header before any decode. A PNG above 16 MiB is <c>too_large</c> as well.</item>
/// <item>Out of memory is 413 <c>too_large</c> and empties the cache, as in the loader.</item>
/// </list>
/// Synchronous; run it through <see cref="ViewerWorkRunner"/>, which holds the request and decode slots until it returns.
/// </summary>
public sealed class ViewerCanvasService
{
    public const int MaxNodeChars = NodeGrammar.MaxNodeChars;
    public const int DefaultMaxSide = 1024;
    public const int FullMaxSide = 4096;
    public const int MaxFrames = 512;
    public const int MaxLinkHops = 8;
    public const long MaxPngBytes = 16L * 1024 * 1024;
    /// <summary>Part of every ETag: bump it when the rendering (scaling, encoding, headers) changes.</summary>
    public const string RendererVersion = "r1";

    private readonly ViewerImageLoader _loader;
    private readonly long _maxPictureBytes;

    /// <param name="maxPictureBytes">Largest canvas (width x height x 4) that is decoded; <see cref="ViewerOptions.MaxPictureBytes"/>.</param>
    public ViewerCanvasService(ViewerImageLoader loader, long maxPictureBytes = ViewerOptions.DefaultMaxPictureMb * 1024L * 1024L)
    {
        _loader = loader;
        _maxPictureBytes = maxPictureBytes;
    }

    /// <summary>Test seam: called with "decode" just before the pixels of a canvas are read (may throw, for instance an OutOfMemoryException).</summary>
    public Action<string>? Probe { get; set; }

    // ---- request parameters

    /// <summary>Checks the node grammar (see <see cref="NodeGrammar"/>) and returns its segments. Throws <c>bad_path</c>.</summary>
    public static NodeSegment[] ParseNode(string? node) => NodeGrammar.Parse(node);

    /// <summary>The longest side of the PNG for <c>maxSide</c>: omitted or "1024" is 1024, "full" is 4096. Anything else is <c>bad_request</c>.</summary>
    public static int ParseMaxSide(string? maxSide, out string normalized)
    {
        switch (maxSide)
        {
            case null or "" or "1024": normalized = "1024"; return DefaultMaxSide;
            case "full": normalized = "full"; return FullMaxSide;
            default: throw new ViewerApiException(400, "bad_request", "maxSide must be 1024 or full");
        }
    }

    /// <summary>
    /// The strong ETag of a rendered canvas: it changes with the file (size, mtime, ctime, inode), the node, maxSide and the
    /// renderer version, and with nothing else, so a matching If-None-Match needs no decode.
    /// </summary>
    public static string ETagFor(string source, string relativePath, FileStamp stamp, string node, string maxSide)
    {
        var text = string.Join('\n', "canvas", RendererVersion, source, relativePath,
            stamp.Size.ToString(CultureInfo.InvariantCulture), stamp.MtimeTicks.ToString(CultureInfo.InvariantCulture),
            stamp.CtimeTicks.ToString(CultureInfo.InvariantCulture), stamp.Inode.ToString(CultureInfo.InvariantCulture), node, maxSide);
        return "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant().Substring(0, 32) + "\"";
    }

    /// <summary>True when an If-None-Match header value names <paramref name="etag"/> (also as a weak tag) or is "*".</summary>
    public static bool IfNoneMatches(string? header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header))
            return false;
        foreach (var part in header.Split(','))
        {
            var tag = part.Trim();
            if (tag == "*")
                return true;
            if (tag.StartsWith("W/", StringComparison.Ordinal))
                tag = tag.Substring(2);
            if (string.Equals(tag, etag, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ---- /v1/canvas

    public CanvasRender Render(ResolvedImage image, NodeSegment[] node, string nodeText, int maxSide, string maxSideText, ViewerLinkContext links, CancellationToken cancellationToken)
    {
        byte[] bgra;
        int width, height, originX, originY;
        int? delay;
        ImageKey key;
        bool viaOutlink;

        using (var lease = _loader.Load(image, cancellationToken))
        {
            key = lease.Loaded.Key;
            var found = Locate(lease.Loaded, node, links, cancellationToken);
            if (found is not CanvasSource source)
                throw NoCanvas();
            viaOutlink = source.ViaOutlink;
            width = source.Width;
            height = source.Height;
            originX = source.OriginX;
            originY = source.OriginY;
            delay = source.DelayMs;
            bgra = Decode(source, cancellationToken);
        }   // the image is given back before the pixels are scaled and encoded

        cancellationToken.ThrowIfCancellationRequested();
        int outW = width, outH = height;
        byte[] png;
        try
        {
            if (Math.Max(width, height) > maxSide)
            {
                double factor = (double)maxSide / Math.Max(width, height);
                outW = Math.Max(1, (int)Math.Round(width * factor));
                outH = Math.Max(1, (int)Math.Round(height * factor));
                png = CanvasImaging.ScaleBgraToPng(bgra, width, height, outW, outH);
            }
            else
                png = CanvasImaging.EncodeBgraToPng(bgra, width, height);
        }
        catch (OutOfMemoryException)
        {
            throw OutOfMemory();
        }
        bgra = null!;

        if (png.Length > MaxPngBytes)
            throw new ViewerApiException(413, "too_large", "the picture is larger than the viewer sends");

        var stamp = new FileStamp(key.Size, key.MtimeTicks, key.CtimeTicks, key.Inode);
        return new CanvasRender(png, width, height, outW, outH, originX, originY, delay,
            viaOutlink ? null : ETagFor(image.Source, image.RelativePath, stamp, nodeText, maxSideText));
    }

    // ---- /v1/frames

    public FramesResult Frames(ResolvedImage image, NodeSegment[] node, string nodeText, ViewerLinkContext links, CancellationToken cancellationToken)
    {
        using var lease = _loader.Load(image, cancellationToken);
        var loaded = lease.Loaded;

        // The numeric children of the parent, in order. Gaps are simply not there; a child that is not a canvas (after UOLs and links) is skipped.
        List<(int Index, WzImageProperty Child)> candidates;
        lock (loaded.Sync)
        {
            var parent = FollowTo(loaded, node, cancellationToken);
            if (parent == null || (parent is not WzSubProperty && parent is not WzCanvasProperty))
                throw NoCanvas();
            candidates = new List<(int, WzImageProperty)>();
            var seen = new HashSet<int>();
            foreach (var child in parent.WzProperties ?? new WzPropertyCollection(null!))
                if (TryIndex(child.Name, out int index) && seen.Add(index))   // the first of two children with one index
                    candidates.Add((index, child));
        }
        candidates.Sort((a, b) => a.Index.CompareTo(b.Index));

        var frames = new List<FrameInfo>();
        bool truncated = false;
        foreach (var (index, child) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = Describe(loaded, child, links, cancellationToken);
            if (info == null)
                continue;
            if (frames.Count >= MaxFrames)
            {
                truncated = true;
                break;
            }
            frames.Add(new FrameInfo(index, nodeText + "/" + index.ToString(CultureInfo.InvariantCulture), info.Width, info.Height, info.OriginX, info.OriginY, info.DelayMs));
        }
        return new FramesResult(frames, truncated);
    }

    /// <summary>"0", "7", "12": no sign, no leading zero (except "0" itself), fits an int.</summary>
    private static bool TryIndex(string? name, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(name) || name.Length > 9)
            return false;
        if (name.Length > 1 && name[0] == '0')
            return false;
        foreach (var c in name)
            if (c is < '0' or > '9')
                return false;
        index = int.Parse(name, CultureInfo.InvariantCulture);
        return true;
    }

    // ---- finding a canvas

    /// <summary>A canvas that holds pixels, and where they live.</summary>
    private sealed record CanvasSource(LoadedImage Owner, WzPngProperty Png, int Width, int Height, int OriginX, int OriginY, int? DelayMs, bool ViaOutlink);

    /// <summary>The canvas at <paramref name="node"/>, or null (no such node, not a canvas, no pixel data, a link that does not resolve).</summary>
    private CanvasSource? Locate(LoadedImage loaded, NodeSegment[] node, ViewerLinkContext links, CancellationToken cancellationToken)
    {
        WzImageProperty? start;
        lock (loaded.Sync)
            start = FollowTo(loaded, node, cancellationToken);
        return start == null ? null : Describe(loaded, start, links, cancellationToken);
    }

    /// <summary>Walks the segments from the top of the image (the k-th child of that name at each level, the first for a bare name), following UOLs. Call under the image's Sync.</summary>
    private static WzImageProperty? FollowTo(LoadedImage loaded, NodeSegment[] node, CancellationToken cancellationToken)
    {
        IEnumerable<WzImageProperty>? children = loaded.Image.WzProperties;
        WzImageProperty? current = null;
        foreach (var segment in node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (children == null)
                return null;
            // name#k: the k-th child with exactly that name, in file order (a bare name is the first)
            current = children.Where(c => string.Equals(c.Name, segment.Name, StringComparison.Ordinal)).Skip(segment.Index).FirstOrDefault();
            current = FollowUol(loaded.Image, current);
            if (current == null)
                return null;
            children = current.WzProperties;
        }
        return current;
    }

    /// <summary>Follows UOLs (at most <see cref="MaxLinkHops"/>) to the property they name, which must be in the same image. Null when it is not.</summary>
    private static WzImageProperty? FollowUol(WzImage image, WzImageProperty? property)
    {
        for (int hop = 0; property is WzUOLProperty uol; hop++)
        {
            if (hop >= MaxLinkHops)
                return null;
            try { property = uol.LinkValue as WzImageProperty; }
            catch (InvalidDataException) { return null; }   // the library detects some UOL loops itself and throws
            if (property != null && !ReferenceEquals(property.ParentImage, image))
                return null;
        }
        return property;
    }

    /// <summary>
    /// Metadata and pixel source of the canvas <paramref name="property"/> (a UOL is followed first). The link chain is walked one
    /// step at a time and <b>every tree read holds the Sync of the image that tree belongs to, and only that one</b>:
    /// <list type="bullet">
    /// <item>an <c>_inlink</c> is looked up in the same image, under its lock;</item>
    /// <item>an <c>_outlink</c> is split into image path and property path under the lock, then the other image is opened through
    /// <see cref="ViewerLinkContext"/> with NO lock held (loading and parsing can take long, and other requests on the first image
    /// must not wait for it), and the property is looked up in it under that image's own lock.</item>
    /// </list>
    /// The library's <c>GetLinkedWzImageProperty()</c> is deliberately not used: for an <c>_outlink</c> it loads the other image through
    /// the static hook while the caller holds the lock, and then reads the other tree under the wrong lock.
    /// </summary>
    private CanvasSource? Describe(LoadedImage loaded, WzImageProperty property, ViewerLinkContext links, CancellationToken cancellationToken)
    {
        int? ownOriginX, ownOriginY, ownDelay;
        bool viaOutlink = false;
        LoadedImage owner = loaded;
        WzCanvasProperty canvas;

        lock (loaded.Sync)
        {
            if (FollowUol(loaded.Image, property) is not WzCanvasProperty first)
                return null;
            canvas = first;
            (ownOriginX, ownOriginY) = OriginOf(canvas);
            ownDelay = DelayOf(canvas);
        }

        for (int hop = 0; ; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? inlink = null, outlink = null;
            bool linked;
            lock (owner.Sync)
            {
                linked = IsLinked(canvas);
                if (linked)
                {
                    inlink = (canvas[WzCanvasProperty.InlinkPropertyName] as WzStringProperty)?.Value;
                    outlink = (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;
                }
            }
            if (!linked)
                break;
            if (hop >= MaxLinkHops)
                return null;

            WzImageProperty? next;
            LoadedImage nextOwner = owner;
            if (!string.IsNullOrEmpty(inlink))
            {
                lock (owner.Sync)
                    next = FollowUol(owner.Image, owner.Image.GetFromPath(inlink));   // the same image, the same lock
            }
            else if (!string.IsNullOrEmpty(outlink))
            {
                viaOutlink = true;
                if (!TrySplitOutlink(outlink, out var imagePath, out var propertyPath))
                    return null;
                var other = links.LoadLinked(imagePath);   // no lock held: the hook may load and parse
                nextOwner = other == null ? null! : links.LoadedFor(other)!;
                if (nextOwner == null)
                    return null;   // outside the rules, another source, missing, over the budget: never read
                lock (nextOwner.Sync)
                    next = FollowUol(nextOwner.Image, nextOwner.Image.GetFromPath(propertyPath));
            }
            else
                return null;   // a link property without a usable text

            if (next is not WzCanvasProperty nextCanvas)
                return null;
            canvas = nextCanvas;
            owner = nextOwner;
        }

        lock (owner.Sync)
        {
            var png = canvas.PngProperty;
            if (png == null)
                return null;
            int width = png.Width, height = png.Height;
            if (width <= 0 || height <= 0)
                return null;
            var (tx, ty) = OriginOf(canvas);
            return new CanvasSource(owner, png, width, height, ownOriginX ?? tx ?? 0, ownOriginY ?? ty ?? 0, ownDelay ?? DelayOf(canvas), viaOutlink);
        }
    }

    /// <summary>
    /// Splits <c>Mob/8800141.img/8800141.png</c> into the image path (<c>Mob/8800141.img</c>) and the property path, as the library's
    /// own outlink lookup does: the image ends at the first segment that ends with <c>.img</c>; backslashes in the image part are slashes.
    /// </summary>
    public static bool TrySplitOutlink(string outlink, out string imagePath, out string propertyPath)
    {
        imagePath = propertyPath = "";
        var path = outlink.Trim().Trim('/');
        int start = 0;
        while (start < path.Length)
        {
            int slash = path.IndexOfAny(new[] { '/', '\\' }, start);
            int end = slash < 0 ? path.Length : slash;
            if (end - start >= 4 && string.Compare(path, end - 4, ".img", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (end + 1 >= path.Length)
                    return false;   // an image without a property path
                imagePath = path.Substring(0, end).Replace('\\', '/');
                propertyPath = path.Substring(end + 1);
                return true;
            }
            if (slash < 0)
                break;
            start = slash + 1;
        }
        return false;
    }

    private static bool IsLinked(WzCanvasProperty c)
        => c[WzCanvasProperty.InlinkPropertyName] != null || c[WzCanvasProperty.OutlinkPropertyName] != null;

    private static (int? X, int? Y) OriginOf(WzCanvasProperty c)
        => c[WzCanvasProperty.OriginPropertyName] is WzVectorProperty v ? (v.X?.Value ?? 0, v.Y?.Value ?? 0) : (null, null);

    private static int? DelayOf(WzCanvasProperty c) => c[WzCanvasProperty.AnimationDelayPropertyName] switch
    {
        WzIntProperty i => i.Value,
        WzShortProperty s => s.Value,
        WzLongProperty l => (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue),
        _ => null,
    };

    // ---- decoding

    /// <summary>413 <c>too_large</c> when a canvas is over 32768 pixels on a side or over <paramref name="maxBytes"/> decoded (width x height x 4). Made from the header, before any decode.</summary>
    public static void CheckDecodedSize(int width, int height, long maxBytes)
    {
        if (width > Diff.DiffOptions.MaxCanvasSide || height > Diff.DiffOptions.MaxCanvasSide || (long)width * height * 4 > maxBytes)
            throw new ViewerApiException(413, "too_large", "the canvas is larger than the viewer decodes");
    }

    /// <summary>Decodes the pixels under the owner's Sync (and nothing else under it). Size limits are checked from the header first.</summary>
    private byte[] Decode(CanvasSource source, CancellationToken cancellationToken)
    {
        CheckDecodedSize(source.Width, source.Height, _maxPictureBytes);

        cancellationToken.ThrowIfCancellationRequested();
        byte[]? pixels;
        int w, h;
        try
        {
            Probe?.Invoke("decode");
            lock (source.Owner.Sync)
                pixels = source.Png.GetPixelsBgra32(out w, out h);
        }
        catch (OutOfMemoryException)
        {
            throw OutOfMemory();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or OverflowException
                                       or IndexOutOfRangeException or InvalidOperationException or IOException)
        {
            throw new ViewerApiException(422, "parse_failed", "the canvas could not be decoded");
        }
        if (pixels == null || w != source.Width || h != source.Height || (long)w * h * 4 != pixels.Length)
            throw NoCanvas();
        return pixels;
    }

    private static ViewerApiException NoCanvas() => new(404, "not_found", "no canvas at that node");

    private ViewerApiException OutOfMemory()
    {
        _loader.Cache.Clear();   // give memory back before the next request
        return new ViewerApiException(413, "too_large", "the picture needs more memory than the viewer allows");
    }
}
#endif
