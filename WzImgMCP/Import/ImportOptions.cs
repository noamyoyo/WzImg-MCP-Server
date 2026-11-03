using System.Net;

namespace WzImgMCP.Import;

/// <summary>
/// Settings of "import by link" (tools import_fetch, import_status, import_preview, import_apply, import_undo).
/// Read from environment variables; a value that is missing or not a whole number inside its range keeps the default.
/// </summary>
public sealed class ImportOptions
{
    public const string AllowedHostsVar = "WZIMG_IMPORT_ALLOWED_HOSTS";
    public const string MaxMbVar = "WZIMG_IMPORT_MAX_MB";
    public const string MaxStagedVar = "WZIMG_IMPORT_MAX_STAGED";
    public const string RetentionDaysVar = "WZIMG_IMPORT_RETENTION_DAYS";
    public const string TimeoutSecondsVar = "WZIMG_IMPORT_TIMEOUT_SECONDS";
    public const string FilesMaxCountVar = "WZIMG_IMPORT_FILES_MAX_COUNT";
    public const string FilesMaxUnpackedMbVar = "WZIMG_IMPORT_FILES_MAX_UNPACKED_MB";
    public const string FilesMaxRatioVar = "WZIMG_IMPORT_FILES_MAX_RATIO";
    public const string PreviewMaxImagesVar = "WZIMG_IMPORT_PREVIEW_MAX_IMAGES";
    public const string PreviewMaxEntriesVar = "WZIMG_IMPORT_PREVIEW_MAX_ENTRIES";

    /// <summary>Google Drive: the share page, the old download host, the current download host and the hosts the files are served from.</summary>
    public const string DefaultAllowedHosts = "drive.google.com,docs.google.com,drive.usercontent.google.com,*.googleusercontent.com";

    /// <summary>Host names a download may use, and the hosts a redirect may lead to. "*.example.com" matches any sub domain, not example.com itself.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = ParseHosts(DefaultAllowedHosts);

    /// <summary>Largest download in bytes (the file itself, not what it unpacks to).</summary>
    public long MaxDownloadBytes { get; init; } = 300L * 1024 * 1024;

    /// <summary>Most imports that may be staged on disk at once (failed imports and fully undone ones do not count).</summary>
    public int MaxStaged { get; init; } = 3;

    /// <summary>A staged import is deleted this long after it was made (or applied, whichever is later).</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Whole-download time limit.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(1800);

    /// <summary>A download that receives nothing for this long is stopped.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Redirects followed per download (each one is checked again).</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Most files in a zip of loose files.</summary>
    public int FilesMaxCount { get; init; } = 200;

    /// <summary>Most bytes all files of one import may take once unpacked.</summary>
    public long FilesMaxUnpackedBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Largest unpacked-size to packed-size ratio of an entry (and of the whole archive); above it the archive is refused as a zip bomb.</summary>
    public int FilesMaxRatio { get; init; } = 50;

    /// <summary>Largest image the preview and apply read to compare it (an .img over this is reported unreadable).</summary>
    public long MaxImageBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Changed images the preview describes in detail (the others are only counted).</summary>
    public int PreviewMaxImages { get; init; } = 100;

    /// <summary>Entries (added / removed / changed properties) listed per changed image.</summary>
    public int PreviewMaxEntries { get; init; } = 25;

    /// <summary>Names listed per plain list of the preview (new images, unreadable images, images only in the tree).</summary>
    public int PreviewMaxNames { get; init; } = 200;

    public static ImportOptions FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;
        var hostsText = get(AllowedHostsVar);
        var hosts = string.IsNullOrWhiteSpace(hostsText) ? ParseHosts(DefaultAllowedHosts) : ParseHosts(hostsText);
        if (hosts.Count == 0)
            hosts = ParseHosts(DefaultAllowedHosts);
        return new ImportOptions
        {
            AllowedHosts = hosts,
            MaxDownloadBytes = Whole(get(MaxMbVar), 1, 4096, 300) * 1024L * 1024L,
            MaxStaged = (int)Whole(get(MaxStagedVar), 1, 20, 3),
            Retention = TimeSpan.FromDays(Whole(get(RetentionDaysVar), 1, 60, 7)),
            DownloadTimeout = TimeSpan.FromSeconds(Whole(get(TimeoutSecondsVar), 30, 86400, 1800)),
            FilesMaxCount = (int)Whole(get(FilesMaxCountVar), 1, 5000, 200),
            FilesMaxUnpackedBytes = Whole(get(FilesMaxUnpackedMbVar), 1, 4096, 100) * 1024L * 1024L,
            FilesMaxRatio = (int)Whole(get(FilesMaxRatioVar), 2, 1000, 50),
            PreviewMaxImages = (int)Whole(get(PreviewMaxImagesVar), 1, 10000, 100),
            PreviewMaxEntries = (int)Whole(get(PreviewMaxEntriesVar), 1, 1000, 25),
        };
    }

    private static long Whole(string? text, long min, long max, long fallback)
        => long.TryParse(text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : fallback;

    public static List<string> ParseHosts(string text)
        => text.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant())
            .Where(h => h.Length > 0 && !h.Contains('/') && !h.Contains(':') && !h.Contains('@'))
            .Distinct()
            .ToList();

    /// <summary>True when <paramref name="host"/> (a DNS name, not an IP address) is on the allow-list.</summary>
    public bool IsHostAllowed(string host)
    {
        if (string.IsNullOrEmpty(host) || IPAddress.TryParse(host.Trim('[', ']'), out _))
            return false;
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var rule in AllowedHosts)
        {
            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = rule.Substring(1);   // ".example.com"
                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.Ordinal))
                    return true;
            }
            else if (host == rule)
            {
                return true;
            }
        }
        return false;
    }
}
