using System.Runtime;
using MapleLib.Img;

namespace WzImgMCP.Server;

/// <summary>
/// When the guard trims. <see cref="SoftLimitBytes"/>: managed heap above this trims (0: never for size).
/// <see cref="IdleTrim"/>: no tool call for this long trims once (zero: never when idle).
/// </summary>
public sealed record MemoryGuardSettings(long SoftLimitBytes, TimeSpan IdleTrim)
{
    /// <summary>How often the timer looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The soft limit trims at most this often. When a trim frees little (<see cref="MemoryGuard.LowYieldFraction"/> of the heap) the
    /// spacing doubles up to <see cref="MaxSoftTrimInterval"/>, and it is back to this value after a trim that frees more or once the
    /// heap is below the limit again, so a heap that stays high (unsaved edits, decoded canvases) is not collected again and again.
    /// </summary>
    public TimeSpan MinSoftTrimInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The longest spacing between two soft limit trims that free little.</summary>
    public TimeSpan MaxSoftTrimInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True: the trim runs an aggressive collection (twice, the second one after the finalizers), which also shrinks the heap
    /// further but pauses longer. False (the default): one blocking, compacting collection of all generations.
    /// WZIMG_GC_AGGRESSIVE=1 turns it on.
    /// </summary>
    public bool AggressiveCollection { get; init; }
}

/// <summary>What one trim did (counts only).</summary>
public sealed record MemoryTrim(string Reason, int ReleasedImages, int PinnedImages, long HeapBefore, long HeapAfter, DateTimeOffset At);

/// <summary>
/// Keeps the managed heap of the long running server from growing for ever. A timer calls <see cref="Tick"/> every 30 seconds:
/// when the managed heap is above the soft limit, or no tool call happened for the idle time, every cached image without unsaved
/// edits is released and the garbage collector runs a blocking, compacting collection that also gives memory back to the
/// system. Images with unsaved edits are never touched. Nothing here takes the session lock, and two ticks never run at once.
/// The clock, the heap size and the collection are replaceable so the logic can be tested without waiting or allocating.
/// </summary>
public sealed class MemoryGuard : IDisposable
{
    private readonly Func<IDataSource?> _source;
    private readonly MemoryGuardSettings _settings;
    private readonly TimeProvider _time;
    private readonly Func<long> _heapBytes;
    private readonly Action _collect;
    private readonly object _timerGate = new();
    private Timer? _timer;
    private Action<string>? _log;

    private long _lastActivity;                 // _time timestamp of the last tool call
    private int _idleTrimmedSinceActivity;      // 1: the idle trim already ran for the current quiet period
    private int _running;                       // 1 while a tick is working
    private long _lastSoftTrim;                 // _time timestamp of the last soft limit trim
    private int _softTrimmed;                   // 1 once a soft limit trim happened
    private TimeSpan _softInterval;             // the spacing required after the last soft limit trim (backs off while trims free little)
    private long _heapReading;                  // the heap size the last tick saw (what IsOverSoftLimit answers from)
    private long _trimCount;
    private MemoryTrim? _lastTrim;

    public MemoryGuard(Func<IDataSource?> source, MemoryGuardSettings settings, TimeProvider? time = null,
        Func<long>? heapBytes = null, Action? collect = null)
    {
        _source = source;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _heapBytes = heapBytes ?? (() => GC.GetTotalMemory(false));
        _collect = collect ?? CollectAndShrink;
        _softInterval = settings.MinSoftTrimInterval;
        _lastActivity = _time.GetTimestamp();
    }

    /// <summary>A trim that frees less than this share of the heap counts as useless and makes the next soft limit trim wait longer.</summary>
    public const double LowYieldFraction = 0.10;

    /// <summary>The spacing the next soft limit trim has to keep from the last one.</summary>
    public TimeSpan CurrentSoftInterval => _softInterval;

    public MemoryGuardSettings Settings => _settings;

    /// <summary>The last trim, or null when there was none.</summary>
    public MemoryTrim? LastTrim => Volatile.Read(ref _lastTrim);

    /// <summary>How many trims ran.</summary>
    public long TrimCount => Interlocked.Read(ref _trimCount);

    /// <summary>
    /// Whether the heap the last tick measured is above the soft limit. The bulk tools (preload, export) ask this between two images
    /// and stop when it is true, because going on would only make the next trim bigger. It reads the timer's last measurement (at
    /// most one interval old) and never measures or collects itself: a collection per image would cost far more than it saves.
    /// Always false when the soft limit is off or before the first tick.
    /// </summary>
    public bool IsOverSoftLimit() => _settings.SoftLimitBytes > 0 && Interlocked.Read(ref _heapReading) > _settings.SoftLimitBytes;

    /// <summary>How long ago the last tool call happened (what the idle trim compares with its limit).</summary>
    public TimeSpan QuietFor => _time.GetElapsedTime(Volatile.Read(ref _lastActivity), _time.GetTimestamp());

