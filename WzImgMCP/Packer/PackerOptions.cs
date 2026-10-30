using System.Globalization;

namespace WzImgMCP.Packer;

/// <summary>Thrown when the packer cannot start because its configuration is missing or invalid.</summary>
public sealed class PackerConfigException : Exception
{
    public PackerConfigException(string message) : base(message) { }
}

/// <summary>
/// Settings of the packer mode (<c>--packer</c>): the shared secret, the three read-only source roots, the read-only folder
/// of the published <c>.wz</c> files, the one writable outbox and the caps. Every cap that comes from the environment is strict: a value that is not a whole number inside its
/// range stops the start (exit code 2) instead of silently falling back.
/// </summary>
public sealed class PackerOptions
{
    public const string TokenEnvVar = "WZ_PACKER_TOKEN";
    public const int MinTokenLength = 32;
    public const int MinDistinctTokenCharacters = 8;
    public const string TokenHeader = "X-Packer-Token";

    public const string RootEnvPrefix = "WZ_PACKER_ROOT_";
    public const string OutboxEnvVar = "WZ_PACKER_OUTBOX";
    public const string TempEnvVar = "WZ_PACKER_TMP";
    public const string PublishedEnvVar = "WZ_PACKER_PUBLISHED_DIR";

    /// <summary>Source names, in the order they are reported. canon is configured and reported by health; owner and friend are what a request can name.</summary>
    public static readonly string[] SourceNames = { "canon", "owner", "friend" };

    /// <summary>The people whose copy can be packed.</summary>
    public static readonly string[] People = { "owner", "friend" };

    public const string DefaultHost = "0.0.0.0";
    public const int DefaultPort = 13500;

    public const string MaxRequestsEnvVar = "WZ_PACKER_MAX_REQUESTS";
    public const int DefaultMaxConcurrentRequests = 8;
    public const string MaxInputMbEnvVar = "WZ_PACKER_MAX_INPUT_MB";
    public const int DefaultMaxInputMb = 512;
    public const string MaxFilesEnvVar = "WZ_PACKER_MAX_FILES";
    public const int DefaultMaxFiles = 2000;
    public const string MaxWzMbEnvVar = "WZ_PACKER_MAX_WZ_MB";
    public const int DefaultMaxWzMb = 1024;
    public const string MaxImageMbEnvVar = "WZ_PACKER_MAX_IMG_MB";
    public const int DefaultMaxImageMb = 64;
    public const string TimeBudgetEnvVar = "WZ_PACKER_TIME_BUDGET_SECONDS";
    public const int DefaultTimeBudgetSeconds = 900;
    public const string MaxDiffsEnvVar = "WZ_PACKER_MAX_DIFFS";
    public const int DefaultMaxDiffs = 200;
    public const string MaxJobFoldersEnvVar = "WZ_PACKER_MAX_JOB_FOLDERS";
    public const int DefaultMaxJobFolders = 500;
    public const string MaxJobsKeptEnvVar = "WZ_PACKER_MAX_JOBS_KEPT";
    public const int DefaultMaxJobsKept = 64;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The secret every request must carry in the <c>X-Packer-Token</c> header (already trimmed).</summary>
    public string Token { get; }

    /// <summary>Set when the token is valid but weak. Never contains the token.</summary>
    public string? Warning { get; }

    /// <summary>Source name to root directory (full path). Each root holds an <c>img</c> folder with the extracted tree. Read only.</summary>
    public IReadOnlyDictionary<string, string> Roots { get; }

    /// <summary>The read-only folder that holds the published <c>&lt;Category&gt;.wz</c> files (<c>WZ_PACKER_PUBLISHED_DIR</c>); null when not configured, and then no pack can start.</summary>
    public string? PublishedDir { get; }

    /// <summary>The only place the packer writes: one folder per job, <c>&lt;jobId&gt;/&lt;Category&gt;.wz</c>.</summary>
    public string Outbox { get; }

    /// <summary>Where verify re-extracts a packed .wz (a tmpfs in the container). Each verify makes its own folder here and removes it.</summary>
    public string TempRoot { get; init; } = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    /// <summary>Requests that may run at the same time; the next one gets 503 <c>busy</c>. Health is outside it.</summary>
    public int MaxConcurrentRequests { get; init; } = DefaultMaxConcurrentRequests;

    /// <summary>Most files one pack request may list; more is a 400 <c>too_many_files</c>.</summary>
    public int MaxFiles { get; init; } = DefaultMaxFiles;

    /// <summary>Most bytes of listed .img files one pack may read; more fails with <c>input_too_large</c>. The listed images are held in memory while the .wz is saved.</summary>
    public long MaxInputBytes { get; init; } = DefaultMaxInputMb * 1024L * 1024L;

