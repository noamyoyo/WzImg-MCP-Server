using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

public class PlatformSupportTests
{
    [Fact]
    public void RequireBitmaps_MatchesPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            PlatformSupport.RequireBitmaps("export_png");
            return;
        }

        var ex = Assert.Throws<FeatureUnavailableException>(() => PlatformSupport.RequireBitmaps("export_png"));
        Assert.Contains("export_png is not available on the Linux build yet", ex.Message);
    }

    [Fact]
    public void UnavailableMessage_NamesTheTool()
    {
        Assert.StartsWith("get_canvas_bitmap is not available on the Linux build yet", PlatformSupport.BitmapUnavailableMessage("get_canvas_bitmap"));
    }
}
