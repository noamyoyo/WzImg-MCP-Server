#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;
using WzImgMCP.Viewer.Diff;

namespace WzImgMCP.Tests;

/// <summary>
/// A UOL's <c>WzProperties</c> are its TARGET's children. A viewer that walks them follows the link: a UOL to its own parent never ends,
/// and a loop of UOLs throws. Every walk in the viewer treats a UOL as a leaf (its link text), and these tests keep it so.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerUolSafetyTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly ViewerTree _tree = new();
    private ViewerTestServer? _server;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _tree.Dispose();
    }

    /// <summary>A sub property holding a UOL to itself (through its parent), and a pair of UOLs that point at each other.</summary>
    private static void Hostile(WzImage img)
    {
        var s = new WzSubProperty("s");
        s.AddProperty(new WzIntProperty("v", 1));
        s.AddProperty(new WzUOLProperty("up", "../s"));
        img.AddProperty(s);
        img.AddProperty(new WzUOLProperty("a", "b"));
        img.AddProperty(new WzUOLProperty("b", "a"));
    }

    private static void Guarded(Action action)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "the walk did not end");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void SizeEstimate_DoesNotFollowUols()
    {
        var img = new WzImage("x.img") { Changed = true };
        Hostile(img);
        long nodes = 0;
        Guarded(() => ViewerImageSizeEstimator.TreeBytes(img, out nodes, out _));
        Assert.Equal(5, nodes);   // s, v, up, a, b
    }

    [Fact]
    public void Differ_TreatsAUolAsItsLinkText_AndNeverWalksItsTarget()
    {
        ImageDiffResult result = null!;
        Guarded(() =>
        {
            var a = new LoadedImage(new ImageKey("canon", "x.img", 1, 1), Make(Hostile), "BMS", 0, 1, 1, "a");
            var b = new LoadedImage(new ImageKey("owner", "x.img", 1, 1), Make(img =>
            {
                Hostile(img);
                img.WzProperties.OfType<WzSubProperty>().Single().AddProperty(new WzIntProperty("w", 2));
            }), "BMS", 0, 1, 1, "b");
            result = ImageDiffer.Diff(a, b, new DiffOptions(), CancellationToken.None);
        });
        var change = Assert.Single(result.Changes);
        Assert.Equal(("s/w", "added"), (change.Path, change.Kind));   // reported once, where it is, not again under the UOL "s/up"
    }

    [Fact]
    public void Differ_ReportsAChangedUolByItsText()
    {
        ImageDiffResult result = null!;
        Guarded(() =>
        {
            var a = new LoadedImage(new ImageKey("canon", "x.img", 1, 1), Make(i => { i.AddProperty(new WzSubProperty("t")); i.AddProperty(new WzUOLProperty("u", "t")); }), "BMS", 0, 1, 1, "a");
            var b = new LoadedImage(new ImageKey("owner", "x.img", 1, 1), Make(i => { i.AddProperty(new WzSubProperty("t")); i.AddProperty(new WzUOLProperty("u", "../t")); }), "BMS", 0, 1, 1, "b");
            result = ImageDiffer.Diff(a, b, new DiffOptions(), CancellationToken.None);
        });
        var change = Assert.Single(result.Changes);
        Assert.Equal(("u", "uol", "t", "../t"), (change.Path, change.Type, change.Before, change.After));
    }

    private static WzImage Make(Action<WzImage> fill)
    {
        var img = new WzImage("x.img") { Changed = true };
        fill(img);
        return img;
    }

    [Theory]
    [InlineData("/v1/inspect?source=owner&path=Mob/Hostile.img")]
    [InlineData("/v1/diff?person=owner&path=Mob/Hostile.img")]
    [InlineData("/v1/canvas?source=owner&path=Mob/Hostile.img&node=a")]
    [InlineData("/v1/frames?source=owner&path=Mob/Hostile.img&node=s")]
    public async Task HostileUols_NeverGiveA500OrAHang(string url)
    {
        ImgFixture.Write(_tree.Root("owner"), "Mob/Hostile.img", ImgFixture.Build(Hostile));
        ImgFixture.Write(_tree.Root("canon"), "Mob/Hostile.img", ImgFixture.Build(img => { Hostile(img); img.AddProperty(new WzIntProperty("extra", 1)); }));
        _server = await ViewerTestServer.StartAsync(_tree.Options(timeout: TimeSpan.FromSeconds(20)));
        var response = await _server.Client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode < 500, $"{url}: {response.StatusCode} {text}");
        if (url.Contains("/v1/diff"))
        {
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("changed", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("extra", doc.RootElement.GetProperty("changes")[0].GetProperty("path").GetString());
        }
        if (url.Contains("/v1/frames"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
#endif
