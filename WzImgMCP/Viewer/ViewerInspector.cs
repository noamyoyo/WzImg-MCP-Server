using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Viewer;

/// <summary>What <c>/v1/inspect</c> reports about one image.</summary>
public sealed record InspectResult(
    string Source, string Path, long SizeBytes, string KeyUsed, long ParseMillis,
    int TopLevelProperties, int CanvasCount, bool Cached, LinkSummary Links);

/// <summary>Canvases that point at another canvas. <c>OutlinksChecked</c> is capped; only the checked ones are resolved.</summary>
public sealed record LinkSummary(int Inlinks, int Outlinks, int OutlinksChecked, int OutlinksResolved);

/// <summary>Counts what is in a parsed image. Proof of the loader only; diffing and rendering come later.</summary>
public static class ViewerInspector
{
    /// <summary>Most outlinks resolved per request (each can open another image).</summary>
    public const int MaxOutlinksChecked = 200;

    public static InspectResult Inspect(ViewerImageLoader loader, ResolvedImage resolved, ViewerLinkContext links, CancellationToken cancellationToken)
    {
        using var lease = loader.Load(resolved, cancellationToken);
        var loaded = lease.Loaded;

        lock (loaded.Sync)
        {
            int canvases = 0, inlinks = 0, outlinks = 0, checkedOutlinks = 0, resolvedOutlinks = 0, visited = 0;
            var stack = new Stack<WzImageProperty>();
            foreach (var p in loaded.Image.WzProperties) stack.Push(p);
            while (stack.Count > 0)
            {
                if ((++visited & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var p = stack.Pop();
                if (p is WzCanvasProperty canvas)
                {
                    canvases++;
                    if (canvas[WzCanvasProperty.InlinkPropertyName] != null)
                        inlinks++;
                    else if (canvas[WzCanvasProperty.OutlinkPropertyName] != null)
                    {
                        outlinks++;
                        if (checkedOutlinks < MaxOutlinksChecked)
                        {
                            checkedOutlinks++;
                            if (!ReferenceEquals(canvas.GetLinkedWzImageProperty(), canvas))
                                resolvedOutlinks++;
                        }
                    }
                }
                var children = p.WzProperties;
                if (children != null)
                    foreach (var c in children) stack.Push(c);
            }

            return new InspectResult(
                resolved.Source, resolved.RelativePath, loaded.FileBytes, loaded.KeyUsed, loaded.ParseMillis,
                loaded.Image.WzProperties.Count, canvases, lease.FromCache,
                new LinkSummary(inlinks, outlinks, checkedOutlinks, resolvedOutlinks));
        }
    }
}