    /// <summary>A tool call happened: the quiet period starts again.</summary>
    public void Touch()
    {
        Volatile.Write(ref _lastActivity, _time.GetTimestamp());
        Volatile.Write(ref _idleTrimmedSinceActivity, 0);
    }

    /// <summary>Starts the timer. <paramref name="log"/> gets one line per trim.</summary>
    public void Start(Action<string>? log = null)
    {
        lock (_timerGate)
        {
            if (_timer != null)
                return;
            _log = log;
            _timer = new Timer(_ => SafeTick(), null, _settings.Interval, _settings.Interval);
        }
    }

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[MemoryGuard] tick failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// One look at the heap and the clock; trims when it should. Returns the trim, or null when nothing was done (also when
    /// another tick is still running).
    /// </summary>
    public MemoryTrim? Tick()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return null;
        try
        {
            long heap = _heapBytes();
            Interlocked.Exchange(ref _heapReading, heap);
            long now = _time.GetTimestamp();

            if (_settings.SoftLimitBytes > 0)
            {
                if (heap <= _settings.SoftLimitBytes)
                {
                    _softInterval = _settings.MinSoftTrimInterval;   // the heap is fine again: the next high reading trims after the shortest spacing
                }
                else if (Volatile.Read(ref _softTrimmed) == 0
                    || _time.GetElapsedTime(Volatile.Read(ref _lastSoftTrim), now) >= _softInterval)
                {
                    Volatile.Write(ref _lastSoftTrim, now);
                    Volatile.Write(ref _softTrimmed, 1);
                    var trim = Trim("memory_soft_limit", heap, now);

                    // A trim that frees little (the heap is held by edits or by data the cache does not own) is not repeated at once.
                    bool useless = trim.HeapBefore - trim.HeapAfter < (long)(trim.HeapBefore * LowYieldFraction);
                    _softInterval = useless
                        ? TimeSpan.FromTicks(Math.Min(_softInterval.Ticks * 2, _settings.MaxSoftTrimInterval.Ticks))
                        : _settings.MinSoftTrimInterval;
                    return trim;
                }
            }

            if (_settings.IdleTrim > TimeSpan.Zero
                && Volatile.Read(ref _idleTrimmedSinceActivity) == 0
                && _time.GetElapsedTime(Volatile.Read(ref _lastActivity), now) >= _settings.IdleTrim)
            {
                Volatile.Write(ref _idleTrimmedSinceActivity, 1);
                return Trim("idle", heap, now);
            }

            return null;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private MemoryTrim Trim(string reason, long heapBefore, long now)
    {
        int released = 0, pinned = 0;
        try
        {
            // The data source is read once, without any lock: init_data_source may replace (and dispose) it meanwhile.
            if (_source() is ImgFileSystemDataSource images)
            {
                released = images.ReleaseUnpinnedImages();
                pinned = images.Manager.PinnedImageCount;
            }
        }
        catch (ObjectDisposedException)
        {
            // the data source was replaced while we were looking at it
        }

        _collect();
        long heapAfter = _heapBytes();
        Interlocked.Exchange(ref _heapReading, heapAfter);

        var trim = new MemoryTrim(reason, released, pinned, heapBefore, heapAfter, _time.GetUtcNow());
        Volatile.Write(ref _lastTrim, trim);
        Interlocked.Increment(ref _trimCount);
        _log?.Invoke($"[MemoryGuard] trim ({reason}): released {released} cached image(s), kept {pinned} with unsaved edits; " +
                     $"managed heap {heapBefore / 1048576} MB -> {heapAfter / 1048576} MB" +
                     (reason == "memory_soft_limit" ? $"; next soft limit trim not before {(int)NextSoftInterval(heapBefore, heapAfter).TotalSeconds} s" : ""));
        return trim;
    }

    private TimeSpan NextSoftInterval(long before, long after)
        => before - after < (long)(before * LowYieldFraction)
            ? TimeSpan.FromTicks(Math.Min(_softInterval.Ticks * 2, _settings.MaxSoftTrimInterval.Ticks))
            : _settings.MinSoftTrimInterval;

    /// <summary>The collection mode of a trim: aggressive only when asked for.</summary>
    public static GCCollectionMode ModeFor(bool aggressive) => aggressive ? GCCollectionMode.Aggressive : GCCollectionMode.Forced;

    /// <summary>
    /// One blocking, compacting collection of all generations, also of the large object heap (compacted once); with
    /// <see cref="MemoryGuardSettings.AggressiveCollection"/> an aggressive one, repeated after the finalizers ran.
    /// </summary>
    private void CollectAndShrink()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        var mode = ModeFor(_settings.AggressiveCollection);
        GC.Collect(GC.MaxGeneration, mode, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();   // the finalizers close the file handles of the images that were dropped
        if (_settings.AggressiveCollection)
            GC.Collect(GC.MaxGeneration, mode, blocking: true, compacting: true);
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
