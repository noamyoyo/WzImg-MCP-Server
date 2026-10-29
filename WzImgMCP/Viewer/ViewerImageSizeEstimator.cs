using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Viewer;

/// <summary>
/// Estimate of the memory a parsed image holds, deliberately on the high side. The cache cap is meant to approximate
/// real memory, and a cache that undercounts lets a container run out of memory. The constants come from measuring
/// GC.GetTotalMemory growth against the node counts of 10 real v83 files (7 KB to 16 MB): the parsed tree really holds
/// 0.7 to 1.6 times the unscaled estimate below (worst: String/Mob.img, 3208 string nodes, 1.58). The multiplier 2.0
/// keeps the estimate above the measurement for all of them. This counts live objects only; the GC heap and the
/// process can be larger than the cache cap suggests, so size the container with room to spare.
/// </summary>
public static class ViewerImageSizeEstimator
{
    /// <summary>Bytes per property node (object, name string, list slot, name index entry) before the multiplier.</summary>
    internal const long NodeBytes = 200;
    /// <summary>Bytes per character of a name or string value (UTF-16).</summary>
    internal const long CharBytes = 2;
    /// <summary>Extra for a canvas: its WzPngProperty and the reader state behind the lazy pixel data.</summary>
    internal const long CanvasBytes = 400;
    /// <summary>Safety factor on the tree part; chosen from the measurement.</summary>
    internal const double Multiplier = 2.0;

    /// <summary>
    /// The file bytes stay in memory (canvas pixels are read from them lazily), plus the parsed tree.
    /// </summary>
    public static long Estimate(WzImage image, long fileBytes) => fileBytes + (long)(TreeBytes(image, out _, out _) * Multiplier);

    /// <summary>The unscaled tree estimate, with the node and canvas counts it is built from (used by the measurement).</summary>
    public static long TreeBytes(WzImage image, out long nodes, out long canvases)
    {
        long total = 0;
        nodes = 0;
        canvases = 0;
        var stack = new Stack<WzImageProperty>();
        foreach (var p in image.WzProperties) stack.Push(p);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            nodes++;
            total += NodeBytes + CharBytes * (p.Name?.Length ?? 0);
            if (p is WzStringProperty s)
                total += CharBytes * (s.Value?.Length ?? 0);
            else if (p is WzUOLProperty u)
                total += CharBytes * (u.Value?.Length ?? 0);
            else if (p is WzCanvasProperty)
            {
                canvases++;
                total += CanvasBytes;
            }
            var children = p.WzProperties;
            if (children != null)
                foreach (var c in children) stack.Push(c);
        }
        return total;
    }
}
