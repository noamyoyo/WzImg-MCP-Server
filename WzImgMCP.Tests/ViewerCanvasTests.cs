#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;
using WzImgMCP.Viewer.Canvas;

namespace WzImgMCP.Tests;

/// <summary>GET /v1/canvas and GET /v1/frames on synthetic images: PNG, scaling, ETag/304, limits, typed errors, slots and leases.</summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerCanvasTests : IAsyncLifetime
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

    private async Task<ViewerTestServer> Start(ViewerOptions? options = null, IViewerFileSystem? fs = null)
    {
        var s = await ViewerTestServer.StartAsync(options ?? _tree.Options(), fs);
        _servers.Add(s);
        return s;
    }

    private string Write(string source, string path, Action<WzImage> fill) => ImgFixture.Write(_tree.Root(source), path, ImgFixture.Build(fill));

    private static WzCanvasProperty Cv(string name, int width, int height, int? delay = null, int originX = 1, int originY = 2, Func<int, int, (byte B, byte G, byte R, byte A)>? pixel = null)
    {
        var c = ImgFixture.PixelCanvas(name, width, height, pixel ?? ((x, y) => ((byte)10, (byte)20, (byte)30, (byte)255)));
        c.AddProperty(new WzVectorProperty("origin", new WzIntProperty("X", originX), new WzIntProperty("Y", originY)));
        if (delay.HasValue) c.AddProperty(new WzIntProperty("delay", delay.Value));
        return c;
    }

    /// <summary>A canvas that only holds a link, with the 1x1 placeholder picture such canvases carry (the writer cannot save a canvas without one).</summary>
    private static WzCanvasProperty LinkCanvas(string name, string linkName, string target)
    {
        var link = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
        link.PngProperty.SetPixelsBgra32(new byte[4], 1, 1);
        link.AddProperty(new WzStringProperty(linkName, target));
        return link;
    }

    private static string Url(string path = "Mob/T.img", string node = "stand", string? maxSide = null, string source = "owner")
        => $"/v1/canvas?source={source}&path={path}&node={node}" + (maxSide == null ? "" : "&maxSide=" + maxSide);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetJson(ViewerTestServer s, string url)
    {
        var response = await s.Client.GetAsync(url);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private static async Task AssertError(ViewerTestServer s, string url, HttpStatusCode status, string code)
    {
        var (actual, body) = await GetJson(s, url);
        Assert.True(status == actual, $"{url}: expected {status}, got {actual}; log: {string.Join(" | ", s.Logs.TakeLast(3))}");
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static int Header(HttpResponseMessage r, string name) => int.Parse(r.Headers.GetValues(name).Single());

    private static SKBitmap DecodePng(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidDataException("not a PNG");

    // ---- /v1/canvas: the picture and its headers

    [Fact]
    public async Task Canvas_IsAValidPng_WithTheRightSizePixelsAndHeaders()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 6, 4, delay: 120, originX: 3, originY: 4,
            pixel: (x, y) => ((byte)(x * 20), (byte)(y * 30), (byte)77, (byte)255))));
        var s = await Start();

        var response = await s.Client.GetAsync(Url());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes.Take(8).ToArray());

        using var bitmap = DecodePng(bytes);
        Assert.Equal((6, 4), (bitmap.Width, bitmap.Height));
        var px = bitmap.GetPixel(2, 3);
        Assert.Equal((77, 90, 40, 255), (px.Red, px.Green, px.Blue, px.Alpha));   // R=77, G=y*30, B=x*20

        Assert.Equal(6, Header(response, "X-Canvas-Width"));
        Assert.Equal(4, Header(response, "X-Canvas-Height"));
        Assert.Equal(3, Header(response, "X-Canvas-Origin-X"));
        Assert.Equal(4, Header(response, "X-Canvas-Origin-Y"));
        Assert.Equal(120, Header(response, "X-Canvas-Delay"));
        Assert.Equal(6, Header(response, "X-Image-Width"));
        Assert.Equal(4, Header(response, "X-Image-Height"));
        Assert.True(response.Headers.CacheControl!.NoCache && response.Headers.CacheControl.Private);   // keep it, revalidate every time
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Canvas_AlphaIsKept()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4, pixel: (x, y) => ((byte)10, (byte)20, (byte)30, x < 2 ? (byte)0 : (byte)255))));
        var s = await Start();
        using var bitmap = DecodePng(await (await s.Client.GetAsync(Url())).Content.ReadAsByteArrayAsync());
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Equal(255, bitmap.GetPixel(3, 0).Alpha);
    }

    [Fact]
    public async Task DelayAndOriginHeaders_AreOnlyThere_WhenTheCanvasHasThem()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var plain = ImgFixture.PixelCanvas("stand", 4, 4, (x, y) => ((byte)1, (byte)2, (byte)3, (byte)255));   // no origin, no delay
            img.AddProperty(plain);
        });
        var s = await Start();
        var response = await s.Client.GetAsync(Url());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Canvas-Delay"));
        Assert.Equal(0, Header(response, "X-Canvas-Origin-X"));   // an absent origin is (0, 0), as in the library
        Assert.Equal(0, Header(response, "X-Canvas-Origin-Y"));
    }

    // ---- scaling

    [Theory]
    [InlineData(2000, 1000, null, 1024, 512)]       // default is 1024
    [InlineData(2000, 1000, "1024", 1024, 512)]
    [InlineData(1000, 2000, "1024", 512, 1024)]
    [InlineData(2000, 1000, "full", 2000, 1000)]    // full keeps what is under the 4096 cap
    [InlineData(100, 50, null, 100, 50)]            // never enlarged
    [InlineData(100, 50, "full", 100, 50)]
    [InlineData(1024, 1024, null, 1024, 1024)]      // exactly at the limit: not scaled
    [InlineData(1025, 1025, null, 1024, 1024)]
    public async Task Canvas_Downscale_KeepsTheAspectRatio_AndNeverEnlarges(int w, int h, string? maxSide, int expectedW, int expectedH)
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", w, h)));
        var s = await Start();
        var response = await s.Client.GetAsync(Url(maxSide: maxSide));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bitmap = DecodePng(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((expectedW, expectedH), (bitmap.Width, bitmap.Height));
        Assert.Equal((expectedW, expectedH), (Header(response, "X-Image-Width"), Header(response, "X-Image-Height")));
        Assert.Equal((w, h), (Header(response, "X-Canvas-Width"), Header(response, "X-Canvas-Height")));   // the canvas itself, for the origin
    }

    [Fact]
    public async Task Canvas_FullIsCappedAt4096PerSide()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 5000, 100)));
        var s = await Start();
        var response = await s.Client.GetAsync(Url(maxSide: "full"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bitmap = DecodePng(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((4096, 82), (bitmap.Width, bitmap.Height));
        Assert.Equal(5000, Header(response, "X-Canvas-Width"));
    }

    [Fact]
    public async Task Canvas_DownscaledPixelsAreStillTheCanvasColour()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 2048, 2048, pixel: (x, y) => ((byte)40, (byte)120, (byte)200, (byte)255))));
        var s = await Start();
        using var bitmap = DecodePng(await (await s.Client.GetAsync(Url())).Content.ReadAsByteArrayAsync());
        Assert.Equal((1024, 1024), (bitmap.Width, bitmap.Height));
        var px = bitmap.GetPixel(500, 500);
        Assert.InRange(px.Red, 198, 202);
        Assert.InRange(px.Green, 118, 122);
        Assert.InRange(px.Blue, 38, 42);
        Assert.Equal(255, px.Alpha);
    }

    [Theory]
    [InlineData("2048")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("FULL")]
    [InlineData("big")]
    public async Task Canvas_BadMaxSide_IsBadRequest(string maxSide)
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        await AssertError(s, Url(maxSide: maxSide), HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task Canvas_MaxSideGivenTwice_IsBadRequest()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        await AssertError(s, Url() + "&maxSide=full&maxSide=1024", HttpStatusCode.BadRequest, "bad_request");
    }

    // ---- ETag and 304

    [Fact]
    public async Task ETag_IsStrong_AndStableForTheSameRequest()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        var a = (await s.Client.GetAsync(Url())).Headers.ETag!;
        var b = (await s.Client.GetAsync(Url())).Headers.ETag!;
        Assert.False(a.IsWeak);
        Assert.Equal(a.Tag, b.Tag);
        Assert.StartsWith("\"", a.Tag);
    }

    [Fact]
    public async Task IfNoneMatch_GivesA304_WithoutAnyParseOrDecode()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var fs = new FakeFileSystem();
        var s = await Start(fs: fs);
        var loader = s.App.Services.GetRequiredService<ViewerImageLoader>();
        var canvases = s.App.Services.GetRequiredService<ViewerCanvasService>();
        int decodes = 0;
        canvases.Probe = _ => Interlocked.Increment(ref decodes);

        var first = await s.Client.GetAsync(Url());
        var etag = first.Headers.ETag!.Tag;
        long parses = loader.Parses;
        int reads = fs.Reads;
        Assert.Equal(1, decodes);

        var request = new HttpRequestMessage(HttpMethod.Get, Url());
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await s.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(0, (await second.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(etag, second.Headers.ETag!.Tag);
        Assert.True(second.Headers.CacheControl!.NoCache && second.Headers.CacheControl.Private);
        Assert.Equal(1, decodes);                  // nothing decoded
        Assert.Equal(parses, loader.Parses);       // nothing parsed
        Assert.Equal(reads, fs.Reads);             // the file was not even read
    }

    [Fact]
    public async Task IfNoneMatch_NoDecode_EvenWhenTheImageIsNoLongerCached()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var fs = new FakeFileSystem();
        var s = await Start(fs: fs);
        var loader = s.App.Services.GetRequiredService<ViewerImageLoader>();
        var etag = (await s.Client.GetAsync(Url())).Headers.ETag!.Tag;
        loader.Cache.Clear();
        long parses = loader.Parses;

        var request = new HttpRequestMessage(HttpMethod.Get, Url());
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await s.Client.SendAsync(request)).StatusCode);
        Assert.Equal(parses, loader.Parses);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("weak")]
    [InlineData("star")]
    public async Task IfNoneMatch_AcceptsListsWeakTagsAndStar(string form)
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        var etag = (await s.Client.GetAsync(Url())).Headers.ETag!.Tag;
        var header = form switch { "list" => "\"nope\", " + etag + ", \"other\"", "weak" => "W/" + etag, _ => "*" };
        var request = new HttpRequestMessage(HttpMethod.Get, Url());
        request.Headers.TryAddWithoutValidation("If-None-Match", header);
        Assert.Equal(HttpStatusCode.NotModified, (await s.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_WithAnotherTag_GivesTheImage()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        var request = new HttpRequestMessage(HttpMethod.Get, Url());
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"0123456789abcdef0123456789abcdef\"");
        var response = await s.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [Fact]
    public async Task ETag_ChangesWithNodeMaxSidePathSourceAndFile()
    {
        Write("owner", "Mob/T.img", img => { img.AddProperty(Cv("stand", 4, 4)); img.AddProperty(Cv("move", 4, 4)); });
        Write("owner", "Mob/U.img", img => img.AddProperty(Cv("stand", 4, 4)));
        Write("friend", "Mob/T.img", img => { img.AddProperty(Cv("stand", 4, 4)); });
        var s = await Start();
        async Task<string> Tag(string url) => (await s.Client.GetAsync(url)).Headers.ETag!.Tag;

        var baseline = await Tag(Url());
        Assert.Equal(baseline, await Tag(Url(maxSide: "1024")));          // an explicit default is the same resource
        var others = new[]
        {
            await Tag(Url(node: "move")),
            await Tag(Url(maxSide: "full")),
            await Tag(Url(path: "Mob/U.img")),
            await Tag(Url(source: "friend")),
        };
        Assert.Equal(others.Length, others.Distinct().Count());
        Assert.DoesNotContain(baseline, others);

        // the file changes with the same size and the same mtime: ctime still moves the tag
        var path = Path.Combine(_tree.Root("owner"), "img", "Mob", "T.img");
        var mtime = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        Thread.Sleep(20);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, mtime);
        Assert.NotEqual(baseline, await Tag(Url()));
    }

    [Fact]
    public async Task ChangedFile_IsNotAnswered304_ByTheOldTag()
    {
        var path = Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4, pixel: (x, y) => ((byte)10, (byte)20, (byte)30, (byte)255))));
        var s = await Start();
        var etag = (await s.Client.GetAsync(Url())).Headers.ETag!.Tag;

        File.WriteAllBytes(path, ImgFixture.Build(img => img.AddProperty(Cv("stand", 4, 4, pixel: (x, y) => ((byte)99, (byte)20, (byte)30, (byte)255)))));
        var request = new HttpRequestMessage(HttpMethod.Get, Url());
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var response = await s.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bitmap = DecodePng(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(99, bitmap.GetPixel(0, 0).Blue);   // the new pixels, not the cached ones (the fixture's tuple is B, G, R, A)
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
    }

    // ---- size caps

    [Fact]
    public async Task PngOver16MiB_IsTooLarge()
    {
        var random = new Random(1);
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 2300, 2300, pixel: (x, y) =>
            ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)255))));   // noise does not compress: ~21 MB of PNG
        var s = await Start(_tree.Options(maxFileBytes: 64L * 1024 * 1024, timeout: TimeSpan.FromSeconds(120)));
        await AssertError(s, Url(maxSide: "full"), HttpStatusCode.RequestEntityTooLarge, "too_large");
    }

    [Theory]
    [InlineData(32768, 1, false)]
    [InlineData(32769, 1, true)]
    [InlineData(1, 32769, true)]
    [InlineData(8192, 8192, false)]   // exactly 256 MiB decoded
    [InlineData(8193, 8192, true)]
    [InlineData(16384, 16385, true)]
    public void DecodedSize_Over256MiBOr32768PerSide_IsTooLarge(int w, int h, bool refused)
    {
        if (!refused)
        {
            ViewerCanvasService.CheckDecodedSize(w, h);
            return;
        }
        var ex = Assert.Throws<ViewerApiException>(() => ViewerCanvasService.CheckDecodedSize(w, h));
        Assert.Equal((413, "too_large"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task FileOverTheFileCap_IsTooLarge()
    {
        var path = Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 8, 8)));
        var s = await Start(new ViewerOptions(ViewerTree.Token, _tree.Roots) { MaxFileBytes = new FileInfo(path).Length - 1 });
        await AssertError(s, Url(), HttpStatusCode.RequestEntityTooLarge, "too_large");
    }

    [Fact]
    public async Task OutOfMemoryWhileDecoding_Is413_AndEmptiesTheCache()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 8, 8)));
        var s = await Start();
        s.App.Services.GetRequiredService<ViewerCanvasService>().Probe = _ => throw new OutOfMemoryException();
        await AssertError(s, Url(), HttpStatusCode.RequestEntityTooLarge, "too_large");
        Assert.Equal(0, s.App.Services.GetRequiredService<ViewerImageCache>().Count);
    }

    // ---- nodes

    [Theory]
    [InlineData("missing")]
    [InlineData("info")]            // a sub property, not a canvas
    [InlineData("name")]            // a string
    [InlineData("info/nothing")]
    [InlineData("name/deeper")]     // below a leaf
    [InlineData("Stand")]           // case matters
    public async Task NodeThatIsNotACanvas_IsNotFound(string node)
    {
        Write("owner", "Mob/T.img", img =>
        {
            img.AddProperty(Cv("stand", 4, 4));
            var info = new WzSubProperty("info");
            info.AddProperty(new WzIntProperty("speed", 1));
            img.AddProperty(info);
            img.AddProperty(new WzStringProperty("name", "x"));
        });
        var s = await Start();
        await AssertError(s, Url(node: node), HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a..b")]
    [InlineData("/stand")]
    [InlineData("stand/")]
    [InlineData("a//b")]
    [InlineData("./stand")]
    [InlineData("a/./b")]
    [InlineData("st%25and")]
    [InlineData("st:and")]
    [InlineData("st\\and")]
    [InlineData("ständ")]
    public async Task BadNodePattern_IsBadPath(string node)
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        await AssertError(s, Url(node: node), HttpStatusCode.BadRequest, "bad_path");
    }

    [Fact]
    public async Task NodeMissingOrTooLong_IsBadPath()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        await AssertError(s, "/v1/canvas?source=owner&path=Mob/T.img", HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, Url(node: new string('a', 201)), HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, Url(node: "a&node=b"), HttpStatusCode.BadRequest, "bad_path");   // given twice
    }

    [Fact]
    public async Task NodeOfExactly200Characters_IsAccepted_AsAPattern()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        await AssertError(s, Url(node: new string('a', 200)), HttpStatusCode.NotFound, "not_found");   // valid pattern, no such node
    }

    [Fact]
    public async Task NodeWithSpacesAndDots_FindsTheCanvas()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var s = new WzSubProperty("a b.c");
            s.AddProperty(Cv("x_y-z", 4, 4));
            img.AddProperty(s);
        });
        var server = await Start();
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync(Url(node: "a%20b.c/x_y-z"))).StatusCode);
    }

    [Fact]
    public async Task ThePathRules_AreTheOnesOfDiff()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        await AssertError(s, Url(path: "../T.img"), HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, Url(path: "Mob/T.txt"), HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, Url(path: "Backups/T.img"), HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, Url(path: "Mob/Nope.img"), HttpStatusCode.NotFound, "not_found");
        await AssertError(s, Url(source: "nobody"), HttpStatusCode.BadRequest, "unknown_source");
        await AssertError(s, "/v1/canvas?path=Mob/T.img&node=stand", HttpStatusCode.BadRequest, "unknown_source");
        await AssertError(s, Url(source: "canon"), HttpStatusCode.NotFound, "not_found");   // the file is only in owner
    }

    [Fact]
    public async Task CanonSource_IsReadable()
    {
        Write("canon", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(Url(source: "canon"))).StatusCode);
    }

    [Fact]
    public async Task ParseFailure_And_MissingImgRoot_AreTyped()
    {
        ImgFixture.Write(_tree.Root("owner"), "Mob/Junk.img", new byte[3000]);
        var s = await Start();
        await AssertError(s, Url(path: "Mob/Junk.img"), HttpStatusCode.UnprocessableEntity, "parse_failed");
        Directory.Delete(Path.Combine(_tree.Root("friend"), "img"), true);
        await AssertError(s, Url(source: "friend"), HttpStatusCode.ServiceUnavailable, "source_unavailable");
    }

    // ---- UOL and links

    [Fact]
    public async Task UolNode_IsFollowedInsideTheImage()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var walk = new WzSubProperty("walk");
            walk.AddProperty(Cv("0", 5, 5, delay: 50, pixel: (x, y) => ((byte)1, (byte)2, (byte)3, (byte)255)));
            walk.AddProperty(new WzUOLProperty("1", "../walk/0"));   // as the game files write it: ".." leaves the container
            walk.AddProperty(new WzUOLProperty("2", "walk/0"));      // a path without ".." starts at the top of the image
            img.AddProperty(walk);
            img.AddProperty(new WzUOLProperty("shortcut", "walk/0"));
        });
        var s = await Start();
        foreach (var node in new[] { "walk/0", "walk/1", "walk/2", "shortcut" })
        {
            var response = await s.Client.GetAsync(Url(node: node));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{node}: {response.StatusCode}");
            Assert.Equal(5, Header(response, "X-Canvas-Width"));
            Assert.Equal(50, Header(response, "X-Canvas-Delay"));
        }
    }

    [Fact]
    public async Task UolLoop_AndUolToNowhere_AreNotFound()
    {
        Write("owner", "Mob/T.img", img =>
        {
            img.AddProperty(new WzUOLProperty("a", "b"));
            img.AddProperty(new WzUOLProperty("b", "a"));
            img.AddProperty(new WzUOLProperty("gone", "nowhere/at/all"));
        });
        var s = await Start();
        await AssertError(s, Url(node: "a"), HttpStatusCode.NotFound, "not_found");
        await AssertError(s, Url(node: "gone"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Inlink_ShowsThePixelsOfItsTarget()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var anim = new WzSubProperty("anim");
            anim.AddProperty(Cv("0", 6, 6, pixel: (x, y) => ((byte)0, (byte)0, (byte)222, (byte)255)));
            var link = new WzCanvasProperty("1") { PngProperty = new WzPngProperty() };
            link.PngProperty.SetPixelsBgra32(new byte[4], 1, 1);   // a placeholder, as linked canvases have
            link.AddProperty(new WzStringProperty("_inlink", "anim/0"));
            link.AddProperty(new WzIntProperty("delay", 90));
            anim.AddProperty(link);
            img.AddProperty(anim);
        });
        var s = await Start();
        var response = await s.Client.GetAsync(Url(node: "anim/1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bitmap = DecodePng(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((6, 6), (bitmap.Width, bitmap.Height));
        Assert.Equal(222, bitmap.GetPixel(0, 0).Red);
        Assert.Equal(90, Header(response, "X-Canvas-Delay"));    // its own delay
        Assert.Equal(1, Header(response, "X-Canvas-Origin-X"));  // the origin of the target, the link has none
        Assert.NotNull(response.Headers.ETag);                    // same image: the file stamp covers it
    }

    [Fact]
    public async Task Outlink_IsFollowedInsideTheSameSource_AndHasNoETag()
    {
        Write("owner", "Mob/Target.img", img => img.AddProperty(Cv("pic", 7, 3, pixel: (x, y) => ((byte)0, (byte)0, (byte)111, (byte)255))));
        Write("owner", "Mob/T.img", img =>
        {
            var link = new WzCanvasProperty("stand") { PngProperty = new WzPngProperty() };
            link.PngProperty.SetPixelsBgra32(new byte[4], 1, 1);
            link.AddProperty(new WzStringProperty("_outlink", "Mob/Target.img/pic"));
            img.AddProperty(link);
        });
        var s = await Start();
        var response = await s.Client.GetAsync(Url());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bitmap = DecodePng(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((7, 3), (bitmap.Width, bitmap.Height));
        Assert.Equal(111, bitmap.GetPixel(0, 0).Red);
        Assert.Null(response.Headers.ETag);   // the target's file stamp is not the link's
        Assert.True(response.Headers.CacheControl!.NoStore && response.Headers.CacheControl.Private);
    }

    [Fact]
    public async Task Outlink_NeverReadsAnotherSource_OrAnythingOutsideTheRules()
    {
        Write("canon", "Mob/OnlyCanon.img", img => img.AddProperty(Cv("pic", 7, 3)));
        Write("owner", "Mob/T.img", img =>
        {
            foreach (var (name, target) in new[] { ("canon", "Mob/OnlyCanon.img/pic"), ("up", "../../etc/x.img/pic"), ("backup", "Backups/Mob.img/pic") })
                img.AddProperty(LinkCanvas(name, "_outlink", target));
        });
        var s = await Start();
        // the target exists in canon, but this request is for owner: it is not there
        await AssertError(s, Url(node: "canon"), HttpStatusCode.NotFound, "not_found");
        await AssertError(s, Url(node: "up"), HttpStatusCode.NotFound, "not_found");
        await AssertError(s, Url(node: "backup"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task LinkThatDoesNotResolve_IsNotFound_NotThePlaceholder()
    {
        Write("owner", "Mob/T.img", img =>
        {
            img.AddProperty(LinkCanvas("in", "_inlink", "nowhere/0"));
            img.AddProperty(LinkCanvas("out", "_outlink", "Mob/Missing.img/pic"));
            img.AddProperty(LinkCanvas("self", "_inlink", "self"));      // a loop through itself
        });
        var s = await Start();
        await AssertError(s, Url(node: "in"), HttpStatusCode.NotFound, "not_found");
        await AssertError(s, Url(node: "out"), HttpStatusCode.NotFound, "not_found");
        await AssertError(s, Url(node: "self"), HttpStatusCode.NotFound, "not_found");
    }

    // ---- /v1/frames

    private static async Task<JsonElement> Frames(ViewerTestServer s, string node, string path = "Mob/T.img", string source = "owner")
    {
        var response = await s.Client.GetAsync($"/v1/frames?source={source}&path={path}&node={node}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Frames_ListsTheNumericChildrenInOrder_WithoutPixelData()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var stand = new WzSubProperty("stand1");
            stand.AddProperty(Cv("0", 10, 20, delay: 100, originX: 5, originY: 6));
            stand.AddProperty(Cv("1", 11, 21, delay: 200, originX: 7, originY: 8));
            stand.AddProperty(Cv("2", 12, 22));   // no delay
            stand.AddProperty(new WzSubProperty("info"));
            img.AddProperty(stand);
        });
        var s = await Start();
        var result = await Frames(s, "stand1");
        Assert.False(result.GetProperty("truncated").GetBoolean());
        var frames = result.GetProperty("frames").EnumerateArray().ToList();
        Assert.Equal(3, frames.Count);
        Assert.Equal(new[] { 0, 1, 2 }, frames.Select(f => f.GetProperty("index").GetInt32()));
        Assert.Equal(new[] { "stand1/0", "stand1/1", "stand1/2" }, frames.Select(f => f.GetProperty("node").GetString()));
        Assert.Equal((10, 20, 5, 6, 100), (frames[0].GetProperty("width").GetInt32(), frames[0].GetProperty("height").GetInt32(),
            frames[0].GetProperty("originX").GetInt32(), frames[0].GetProperty("originY").GetInt32(), frames[0].GetProperty("delayMs").GetInt32()));
        Assert.Equal(200, frames[1].GetProperty("delayMs").GetInt32());
        Assert.Equal(JsonValueKind.Null, frames[2].GetProperty("delayMs").ValueKind);
        Assert.Equal(new[] { "delayMs", "height", "index", "node", "originX", "originY", "width" },
            frames[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain("png", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "frames", "truncated" }, result.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Frames_ToleratesGaps_AndSortsNumerically()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var a = new WzSubProperty("a");
            foreach (var i in new[] { 10, 2, 0, 5 }) a.AddProperty(Cv(i.ToString(), 4, 4));   // file order is not numeric order
            a.AddProperty(Cv("01", 4, 4));       // not an index
            a.AddProperty(Cv("-1", 4, 4));
            a.AddProperty(Cv("x", 4, 4));
            a.AddProperty(Cv("1.5", 4, 4));
            img.AddProperty(a);
        });
        var s = await Start();
        var frames = (await Frames(s, "a")).GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("index").GetInt32()).ToList();
        Assert.Equal(new[] { 0, 2, 5, 10 }, frames);
    }

    [Fact]
    public async Task Frames_SkipsChildrenThatAreNotCanvases_AndFollowsUolFrames()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var a = new WzSubProperty("a");
            a.AddProperty(Cv("0", 8, 9, delay: 30, originX: 2, originY: 3));
            a.AddProperty(new WzUOLProperty("1", "../a/0"));               // a UOL frame: the same picture
            a.AddProperty(new WzIntProperty("2", 5));                      // not a canvas
            a.AddProperty(new WzUOLProperty("3", "nowhere"));              // a UOL to nothing
            a.AddProperty(new WzUOLProperty("4", "b/0"));                  // a UOL into another animation of this image
            img.AddProperty(a);
            var b = new WzSubProperty("b");
            b.AddProperty(Cv("0", 3, 4));
            img.AddProperty(b);
        });
        var s = await Start();
        var frames = (await Frames(s, "a")).GetProperty("frames").EnumerateArray().ToList();
        Assert.Equal(new[] { 0, 1, 4 }, frames.Select(f => f.GetProperty("index").GetInt32()));
        Assert.Equal((8, 9, 2, 3, 30), (frames[1].GetProperty("width").GetInt32(), frames[1].GetProperty("height").GetInt32(),
            frames[1].GetProperty("originX").GetInt32(), frames[1].GetProperty("originY").GetInt32(), frames[1].GetProperty("delayMs").GetInt32()));
        Assert.Equal((3, 4), (frames[2].GetProperty("width").GetInt32(), frames[2].GetProperty("height").GetInt32()));
        Assert.Equal("a/1", frames[1].GetProperty("node").GetString());   // the node the panel passes to /v1/canvas

        // and that node really renders
        var picture = await s.Client.GetAsync(Url(node: "a/1"));
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal(8, Header(picture, "X-Canvas-Width"));
    }

    [Fact]
    public async Task Frames_UolNode_AndNestedNode()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var outer = new WzSubProperty("outer");
            var inner = new WzSubProperty("inner");
            inner.AddProperty(Cv("0", 4, 4));
            outer.AddProperty(inner);
            img.AddProperty(outer);
            img.AddProperty(new WzUOLProperty("alias", "outer/inner"));
        });
        var s = await Start();
        Assert.Single((await Frames(s, "outer/inner")).GetProperty("frames").EnumerateArray());
        var viaUol = await Frames(s, "alias");
        Assert.Single(viaUol.GetProperty("frames").EnumerateArray());
        Assert.Equal("alias/0", viaUol.GetProperty("frames")[0].GetProperty("node").GetString());
    }

    [Theory]
    [InlineData(511, false, 511)]
    [InlineData(512, false, 512)]
    [InlineData(513, true, 512)]
    [InlineData(600, true, 512)]
    public async Task Frames_StopAt512_AndSayTruncated(int count, bool truncated, int listed)
    {
        Write("owner", "Mob/T.img", img =>
        {
            var a = new WzSubProperty("a");
            for (int i = 0; i < count; i++) a.AddProperty(Cv(i.ToString(), 2, 2));
            img.AddProperty(a);
        });
        var s = await Start(_tree.Options(timeout: TimeSpan.FromSeconds(120)));
        var result = await Frames(s, "a");
        Assert.Equal(truncated, result.GetProperty("truncated").GetBoolean());
        Assert.Equal(listed, result.GetProperty("frames").GetArrayLength());
        Assert.Equal(listed - 1, result.GetProperty("frames")[listed - 1].GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task Frames_TruncationCountsOnlyRealFrames_NotTheSkippedOnes()
    {
        Write("owner", "Mob/T.img", img =>
        {
            var a = new WzSubProperty("a");
            for (int i = 0; i < 600; i++) a.AddProperty(i % 2 == 0 ? Cv(i.ToString(), 2, 2) : new WzIntProperty(i.ToString(), i));   // 300 canvases
            img.AddProperty(a);
        });
        var s = await Start(_tree.Options(timeout: TimeSpan.FromSeconds(120)));
        var result = await Frames(s, "a");
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(300, result.GetProperty("frames").GetArrayLength());
    }

    [Fact]
    public async Task Frames_NodeWithoutFrames_IsAnEmptyList_AndAnUnknownNodeIsNotFound()
    {
        Write("owner", "Mob/T.img", img =>
        {
            img.AddProperty(new WzSubProperty("empty"));
            img.AddProperty(new WzIntProperty("leaf", 1));
            img.AddProperty(Cv("stand", 4, 4));
        });
        var s = await Start();
        var result = await Frames(s, "empty");
        Assert.Equal(0, result.GetProperty("frames").GetArrayLength());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        await AssertError(s, "/v1/frames?source=owner&path=Mob/T.img&node=nope", HttpStatusCode.NotFound, "not_found");
        await AssertError(s, "/v1/frames?source=owner&path=Mob/T.img&node=leaf", HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Frames_BadNode_BadPath_AndErrors_AreTyped()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(new WzSubProperty("a")));
        ImgFixture.Write(_tree.Root("owner"), "Mob/Junk.img", new byte[3000]);
        var s = await Start();
        await AssertError(s, "/v1/frames?source=owner&path=Mob/T.img&node=..", HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, "/v1/frames?source=owner&path=Mob/T.img", HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, "/v1/frames?source=owner&path=../T.img&node=a", HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, "/v1/frames?source=zzz&path=Mob/T.img&node=a", HttpStatusCode.BadRequest, "unknown_source");
        await AssertError(s, "/v1/frames?source=owner&path=Mob/Nope.img&node=a", HttpStatusCode.NotFound, "not_found");
        await AssertError(s, "/v1/frames?source=owner&path=Mob/Junk.img&node=a", HttpStatusCode.UnprocessableEntity, "parse_failed");
    }

    [Fact]
    public async Task Frames_OfALinkedFrame_ReportTheTargetsSize()
    {
        Write("owner", "Mob/Target.img", img => img.AddProperty(Cv("pic", 13, 14)));
        Write("owner", "Mob/T.img", img =>
        {
            var a = new WzSubProperty("a");
            var link = new WzCanvasProperty("0") { PngProperty = new WzPngProperty() };
            link.PngProperty.SetPixelsBgra32(new byte[4], 1, 1);
            link.AddProperty(new WzStringProperty("_outlink", "Mob/Target.img/pic"));
            a.AddProperty(link);
            img.AddProperty(a);
        });
        var s = await Start();
        var frame = (await Frames(s, "a")).GetProperty("frames")[0];
        Assert.Equal((13, 14), (frame.GetProperty("width").GetInt32(), frame.GetProperty("height").GetInt32()));
    }

    // ---- both endpoints: token, method, limiter, slots, leases

    [Theory]
    [InlineData("/v1/canvas?source=owner&path=Mob/T.img&node=stand")]
    [InlineData("/v1/frames?source=owner&path=Mob/T.img&node=stand")]
    public async Task NeedsTheToken_AndOnlyGet(string url)
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        using var bare = new HttpClient { BaseAddress = s.Client.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await bare.GetAsync(url)).StatusCode);
        using var wrong = new HttpClient { BaseAddress = s.Client.BaseAddress };
        wrong.DefaultRequestHeaders.TryAddWithoutValidation(ViewerOptions.TokenHeader, "x" + s.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync(url)).StatusCode);
        var body = await (await bare.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains("unauthorized", body);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await s.Client.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await s.Client.PutAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await s.Client.DeleteAsync(url)).StatusCode);
    }

    [Fact]
    public async Task ABareUnauthorisedRequest_SendsNoETagOrPicture()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        using var bare = new HttpClient { BaseAddress = s.Client.BaseAddress };
        var etag = (await s.Client.GetAsync(Url())).Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Get, Url());
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var response = await bare.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // a matching tag is no way around the token
        Assert.Null(response.Headers.ETag);
    }

    [Theory]
    [InlineData("/v1/canvas?source=owner&path=Mob/T.img&node=stand")]
    [InlineData("/v1/frames?source=owner&path=Mob/T.img&node=stand")]
    public async Task WhenAllRequestSlotsAreTaken_TheAnswerIsBusy_AndTheRequestLimiterIsFreeAgain(string url)
    {
        Write("owner", "Mob/T.img", img => { var a = new WzSubProperty("stand"); a.AddProperty(Cv("0", 4, 4)); img.AddProperty(a); img.AddProperty(Cv("stand2", 4, 4)); });
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var s = await Start(_tree.Options(timeout: TimeSpan.FromMilliseconds(400), maxRequests: 1, maxDecodes: 1), fs);

        await AssertError(s, url, HttpStatusCode.GatewayTimeout, "timeout");          // the read is blocked, the request times out
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertError(s, url, HttpStatusCode.ServiceUnavailable, "busy");        // the slot is still held by the work that cannot be cancelled
        await AssertError(s, "/v1/resolve?source=owner&path=Mob/T.img", HttpStatusCode.ServiceUnavailable, "busy");

        gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode last;
        do { last = (await s.Client.GetAsync("/v1/resolve?source=owner&path=Mob/T.img")).StatusCode; }
        while (last == HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < deadline);
        Assert.Equal(HttpStatusCode.OK, last);

        // the decode slot is free as well (only one exists): the same call now works, twice
        var ok1 = await s.Client.GetAsync(url.Replace("node=stand", "node=stand2").Replace("/v1/frames", "/v1/canvas"));
        Assert.Equal(HttpStatusCode.OK, ok1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(url.Replace("node=stand", "node=stand2").Replace("/v1/frames", "/v1/canvas"))).StatusCode);
        s.App.Services.GetRequiredService<ViewerImageLoader>().Cache.Clear();
    }

    [Fact]
    public async Task AfterEveryKindOfError_NoLeaseIsHeld_AndNoDecodeSlotIsLeaked()
    {
        Write("owner", "Mob/T.img", img =>
        {
            img.AddProperty(Cv("stand", 8, 8));
            var a = new WzSubProperty("a");
            a.AddProperty(Cv("0", 4, 4));
            img.AddProperty(a);
            img.AddProperty(LinkCanvas("lnk", "_outlink", "Mob/T2.img/pic"));
        });
        Write("owner", "Mob/T2.img", img => img.AddProperty(Cv("pic", 4, 4)));
        ImgFixture.Write(_tree.Root("owner"), "Mob/Junk.img", new byte[3000]);
        var s = await Start(_tree.Options(maxDecodes: 1, maxRequests: 1));   // one slot of each kind: a leak would show at once
        var loader = s.App.Services.GetRequiredService<ViewerImageLoader>();
        var resolver = s.App.Services.GetRequiredService<ViewerPathResolver>();
        LoadedImage main, linked;
        using (var a = loader.Load(resolver.Resolve("owner", "Mob/T.img"), CancellationToken.None)) main = a.Loaded;
        using (var b = loader.Load(resolver.Resolve("owner", "Mob/T2.img"), CancellationToken.None)) linked = b.Loaded;

        for (int round = 0; round < 3; round++)
        {
            await AssertError(s, Url(node: "missing"), HttpStatusCode.NotFound, "not_found");
            await AssertError(s, Url(node: "a"), HttpStatusCode.NotFound, "not_found");
            await AssertError(s, Url(node: ".."), HttpStatusCode.BadRequest, "bad_path");
            await AssertError(s, Url(maxSide: "x"), HttpStatusCode.BadRequest, "bad_request");
            await AssertError(s, Url(path: "Mob/Junk.img"), HttpStatusCode.UnprocessableEntity, "parse_failed");
            await AssertError(s, "/v1/frames?source=owner&path=Mob/T.img&node=missing", HttpStatusCode.NotFound, "not_found");
            Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(Url(node: "lnk"))).StatusCode);   // opens a second image through a link
        }
        s.App.Services.GetRequiredService<ViewerCanvasService>().Probe = _ => throw new InvalidDataException();   // a decode that fails
        await AssertError(s, Url(), HttpStatusCode.UnprocessableEntity, "parse_failed");
        s.App.Services.GetRequiredService<ViewerCanvasService>().Probe = _ => throw new OperationCanceledException();
        var cancelled = await s.Client.GetAsync(Url());
        Assert.NotEqual(HttpStatusCode.OK, cancelled.StatusCode);
        s.App.Services.GetRequiredService<ViewerCanvasService>().Probe = null;
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(Url())).StatusCode);   // still served: the request slot and the decode slot came back each time

        Assert.False(main.Disposed);
        loader.Cache.Clear();
        Assert.True(main.Disposed);
        Assert.True(linked.Disposed);
    }

    [Fact]
    public async Task ClientGoingAway_DoesNotLeaveTheSlotsTaken()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var s = await Start(_tree.Options(maxRequests: 1, maxDecodes: 1), fs);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Client.GetAsync(Url(), cts.Token));
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(10)));
        gate.Set();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode last;
        do { last = (await s.Client.GetAsync(Url())).StatusCode; if (last == HttpStatusCode.ServiceUnavailable) await Task.Delay(50); }
        while (last == HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < deadline);
        Assert.Equal(HttpStatusCode.OK, last);
        s.App.Services.GetRequiredService<ViewerImageLoader>().Cache.Clear();
    }

    [Fact]
    public async Task Log_HasNoPathNodeOrSource()
    {
        Write("owner", "Mob/SecretName.img", img => img.AddProperty(Cv("SecretNode", 4, 4)));
        var s = await Start();
        await s.Client.GetAsync(Url(path: "Mob/SecretName.img", node: "SecretNode"));
        await s.Client.GetAsync("/v1/frames?source=owner&path=Mob/SecretName.img&node=SecretNode");
        var logs = string.Join("\n", s.Logs);
        Assert.Contains("GET /v1/canvas 200", logs);
        Assert.Contains("GET /v1/frames 200", logs);
        Assert.DoesNotContain("SecretName", logs);
        Assert.DoesNotContain("SecretNode", logs);
        Assert.DoesNotContain("node=", logs);
    }

    [Fact]
    public async Task ErrorBodies_NeverCarryAPathOrAnException()
    {
        Write("owner", "Mob/T.img", img => img.AddProperty(Cv("stand", 4, 4)));
        var s = await Start();
        s.App.Services.GetRequiredService<ViewerCanvasService>().Probe = _ => throw new InvalidOperationException("/etc/secret detail");
        var (status, body) = await GetJson(s, Url());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.DoesNotContain("/etc", body.GetRawText());
        Assert.DoesNotContain(_tree.Temp, body.GetRawText());
    }

    // ---- node and ETag helpers

    [Theory]
    [InlineData("stand", new[] { "stand" })]
    [InlineData("a/b/c", new[] { "a", "b", "c" })]
    [InlineData("a b/c.d_e-f", new[] { "a b", "c.d_e-f" })]
    public void ParseNode_Accepts(string node, string[] segments) => Assert.Equal(segments, ViewerCanvasService.ParseNode(node));

    [Fact]
    public void ParseMaxSide_Rules()
    {
        Assert.Equal(1024, ViewerCanvasService.ParseMaxSide(null, out var a));
        Assert.Equal("1024", a);
        Assert.Equal(1024, ViewerCanvasService.ParseMaxSide("", out _));
        Assert.Equal(4096, ViewerCanvasService.ParseMaxSide("full", out var b));
        Assert.Equal("full", b);
        Assert.Equal(400, Assert.Throws<ViewerApiException>(() => ViewerCanvasService.ParseMaxSide("512", out _)).Status);
    }

    [Fact]
    public void IfNoneMatches_Rules()
    {
        Assert.False(ViewerCanvasService.IfNoneMatches(null, "\"a\""));
        Assert.False(ViewerCanvasService.IfNoneMatches("", "\"a\""));
        Assert.False(ViewerCanvasService.IfNoneMatches("\"b\"", "\"a\""));
        Assert.False(ViewerCanvasService.IfNoneMatches("a", "\"a\""));   // not a quoted tag
        Assert.True(ViewerCanvasService.IfNoneMatches("\"a\"", "\"a\""));
        Assert.True(ViewerCanvasService.IfNoneMatches("\"b\" , \"a\"", "\"a\""));
        Assert.True(ViewerCanvasService.IfNoneMatches("W/\"a\"", "\"a\""));
        Assert.True(ViewerCanvasService.IfNoneMatches("*", "\"a\""));
    }

    [Fact]
    public void ETagFor_DependsOnEveryInput()
    {
        var stamp = new FileStamp(100, 200, 300, 400);
        var baseline = ViewerCanvasService.ETagFor("owner", "A.img", stamp, "n", "1024");
        Assert.Equal(baseline, ViewerCanvasService.ETagFor("owner", "A.img", stamp, "n", "1024"));
        var others = new[]
        {
            ViewerCanvasService.ETagFor("friend", "A.img", stamp, "n", "1024"),
            ViewerCanvasService.ETagFor("owner", "B.img", stamp, "n", "1024"),
            ViewerCanvasService.ETagFor("owner", "A.img", stamp with { Size = 101 }, "n", "1024"),
            ViewerCanvasService.ETagFor("owner", "A.img", stamp with { MtimeTicks = 201 }, "n", "1024"),
            ViewerCanvasService.ETagFor("owner", "A.img", stamp with { CtimeTicks = 301 }, "n", "1024"),
            ViewerCanvasService.ETagFor("owner", "A.img", stamp with { Inode = 401 }, "n", "1024"),
            ViewerCanvasService.ETagFor("owner", "A.img", stamp, "m", "1024"),
            ViewerCanvasService.ETagFor("owner", "A.img", stamp, "n", "full"),
        };
        Assert.Equal(others.Length, others.Distinct().Count());
        Assert.DoesNotContain(baseline, others);
        Assert.Matches("^\"[0-9a-f]{32}\"$", baseline);
    }
}
#endif
