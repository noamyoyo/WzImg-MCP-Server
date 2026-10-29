using System.Runtime.CompilerServices;
using MapleLib.WzLib;

namespace WzImgMCP.Server;

/// <summary>
/// One lock object per image instance. Every tool that mutates an image, and every operation that would unparse, discard, save or
/// read a cached image, takes the lock of that image, so none of them can interleave with another on the same image.
/// </summary>
public sealed class ImageGate
{
    private readonly ConditionalWeakTable<WzImage, object> _locks = new();

    public object For(WzImage image) => _locks.GetValue(image, _ => new object());
}

/// <summary>
/// An image that a tool is about to change. It is created by <see cref="WzSessionManager.BeginEdit"/>, which gets the image,
/// parses it, flags it as changed (so the cache never evicts, clears or trims it) and checks that it is still the cached
/// instance, all before the tool touches anything, and which holds the lock of the image until this object is disposed.
/// <see cref="Commit"/> keeps the flag (the edit happened); without it, disposing puts the flag back where it was, so a tool that
/// finds nothing to change does not leave an image that looks edited.
/// </summary>
public sealed class ImageEdit : IDisposable
{
    private readonly object _gate;
    private readonly bool _wasChanged;
    private bool _committed;
    private bool _disposed;

    public WzImage Image { get; }

    internal ImageEdit(WzImage image, object gate, bool wasChanged)
    {
        Image = image;
        _gate = gate;
        _wasChanged = wasChanged;
    }

    /// <summary>The image was changed: it stays flagged until it is saved or discarded.</summary>
    public void Commit() => _committed = true;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_committed)
            Image.Changed = _wasChanged;
        Monitor.Exit(_gate);
    }
}
