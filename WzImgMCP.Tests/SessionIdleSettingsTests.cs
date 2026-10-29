using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

public class SessionIdleSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_UsesDefault_WithoutWarning(string? raw)
    {
        Assert.Equal(604800, SessionIdleSettings.Parse(raw, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("20", 20)]
    [InlineData(" 300 ", 300)]
    [InlineData("10", 10)]          // lower bound is accepted
    [InlineData("604800", 604800)]  // upper bound (and default) is accepted
    [InlineData("86400", 86400)]
    public void Valid_IsUsed_WithoutWarning(string raw, int expected)
    {
        Assert.Equal(expected, SessionIdleSettings.Parse(raw, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("0")]
    [InlineData("-5")]
    public void BelowRange_FallsBackToDefault_WithWarning(string raw)
    {
        Assert.Equal(604800, SessionIdleSettings.Parse(raw, out var warning));
        Assert.NotNull(warning);
        Assert.Contains("WZIMG_SESSION_IDLE_SECONDS", warning);
    }

    [Theory]
    [InlineData("604801")]
    [InlineData("99999999999")]
    [InlineData("99999999999999999999999")]   // does not fit in a long
    public void AboveRange_FallsBackToDefault_WithWarning(string raw)
    {
        Assert.Equal(604800, SessionIdleSettings.Parse(raw, out var warning));
        Assert.NotNull(warning);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("20s")]
    [InlineData("1e3")]
    public void NotANumber_FallsBackToDefault_WithWarning(string raw)
    {
        Assert.Equal(604800, SessionIdleSettings.Parse(raw, out var warning));
        Assert.NotNull(warning);
        Assert.Contains("not a whole number", warning);
    }

    [Fact]
    public void LibraryTimeout_IsScaledByTimestampFrequency()
    {
        // Linux: Stopwatch/TimeProvider timestamps are nanoseconds, the library compares them with IdleTimeout.Ticks.
        Assert.Equal(20_000_000_000L, SessionIdleSettings.ToLibraryTimeout(20, 1_000_000_000L).Ticks);
        // Windows (10 MHz): identical to TimeSpan.FromSeconds.
        Assert.Equal(TimeSpan.FromSeconds(20), SessionIdleSettings.ToLibraryTimeout(20, TimeSpan.TicksPerSecond));
        // Largest accepted value must not overflow.
        Assert.Equal(604800L * 1_000_000_000L, SessionIdleSettings.ToLibraryTimeout(604800, 1_000_000_000L).Ticks);
    }
}
