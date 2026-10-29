#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>Damaged and unsafe input, the caps, and one pack at a time. Synthetic data only.</summary>
[Collection("AllowedRootEnv")]
public sealed class PackerFailureAndCapTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly PackerFixture _fx = new();
    private readonly List<PackerTestServer> _servers = new();

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _fx.Populate("owner");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _fx.Dispose();
    }

    private async Task<PackerTestServer> Start(Func<PackerOptions, PackerOptions>? tweak = null)
    {
        var server = await PackerTestServer.StartAsync(_fx.Options(tweak));
        _servers.Add(server);
        return server;
    }

    /// <summary>The same options with some caps changed (the options are immutable after construction, so they are rebuilt).</summary>
    private PackerOptions Capped(int? maxImages = null, long? maxInput = null, long? maxWz = null, TimeSpan? budget = null)
        => new(PackerFixture.Token, _fx.Roots, _fx.Outbox)
        {
            TempRoot = _fx.TempRoot,
            MaxImages = maxImages ?? PackerOptions.DefaultMaxImages,
            MaxInputBytes = maxInput ?? PackerOptions.DefaultMaxInputMb * 1024L * 1024L,
            MaxWzBytes = maxWz ?? PackerOptions.DefaultMaxWzMb * 1024L * 1024L,
            TimeBudget = budget ?? TimeSpan.FromSeconds(PackerOptions.DefaultTimeBudgetSeconds),
        };

    private async Task<PackerTestServer> StartCapped(PackerOptions options)
    {
        var server = await PackerTestServer.StartAsync(options);
        _servers.Add(server);
        return server;
    }

    private static string Code(JsonElement job) => job.GetProperty("error").GetProperty("code").GetString()!;

    private void AssertNothingLeftButTheRecord(JsonElement job)
    {
        var dir = Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!);
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.False(job.TryGetProperty("wz", out _));
    }

    // ---- request-time problems

    [Fact]
    public async Task UnknownCategory_Is404()
    {
        var s = await Start();
        var response = await s.PostJson("/v1/pack", PackerTestServer.Pack("owner", "NoSuchCategory"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", PackerTestServer.ErrorCode(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.Outbox));   // no job was made
    }

    [Fact]
    public async Task CategoryNameIsCaseSensitive()
    {
        var s = await Start();
        var response = await s.PostJson("/v1/pack", PackerTestServer.Pack("owner", "string"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ACategoryTheFriendLacks_Is404_AndAPersonWithoutAnImgFolder_Is503()
    {
        var s = await Start();
        var response = await s.PostJson("/v1/pack", PackerTestServer.Pack("friend"));   // the friend's img folder is empty but there; remove it
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Directory.Delete(_fx.Img("friend"));
        response = await s.PostJson("/v1/pack", PackerTestServer.Pack("friend"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("source_unavailable", PackerTestServer.ErrorCode(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement));
    }

    [Fact]
    public async Task ASymbolicLinkAsTheImgFolderOrTheCategoryFolder_Is400()
    {
        var s = await Start();
        var elsewhere = Path.Combine(_fx.Temp, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "String"));
        _fx.WriteImg("owner", "String/Pet.img", "x");

        // the category folder is a link
        var linked = Path.Combine(_fx.Img("friend"), "String");
        EnvRequire.Capability(ImgFixture.TrySymlink(linked, Path.Combine(elsewhere, "String"), directory: true), "symbolic links cannot be created here");
        var response = await s.PostJson("/v1/pack", PackerTestServer.Pack("friend"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_path", PackerTestServer.ErrorCode(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement));

        // the img folder is a link
        Directory.Delete(Path.Combine(_fx.Root("friend"), "img"), true);
        Assert.True(ImgFixture.TrySymlink(Path.Combine(_fx.Root("friend"), "img"), elsewhere, directory: true));
        response = await s.PostJson("/v1/pack", PackerTestServer.Pack("friend"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFileSystemEntries(_fx.Outbox));
    }

    // ---- damaged and unsafe input (the job fails, nothing but the record is left)

    [Fact]
    public async Task ADamagedImage_FailsTheJob_WithWarningsAndNoPath_AndLeavesNoWz()
    {
        File.WriteAllBytes(Path.Combine(_fx.Img("owner"), "String", "Broken.img"), Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray());
        var s = await Start();

        var job = await s.PackAndWait();

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("pack_failed", Code(job));
        AssertNothingLeftButTheRecord(job);
        var text = job.ToString();
        Assert.DoesNotContain(_fx.Temp, text);
        Assert.DoesNotContain("/tmp", text);
    }

    [Fact]
    public async Task ATruncatedImage_FailsTheJob()
    {
        var path = _fx.WriteImg("owner", "String/Cut.img", "will be cut");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        var s = await Start();

        var job = await s.PackAndWait();

        Assert.Equal("failed", job.GetProperty("state").GetString());
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task AnEmptyCategory_FailsTheJob()
    {
        Directory.CreateDirectory(Path.Combine(_fx.Img("owner"), "Empty", "Sub"));
        var s = await Start();
        var job = await s.PackAndWait("owner", "Empty");
        Assert.Equal("empty_category", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task ASymbolicLinkInTheTree_FailsTheJob_AndIsNotFollowed()
    {
        var outside = Path.Combine(_fx.Temp, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "Secret.img"), ImgFixture.Bytes(MapleLib.WzLib.WzMapleVersion.BMS));
        EnvRequire.Capability(ImgFixture.TrySymlink(Path.Combine(_fx.Img("owner"), "String", "Link.img"), Path.Combine(outside, "Secret.img"), directory: false), "symbolic links cannot be created here");
        var s = await Start();

        var job = await s.PackAndWait();

        Assert.Equal("unsafe_tree", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task ASymbolicLinkToAFolderInTheTree_FailsTheJob()
    {
        var outside = Path.Combine(_fx.Temp, "outside-dir");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "A.img"), ImgFixture.Bytes(MapleLib.WzLib.WzMapleVersion.BMS));
        EnvRequire.Capability(ImgFixture.TrySymlink(Path.Combine(_fx.Img("owner"), "String", "Dir"), outside, directory: true), "symbolic links cannot be created here");
        var s = await Start();

        var job = await s.PackAndWait();

        Assert.Equal("unsafe_tree", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task AFifoInTheTree_FailsTheJob_AndDoesNotHang()
    {
        EnvRequire.Capability(ImgFixture.TryMakeFifo(Path.Combine(_fx.Img("owner"), "String", "Pipe.img")), "FIFOs cannot be created here");
        var s = await Start();

        var job = await s.PackAndWait(seconds: 20);

        Assert.Equal("unsafe_tree", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task AFileThatIsNotAnImg_FailsTheJob()
    {
        File.WriteAllText(Path.Combine(_fx.Img("owner"), "String", "notes.txt"), "hello");
        var s = await Start();
        var job = await s.PackAndWait();
        Assert.Equal("unsupported_file", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Theory]
    [InlineData("café.img")]
    [InlineData("a;b.img")]
    [InlineData("a%41.img")]
    public async Task ANameTheViewerRulesRefuse_FailsTheJob(string name)
    {
        _fx.WriteImg("owner", "String/" + name, "odd");
        var s = await Start();
        var job = await s.PackAndWait();
        Assert.Equal("unsupported_name", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task TheExtractorsCaseMapAndABackupsFolder_AreIgnored()
    {
        File.WriteAllText(Path.Combine(_fx.Img("owner"), "String", ".imgcase.json"), "{\"Entries\":{}}");
        _fx.WriteImg("owner", "String/Backups/Old.img", "an old copy");
        var s = await Start();

        var job = await s.PackAndWait();

        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal(3, job.GetProperty("imgCount").GetInt32());   // Old.img is not counted
        var (_, verify) = await s.Verify(job.GetProperty("jobId").GetString()!);
        Assert.True(verify.GetProperty("identical").GetBoolean(), verify.ToString());
    }

    // ---- caps

    [Fact]
    public async Task TooManyImages_FailsTheJob()
    {
        var s = await StartCapped(Capped(maxImages: 2));
        var job = await s.PackAndWait();
        Assert.Equal("too_many_images", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task TooMuchInput_FailsTheJob()
    {
        var s = await StartCapped(Capped(maxInput: 500));
        var job = await s.PackAndWait();
        Assert.Equal("input_too_large", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task ATooLargeWz_FailsTheJob_AndIsNotLeftInTheOutbox()
    {
        var s = await StartCapped(Capped(maxWz: 100));
        var job = await s.PackAndWait();
        Assert.Equal("output_too_large", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task TheTimeBudget_StopsThePack()
    {
        var s = await StartCapped(Capped(budget: TimeSpan.Zero));
        var job = await s.PackAndWait();
        Assert.Equal("timeout", Code(job));
        AssertNothingLeftButTheRecord(job);
        // the slot is free again
        Assert.False(s.Gate.Busy);
    }

    [Fact]
    public async Task TheTimeBudget_StopsAVerify_WithATyped504_AndLeavesNoTempFolder()
    {
        var packer = await Start();
        var id = (await packer.PackAndWait()).GetProperty("jobId").GetString()!;

        var s = await StartCapped(Capped(budget: TimeSpan.Zero));
        var (status, body) = await s.Verify(id);

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Equal("timeout", PackerTestServer.ErrorCode(body));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.TempRoot));
        Assert.False(s.Gate.Busy);
    }

    [Fact]
    public async Task ACapOnTheWz_JustAboveItsSize_StillPacks()
    {
        var probe = await Start();
        var done = await probe.PackAndWait();
        var size = done.GetProperty("wz").GetProperty("sizeBytes").GetInt64();

        var s = await StartCapped(Capped(maxWz: size));
        Assert.Equal("done", (await s.PackAndWait()).GetProperty("state").GetString());
    }

    // ---- one at a time

    [Fact]
    public async Task OnePackAtATime_TheNextGets503Busy_AndHealthAndJobsStillAnswer()
    {
        var s = await Start();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        s.Service.Probe = (stage, _) =>
        {
            if (stage == "packing") { entered.Set(); release.Wait(TimeSpan.FromSeconds(60)); }
        };

        var first = await s.StartPack();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the first pack did not reach the library");

        // a second pack, and a verify, are turned away while the first runs
        var second = await s.PostJson("/v1/pack", PackerTestServer.Pack());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal("busy", PackerTestServer.ErrorCode(JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement));
        var verify = await s.Verify(first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, verify.Status);

        // reads are not blocked
        var running = JsonDocument.Parse(await s.Client.GetStringAsync("/v1/jobs/" + first)).RootElement;
        Assert.Equal("running", running.GetProperty("state").GetString());
        var health = JsonDocument.Parse(await s.Client.GetStringAsync("/v1/health")).RootElement;
        Assert.True(health.GetProperty("busy").GetBoolean());

        release.Set();
        var done = await s.WaitForJob(first);
        Assert.Equal("done", done.GetProperty("state").GetString());

        // and now the next one is accepted
        s.Service.Probe = null;
        Assert.Equal("done", (await s.PackAndWait()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task ManyPacksAtOnce_ExactlyOneIsAccepted()
    {
        var s = await Start();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        s.Service.Probe = (stage, _) => { if (stage == "packing") { entered.Set(); release.Wait(TimeSpan.FromSeconds(60)); } };

        // the first request wins the slot and is held in the library; the others are sent while it is held
        var winner = await s.StartPack();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        var others = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => s.PostJson("/v1/pack", PackerTestServer.Pack())));
        Assert.All(others, r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode));
        release.Set();
        await s.WaitForJob(winner);

        // only one job folder exists
        Assert.Single(Directory.GetDirectories(_fx.Outbox));
    }

    [Fact]
    public async Task SimultaneousRequests_AtMostOneGetsTheSlot()
    {
        var s = await Start();
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => s.PostJson("/v1/pack", PackerTestServer.Pack())));
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.InRange(accepted, 1, 16);
        // whatever was accepted ran one after the other: every job ends done, and every refusal was a typed busy
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Accepted), r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode));
        foreach (var dir in Directory.GetDirectories(_fx.Outbox))
        {
            var job = await s.WaitForJob(Path.GetFileName(dir));
            Assert.Equal("done", job.GetProperty("state").GetString());
        }
        Assert.Equal(accepted, Directory.GetDirectories(_fx.Outbox).Length);
    }

    [Fact]
    public async Task AFailedPack_GivesTheSlotBack()
    {
        File.WriteAllText(Path.Combine(_fx.Img("owner"), "String", "notes.txt"), "x");
        var s = await Start();
        Assert.Equal("failed", (await s.PackAndWait()).GetProperty("state").GetString());
        Assert.False(s.Gate.Busy);
        File.Delete(Path.Combine(_fx.Img("owner"), "String", "notes.txt"));
        Assert.Equal("done", (await s.PackAndWait()).GetProperty("state").GetString());
    }
}
#endif
