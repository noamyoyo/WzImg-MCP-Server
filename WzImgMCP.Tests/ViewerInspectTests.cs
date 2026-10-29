#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;

namespace WzImgMCP.Tests;

/// <summary>
/// /v1/inspect over HTTP, the work runner (slots held until work really ends, decode cap) and the _outlink hook.
/// Shares the non-parallel collection because the resolver reads WZIMG_ALLOWED_ROOT.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerInspectTests : IAsyncLifetime
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

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task AssertTypedError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(code, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(_tree.Temp, body);   // never an absolute path
        Assert.DoesNotContain("Exception", body);
    }

    // ---- /v1/inspect

    [Fact]
    public async Task Inspect_ReportsTheImage_ThenServesItFromTheCache()
    {
        ImgFixture.Write(_tree.Root("owner"), "String/Fixture.img", WzMapleVersion.GMS);
        var s = await Start();

        var first = await Json(await s.Client.GetAsync("/v1/inspect?source=owner&path=String/Fixture.img"));
        Assert.Equal("owner", first.GetProperty("source").GetString());
        Assert.Equal("String/Fixture.img", first.GetProperty("path").GetString());
        Assert.Equal("GMS", first.GetProperty("keyUsed").GetString());
        Assert.Equal(new FileInfo(Path.Combine(_tree.Root("owner"), "img", "String", "Fixture.img")).Length, first.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(6, first.GetProperty("topLevelProperties").GetInt32());
        Assert.Equal(2, first.GetProperty("canvasCount").GetInt32());
        Assert.True(first.GetProperty("parseMillis").GetInt64() >= 0);
        Assert.False(first.GetProperty("cached").GetBoolean());

        var second = await Json(await s.Client.GetAsync("/v1/inspect?source=owner&path=String/Fixture.img"));
        Assert.True(second.GetProperty("cached").GetBoolean());
        Assert.Equal(first.GetProperty("parseMillis").GetInt64(), second.GetProperty("parseMillis").GetInt64());
    }

    [Fact]
    public async Task Inspect_NeedsTheToken()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var s = await Start();
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/inspect?source=owner&path=A.img");
        request.Headers.Remove(ViewerOptions.TokenHeader);
        using var bare = new HttpClient { BaseAddress = s.Client.BaseAddress };
        await AssertTypedError(await bare.SendAsync(request), HttpStatusCode.Unauthorized, "unauthorized");
        var post = await s.Client.PostAsync("/v1/inspect?source=owner&path=A.img", null);
        await AssertTypedError(post, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
    }

    [Fact]
    public async Task Inspect_Errors_AreTyped_AndNeverLeakPaths()
    {
        ImgFixture.Write(_tree.Root("owner"), "Ok.img");
        ImgFixture.Write(_tree.Root("owner"), "Custom.img", ImgFixture.Bytes(WzMapleVersion.BMS, customIv: new byte[] { 9, 9, 9, 9 }));
        ImgFixture.Write(_tree.Root("owner"), "Cut.img", ImgFixture.Bytes(WzMapleVersion.BMS).Take(60).ToArray());
        var s = await Start();

        await AssertTypedError(await s.Client.GetAsync("/v1/inspect?source=owner&path=Custom.img"), HttpStatusCode.UnprocessableEntity, "parse_failed");
        await AssertTypedError(await s.Client.GetAsync("/v1/inspect?source=owner&path=Cut.img"), HttpStatusCode.UnprocessableEntity, "parse_failed");
        await AssertTypedError(await s.Client.GetAsync("/v1/inspect?source=owner&path=Nope.img"), HttpStatusCode.NotFound, "not_found");
        await AssertTypedError(await s.Client.GetAsync("/v1/inspect?source=owner&path=../Ok.img"), HttpStatusCode.BadRequest, "bad_path");
        await AssertTypedError(await s.Client.GetAsync("/v1/inspect?source=other&path=Ok.img"), HttpStatusCode.BadRequest, "unknown_source");
    }

    [Fact]
    public async Task Inspect_TooLarge_Is413()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "Big.img");
        var s = await Start(_tree.Options(maxFileBytes: new FileInfo(path).Length - 1));
        await AssertTypedError(await s.Client.GetAsync("/v1/inspect?source=owner&path=Big.img"), HttpStatusCode.RequestEntityTooLarge, "too_large");
    }

    [Fact]
    public async Task Inspect_FileThatKeepsChanging_Is409()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var fs = new FakeFileSystem { Rewrite = (n, real) => real with { After = real.After with { Size = real.After.Size + n } } };
        var s = await Start(fs: fs);
        await AssertTypedError(await s.Client.GetAsync("/v1/inspect?source=owner&path=A.img"), HttpStatusCode.Conflict, "file_busy");
    }

    [Fact]
    public async Task Inspect_Log_HasNoPathOrQuery()
    {
        ImgFixture.Write(_tree.Root("owner"), "SecretName.img");
        var s = await Start();
        await s.Client.GetAsync("/v1/inspect?source=owner&path=SecretName.img");
        var logs = string.Join("\n", s.Logs);
        Assert.Contains("GET /v1/inspect 200", logs);
        Assert.DoesNotContain("SecretName", logs);
        Assert.DoesNotContain(_tree.Temp, logs);
    }

    [Fact]
    public async Task Inspect_TimedOutRequest_KeepsItsSlotUntilTheWorkEnds()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var s = await Start(_tree.Options(timeout: TimeSpan.FromMilliseconds(400), maxRequests: 1), fs);

        var timedOut = await s.Client.GetAsync("/v1/inspect?source=owner&path=A.img");   // the read blocks: cut off at 400 ms
        await AssertTypedError(timedOut, HttpStatusCode.GatewayTimeout, "timeout");
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(10)));

        // the answer is out, but the parse has not finished: its slot is still taken
        await AssertTypedError(await s.Client.GetAsync("/v1/resolve?source=owner&path=A.img"), HttpStatusCode.ServiceUnavailable, "busy");
        // health never needs a slot
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/v1/health")).StatusCode);

        gate.Set();   // the work finishes ...
        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode last;
        do
        {
            last = (await s.Client.GetAsync("/v1/resolve?source=owner&path=A.img")).StatusCode;
        }
        while (last == HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < deadline);
        Assert.Equal(HttpStatusCode.OK, last);   // ... and only then is the slot free again

        // the abandoned work saw the cancelled token right after the read and stopped before parsing: nothing was cached
        var again = await Json(await s.Client.GetAsync("/v1/inspect?source=owner&path=A.img"));
        Assert.False(again.GetProperty("cached").GetBoolean());
    }

    // ---- the work runner

    private ViewerWorkRunner Runner(int decodes, out ViewerPathResolver resolver)
    {
        var options = _tree.Options(maxDecodes: decodes);
        var fs = new PosixViewerFileSystem();
        resolver = new ViewerPathResolver(options.Roots, fs);
        return new ViewerWorkRunner(new ViewerDecodeLimiter(decodes), resolver, new ViewerImageLoader(options, fs, new ViewerImageCache(options.CacheBytes)));
    }

    [Fact]
    public async Task Runner_NeverRunsMoreWorkThanTheDecodeCap()
    {
        var runner = Runner(2, out _);
        var permits = new SemaphoreSlim(0);    // each permit lets one running work item finish
        var started = new SemaphoreSlim(0);
        int active = 0, max = 0;

        int Work(ViewerLinkContext _)
        {
            int now = Interlocked.Increment(ref active);
            int seen;
            while ((seen = Volatile.Read(ref max)) < now && Interlocked.CompareExchange(ref max, now, seen) != seen) { }
            started.Release();
            permits.Wait();
            Interlocked.Decrement(ref active);
            return 1;
        }

        var tasks = Enumerable.Range(0, 6).Select(_ => runner.RunAsync("owner", null, CancellationToken.None, Work)).ToList();

        // two start (and only two can); each later start needs a finish first, which the test grants one at a time
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, Volatile.Read(ref active));
        for (int finished = 0; finished < 4; finished++)
        {
            permits.Release();                                                    // one finishes ...
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));      // ... and exactly then the next one starts
        }
        permits.Release(2);                                                       // the last two
        await Task.WhenAll(tasks);

        Assert.Equal(2, Volatile.Read(ref max));   // two ran together, never a third
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task Runner_CancelledRequest_DoesNotFreeItsSlotsUntilTheWorkEnds()
    {
        var runner = Runner(1, out _);
        var requestLimiter = new ViewerRequestLimiter(1);
        var slot = requestLimiter.TryEnter();
        Assert.NotNull(slot);
        using var gate = new ManualResetEventSlim(false);
        var entered = new SemaphoreSlim(0);
        using var cts = new CancellationTokenSource();

        // work that cannot be cancelled (the library parse): it ignores the token
        int Stubborn(ViewerLinkContext _) { entered.Release(); gate.Wait(); return 1; }

        var first = runner.RunAsync("owner", slot, cts.Token, Stubborn);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);   // the waiting request is released at once ...

        slot!.ReleaseUnlessHeld();                  // ... what the pipeline does when the request ends
        Assert.Null(requestLimiter.TryEnter());     // but the request slot stays taken

        var ran = false;
        var second = runner.RunAsync("owner", null, CancellationToken.None, _ => { ran = true; return 2; });
        Assert.False(second.IsCompleted);
        Assert.False(ran);                           // the decode slot is still held by the stubborn work

        gate.Set();                                  // the work really ends ...
        Assert.Equal(2, await second);               // ... then the decode slot is free ...
        var deadline = DateTime.UtcNow.AddSeconds(30);
        ViewerRequestSlot? reacquired;
        while ((reacquired = requestLimiter.TryEnter()) == null && DateTime.UtcNow < deadline)
            await Task.Yield();
        Assert.NotNull(reacquired);                  // ... and the request slot too
    }

    [Fact]
    public async Task Runner_CooperativeWork_StopsEarlyOnCancel_AndFreesTheSlot()
    {
        var runner = Runner(1, out _);
        var requestLimiter = new ViewerRequestLimiter(1);
        var slot = requestLimiter.TryEnter();
        using var cts = new CancellationTokenSource();
        var entered = new SemaphoreSlim(0);
        var token = cts.Token;

        int Polite(ViewerLinkContext _)
        {
            entered.Release();
            while (true) { token.ThrowIfCancellationRequested(); Thread.SpinWait(1000); }
        }

        var task = runner.RunAsync("owner", slot, token, Polite);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        slot!.ReleaseUnlessHeld();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        ViewerRequestSlot? again;
        while ((again = requestLimiter.TryEnter()) == null && DateTime.UtcNow < deadline)
            await Task.Yield();
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Runner_CancelledWhileWaitingForADecodeSlot_NeverStartsTheWork()
    {
        var runner = Runner(1, out _);
        using var gate = new ManualResetEventSlim(false);
        var entered = new SemaphoreSlim(0);
        var holder = runner.RunAsync("owner", null, CancellationToken.None, _ => { entered.Release(); gate.Wait(); return 1; });
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)));

        using var cts = new CancellationTokenSource();
        var ran = false;
        var waiting = runner.RunAsync("owner", null, cts.Token, _ => { ran = true; return 2; });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.Set();
        await holder;
        Assert.False(ran);
    }

    [Fact]
    public async Task Runner_WorkException_ReleasesTheDecodeSlot()
    {
        var runner = Runner(1, out _);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync<int>("owner", null, CancellationToken.None, _ => throw new InvalidOperationException("boom")));
        Assert.Equal(5, await runner.RunAsync("owner", null, CancellationToken.None, _ => 5));
    }

    // ---- _outlink / _inlink through the hook

    private (ViewerImageLoader Loader, ViewerPathResolver Resolver) Services()
    {
        var options = _tree.Options();
        var fs = new PosixViewerFileSystem();
        return (new ViewerImageLoader(options, fs, new ViewerImageCache(options.CacheBytes)), new ViewerPathResolver(options.Roots, fs));
    }

    private static Action<WzImage> WithLink(string name, string outlink) => img =>
    {
        var canvas = ImgFixture.Canvas(name);
        canvas.AddProperty(new WzStringProperty("_outlink", outlink));
        img.AddProperty(canvas);
    };

    private static InspectResult Inspect(ViewerImageLoader loader, ViewerPathResolver resolver, string source, string path)
    {
        ViewerLinkContext.Install();
        using var context = new ViewerLinkContext(source, resolver, loader, CancellationToken.None);
        using var scope = context.Enter();
        return ViewerInspector.Inspect(loader, resolver.Resolve(source, path), context, CancellationToken.None);
    }

    [Fact]
    public void Outlink_ResolvesInsideTheSameSource()
    {
        ImgFixture.Write(_tree.Root("owner"), "Mob/B.img");
        ImgFixture.Write(_tree.Root("owner"), "Mob/A.img", WzMapleVersion.BMS, WithLink("link", "Mob/B.img/1"));
        var (loader, resolver) = Services();
        var result = Inspect(loader, resolver, "owner", "Mob/A.img");
        Assert.Equal(3, result.CanvasCount);
        Assert.Equal(new LinkSummary(0, 1, 1, 1), result.Links);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Outlink_NeverReachesAnotherSource()
    {
        // B exists in canon only: the owner's A must not find it, canon's own A does.
        ImgFixture.Write(_tree.Root("canon"), "Mob/B.img");
        ImgFixture.Write(_tree.Root("owner"), "Mob/A.img", WzMapleVersion.BMS, WithLink("link", "Mob/B.img/1"));
        ImgFixture.Write(_tree.Root("canon"), "Mob/A.img", WzMapleVersion.BMS, WithLink("link", "Mob/B.img/1"));
        var (loader, resolver) = Services();
        Assert.Equal(0, Inspect(loader, resolver, "owner", "Mob/A.img").Links.OutlinksResolved);
        Assert.Equal(1, Inspect(loader, resolver, "canon", "Mob/A.img").Links.OutlinksResolved);
    }

    [Fact]
    public void Outlink_WithTheSamePathInTwoSources_ReadsEachFromItsOwnRoot()
    {
        ImgFixture.Write(_tree.Root("canon"), "Mob/B.img", WzMapleVersion.BMS, img => ((WzIntProperty)img["marker"]).Value = 100);
        ImgFixture.Write(_tree.Root("owner"), "Mob/B.img", WzMapleVersion.BMS, img => ((WzIntProperty)img["marker"]).Value = 200);
        ImgFixture.Write(_tree.Root("owner"), "Mob/A.img", WzMapleVersion.BMS, WithLink("link", "Mob/B.img/1"));
        ImgFixture.Write(_tree.Root("canon"), "Mob/A.img", WzMapleVersion.BMS, WithLink("link", "Mob/B.img/1"));
        var (loader, resolver) = Services();
        ViewerLinkContext.Install();

        foreach (var (source, expected) in new[] { ("owner", 200), ("canon", 100) })
        {
            using var context = new ViewerLinkContext(source, resolver, loader, CancellationToken.None);
            using var scope = context.Enter();
            using var lease = loader.Load(resolver.Resolve(source, "Mob/A.img"), CancellationToken.None);
            var target = ((WzCanvasProperty)lease.Image["link"]).GetLinkedWzImageProperty();
            Assert.NotSame(lease.Image["link"], target);
            Assert.Equal(expected, ((WzIntProperty)target.ParentImage["marker"]).Value);
        }
    }

    [Theory]
    [InlineData("../canon/Mob/B.img/1")]
    [InlineData("../../Mob/B.img/1")]
    [InlineData("Backups/B.img/1")]
    [InlineData("/etc/passwd.img/1")]
    [InlineData("Mob\\..\\B.img/1")]
    [InlineData("Mob/Missing.img/1")]
    [InlineData("Mob/B.img/NoSuchChild")]
    [InlineData("Mob/.hidden.img/1")]
    public void Outlink_OutsideTheRules_StaysUnresolved(string outlink)
    {
        ImgFixture.Write(_tree.Root("owner"), "Mob/B.img");
        ImgFixture.Write(_tree.Root("canon"), "Mob/B.img");
        ImgFixture.Write(_tree.Root("owner"), "Backups/B.img");
        ImgFixture.Write(_tree.Root("owner"), "Mob/A.img", WzMapleVersion.BMS, WithLink("link", outlink));
        var (loader, resolver) = Services();
        var result = Inspect(loader, resolver, "owner", "Mob/A.img");
        Assert.Equal(1, result.Links.Outlinks);
        Assert.Equal(0, result.Links.OutlinksResolved);
    }

    [SkippableFact]
    public void Outlink_ThroughASymlinkInsideTheSource_StaysUnresolved()
    {
        ImgFixture.Write(_tree.Root("canon"), "Real/B.img");
        var link = Path.Combine(_tree.Root("owner"), "img", "Alias");
        EnvRequire.Capability(ImgFixture.TrySymlink(link, Path.Combine(_tree.Root("canon"), "img", "Real"), directory: true), "symbolic links cannot be created here");
        ImgFixture.Write(_tree.Root("owner"), "A.img", WzMapleVersion.BMS, WithLink("link", "Alias/B.img/1"));
        var (loader, resolver) = Services();
        Assert.Equal(0, Inspect(loader, resolver, "owner", "A.img").Links.OutlinksResolved);
    }

    [Fact]
    public void Outlink_TargetIsLoadedOnce_AndBoundedPerRequest()
    {
        ImgFixture.Write(_tree.Root("owner"), "Mob/B.img");
        ImgFixture.Write(_tree.Root("owner"), "Mob/A.img", WzMapleVersion.BMS, img =>
        {
            for (int i = 0; i < 10; i++) WithLink("l" + i, "Mob/B.img/1")(img);
        });
        var fs = new FakeFileSystem();
        var options = _tree.Options();
        var loader = new ViewerImageLoader(options, fs, new ViewerImageCache(options.CacheBytes));
        var resolver = new ViewerPathResolver(options.Roots, fs);
        var result = Inspect(loader, resolver, "owner", "Mob/A.img");
        Assert.Equal(10, result.Links.OutlinksResolved);
        Assert.Equal(2, fs.Reads);   // A and B, once each
    }

    [Fact]
    public void Outlink_MoreThanEightTargets_StayUnresolvedWithAWarning()
    {
        for (int i = 0; i < 10; i++)
            ImgFixture.Write(_tree.Root("owner"), $"Mob/T{i}.img");
        ImgFixture.Write(_tree.Root("owner"), "Mob/A.img", WzMapleVersion.BMS, img =>
        {
            for (int i = 0; i < 10; i++) WithLink("l" + i, $"Mob/T{i}.img/1")(img);
        });
        var (loader, resolver) = Services();
        var result = Inspect(loader, resolver, "owner", "Mob/A.img");
        Assert.Equal(ViewerLinkContext.MaxLinkedImages, result.Links.OutlinksResolved);
        Assert.Equal(10, result.Links.Outlinks);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("2 link(s)", warning);
    }

    [Fact]
    public void Outlink_ByteBudget_LeavesTheLinkUnresolved_WithoutThrowing()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "Mob/B.img");
        var size = new FileInfo(path).Length;
        var (loader, resolver) = Services();

        using (var tight = new ViewerLinkContext("owner", resolver, loader, CancellationToken.None, maxBytes: size - 1))
        {
            Assert.Null(tight.LoadLinked("Mob/B.img"));
            Assert.Equal(1, tight.SkippedByBudget);
        }
        using (var enough = new ViewerLinkContext("owner", resolver, loader, CancellationToken.None, maxBytes: size))
        {
            Assert.NotNull(enough.LoadLinked("Mob/B.img"));
            Assert.Equal(0, enough.SkippedByBudget);
            ImgFixture.Write(_tree.Root("owner"), "Mob/C.img");
            Assert.Null(enough.LoadLinked("Mob/C.img"));   // the budget is used up by B
            Assert.Equal(1, enough.SkippedByBudget);
            Assert.NotNull(enough.LoadLinked("Mob/B.img"));   // B again costs nothing
        }
    }

    [Fact]
    public void Hook_WithoutARequest_AnswersNull()
    {
        ViewerLinkContext.Install();
        Assert.Null(ViewerLinkContext.Current);
        Assert.NotNull(WzCanvasProperty.ExternalImageResolver);
        Assert.Null(WzCanvasProperty.ExternalImageResolver("Mob/B.img"));
    }

    [Fact]
    public async Task Hook_ContextDoesNotLeakIntoOtherFlows()
    {
        var (loader, resolver) = Services();
        using var context = new ViewerLinkContext("owner", resolver, loader, CancellationToken.None);
        using (context.Enter())
        {
            Assert.Same(context, ViewerLinkContext.Current);
            Assert.Same(context, await Task.Run(() => ViewerLinkContext.Current));   // flows into the work it starts
        }
        Assert.Null(ViewerLinkContext.Current);
        Assert.Null(await Task.Run(() => ViewerLinkContext.Current));
    }

    [Fact]
    public void Inlink_ResolvesWithinTheImage_WithoutTheHook()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img", WzMapleVersion.BMS, img =>
        {
            var c = ImgFixture.Canvas("viaInlink");
            c.AddProperty(new WzStringProperty("_inlink", "0"));
            img.AddProperty(c);
        });
        var (loader, resolver) = Services();
        var result = Inspect(loader, resolver, "owner", "A.img");
        Assert.Equal(1, result.Links.Inlinks);
        Assert.Equal(0, result.Links.Outlinks);
    }
}
#endif
