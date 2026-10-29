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
    public const int DefaultMaxConcurrentRequests = 4;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The secret every request must carry in the <c>X-Viewer-Token</c> header (already trimmed).</summary>
    public string Token { get; }

    /// <summary>Set when the token is valid but weak (fewer than <see cref="MinDistinctTokenCharacters"/> distinct characters). Never contains the token.</summary>
    public string? Warning { get; }

    /// <summary>Source name to root directory (full path). Each root holds an <c>img</c> folder with the extracted tree.</summary>
    public IReadOnlyDictionary<string, string> Roots { get; }

    /// <summary>Requests that may run at the same time; the next one gets 503 <c>busy</c>.</summary>
    public int MaxConcurrentRequests { get; init; } = DefaultMaxConcurrentRequests;

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
        return new ViewerOptions(getEnv(TokenEnvVar), roots);
    }

    public static ViewerOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);
}
