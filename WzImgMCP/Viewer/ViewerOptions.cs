namespace WzImgMCP.Viewer;

/// <summary>Thrown when the viewer cannot start because its configuration is missing or invalid.</summary>
public sealed class ViewerConfigException : Exception
{
    public ViewerConfigException(string message) : base(message) { }
}

/// <summary>
/// Settings of the read-only viewer mode (<c>--viewer</c>): the shared secret and the three named source roots.
/// </summary>
public sealed class ViewerOptions
{
    public const string TokenEnvVar = "WZ_VIEWER_TOKEN";
    public const int MinTokenLength = 32;
    public const int MinDistinctTokenCharacters = 8;
    public const string TokenHeader = "X-Viewer-Token";

    /// <summary>Source names, in the order they are reported. Every request names one of these.</summary>
    public static readonly string[] SourceNames = { "canon", "owner", "friend" };

    public const string DefaultHost = "0.0.0.0";
    public const int DefaultPort = 13400;
    public const string MaxRequestsEnvVar = "WZ_VIEWER_MAX_REQUESTS";
    public const int DefaultMaxConcurrentRequests = 8;
    public const int MinConcurrentRequests = 1;
    public const int MaxConcurrentRequestsLimit = 32;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The secret every request must carry in the <c>X-Viewer-Token</c> header (already trimmed).</summary>
    public string Token { get; }

    /// <summary>Set when the token is valid but weak (fewer than <see cref="MinDistinctTokenCharacters"/> distinct characters). Never contains the token.</summary>
    public string? Warning { get; }

    /// <summary>Source name to root directory (full path). Each root holds an <c>img</c> folder with the extracted tree.</summary>
    public IReadOnlyDictionary<string, string> Roots { get; }

    /// <summary>Requests that may run at the same time (<c>WZ_VIEWER_MAX_REQUESTS</c>, 1 to 32, default 8); the next one gets 503 <c>busy</c>. Health is outside it; the decode cap (<see cref="MaxDecodes"/>) is separate.</summary>
    public int MaxConcurrentRequests { get; init; } = DefaultMaxConcurrentRequests;

    public const string MaxFileMbEnvVar = "WZ_VIEWER_MAX_FILE_MB";
    public const string MaxDecodesEnvVar = "WZ_VIEWER_MAX_DECODES";
    public const string CacheMbEnvVar = "WZ_VIEWER_CACHE_MB";
    public const int DefaultMaxFileMb = 64;
    public const int DefaultMaxDecodes = 2;
    public const int DefaultCacheMb = 256;
    public const string LinkBudgetMbEnvVar = "WZ_VIEWER_LINK_BUDGET_MB";
    public const int DefaultLinkBudgetMb = 64;
    public const string MaxChangesEnvVar = "WZ_VIEWER_MAX_CHANGES";
    public const string MaxCanvasDecodesEnvVar = "WZ_VIEWER_MAX_CANVAS_DECODES";
    public const string DiffBudgetSecondsEnvVar = "WZ_VIEWER_DIFF_BUDGET_SECONDS";
    public const string MaxDecodedMbEnvVar = "WZ_VIEWER_MAX_DECODED_MB";
    public const string MaxDiffResponseKbEnvVar = "WZ_VIEWER_MAX_DIFF_RESPONSE_KB";
    public const string MaxPictureMbEnvVar = "WZ_VIEWER_MAX_PICTURE_MB";
    public const int DefaultMaxPictureMb = 64;
    public const int DefaultMaxDecodedMb = 256;
    public const int DefaultMaxDiffResponseKb = 4096;
    public const int DefaultMaxChanges = 2000;
    public const int DefaultMaxCanvasDecodes = 200;
    public const int DefaultDiffBudgetSeconds = 25;

    /// <summary>Most changes a diff lists; more set <c>truncated</c>.</summary>
    public int MaxChanges { get; init; } = DefaultMaxChanges;

    /// <summary>Most canvas pairs one diff decodes to pixels; further differing pairs are reported as <c>pixels_unverified</c>.</summary>
    public int MaxCanvasDecodes { get; init; } = DefaultMaxCanvasDecodes;

    /// <summary>
    /// Largest canvas (width x height x 4) that is decoded, by the diff and by <c>/v1/canvas</c> alike; 64 MiB is 4096 x 4096. Peak memory of
    /// the decodes is about <see cref="MaxDecodes"/> x 3 x this value (library buffers, and the scaling), so raise it only with more memory.
    /// </summary>
    public long MaxPictureBytes { get; init; } = DefaultMaxPictureMb * 1024L * 1024L;

    /// <summary>Most bytes of decoded pixels (width x height x 4, both sides of every pair) one diff may decode; further pairs are <c>pixels_unverified</c>.</summary>
    public long MaxDecodedBytes { get; init; } = DefaultMaxDecodedMb * 1024L * 1024L;

    /// <summary>Upper bound for the JSON a diff lists (paths, values, names); more sets <c>truncated</c> with a warning.</summary>
    public long MaxDiffResponseBytes { get; init; } = DefaultMaxDiffResponseKb * 1024L;

    /// <summary>Wall-clock time a diff may spend comparing before it stops and returns what it has, flagged as truncated.</summary>
    public TimeSpan DiffBudget { get; init; } = TimeSpan.FromSeconds(DefaultDiffBudgetSeconds);

    /// <summary>Largest .img file the viewer reads; a bigger one gets 413 <c>too_large</c>.</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileMb * 1024L * 1024L;

