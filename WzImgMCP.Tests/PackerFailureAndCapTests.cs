#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>Unsafe and missing input, the caps, and one pack at a time. Synthetic data only.</summary>
[Collection("AllowedRootEnv")]
public sealed class PackerFailureAndCapTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly PackerFixture _fx = new();
    private readonly List<PackerTestServer> _servers = new();

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _fx.BuildPublished();
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
    private PackerOptions Capped(int? maxFiles = null, long? maxInput = null, long? maxWz = null, long? maxImage = null, TimeSpan? budget = null)
        => new(PackerFixture.Token, _fx.Roots, _fx.Outbox, _fx.Published)
        {
            TempRoot = _fx.TempRoot,
            MaxFiles = maxFiles ?? PackerOptions.DefaultMaxFiles,
            MaxInputBytes = maxInput ?? PackerOptions.DefaultMaxInputMb * 1024L * 1024L,
            MaxWzBytes = maxWz ?? PackerOptions.DefaultMaxWzMb * 1024L * 1024L,
            MaxImageBytes = maxImage ?? PackerOptions.DefaultMaxImageMb * 1024L * 1024L,
            TimeBudget = budget ?? TimeSpan.FromSeconds(PackerOptions.DefaultTimeBudgetSeconds),
        };

    private async Task<PackerTestServer> StartCapped(PackerOptions options)
    {
        var server = await PackerTestServer.StartAsync(options);
        _servers.Add(server);
        return server;
    }

    private static string Code(JsonElement job) => job.GetProperty("error").GetProperty("code").GetString()!;

    private static async Task<(HttpStatusCode Status, string Code)> Refused(HttpResponseMessage response)
        => (response.StatusCode, PackerTestServer.ErrorCode(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement));

    private void AssertNothingLeftButTheRecord(JsonElement job)
    {
        var dir = Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!);
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.False(job.TryGetProperty("wz", out _));
    }

    // ---- request-time problems: nothing is made

    [Fact]
    public async Task ACategoryWithNoPublishedFile_Is404_AndCategoryNamesAreCaseSensitive()
    {
        var s = await Start();
        _fx.WriteImg("owner", "String/Pet.img", "x");
        foreach (var category in new[] { "NoSuchCategory", "string" })
        {
            var response = await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Remove(category + "/Pet.img") }, category));
            Assert.Equal((HttpStatusCode.NotFound, "not_found"), await Refused(response));
        }
        Assert.Empty(Directory.GetFileSystemEntries(_fx.Outbox));   // no job was made
    }

    [Fact]
    public async Task ASetWhoseSourceFileIsMissing_Is404_AndAPersonWithoutAnImgFolder_Is503()
    {
        var s = await Start();
        var response = await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Missing.img") }));
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), await Refused(response));

        // the friend's img folder is empty but there: a missing file is a missing file; with no img folder at all the source is unavailable
        response = await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Pet.img", "friend") }));
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), await Refused(response));
        Directory.Delete(_fx.Img("friend"));
        response = await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Pet.img", "friend") }));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "source_unavailable"), await Refused(response));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.Outbox));
    }

    [Fact]
    public async Task ARemove_NeedsNoSourceFile_EvenWhenThePersonHasNone()
    {
        var s = await Start();
        Directory.Delete(_fx.Img("friend"));
        var job = await s.PackAndWait(PackerTestServer.Remove("String/Cash.img", "friend"));
        Assert.Equal("done", job.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ASymbolicLinkInThePathOfASource_Is400_AndNothingIsMade()
    {
        var s = await Start();
        var elsewhere = Path.Combine(_fx.Temp, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "Sub"));
        File.WriteAllBytes(Path.Combine(elsewhere, "Pet.img"), SyntheticWz.ImgBytes(SyntheticWz.Content("LINKED")));
        File.WriteAllBytes(Path.Combine(elsewhere, "Sub", "Deep.img"), SyntheticWz.ImgBytes(SyntheticWz.Content("LINKED")));
        void Link(string link, string target, bool dir) => EnvRequire.Capability(ImgFixture.TrySymlink(link, target, dir), "symbolic links cannot be created here");

        // the file itself, a folder on the way, and the person's img folder
        Directory.CreateDirectory(Path.Combine(_fx.Img("friend"), "String"));
        Link(Path.Combine(_fx.Img("friend"), "String", "Pet.img"), Path.Combine(elsewhere, "Pet.img"), false);
        Link(Path.Combine(_fx.Img("friend"), "String", "Sub"), Path.Combine(elsewhere, "Sub"), true);
        foreach (var img in new[] { "String/Pet.img", "String/Sub/Deep.img" })
            Assert.Equal((HttpStatusCode.BadRequest, "bad_path"), await Refused(await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set(img, "friend") }))));

        Directory.Delete(Path.Combine(_fx.Root("friend"), "img"), true);
        Link(Path.Combine(_fx.Root("friend"), "img"), elsewhere, true);
        Assert.Equal((HttpStatusCode.BadRequest, "bad_path"), await Refused(await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Pet.img", "friend") }))));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.Outbox));
    }

    [Fact]
    public async Task ASymbolicLinkAsThePublishedFile_Is400()
    {
        var s = await Start();
        var elsewhere = Path.Combine(_fx.Temp, "elsewhere-wz");
        File.Move(_fx.PublishedWz(), elsewhere);
        EnvRequire.Capability(ImgFixture.TrySymlink(_fx.PublishedWz(), elsewhere, false), "symbolic links cannot be created here");
        Assert.Equal((HttpStatusCode.BadRequest, "bad_path"), await Refused(await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Remove("String/Cash.img") }))));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.Outbox));
    }

    // ---- a source that goes bad after the request was accepted: the job fails, nothing but the record is left

    [Fact]
    public async Task AFifoAsASource_FailsTheJob_AndDoesNotHang()
    {
        var fifo = Path.Combine(_fx.Img("owner"), "String", "Pet.img");
        File.Delete(fifo);
        EnvRequire.Capability(ImgFixture.TryMakeFifo(fifo), "a FIFO cannot be created here");
        var s = await Start();
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("bad_path", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task ASourceThatVanishesAfterTheRequest_FailsTheJob()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await Start();
        s.Service.Probe = (stage, _) => { if (stage == "start") File.Delete(Path.Combine(_fx.Img("owner"), "String", "Pet.img")); };
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("not_found", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task AFileRewrittenAfterTheRequest_IsPackedAsItIsWhenRead_AndThatIsWhatIsRecorded()
    {
        // the file is read once, as it is then; the snapshot holds those bytes and the record holds their hash
        _fx.WriteImg("owner", "String/Pet.img", "BEFORE");
        var s = await Start();
        s.Service.Probe = (stage, _) =>
        {
            if (stage == "start") _fx.WriteImg("owner", "String/Pet.img", "AFTER");
        };
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("done", job.GetProperty("state").GetString());
        var recorded = job.GetProperty("files")[0].GetProperty("sha256").GetString();
        Assert.Equal(SyntheticWz.Sha256(Path.Combine(_fx.Img("owner"), "String", "Pet.img")), recorded);   // what was read is what is recorded
        var (_, verify) = await s.Verify(job.GetProperty("jobId").GetString()!);
        Assert.True(verify.GetProperty("identical").GetBoolean(), verify.ToString());
    }

    // ---- caps

    [Fact]
    public async Task TooMuchInput_FailsTheJob()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await StartCapped(Capped(maxInput: 100));
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("input_too_large", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task OneImageLargerThanTheImageCap_FailsTheJob()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await StartCapped(Capped(maxImage: 100));
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("image_too_large", Code(job));
        Assert.Contains("String/Pet.img", job.GetProperty("error").GetProperty("message").GetString());
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task APublishedFileLargerThanTheWzCap_FailsTheJob()
    {
        var s = await StartCapped(Capped(maxWz: 100));
        var job = await s.PackAndWait(PackerTestServer.Remove("String/Cash.img"));
        Assert.Equal("output_too_large", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task AnOutputLargerThanTheWzCap_FailsTheJob_AndIsNotLeftInTheOutbox()
    {
        var rnd = new Random(7);
        _fx.WriteImg("owner", "String/Pet.img", "BIG", img => img.AddProperty(ImgFixture.PixelCanvas("noise", 200, 200,
            (x, y) => ((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255))));   // does not compress
        var s = await StartCapped(Capped(maxWz: new FileInfo(_fx.PublishedWz()).Length + 1000));
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("output_too_large", Code(job));
        AssertNothingLeftButTheRecord(job);
    }

    [Fact]
    public async Task ACapOnTheWz_JustAboveItsSize_StillPacks()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var probe = await Start();
        var size = (await probe.PackAndWait(PackerTestServer.Set("String/Pet.img"))).GetProperty("wz").GetProperty("sizeBytes").GetInt64();

        var s = await StartCapped(Capped(maxWz: Math.Max(size, new FileInfo(_fx.PublishedWz()).Length)));   // the cap holds for the published file that is opened, too
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.True(job.GetProperty("state").GetString() == "done", job.ToString());
    }

    [Fact]
    public async Task TheTimeBudget_StopsThePack_AndFreesTheSlot()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await StartCapped(Capped(budget: TimeSpan.Zero));
        var job = await s.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("timeout", Code(job));
        AssertNothingLeftButTheRecord(job);
        Assert.False(s.Gate.Busy);
    }

    [Fact]
    public async Task TheTimeBudget_StopsAVerify_WithATyped504_AndLeavesNoTempFolder()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var packer = await Start();
        var id = (await packer.PackAndWait(PackerTestServer.Set("String/Pet.img"))).GetProperty("jobId").GetString()!;

        var s = await StartCapped(Capped(budget: TimeSpan.Zero));
        var (status, body) = await s.Verify(id);

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Equal("timeout", PackerTestServer.ErrorCode(body));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.TempRoot));
        Assert.False(s.Gate.Busy);
    }

    // ---- one at a time

    [Fact]
    public async Task OnePackAtATime_TheNextGets503Busy_AndHealthAndJobsStillAnswer()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await Start();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        s.Service.Probe = (stage, _) =>
        {
            if (stage == "packing") { entered.Set(); release.Wait(TimeSpan.FromSeconds(60)); }
        };

        var first = await s.StartPack(PackerTestServer.Set("String/Pet.img"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the first pack did not reach the library");

        // a second pack, and a verify, are turned away while the first runs
        var second = await s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Pet.img") }));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "busy"), await Refused(second));
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
        Assert.Equal("done", (await s.PackAndWait(PackerTestServer.Set("String/Pet.img"))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task ManyPacksAtOnce_ExactlyOneIsAccepted()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await Start();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        s.Service.Probe = (stage, _) => { if (stage == "packing") { entered.Set(); release.Wait(TimeSpan.FromSeconds(60)); } };

        // the first request wins the slot and is held; the others are sent while it is held
        var winner = await s.StartPack(PackerTestServer.Set("String/Pet.img"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        var others = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Pet.img") }))));
        Assert.All(others, r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode));
        release.Set();
        await s.WaitForJob(winner);

        // only one job folder exists
        Assert.Single(Directory.GetDirectories(_fx.Outbox));
    }

    [Fact]
    public async Task SimultaneousRequests_AtMostOneGetsTheSlot()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await Start();
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => s.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Pet.img") }))));
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
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var s = await Start();
        Assert.Equal("failed", (await s.PackAndWait(PackerTestServer.Remove("String/Nothing.img"))).GetProperty("state").GetString());
        Assert.False(s.Gate.Busy);
        Assert.Equal("done", (await s.PackAndWait(PackerTestServer.Set("String/Pet.img"))).GetProperty("state").GetString());
    }
}
#endif
