namespace WzImgMCP.Utils;

/// <summary>
/// Thrown by tools that depend on a platform feature this build does not have.
/// </summary>
public sealed class FeatureUnavailableException : Exception
{
    public FeatureUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Capability checks for the Linux build. MapleLib decodes and encodes canvas bitmaps with
/// System.Drawing (GDI+), which is not available on Linux, so the tools that need a decoded bitmap
/// report a clear error there instead of failing with a type-initializer crash. Phase 1 of the Linux
/// build keeps everything that works on compressed canvas data (info, origin, extract, pack, edit,
/// sounds) and defers bitmap I/O.
/// </summary>
public static class PlatformSupport
{
    /// <summary>True when canvas bitmaps can be decoded/encoded (GDI+ is present).</summary>
    public static bool BitmapsSupported => OperatingSystem.IsWindows();

    public static string BitmapUnavailableMessage(string tool) =>
        $"{tool} is not available on the Linux build yet: canvas bitmap decoding/encoding needs GDI+ (System.Drawing), " +
        "which this build does not have. Metadata, string/number edits, extract_to_img, pack_to_wz and sound tools still work.";

    /// <summary>Throws <see cref="FeatureUnavailableException"/> on platforms without bitmap support.</summary>
    public static void RequireBitmaps(string tool)
    {
        if (!BitmapsSupported)
            throw new FeatureUnavailableException(BitmapUnavailableMessage(tool));
    }
}
