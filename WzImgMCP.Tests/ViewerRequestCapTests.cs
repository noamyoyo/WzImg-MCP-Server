#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;

namespace WzImgMCP.Tests;

/// <summary>WZ_VIEWER_MAX_REQUESTS: default, range, refusal at startup, and the cap itself (separate from the decode cap, health outside it).</summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerRequestCapTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly ViewerTree _tree = new();
    private readonly List<ViewerTestServer> _servers = new();

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _tree.Dispose();
    }

    private const string Token = "abcdefgh" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static Func<string, string?> Env(string? requests) => n => n switch
    {
        ViewerOptions.TokenEnvVar => Token,
        ViewerOptions.MaxRequestsEnvVar => requests,
        _ => null,
    };

    // ---- the setting

    [Fact]
    public void Default_IsEight()
    {
        Assert.Equal(8, ViewerOptions.DefaultMaxConcurrentRequests);
        Assert.Equal(8, ViewerOptions.FromEnvironment(Env(null)).MaxConcurrentRequests);
        Assert.Equal(ViewerOptions.DefaultMaxConcurrentRequests, new ViewerOptions(Token, _tree.Roots).MaxConcurrentRequests);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("8", 8)]
    [InlineData("32", 32)]
    [InlineData("  16 \n", 16)]
    [InlineData("", 8)]        // blank keeps the default
    [InlineData("   ", 8)]
    public void ValidValues_AreRead(string raw, int expected)
        => Assert.Equal(expected, ViewerOptions.FromEnvironment(Env(raw)).MaxConcurrentRequests);

    [Theory]
    [InlineData("0")]
    [InlineData("33")]
    [InlineData("100")]
    [InlineData("-1")]
    [InlineData("+4")]
    [InlineData("1.5")]
    [InlineData("4 5")]
    [InlineData("abc")]
    [InlineData("0x10")]
    [InlineData("99999999999999999999")]
    public void BadValues_AreRefused_NotSilentlyReplaced(string raw)
    {
        var ex = Assert.Throws<ViewerConfigException>(() => ViewerOptions.FromEnvironment(Env(raw)));
        Assert.Contains("WZ_VIEWER_MAX_REQUESTS", ex.Message);
        Assert.Contains("1 to 32", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("33")]
    [InlineData("many")]
    public async Task BadValue_StopsStartup_WithExitCode2_AndTheReasonOnStandardError(string raw)
    {
        var previous = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        int exit;
        try { exit = await ViewerHost.RunAsync(Array.Empty<string>(), "127.0.0.1", 0, Env(raw)); }
        finally { Console.SetError(previous); }

        Assert.Equal(2, exit);
        Assert.Contains("WZ_VIEWER_MAX_REQUESTS", captured.ToString());
        Assert.DoesNotContain(Token, captured.ToString());
    }

    // ---- the cap

    private async Task<ViewerTestServer> Start(ViewerOptions options, IViewerFileSystem fs)
    {
        var s = await ViewerTestServer.StartAsync(options, fs);
        _servers.Add(s);
        return s;
    }

    private async Task AssertBusy(ViewerTestServer s, string url)
    {
        var response = await s.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("busy", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]   // the decode cap is as high as the request cap
    [InlineData(3, 1)]   // and much lower: the request cap does not depend on it
    public async Task OnlyTheConfiguredNumberOfRequestsRun_TheNextIsBusy_AndHealthIsOutsideTheCap(int cap, int decodes)
    {
        for (int i = 0; i < cap; i++)
            ImgFixture.Write(_tree.Root("owner"), $"F{i}.img");
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var s = await Start(_tree.Options(maxRequests: cap, maxDecodes: decodes, timeout: TimeSpan.FromSeconds(120)), fs);

        // `cap` requests that stay inside the loader: each holds a request slot
        var running = Enumerable.Range(0, cap).Select(i => s.Client.GetAsync($"/v1/inspect?source=owner&path=F{i}.img")).ToList();
        for (int i = 0; i < Math.Min(cap, decodes); i++)
            Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)), "a request never reached its read");
        // (with fewer decode slots than requests the others wait for a decode slot, still holding their request slot)
        await Task.Delay(300);

        await AssertBusy(s, "/v1/resolve?source=owner&path=F0.img");                // cap + 1
        await AssertBusy(s, "/v1/diff?person=owner&path=F0.img");
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/v1/health")).StatusCode);   // health is outside the cap

        gate.Set();
        foreach (var task in running)
            Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);

        // all slots are back: `cap` more at once are fine (no leak), and a long run of single requests too
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/v1/resolve?source=owner&path=F0.img")).StatusCode);
        var again = await Task.WhenAll(Enumerable.Range(0, cap).Select(i => s.Client.GetAsync($"/v1/inspect?source=owner&path=F{i}.img")));
        Assert.All(again, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task ASetting_AboveTheOldCapOfFour_IsReallyUsed()
    {
        const int cap = 6;
        for (int i = 0; i < cap; i++)
            ImgFixture.Write(_tree.Root("owner"), $"F{i}.img");
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var s = await Start(_tree.Options(maxRequests: cap, maxDecodes: cap, timeout: TimeSpan.FromSeconds(120)), fs);

        var running = Enumerable.Range(0, cap).Select(i => s.Client.GetAsync($"/v1/inspect?source=owner&path=F{i}.img")).ToList();
        for (int i = 0; i < cap; i++)
            Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)));   // six requests inside at once: more than four
        await AssertBusy(s, "/v1/resolve?source=owner&path=F0.img");
        gate.Set();
        foreach (var task in running) Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    [Fact]
    public async Task TheEnvironmentValue_ReachesTheLimiter()
    {
        // through FromEnvironment, not the test helper: "2" allows two requests and refuses the third
        for (int i = 0; i < 2; i++)
            ImgFixture.Write(_tree.Root("owner"), $"F{i}.img");
        var env = new Dictionary<string, string?>
        {
            [ViewerOptions.TokenEnvVar] = ViewerTree.Token,
            [ViewerOptions.MaxRequestsEnvVar] = "2",
            ["WZ_VIEWER_ROOT_CANON"] = _tree.Root("canon"),
            ["WZ_VIEWER_ROOT_OWNER"] = _tree.Root("owner"),
            ["WZ_VIEWER_ROOT_FRIEND"] = _tree.Root("friend"),
            [ViewerOptions.MaxDecodesEnvVar] = "2",
        };
        var options = ViewerOptions.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal(2, options.MaxConcurrentRequests);
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var s = await Start(options, fs);
        var running = Enumerable.Range(0, 2).Select(i => s.Client.GetAsync($"/v1/inspect?source=owner&path=F{i}.img")).ToList();
        for (int i = 0; i < 2; i++) Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)));
        await AssertBusy(s, "/v1/resolve?source=owner&path=F0.img");
        gate.Set();
        foreach (var task in running) Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    // ---- 404 for a node that does not exist, [] only for an existing container without frames

    private static WzCanvasProperty Cv(string name)
    {
        var c = ImgFixture.PixelCanvas(name, 4, 4, (x, y) => ((byte)1, (byte)2, (byte)3, (byte)255));
        return c;
    }

    [Fact]
    public async Task MissingNode_Is404OnBothEndpoints_AndAnEmptyListOnlyForAnExistingContainer()
    {
        ImgFixture.Write(_tree.Root("owner"), "Mob/T.img", ImgFixture.Build(img =>
        {
            img.AddProperty(new WzSubProperty("empty"));                                   // a container without children
            var other = new WzSubProperty("noframes");                                     // children, but no frames
            other.AddProperty(new WzIntProperty("speed", 3));
            other.AddProperty(new WzIntProperty("0", 5));                                  // a number named like a frame, not a canvas
            other.AddProperty(new WzSubProperty("info"));
            img.AddProperty(other);
            var anim = new WzSubProperty("anim"); anim.AddProperty(Cv("0")); img.AddProperty(anim);
            img.AddProperty(new WzIntProperty("leaf", 1));
            img.AddProperty(Cv("pic"));                                                    // a canvas without numeric children
        }));
        var s = await Start(_tree.Options(), new FakeFileSystem());

        async Task<(HttpStatusCode Status, JsonElement Body)> Get(string url)
        {
            var response = await s.Client.GetAsync(url);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, doc.RootElement.Clone());
        }
        string Frames(string node) => $"/v1/frames?source=owner&path=Mob/T.img&node={node}";
        string Canvas(string node) => $"/v1/canvas?source=owner&path=Mob/T.img&node={node}";

        // existing containers without frames: 200 and an empty list, truncated false
        foreach (var node in new[] { "empty", "noframes", "pic" })
        {
            var (status, body) = await Get(Frames(node));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(0, body.GetProperty("frames").GetArrayLength());
            Assert.False(body.GetProperty("truncated").GetBoolean());
        }
        Assert.Equal(1, (await Get(Frames("anim"))).Body.GetProperty("frames").GetArrayLength());

        // nodes that do not exist: 404 not_found on both endpoints, never an empty list
        foreach (var node in new[] { "missing", "empty/missing", "anim/5", "anim%231", "missing/deeper", "Empty" })
        {
            foreach (var url in new[] { Frames(node), Canvas(node) })
            {
                var (status, body) = await Get(url);
                Assert.True(status == HttpStatusCode.NotFound, $"{url}: {status}");
                Assert.Equal("not_found", body.GetProperty("error").GetProperty("code").GetString());
                Assert.False(body.TryGetProperty("frames", out _));
            }
        }

        // existing but not a container (frames) / not a canvas (canvas): also 404
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Frames("leaf"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Canvas("leaf"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Canvas("empty"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(Canvas("pic"))).StatusCode);
    }
}
#endif
