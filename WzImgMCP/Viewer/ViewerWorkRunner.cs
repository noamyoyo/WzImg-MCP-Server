namespace WzImgMCP.Viewer;

/// <summary>
/// Runs parse and decode work for a request. The library cannot cancel a parse, so the work runs on its own thread
/// and the request only waits for it. When the request times out the answer is sent at once, but:
/// <list type="bullet">
/// <item>the request slot stays taken until the work itself has finished (<see cref="ViewerRequestSlot.HoldUntil"/>);</item>
/// <item>the decode slot is held by the work and given back by the work, never by the waiting request;</item>
/// <item>the token is passed to the work, which checks it between steps and stops early when it can.</item>
/// </list>
/// Link resolution (<see cref="ViewerLinkContext"/>) is set up for the work and torn down when it ends.
/// </summary>
public sealed class ViewerWorkRunner
{
    private readonly ViewerDecodeLimiter _decodeLimiter;
    private readonly ViewerPathResolver _resolver;
    private readonly ViewerImageLoader _loader;
    private readonly long _linkBudgetBytes;

    public ViewerWorkRunner(ViewerDecodeLimiter decodeLimiter, ViewerPathResolver resolver, ViewerImageLoader loader,
        long linkBudgetBytes = ViewerOptions.DefaultLinkBudgetMb * 1024L * 1024L)
    {
        _linkBudgetBytes = linkBudgetBytes;
        _decodeLimiter = decodeLimiter;
        _resolver = resolver;
        _loader = loader;
    }

    public async Task<T> RunAsync<T>(string source, ViewerRequestSlot? slot, CancellationToken cancellationToken, Func<ViewerLinkContext, T> work)
    {
        var decodeSlot = await _decodeLimiter.EnterAsync(cancellationToken);   // cancelled here: nothing has started
        Task<T> task;
        try
        {
            task = Task.Run(() =>
            {
                try
                {
                    using var context = new ViewerLinkContext(source, _resolver, _loader, cancellationToken, _linkBudgetBytes);
                    using var scope = context.Enter();
                    return work(context);
                }
                finally
                {
                    decodeSlot.Dispose();   // only the work gives the decode slot back
                }
            }, CancellationToken.None);   // never a token here: a task cancelled before it starts would never release the slot
        }
        catch
        {
            decodeSlot.Dispose();
            throw;
        }

        slot?.HoldUntil(task);
        return await task.WaitAsync(cancellationToken);
    }
}
