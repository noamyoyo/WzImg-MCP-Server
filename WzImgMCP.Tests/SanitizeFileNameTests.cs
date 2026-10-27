using System.Text;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

public class SanitizeFileNameTests
{
    [Theory]
    [InlineData("name.", "name")]
    [InlineData("name. . ", "name")]
    [InlineData("name   ", "name")]
    [InlineData("a b .", "a b")]
    [InlineData("x.png", "x.png")]
    [InlineData("a.b", "a.b")]
    public void TrailingDotsAndSpacesAreRemoved(string input, string expected)
        => Assert.Equal(expected, PathGuard.SanitizeFileName(input));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("Nul", "_Nul")]
    [InlineData("PRN", "_PRN")]
    [InlineData("AUX", "_AUX")]
    [InlineData("COM1", "_COM1")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("CON.", "_CON")]
    [InlineData("com1 ", "_com1")]
    [InlineData("a/CON", "a_CON")]          // the separator becomes '_': no longer a bare device name
    public void ReservedDeviceNamesGetAPrefix(string input, string expected)
        => Assert.Equal(expected, PathGuard.SanitizeFileName(input));

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("COM")]
    [InlineData("xCON")]
    [InlineData("LPT")]
    [InlineData("walk_0")]
    public void OtherNamesAreNotChanged(string input)
        => Assert.Equal(input, PathGuard.SanitizeFileName(input));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(". . .")]
    [InlineData("   ")]
    public void NamesWithNothingLeftUseTheFallback(string input)
        => Assert.Equal("fb", PathGuard.SanitizeFileName(input, "fb"));

    [Fact]
    public void UniqueName_IgnoresCase_WithANameSetFromNewNameSet()
    {
        var used = PathGuard.NewNameSet();
        Assert.Equal("Frame", PathGuard.UniqueName("Frame", used));
        Assert.Equal("frame_2", PathGuard.UniqueName("frame", used));
        Assert.Equal("FRAME_3", PathGuard.UniqueName("FRAME", used));
        Assert.Equal("frame_2_2", PathGuard.UniqueName("frame_2", used));   // a real name that looks like a suffix
    }
}
