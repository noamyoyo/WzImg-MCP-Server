using System.Diagnostics;
using System.Text.Json;
using MapleLib.Helpers;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;

namespace WzImgMCP.Viewer;

/// <summary>
/// Loads one <c>.img</c> from a source into a parsed <see cref="WzImage"/> without a data source: the bytes are read
/// by <see cref="IViewerFileSystem"/> (safe open, size cap, stable-read check), parsed from memory with
/// <c>WzImgDeserializer.WzImageFromIMGBytes(..., freeResources:false)</c> and kept in a <see cref="ViewerImageCache"/>.
///
/// Encryption key: the one named in the source's <c>img/manifest.json</c> (<c>encryption</c>) is tried first, then BMS,
/// GMS and EMS. A key that does not fit makes the header check of <c>ParseImage</c> fail, which is an ordinary result,
/// not an exception. When no key fits the answer is <c>parse_failed</c> (422) with a generic message.
///
/// <see cref="Load"/> is synchronous and not cancellable inside the library parse; it checks the token between
/// steps. Callers run it through <see cref="ViewerWorkRunner"/>, which holds the request slot until it has returned.
/// </summary>
public sealed class ViewerImageLoader
{
    private static readonly WzMapleVersion[] DefaultKeyOrder = { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS };
    private const long MaxManifestBytes = 1024 * 1024;

    private readonly ViewerOptions _options;
    private readonly IViewerFileSystem _fileSystem;
    private readonly ViewerImageCache _cache;
    private readonly Dictionary<string, (FileStamp Stamp, WzMapleVersion? Key)> _manifestKeys = new();

    public ViewerImageLoader(ViewerOptions options, IViewerFileSystem fileSystem, ViewerImageCache cache)
    {
        _options = options;
        _fileSystem = fileSystem;
        _cache = cache;
    }

    public ViewerImageCache Cache => _cache;

    /// <summary>The keys to try, in order: the preferred one (if any), then BMS, GMS, EMS, each once.</summary>
    public static IReadOnlyList<WzMapleVersion> KeyOrder(WzMapleVersion? preferred)
        => (preferred.HasValue ? new[] { preferred.Value }.Concat(DefaultKeyOrder) : DefaultKeyOrder).Distinct().ToList();

    /// <summary>Returns the parsed image (from the cache or freshly parsed). Dispose the lease when done with the image.</summary>
    public ImageLease Load(ResolvedImage resolved, CancellationToken cancellationToken)
    {
        var stamp = _fileSystem.Stat(resolved.FullPath) ?? throw new ViewerApiException(404, "not_found", "image not found");
        if (stamp.Size > _options.MaxFileBytes)
            throw TooLarge();

        var lookup = new ImageKey(resolved.Source, resolved.RelativePath, stamp.MtimeTicks, stamp.Size);
        var hit = _cache.TryGet(lookup);
        if (hit != null)
            return hit;

        cancellationToken.ThrowIfCancellationRequested();
        var read = ReadStable(resolved);

        // The stamp of the bytes actually read is the key, not the earlier stat.
        var key = new ImageKey(resolved.Source, resolved.RelativePath, read.After.MtimeTicks, read.After.Size);
        hit = _cache.TryGet(key);
        if (hit != null)
            return hit;

        cancellationToken.ThrowIfCancellationRequested();
        var (image, keyUsed, millis) = Parse(resolved, read.Bytes, cancellationToken);
        var cost = ViewerImageSizeEstimator.Estimate(image, read.Bytes.Length);
        return _cache.Add(new LoadedImage(key, image, keyUsed.ToString(), millis, read.Bytes.Length, cost));
    }

    /// <summary>Reads the file; if it changed while being read, once more; still changing gives 409 <c>file_busy</c>.</summary>
    private FileRead ReadStable(ResolvedImage resolved)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var read = _fileSystem.Read(resolved.FullPath, _options.MaxFileBytes, resolved.Identity);
            if (read.Before == read.After && read.Bytes.Length == read.After.Size)
                return read;
        }
        throw new ViewerApiException(409, "file_busy", "the file is being changed; try again shortly");
    }

    private (WzImage Image, WzMapleVersion Key, long Millis) Parse(ResolvedImage resolved, byte[] bytes, CancellationToken cancellationToken)
    {
        var name = System.IO.Path.GetFileName(resolved.RelativePath);
        var clock = Stopwatch.StartNew();
        foreach (var version in KeyOrder(PreferredKey(resolved.Source)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WzImage? image = null;
            try
            {
                var deserializer = new WzImgDeserializer(freeResources: false, calculateChecksum: false);
                image = deserializer.WzImageFromIMGBytes(bytes, version, name, freeResources: false);
                if (image.ParseImage())
                    return (image, version, clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                image?.Dispose();
                throw;
            }
            catch (Exception)
            {
                // a key that does not fit, or a damaged file: try the next key
            }
            image?.Dispose();
        }

        // MapleLib keeps every logged problem in a list that only grows; this process has no use for it.
        ErrorLogger.ClearErrors();
        throw new ViewerApiException(422, "parse_failed", "the image could not be parsed");
    }

    /// <summary>The key named in the source's manifest, when that is one of GMS, EMS, BMS.</summary>
    private WzMapleVersion? PreferredKey(string source)
    {
        var path = System.IO.Path.Combine(_options.Roots[source], "img", "manifest.json");
        try
        {
            var stamp = _fileSystem.Stat(path);
            if (stamp == null)
                return null;
            lock (_manifestKeys)
                if (_manifestKeys.TryGetValue(source, out var known) && known.Stamp == stamp.Value)
                    return known.Key;

            var read = _fileSystem.Read(path, MaxManifestBytes, null);
            WzMapleVersion? key = null;
            using (var doc = JsonDocument.Parse(read.Bytes))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("encryption", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && Enum.TryParse<WzMapleVersion>(value.GetString(), ignoreCase: true, out var parsed)
                    && parsed is WzMapleVersion.GMS or WzMapleVersion.EMS or WzMapleVersion.BMS)
                {
                    key = parsed;
                }
            }
            lock (_manifestKeys)
                _manifestKeys[source] = (read.After, key);
            return key;
        }
        catch (Exception ex) when (ex is ViewerApiException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;   // no usable manifest: the default order applies
        }
    }

    private static ViewerApiException TooLarge() => new(413, "too_large", "the image is larger than the viewer accepts");
}
