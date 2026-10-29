using MapleLib.Helpers;
using WzImgMCP.Server;

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

    // Memory and big categories (see README-LINUX.md). A value that is not a whole number in the allowed range keeps the default.

    public const string CacheMbEnvVar = "WZIMG_CACHE_MB";
    public const int DefaultCacheMb = 400;
    public const string ScanMaxImagesEnvVar = "WZIMG_SCAN_MAX_IMAGES";
    public const int DefaultScanMaxImages = 150;
    public const string ScanBudgetSecondsEnvVar = "WZIMG_SCAN_BUDGET_SECONDS";
    public const int DefaultScanBudgetSeconds = 20;
    public const string BulkMaxImagesEnvVar = "WZIMG_BULK_MAX_IMAGES";
    public const int DefaultBulkMaxImages = 2000;
    public const string MemorySoftLimitMbEnvVar = "WZIMG_MEMORY_SOFT_LIMIT_MB";
    public const int DefaultMemorySoftLimitMb = 1536;
    public const string GcAggressiveEnvVar = "WZIMG_GC_AGGRESSIVE";
    public const string IdleTrimSecondsEnvVar = "WZIMG_IDLE_TRIM_SECONDS";
    public const int DefaultIdleTrimSeconds = 300;

    /// <summary>Size limit of the image cache in MB (the same setting as HaCreatorConfig Cache.MaxMemoryCacheMB). Default 400.</summary>
    public static int CacheMb => ParseInt(Environment.GetEnvironmentVariable(CacheMbEnvVar), DefaultCacheMb, 16, 65536);

    /// <summary>Limits of scans and of bulk tools, from the environment.</summary>
    public static ScanLimits ScanLimitsFromEnvironment() => new(
        ParseInt(Environment.GetEnvironmentVariable(ScanMaxImagesEnvVar), DefaultScanMaxImages, 1, 1_000_000),
        TimeSpan.FromSeconds(ParseInt(Environment.GetEnvironmentVariable(ScanBudgetSecondsEnvVar), DefaultScanBudgetSeconds, 0, 86_400)),
        ParseInt(Environment.GetEnvironmentVariable(BulkMaxImagesEnvVar), DefaultBulkMaxImages, 1, 1_000_000));

    /// <summary>Settings of the memory guard, from the environment. 0 switches the soft limit or the idle trim off.</summary>
    public static MemoryGuardSettings MemoryGuardFromEnvironment() => new(
        ParseInt(Environment.GetEnvironmentVariable(MemorySoftLimitMbEnvVar), DefaultMemorySoftLimitMb, 0, 1_048_576) * 1024L * 1024L,
        TimeSpan.FromSeconds(ParseInt(Environment.GetEnvironmentVariable(IdleTrimSecondsEnvVar), DefaultIdleTrimSeconds, 0, 604_800)))
    {
        AggressiveCollection = ParseFlag(Environment.GetEnvironmentVariable(GcAggressiveEnvVar), false)
    };

    /// <summary>A whole number from min to max, otherwise the fallback.</summary>
    public static int ParseInt(string? value, int fallback, int min, int max)
        => int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : fallback;

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
