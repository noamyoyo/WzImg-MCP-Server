using System.Globalization;

namespace WzImgMCP.Utils;

/// <summary>
/// Idle timeout of the streamable HTTP sessions (WZIMG_SESSION_IDLE_SECONDS). A session that has had no request in
/// flight for this long is closed by the MCP library, and the next request with its id gets "404 Session not found".
/// </summary>
public static class SessionIdleSettings
{
    public const string EnvVar = "WZIMG_SESSION_IDLE_SECONDS";
    public const int DefaultSeconds = 604800;  // 7 days (the library has no stateless mode in this version, so sessions must outlive a long idle spell)
    public const int MinSeconds = 10;
    public const int MaxSeconds = 604800;      // 7 days

    /// <summary>Upper bound for the number of idle sessions the library keeps before it evicts the oldest ones.</summary>
    public const int MaxIdleSessionCount = 100000;

    /// <summary>
    /// Reads the idle seconds from the raw environment value. Unset or blank gives the default silently; a value that
    /// is not a whole number, or is outside 10 to 604800, gives the default and a warning text.
    /// </summary>
    public static int Parse(string? raw, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultSeconds;

        var text = raw.Trim();
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            warning = $"{EnvVar}='{Truncate(text)}' is not a whole number of seconds; using the default {DefaultSeconds}.";
            return DefaultSeconds;
        }
        if (value < MinSeconds || value > MaxSeconds)
        {
            warning = $"{EnvVar}={value} is outside the accepted range {MinSeconds} to {MaxSeconds}; using the default {DefaultSeconds}.";
            return DefaultSeconds;
        }
        return (int)value;
    }

    /// <summary>
    /// The value to give <c>HttpServerTransportOptions.IdleTimeout</c> so that sessions really live for
    /// <paramref name="seconds"/>. ModelContextProtocol.AspNetCore 0.1.0-preview.12 compares
    /// <c>TimeProvider.GetTimestamp()</c> (units of 1/TimestampFrequency second, 10^9 per second on Linux) with
    /// <c>IdleTimeout.Ticks</c> (10^7 per second) without converting, so a plain <c>TimeSpan.FromSeconds(n)</c>
    /// lasts only n/100 seconds on Linux (the 2 hour default lasts 72 seconds). Scaling the ticks by the timestamp
    /// frequency gives the intended length on any platform. If a later library version converts correctly, remove this
    /// scaling and use <c>TimeSpan.FromSeconds</c>; a timed test with a short setting (for example 20 s) catches the difference.
    /// </summary>
    public static TimeSpan ToLibraryTimeout(int seconds, long timestampFrequency)
        => TimeSpan.FromTicks(checked(seconds * timestampFrequency));

    public static TimeSpan ToLibraryTimeout(int seconds) => ToLibraryTimeout(seconds, TimeProvider.System.TimestampFrequency);

    private static string Truncate(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "...";
}
