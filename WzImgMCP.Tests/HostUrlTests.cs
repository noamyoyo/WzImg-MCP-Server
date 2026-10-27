using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

public class HostUrlTests
{
    [Theory]
    [InlineData("::", "[::]")]
    [InlineData("::1", "[::1]")]
    [InlineData("fe80::1", "[fe80::1]")]
    [InlineData("[::]", "[::]")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    [InlineData(" 0.0.0.0 ", "0.0.0.0")]
    public void ForUrl_BracketsIpv6Only(string host, string expected)
    {
        Assert.Equal(expected, HostUrl.ForUrl(host));
        Assert.True(Uri.TryCreate($"http://{HostUrl.ForUrl(host)}:13339", UriKind.Absolute, out _));
    }
}
