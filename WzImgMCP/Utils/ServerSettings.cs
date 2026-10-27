using MapleLib.Helpers;

namespace WzImgMCP.Utils;

/// <summary>Process wide settings that come from environment variables.</summary>
public static class ServerSettings
{
    public const string PreBigBangEnvVar = "WZIMG_PRE_BIG_BANG";

    /// <summary>
    /// Whether the server targets a pre Big Bang client (default: yes, a v83 client is one). Such clients cannot
    /// read DXT images, so MapleLib's automatic format choice must never pick them. WZIMG_PRE_BIG_BANG=0 (or false)
    /// turns it off, 1 (or true) turns it on; anything else keeps the default.
    /// </summary>
    public static bool PreBigBang => ParseFlag(Environment.GetEnvironmentVariable(PreBigBangEnvVar), true);

    public const string MaxResponseBytesEnvVar = "WZIMG_MAX_RESPONSE_BYTES";
    public const long DefaultMaxResponseBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Upper limit for the image data in one tool response: the base64 text of one canvas (get_canvas_bitmap,
    /// resolve_canvas_link), the total base64 text of get_animation_frames, and (as base64 text) the PNG of view_canvas.
    /// Default 8 MiB; WZIMG_MAX_RESPONSE_BYTES overrides it (a positive whole number of bytes).
    /// </summary>
    public static long MaxResponseBytes
        => long.TryParse(Environment.GetEnvironmentVariable(MaxResponseBytesEnvVar), out var v) && v > 0 ? v : DefaultMaxResponseBytes;

    /// <summary>Copies the settings into the library. Called once at startup.</summary>
    public static void Apply()
    {
        ImageFormatDetector.UsePreBigBangImageFormats = PreBigBang;
    }

    internal static bool ParseFlag(string? value, bool fallback)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on": return true;
            case "0": case "false": case "no": case "off": return false;
            default: return fallback;
        }
    }
}