    /// <summary>Largest .wz a pack may open or produce; a bigger one fails with <c>output_too_large</c> (the output is deleted).</summary>
    public long MaxWzBytes { get; init; } = DefaultMaxWzMb * 1024L * 1024L;

    /// <summary>Largest single .img the verify step reads (the viewer loader's cap).</summary>
    public long MaxImageBytes { get; init; } = DefaultMaxImageMb * 1024L * 1024L;

    /// <summary>Wall-clock time one pack, and one verify, may take.</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(DefaultTimeBudgetSeconds);

    /// <summary>Most differences verify lists; more sets <c>truncated</c>.</summary>
    public int MaxDiffs { get; init; } = DefaultMaxDiffs;

    /// <summary>Most job folders the outbox may hold; with that many a new pack gets 503 <c>outbox_full</c> until the publisher cleans up.</summary>
    public int MaxJobFolders { get; init; } = DefaultMaxJobFolders;

    /// <summary>Finished jobs kept in memory (older ones are still read from their job.json).</summary>
    public int MaxJobsKept { get; init; } = DefaultMaxJobsKept;

    /// <summary>Longest time an ordinary request (not verify) may run before it is cut off with 504 <c>timeout</c>.</summary>
    public TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    public PackerOptions(string? token, IReadOnlyDictionary<string, string> roots, string? outbox, string? publishedDir = null)
    {
        token = token?.Trim();   // a trailing newline from a secret file is not part of the token
        if (string.IsNullOrEmpty(token))
            throw new PackerConfigException($"{TokenEnvVar} is not set. The packer refuses to start without a token of at least {MinTokenLength} characters.");
        if (token.Length < MinTokenLength)
            throw new PackerConfigException($"{TokenEnvVar} is too short ({token.Length} characters). The packer needs at least {MinTokenLength} characters.");
        if (token.Any(c => c <= ' ' || c > '~'))
            throw new PackerConfigException($"{TokenEnvVar} contains a character that is not allowed (non-ASCII, whitespace or control character). Use printable ASCII without spaces.");
        if (token.Distinct().Count() < MinDistinctTokenCharacters)
            Warning = $"{TokenEnvVar} has fewer than {MinDistinctTokenCharacters} distinct characters and is easy to guess; use a random value.";

        foreach (var name in SourceNames)
            if (!roots.ContainsKey(name))
                throw new PackerConfigException($"no root configured for source '{name}'.");

        if (string.IsNullOrWhiteSpace(outbox))
            throw new PackerConfigException($"{OutboxEnvVar} is not set. The packer needs one writable folder for its output.");
        outbox = outbox.Trim();
        if (!Path.IsPathRooted(outbox))
            throw new PackerConfigException($"{OutboxEnvVar} must be an absolute path.");
        outbox = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outbox));

        // The outbox is the only writable place: it must not be, or contain, or lie inside a read-only root.
        foreach (var (name, root) in roots)
        {
            if (Overlaps(outbox, root))
                throw new PackerConfigException($"{OutboxEnvVar} must not be the same folder as, or inside or around, the root of '{name}'.");
        }

        if (!string.IsNullOrWhiteSpace(publishedDir))
        {
            publishedDir = publishedDir.Trim();
            if (!Path.IsPathRooted(publishedDir))
                throw new PackerConfigException($"{PublishedEnvVar} must be an absolute path.");
            publishedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(publishedDir));
            if (Overlaps(outbox, publishedDir))
                throw new PackerConfigException($"{OutboxEnvVar} must not be the same folder as, or inside or around, {PublishedEnvVar}.");
            foreach (var (name, root) in roots)
                if (Overlaps(publishedDir, root))
                    throw new PackerConfigException($"{PublishedEnvVar} must not be the same folder as, or inside or around, the root of '{name}'.");
            PublishedDir = publishedDir;
        }

        Token = token;
        Roots = roots;
        Outbox = outbox;
    }

    private static bool Overlaps(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(a) + Path.DirectorySeparatorChar;
        b = Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the settings from the environment: <c>WZ_PACKER_TOKEN</c>, <c>WZ_PACKER_ROOT_CANON</c>, <c>_OWNER</c>, <c>_FRIEND</c> (a blank
    /// root keeps <c>/data/&lt;source&gt;</c>), <c>WZ_PACKER_OUTBOX</c> and the caps listed above. <paramref name="getEnv"/> is a
    /// parameter so tests need not touch the process environment.
    /// </summary>
    public static PackerOptions FromEnvironment(Func<string, string?> getEnv)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in SourceNames)
        {
            var configured = getEnv(RootEnvPrefix + name.ToUpperInvariant());
            roots[name] = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? "/data/" + name : configured.Trim());
        }

        var temp = getEnv(TempEnvVar);
        if (!string.IsNullOrWhiteSpace(temp) && !Path.IsPathRooted(temp.Trim()))
            throw new PackerConfigException($"{TempEnvVar} must be an absolute path.");

        var published = getEnv(PublishedEnvVar);
        if (string.IsNullOrWhiteSpace(published))
            throw new PackerConfigException($"{PublishedEnvVar} is not set. The packer opens the published .wz files from this read-only folder.");

        return new PackerOptions(getEnv(TokenEnvVar), roots, getEnv(OutboxEnvVar), published)
        {
            TempRoot = string.IsNullOrWhiteSpace(temp) ? Path.TrimEndingDirectorySeparator(Path.GetTempPath()) : Path.TrimEndingDirectorySeparator(Path.GetFullPath(temp.Trim())),
            MaxConcurrentRequests = Strict(getEnv(MaxRequestsEnvVar), MaxRequestsEnvVar, DefaultMaxConcurrentRequests, 1, 32),
            MaxInputBytes = Strict(getEnv(MaxInputMbEnvVar), MaxInputMbEnvVar, DefaultMaxInputMb, 1, 65536) * 1024L * 1024L,
            MaxWzBytes = Strict(getEnv(MaxWzMbEnvVar), MaxWzMbEnvVar, DefaultMaxWzMb, 1, 16384) * 1024L * 1024L,
            MaxFiles = Strict(getEnv(MaxFilesEnvVar), MaxFilesEnvVar, DefaultMaxFiles, 1, 20000),
            MaxImageBytes = Strict(getEnv(MaxImageMbEnvVar), MaxImageMbEnvVar, DefaultMaxImageMb, 1, 1024) * 1024L * 1024L,
            TimeBudget = TimeSpan.FromSeconds(Strict(getEnv(TimeBudgetEnvVar), TimeBudgetEnvVar, DefaultTimeBudgetSeconds, 10, 14400)),
            MaxDiffs = Strict(getEnv(MaxDiffsEnvVar), MaxDiffsEnvVar, DefaultMaxDiffs, 1, 100000),
            MaxJobFolders = Strict(getEnv(MaxJobFoldersEnvVar), MaxJobFoldersEnvVar, DefaultMaxJobFolders, 1, 1_000_000),
            MaxJobsKept = Strict(getEnv(MaxJobsKeptEnvVar), MaxJobsKeptEnvVar, DefaultMaxJobsKept, 1, 10000),
        };
    }

    /// <summary>The published file of a category: <c>&lt;PublishedDir&gt;/&lt;Category&gt;.wz</c>. The category is a plain name (checked by the caller).</summary>
    public string PublishedFile(string category)
        => Path.Combine(PublishedDir ?? throw new InvalidOperationException("no published folder is configured"), category + ".wz");

    /// <summary>Unset or blank gives the default; anything else must be a whole number in [min, max] or the start fails.</summary>
    private static int Strict(string? raw, string name, int fallback, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new PackerConfigException($"{name} must be a whole number from {min} to {max}.");
        return value;
    }

    /// <summary>The published folder must exist and be a real folder (not a symbolic link). Called once at start; throws <see cref="PackerConfigException"/> (no path in the message).</summary>
    public void CheckPublished()
    {
        if (PublishedDir == null)
            throw new PackerConfigException($"{PublishedEnvVar} is not set.");
        var info = new DirectoryInfo(PublishedDir);
        if (info.LinkTarget != null)
            throw new PackerConfigException($"{PublishedEnvVar} is a symbolic link; point it at the real folder.");
        if (!info.Exists)
            throw new PackerConfigException($"{PublishedEnvVar} does not exist or is not a folder.");
    }

    /// <summary>
    /// Checks the outbox on disk: it must exist, be a real folder (not a symbolic link) and be one this process can create a
    /// folder in. Called once at start; throws <see cref="PackerConfigException"/> (no path in the message).
    /// </summary>
    public void CheckOutbox()
    {
        var info = new DirectoryInfo(Outbox);
        if (info.LinkTarget != null)
            throw new PackerConfigException($"{OutboxEnvVar} is a symbolic link; point it at the real folder.");
        if (!info.Exists)
            throw new PackerConfigException($"{OutboxEnvVar} does not exist or is not a folder.");
        var probe = Path.Combine(Outbox, ".probe-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackerConfigException($"{OutboxEnvVar} is not writable by the packer.");
        }
    }
}
