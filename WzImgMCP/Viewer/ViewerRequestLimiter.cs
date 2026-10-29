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

    /// <summary>Takes a slot without waiting. False when all slots are in use.</summary>
    public bool TryEnter() => _slots.Wait(0);

    public void Exit() => _slots.Release();
}
