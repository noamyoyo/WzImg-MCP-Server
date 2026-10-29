using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Viewer;

/// <summary>
/// The per-request state behind MapleLib's static <c>WzCanvasProperty.ExternalImageResolver</c> hook, which the library
/// calls to find the image an <c>_outlink</c> points at. One process-wide delegate is installed at startup
/// (<see cref="Install"/>); it looks at an <see cref="AsyncLocal{T}"/> to learn which request, and so which source, is
/// asking. A request therefore resolves link targets only inside its own source, through the same safe loader and the
/// same path rules as a requested image (<see cref="ViewerPathResolver"/>): a link such as <c>../../etc/x.img</c> or
/// <c>Backups/x.img</c> is simply unresolved. Without a context (no request) the hook answers null, as if unset.
///
/// The images found stay leased until the context is disposed at the end of the work, because the caller keeps using the
/// returned property after the hook has returned.
/// </summary>
public sealed class ViewerLinkContext : IDisposable
{
    /// <summary>Most different link targets one request may open; more are left unresolved.</summary>
    public const int MaxLinkedImages = 8;

    private static readonly AsyncLocal<ViewerLinkContext?> CurrentContext = new();
    private readonly ViewerPathResolver _resolver;
    private readonly ViewerImageLoader _loader;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<string, ImageLease?> _leases = new(StringComparer.Ordinal);
    private readonly long _maxBytes;
    private long _bytes;

    public string Source { get; }

    /// <summary>Links left unresolved because the count or the byte budget of this context was used up.</summary>
    public int SkippedByBudget { get; private set; }

    public ViewerLinkContext(string source, ViewerPathResolver resolver, ViewerImageLoader loader, CancellationToken cancellationToken,
        long maxBytes = ViewerOptions.DefaultLinkBudgetMb * 1024L * 1024L)
    {
        _maxBytes = maxBytes;
        Source = source;
        _resolver = resolver;
        _loader = loader;
        _cancellationToken = cancellationToken;
    }

    public static ViewerLinkContext? Current => CurrentContext.Value;

    /// <summary>Makes this the context of the current flow of execution until the returned scope is disposed.</summary>
    public IDisposable Enter()
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = this;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ViewerLinkContext? _previous;
        public Scope(ViewerLinkContext? previous) { _previous = previous; }
        public void Dispose() => CurrentContext.Value = _previous;
    }

    /// <summary>Installs the process-wide hook. Safe to call more than once.</summary>
    public static void Install()
    {
        WzCanvasProperty.ExternalImageResolver = static imagePath => Current?.LoadLinked(imagePath)!;   // null means "not found" to the library
    }

    /// <summary>The parsed image at the category-rooted path <paramref name="imagePath"/> (such as <c>Mob/8800141.img</c>) in this source, or null.</summary>
    public WzImage? LoadLinked(string imagePath)
    {
        if (_leases.TryGetValue(imagePath, out var known))
            return known?.Image;
        if (_leases.Count >= MaxLinkedImages)
        {
            SkippedByBudget++;
            return null;
        }

        ImageLease? lease = null;
        try
        {
            var resolved = _resolver.Resolve(Source, imagePath);
            if (_bytes + resolved.SizeBytes > _maxBytes)
            {
                SkippedByBudget++;   // not remembered: a smaller target later may still fit
                return null;
            }
            lease = _loader.Load(resolved, _cancellationToken);
            _bytes += lease.Loaded.FileBytes;
        }
        catch (ViewerApiException)
        {
            // outside the rules, missing, too large, broken: the link stays unresolved
        }
        _leases[imagePath] = lease;
        return lease?.Image;
    }

    public void Dispose()
    {
        foreach (var lease in _leases.Values)
            lease?.Dispose();
        _leases.Clear();
    }
}
