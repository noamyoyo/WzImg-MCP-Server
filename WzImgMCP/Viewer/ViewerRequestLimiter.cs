namespace WzImgMCP.Viewer;

/// <summary>
/// The cap on requests running at the same time. A type of its own (not a bare SemaphoreSlim) so that another
/// semaphore registered in the service collection can never replace it.
/// </summary>
public sealed class ViewerRequestLimiter
{
    private readonly SemaphoreSlim _slots;

    public ViewerRequestLimiter(int maxConcurrent)
    {
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>Takes a slot without waiting. Null when all slots are in use.</summary>
    public ViewerRequestSlot? TryEnter() => _slots.Wait(0) ? new ViewerRequestSlot(_slots) : null;
}

/// <summary>
/// One taken request slot. It is released when the request ends, unless work that cannot be cancelled is still
/// running: <see cref="HoldUntil"/> then keeps the slot until that work has really finished. A request that timed
/// out must not free its slot while its parse is still using the CPU and memory.
/// </summary>
public sealed class ViewerRequestSlot
{
    private readonly SemaphoreSlim _owner;
    private int _held;       // 1 when HoldUntil was called
    private int _released;

    internal ViewerRequestSlot(SemaphoreSlim owner) { _owner = owner; }

    /// <summary>Keeps the slot until <paramref name="work"/> has completed (successfully, failed or cancelled).</summary>
    public void HoldUntil(Task work)
    {
        Volatile.Write(ref _held, 1);
        work.ContinueWith(_ => Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Called by the request pipeline when the request ends: releases the slot unless it is held by running work.</summary>
    public void ReleaseUnlessHeld()
    {
        if (Volatile.Read(ref _held) == 0)
            Release();
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _owner.Release();
    }
}

/// <summary>
/// The cap on parses (and later pixel decodes) running at the same time. Its own type for the same reason as
/// <see cref="ViewerRequestLimiter"/>.
/// </summary>
public sealed class ViewerDecodeLimiter
{
    private readonly SemaphoreSlim _slots;

    public ViewerDecodeLimiter(int maxConcurrent)
    {
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>Waits for a slot (until <paramref name="cancellationToken"/> fires); dispose the result to give it back.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        return new Releaser(_slots);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _slots;
        public Releaser(SemaphoreSlim slots) { _slots = slots; }
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