    /// <summary>Parses (and later pixel decodes) that may run at the same time.</summary>
    public int MaxDecodes { get; init; } = DefaultMaxDecodes;

    /// <summary>Upper bound for the estimated memory of the parsed-image cache.</summary>
    public long CacheBytes { get; init; } = DefaultCacheMb * 1024L * 1024L;

    /// <summary>Most bytes of linked images (_outlink targets) one request may open; a link past it stays unresolved.</summary>
    public long LinkBudgetBytes { get; init; } = DefaultLinkBudgetMb * 1024L * 1024L;

    /// <summary>Longest time one request may run before it is cut off with 504 <c>timeout</c>.</summary>
    public TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    public ViewerOptions(string? token, IReadOnlyDictionary<string, string> roots)
    {
        token = token?.Trim();   // a trailing newline from a secret file is not part of the token (an HTTP header value is trimmed too)
        if (string.IsNullOrEmpty(token))
            throw new ViewerConfigException($"{TokenEnvVar} is not set. The viewer refuses to start without a token of at least {MinTokenLength} characters.");
        if (token.Length < MinTokenLength)
            throw new ViewerConfigException($"{TokenEnvVar} is too short ({token.Length} characters). The viewer needs at least {MinTokenLength} characters.");
        // Printable ASCII without space only: anything else cannot be sent reliably in an HTTP header.
        if (token.Any(c => c <= ' ' || c > '~'))
            throw new ViewerConfigException($"{TokenEnvVar} contains a character that is not allowed (non-ASCII, whitespace or control character). Use printable ASCII without spaces.");
        if (token.Distinct().Count() < MinDistinctTokenCharacters)
            Warning = $"{TokenEnvVar} has fewer than {MinDistinctTokenCharacters} distinct characters and is easy to guess; use a random value.";

        foreach (var name in SourceNames)
            if (!roots.ContainsKey(name))
                throw new ViewerConfigException($"no root configured for source '{name}'.");

        Token = token;
        Roots = roots;
    }

    /// <summary>
    /// Reads the token and the roots from the environment (<c>WZ_VIEWER_TOKEN</c>, <c>WZ_VIEWER_ROOT_CANON</c>,
    /// <c>WZ_VIEWER_ROOT_OWNER</c>, <c>WZ_VIEWER_ROOT_FRIEND</c>). A blank root variable keeps the default
    /// <c>/data/&lt;source&gt;</c>. <paramref name="getEnv"/> is a parameter so tests need not touch the process environment.
    /// </summary>
    public static ViewerOptions FromEnvironment(Func<string, string?> getEnv)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in SourceNames)
        {
            var configured = getEnv("WZ_VIEWER_ROOT_" + name.ToUpperInvariant());
            roots[name] = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? "/data/" + name : configured.Trim());
        }
        return new ViewerOptions(getEnv(TokenEnvVar), roots)
        {
            MaxConcurrentRequests = ParseStrict(getEnv(MaxRequestsEnvVar), MaxRequestsEnvVar, DefaultMaxConcurrentRequests, MinConcurrentRequests, MaxConcurrentRequestsLimit),
            MaxFileBytes = ParseWhole(getEnv(MaxFileMbEnvVar), DefaultMaxFileMb, 1, 1024) * 1024L * 1024L,
            MaxDecodes = (int)ParseWhole(getEnv(MaxDecodesEnvVar), DefaultMaxDecodes, 1, 16),
            CacheBytes = ParseWhole(getEnv(CacheMbEnvVar), DefaultCacheMb, 0, 16384) * 1024L * 1024L,
            LinkBudgetBytes = ParseWhole(getEnv(LinkBudgetMbEnvVar), DefaultLinkBudgetMb, 0, 1024) * 1024L * 1024L,
            MaxChanges = (int)ParseWhole(getEnv(MaxChangesEnvVar), DefaultMaxChanges, 1, 100000),
            MaxCanvasDecodes = (int)ParseWhole(getEnv(MaxCanvasDecodesEnvVar), DefaultMaxCanvasDecodes, 0, 100000),
            MaxPictureBytes = ParseWhole(getEnv(MaxPictureMbEnvVar), DefaultMaxPictureMb, 1, 256) * 1024L * 1024L,
            MaxDecodedBytes = ParseWhole(getEnv(MaxDecodedMbEnvVar), DefaultMaxDecodedMb, 1, 16384) * 1024L * 1024L,
            MaxDiffResponseBytes = ParseWhole(getEnv(MaxDiffResponseKbEnvVar), DefaultMaxDiffResponseKb, 16, 262144) * 1024L,
            DiffBudget = TimeSpan.FromSeconds(ParseWhole(getEnv(DiffBudgetSecondsEnvVar), DefaultDiffBudgetSeconds, 1, 600)),
        };
    }

    /// <summary>
    /// A whole number inside [min, max]; unset or blank gives the default, but anything else that is not such a number is a
    /// configuration error (the viewer refuses to start, exit code 2) instead of a silent fallback.
    /// </summary>
    private static int ParseStrict(string? raw, string name, int fallback, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ViewerConfigException($"{name} must be a whole number from {min} to {max}.");
        return value;
    }

    /// <summary>A whole number inside [min, max]; blank, not a number or out of range gives the default.</summary>
    private static long ParseWhole(string? raw, long fallback, long min, long max)
        => long.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : fallback;

    public static ViewerOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);
}
