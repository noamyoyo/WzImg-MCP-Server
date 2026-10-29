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

    /// <summary>The soft limit trims at most this often, so a heap that stays high because of unsaved edits is not collected again and again.</summary>
    public TimeSpan MinSoftTrimInterval { get; init; } = TimeSpan.FromSeconds(60);
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
        _lastActivity = _time.GetTimestamp();
    }

    public MemoryGuardSettings Settings => _settings;

    /// <summary>The last trim, or null when there was none.</summary>
    public MemoryTrim? LastTrim => Volatile.Read(ref _lastTrim);

    /// <summary>How many trims ran.</summary>
    public long TrimCount => Interlocked.Read(ref _trimCount);

    /// <summary>
    /// Whether the managed heap is above the soft limit even after a plain collection (the heap size counts garbage that was not
    /// collected yet). The bulk tools (preload, export) ask this between two images and stop when it is true, because going on
    /// would only make the next trim bigger. Always false when the soft limit is off.
    /// </summary>
    public bool IsOverSoftLimit()
    {
        if (_settings.SoftLimitBytes <= 0 || _heapBytes() <= _settings.SoftLimitBytes)
            return false;

        GC.Collect();
        return _heapBytes() > _settings.SoftLimitBytes;
    }

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
            long now = _time.GetTimestamp();

            if (_settings.SoftLimitBytes > 0 && heap > _settings.SoftLimitBytes)
            {
                if (Volatile.Read(ref _softTrimmed) == 0
                    || _time.GetElapsedTime(Volatile.Read(ref _lastSoftTrim), now) >= _settings.MinSoftTrimInterval)
                {
                    Volatile.Write(ref _lastSoftTrim, now);
                    Volatile.Write(ref _softTrimmed, 1);
                    return Trim("memory_soft_limit", heap, now);
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

        var trim = new MemoryTrim(reason, released, pinned, heapBefore, heapAfter, _time.GetUtcNow());
        Volatile.Write(ref _lastTrim, trim);
        Interlocked.Increment(ref _trimCount);
        _log?.Invoke($"[MemoryGuard] trim ({reason}): released {released} cached image(s), kept {pinned} with unsaved edits; " +
                     $"managed heap {heapBefore / 1048576} MB -> {heapAfter / 1048576} MB");
        return trim;
    }

    /// <summary>
    /// A blocking, compacting collection of all generations, also of the large object heap, that asks the runtime to give the
    /// freed memory back to the system; then once more after the finalizers ran (they close the file handles of dropped images).
    /// </summary>
    private static void CollectAndShrink()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
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
