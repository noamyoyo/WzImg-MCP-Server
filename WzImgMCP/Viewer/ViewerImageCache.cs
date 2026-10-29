using MapleLib.WzLib;

namespace WzImgMCP.Viewer;

/// <summary>Cache key: a new mtime or size is a new key, and sources never share entries.</summary>
public readonly record struct ImageKey(string Source, string Path, long MtimeTicks, long Size);

/// <summary>
/// A parsed image and what is known about it. Reference counted: the cache holds one reference and every user holds
/// one through an <see cref="ImageLease"/>, so evicting an entry never disposes an image a request is still reading.
/// The image is disposed when the last reference goes.
/// </summary>
public sealed class LoadedImage
{
    private int _references = 1;   // the creator's reference

    public ImageKey Key { get; }
    public WzImage Image { get; }
    public string KeyUsed { get; }
    public long ParseMillis { get; }
    public long FileBytes { get; }

    /// <summary>Estimated memory held (bytes of the file kept for lazy canvases plus the parsed tree).</summary>
    public long Cost { get; }

    /// <summary>
    /// The parsed tree and the shared reader behind it are not thread safe. Anything that walks the tree or reads a
    /// canvas of a cached image must hold this lock.
    /// </summary>
    public object Sync { get; } = new();

    public LoadedImage(ImageKey key, WzImage image, string keyUsed, long parseMillis, long fileBytes, long cost)
    {
        Key = key;
        Image = image;
        KeyUsed = keyUsed;
        ParseMillis = parseMillis;
        FileBytes = fileBytes;
        Cost = cost;
    }

    internal bool TryAcquire()
    {
        while (true)
        {
            int current = Volatile.Read(ref _references);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current) return true;
        }
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            try { Image.Dispose(); } catch { /* nothing useful to do with a failed dispose */ }
        }
    }
}

/// <summary>One user's reference to a <see cref="LoadedImage"/>. Dispose it (twice is fine).</summary>
public sealed class ImageLease : IDisposable
{
    private LoadedImage? _loaded;
    public LoadedImage Loaded => _loaded ?? throw new ObjectDisposedException(nameof(ImageLease));
    public WzImage Image => Loaded.Image;
    /// <summary>True when the image came from the cache and was not parsed for this call.</summary>
    public bool FromCache { get; }

    internal ImageLease(LoadedImage loaded, bool fromCache) { _loaded = loaded; FromCache = fromCache; }

    public void Dispose() => Interlocked.Exchange(ref _loaded, null)?.Release();
}

/// <summary>
/// LRU cache of parsed images with a byte cap. Entries are evicted least recently used first; an entry bigger than the
/// whole cap is not cached at all. A file that changed (new key for the same source and path) replaces the older entry.
/// </summary>
public sealed class ViewerImageCache
{
    private readonly object _gate = new();
    private readonly Dictionary<ImageKey, LinkedListNode<LoadedImage>> _map = new();
    private readonly LinkedList<LoadedImage> _lru = new();   // first = most recently used
    private readonly long _capBytes;
    private long _bytes;

    public ViewerImageCache(long capBytes) { _capBytes = capBytes; }

    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _map.Count; } }

    public ImageLease? TryGet(ImageKey key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            if (!node.Value.TryAcquire()) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return new ImageLease(node.Value, fromCache: true);
        }
    }

    /// <summary>
    /// Adds a freshly parsed image (the caller's reference becomes the returned lease). When an entry with the same key
    /// is already there (two requests parsed the same file), the new image is dropped and the cached one is returned.
    /// </summary>
    public ImageLease Add(LoadedImage loaded)
    {
        List<LoadedImage> dropped = new();
        ImageLease lease;
        lock (_gate)
        {
            if (_map.TryGetValue(loaded.Key, out var existing) && existing.Value.TryAcquire())
            {
                dropped.Add(loaded);   // release the caller's own reference below
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                lease = new ImageLease(existing.Value, fromCache: true);
            }
            else if (loaded.Cost > _capBytes)
            {
                lease = new ImageLease(loaded, fromCache: false);   // too big to keep: only the caller holds it
            }
            else
            {
                // Older versions of the same file are dead weight.
                foreach (var key in _map.Keys.Where(k => k.Source == loaded.Key.Source && k.Path == loaded.Key.Path).ToList())
                    dropped.Add(Remove(key));

                loaded.TryAcquire();   // the cache's reference, on top of the caller's
                _map[loaded.Key] = _lru.AddFirst(loaded);
                _bytes += loaded.Cost;
                lease = new ImageLease(loaded, fromCache: false);

                while (_bytes > _capBytes && _lru.Last != null && _lru.Last.Value != loaded)
                    dropped.Add(Remove(_lru.Last.Value.Key));
            }
        }
        foreach (var d in dropped) d.Release();   // outside the lock
        return lease;
    }

    private LoadedImage Remove(ImageKey key)
    {
        var node = _map[key];
        _map.Remove(key);
        _lru.Remove(node);
        _bytes -= node.Value.Cost;
        return node.Value;
    }

    /// <summary>Drops everything (the images are disposed once no request holds them any more).</summary>
    public void Clear()
    {
        List<LoadedImage> dropped;
        lock (_gate)
        {
            dropped = _lru.ToList();
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
        foreach (var d in dropped) d.Release();
    }
}
