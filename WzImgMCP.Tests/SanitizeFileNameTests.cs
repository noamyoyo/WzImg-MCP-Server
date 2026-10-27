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

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    [Theory]
    [InlineData("a", 400, 150)]              // ASCII: 150 characters
    [InlineData("é", 400, 75)]          // 2 bytes each
    [InlineData("中", 400, 50)]          // 3 bytes each
    [InlineData("\U0001F600", 400, 37)]      // 4 bytes each (a surrogate pair): 37 * 4 = 148, never half of one
    public void LongNamesAreCutByUtf8ByteCount_WithoutSplittingACharacter(string unit, int repeat, int expectedUnits)
    {
        var s = PathGuard.SanitizeFileName(string.Concat(Enumerable.Repeat(unit, repeat)));
        Assert.Equal(string.Concat(Enumerable.Repeat(unit, expectedUnits)), s);
        Assert.True(StrictUtf8.GetByteCount(s) <= PathGuard.MaxNameBytes);
        Assert.Equal(s, StrictUtf8.GetString(StrictUtf8.GetBytes(s)));   // valid UTF-8 and round trips
    }

    [Fact]
    public void LongestNameWithSuffixAndExtension_FitsIntoA255ByteFileName_AndCanBeWritten()
    {
        var name = PathGuard.SanitizeFileName(string.Concat(Enumerable.Repeat("\U0001F600", 100)));
        var used = PathGuard.NewNameSet();
        PathGuard.UniqueName(name, used);
        var unique = PathGuard.UniqueName(name, used);                   // name_2
        var fileName = unique + ".png";
        Assert.True(Encoding.UTF8.GetByteCount(fileName) <= 255);

        var dir = Directory.CreateTempSubdirectory("long-name-").FullName;
        try
        {
            var path = PathGuard.WriteFileGuarded(dir, fileName, new byte[] { 1 });
            Assert.True(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void AnUnpairedSurrogateDoesNotProduceInvalidUtf8()
    {
        var s = PathGuard.SanitizeFileName("ab\uD800cd");
        Assert.Equal(s, StrictUtf8.GetString(StrictUtf8.GetBytes(s)));
    }

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
