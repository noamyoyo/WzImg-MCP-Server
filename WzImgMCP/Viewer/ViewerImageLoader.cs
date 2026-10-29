using System.Collections.Concurrent;
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
/// Single flight: requests for the same uncached image share one read and parse; the others wait for it (and stop
/// waiting when their own token fires). A creator that is cancelled does not fail the waiters: they try again.
///
/// Memory: an <see cref="OutOfMemoryException"/> while reading or parsing is answered with 413 <c>too_large</c>, and the
/// cache is emptied to give memory back. The process is not meant to die of one crafted file.
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
    private readonly Action _clearLibraryErrors;
    private readonly Dictionary<string, (FileStamp Stamp, WzMapleVersion? Key)> _manifestKeys = new();
    private readonly ConcurrentDictionary<ImageKey, Flight> _flights = new();
    private long _parses;
    private long _joins;

    /// <param name="clearLibraryErrors">
    /// Called after every parse, successful or not. MapleLib keeps every problem it logs in a list that only grows;
    /// the default empties it. A parameter so a test can count the calls.
    /// </param>
    public ViewerImageLoader(ViewerOptions options, IViewerFileSystem fileSystem, ViewerImageCache cache, Action? clearLibraryErrors = null)
    {
        _options = options;
        _fileSystem = fileSystem;
        _cache = cache;
        _clearLibraryErrors = clearLibraryErrors ?? ErrorLogger.ClearErrors;
    }

    public ViewerImageCache Cache => _cache;

    /// <summary>Parses started (one per attempt that reached the parser, cache hits and joined waits excluded).</summary>
    public long Parses => Interlocked.Read(ref _parses);

    /// <summary>Requests that waited for another request's parse instead of parsing themselves.</summary>
    public long Joins => Interlocked.Read(ref _joins);

    /// <summary>Test seam: called before each key attempt (may throw, for instance an OutOfMemoryException).</summary>
    public Action<WzMapleVersion>? ParseProbe { get; set; }

    /// <summary>The keys to try, in order: the preferred one (if any), then BMS, GMS, EMS, each once.</summary>
    public static IReadOnlyList<WzMapleVersion> KeyOrder(WzMapleVersion? preferred)
        => (preferred.HasValue ? new[] { preferred.Value }.Concat(DefaultKeyOrder) : DefaultKeyOrder).Distinct().ToList();

    /// <summary>One parse in progress, and the waiters that joined it.</summary>
    private sealed class Flight
    {
        public readonly object Gate = new();
        public readonly ManualResetEventSlim Done = new(false);
        public LoadedImage? Result;
        public Exception? Error;
        public bool Completed;
        public bool ResultReleased;
        public int Joined;     // waiters that will take (or have given up taking) a reference
        public int Settled;    // waiters that have taken one or given up

        /// <summary>The flight keeps one reference to the result until every joined waiter has taken its own. Call under <see cref="Gate"/>.</summary>
        public void ReleaseWhenSettled()
        {
            if (Completed && Result != null && !ResultReleased && Settled == Joined)
            {
                ResultReleased = true;
                Result.Release();
            }
        }
    }

    /// <summary>Returns the parsed image (from the cache or freshly parsed). Dispose the lease when done with the image.</summary>
    public ImageLease Load(ResolvedImage resolved, CancellationToken cancellationToken)
    {
        // Fail closed: without the identity the resolver recorded, the "same file" and "same parent" checks cannot be made.
        if (resolved.Identity == null)
            throw new ViewerApiException(400, "bad_path", "the file could not be verified");

        var stamp = _fileSystem.Stat(resolved.FullPath) ?? throw new ViewerApiException(404, "not_found", "image not found");
        if (stamp.Size > _options.MaxFileBytes)
            throw TooLarge();

        var preferred = PreferredKey(resolved.Source);
        var manifest = preferred?.ToString() ?? "";
        var lookup = new ImageKey(resolved.Source, resolved.RelativePath, stamp.MtimeTicks, stamp.Size, manifest, stamp.CtimeTicks, stamp.Inode);

        while (true)
        {
            var hit = _cache.TryGet(lookup);
            if (hit != null)
                return hit;

            var mine = new Flight();
            var flight = _flights.GetOrAdd(lookup, mine);
            if (ReferenceEquals(flight, mine))
                return RunFlight(mine, lookup, resolved, preferred, manifest, cancellationToken);

            var joined = WaitFor(flight, cancellationToken);
            if (joined != null)
                return joined;
            // the other request failed in a way that is its own (it was cancelled): look again, maybe parse here
        }
    }

    private ImageLease RunFlight(Flight flight, ImageKey lookup, ResolvedImage resolved, WzMapleVersion? preferred, string manifest, CancellationToken cancellationToken)
    {
        try
        {
            var lease = LoadUncached(resolved, preferred, manifest, cancellationToken);
            lease.Loaded.TryAcquire();   // the flight's own reference, for the waiters
            lock (flight.Gate)
            {
                flight.Result = lease.Loaded;
                flight.Completed = true;
                _flights.TryRemove(new KeyValuePair<ImageKey, Flight>(lookup, flight));
                flight.ReleaseWhenSettled();
            }
            flight.Done.Set();
            return lease;
        }
        catch (Exception ex)
        {
            lock (flight.Gate)
            {
                flight.Error = ex;
                flight.Completed = true;
                _flights.TryRemove(new KeyValuePair<ImageKey, Flight>(lookup, flight));
            }
            flight.Done.Set();
            throw;
        }
    }

    /// <summary>Waits for another request's parse. Null when that request ended in an error that should not be inherited.</summary>
    private ImageLease? WaitFor(Flight flight, CancellationToken cancellationToken)
    {
        lock (flight.Gate)
        {
            if (flight.Completed)
                return TakeFrom(flight, counted: false);
            flight.Joined++;
        }
        Interlocked.Increment(ref _joins);

        try
        {
            flight.Done.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            lock (flight.Gate)
            {
                flight.Settled++;
                flight.ReleaseWhenSettled();
            }
            throw;
        }

        lock (flight.Gate)
            return TakeFrom(flight, counted: true);
    }

    /// <summary>Call under the flight's gate, once it is completed.</summary>
    private static ImageLease? TakeFrom(Flight flight, bool counted)
    {
        ImageLease? lease = null;
        if (flight.Result != null && flight.Result.TryAcquire())
            lease = new ImageLease(flight.Result, fromCache: true);
        if (counted)
        {
            flight.Settled++;
            flight.ReleaseWhenSettled();
        }
        if (lease != null)
            return lease;
        // A failed parse is the same failure for everyone, except a cancelled one, which belonged to its request.
        if (flight.Error is ViewerApiException api)
            throw new ViewerApiException(api.Status, api.Code, api.Message);
        return null;
    }

    private ImageLease LoadUncached(ResolvedImage resolved, WzMapleVersion? preferred, string manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = ReadStable(resolved);

        // The stamp of the bytes actually read is the key, not the earlier stat.
        var key = new ImageKey(resolved.Source, resolved.RelativePath, read.After.MtimeTicks, read.After.Size, manifest, read.After.CtimeTicks, read.After.Inode);
        var hit = _cache.TryGet(key);
        if (hit != null)
            return hit;

        cancellationToken.ThrowIfCancellationRequested();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(read.Bytes)).ToLowerInvariant();
        var (image, keyUsed, millis) = Parse(resolved, read.Bytes, preferred, cancellationToken);
        var cost = ViewerImageSizeEstimator.Estimate(image, read.Bytes.Length);
        return _cache.Add(new LoadedImage(key, image, keyUsed.ToString(), millis, read.Bytes.Length, cost, sha));
    }

    /// <summary>Reads the file; if it changed while being read, once more; still changing gives 409 <c>file_busy</c>.</summary>
    private FileRead ReadStable(ResolvedImage resolved)
    {
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var read = _fileSystem.Read(resolved.FullPath, _options.MaxFileBytes, resolved.Identity);
                if (read.Before == read.After && read.Bytes.Length == read.After.Size)
                    return read;
            }
        }
        catch (OutOfMemoryException)
        {
            throw OutOfMemory();
        }
        throw new ViewerApiException(409, "file_busy", "the file is being changed; try again shortly");
    }

    private (WzImage Image, WzMapleVersion Key, long Millis) Parse(ResolvedImage resolved, byte[] bytes, WzMapleVersion? preferred, CancellationToken cancellationToken)
    {
        var name = System.IO.Path.GetFileName(resolved.RelativePath);
        var clock = Stopwatch.StartNew();
        Interlocked.Increment(ref _parses);
        try
        {
            foreach (var version in KeyOrder(preferred))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WzImage? image = null;
                try
                {
                    ParseProbe?.Invoke(version);
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
                catch (OutOfMemoryException)   // also InsufficientMemoryException
                {
                    image?.Dispose();
                    throw OutOfMemory();
                }
                catch (Exception)
                {
                    // a key that does not fit, or a damaged file: try the next key
                }
                image?.Dispose();
            }
            throw new ViewerApiException(422, "parse_failed", "the image could not be parsed");
        }
        finally
        {
            _clearLibraryErrors();   // after every parse: MapleLib's list of logged problems only grows
        }
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

            var read = _fileSystem.Read(path, MaxManifestBytes, null);   // a configured path, not a client one: no identity to compare
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

    private ViewerApiException OutOfMemory()
    {
        _cache.Clear();   // give memory back before the next request
        return new ViewerApiException(413, "too_large", "the image needs more memory than the viewer allows");
    }
}
