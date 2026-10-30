#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// The packer writes only below its outbox (and a temp folder of its own for verify), never through a symbolic link, and
/// leaves the three source trees as they were. A link is planted while a job runs, through the test seam on the service.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackerConfinementTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly PackerFixture _fx = new();
    private readonly string _outside;
    private PackerTestServer _server = null!;

    public PackerConfinementTests()
    {
        _outside = Path.Combine(_fx.Temp, "outside");
        Directory.CreateDirectory(_outside);
    }

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _fx.BuildPublished();
        _fx.Populate("owner");
        _fx.Populate("friend");
        _fx.Populate("canon");
        _server = await PackerTestServer.StartAsync(_fx.Options());
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _fx.Dispose();
    }

    private string JobDir(string stageJobId) => Path.Combine(_fx.Outbox, stageJobId);

    private Task<JsonElement> Pack(PackerTestServer? on = null) => (on ?? _server).PackAndWait(PackerTestServer.Set("String/Pet.img"), PackerTestServer.Remove("String/Cash.img"));

    private static void Link(string link, string target, bool directory)
        => EnvRequire.Capability(ImgFixture.TrySymlink(link, target, directory), "symbolic links cannot be created here");

    [Fact]
    public async Task PackAndVerify_WriteOnlyInTheOutbox_AndLeaveTheTreesAndThePublishedFolderAsTheyWere()
    {
        var trees = _fx.SnapshotRoots("canon", "owner", "friend");
        var published = PackerFixture.Snapshot(_fx.Published);
        var job = await Pack();
        var id = job.GetProperty("jobId").GetString()!;
        var (_, verify) = await _server.Verify(id);
        Assert.True(verify.GetProperty("identical").GetBoolean());

        Assert.Equal(trees, _fx.SnapshotRoots("canon", "owner", "friend"));   // the sources are untouched
        Assert.Equal(published, PackerFixture.Snapshot(_fx.Published));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.TempRoot));                           // verify removed its folder
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
        var top = Directory.GetFileSystemEntries(_fx.Outbox).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { id }, top);                                                      // one folder per job, nothing next to it
        Assert.Equal(new[] { "String.wz", "input", "job.json" }, Directory.GetFileSystemEntries(JobDir(id)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(SyntheticWz.Sha256(_fx.PublishedWz()), job.GetProperty("published").GetProperty("sha256").GetString());   // the published folder is only read
        Assert.Equal(new[] { "String.wz" }, Directory.GetFileSystemEntries(_fx.Published).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task EachJobGetsItsOwnFolder_AndAJobFolderIsNeverReused()
    {
        var a = (await Pack()).GetProperty("jobId").GetString()!;
        var b = (await Pack()).GetProperty("jobId").GetString()!;
        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.Equal(2, Directory.GetDirectories(_fx.Outbox).Length);
    }

    [Fact]
    public async Task ALinkPlantedAsTheWorkFolder_IsRefused_AndNothingIsWrittenThroughIt()
    {
        var target = Path.Combine(_outside, "work-target");
        Directory.CreateDirectory(target);
        _server.Service.Probe = (stage, id) => { if (stage == "start") Link(Path.Combine(JobDir(id), ".work"), target, directory: true); };

        var job = await Pack();

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("outbox_unsafe", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.False(new DirectoryInfo(Path.Combine(JobDir(job.GetProperty("jobId").GetString()!), ".work")).Exists && new DirectoryInfo(Path.Combine(JobDir(job.GetProperty("jobId").GetString()!), ".work")).LinkTarget != null,
            "the planted link is removed with the work folder");
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task ALinkPlantedAsTheFinalWz_ToAnExistingFile_IsNotWrittenThrough()
    {
        var victim = Path.Combine(_outside, "victim.bin");
        File.WriteAllText(victim, "KEEP");
        _server.Service.Probe = (stage, id) => { if (stage == "packing") Link(Path.Combine(JobDir(id), "String.wz"), victim, directory: false); };

        var job = await Pack();

        Assert.Equal("KEEP", File.ReadAllText(victim));
        Assert.Equal("failed", job.GetProperty("state").GetString());   // the move refuses to replace what is there
    }

    [Fact]
    public async Task ALinkPlantedAsTheFinalWz_ToAMissingFile_DoesNotCreateIt()
    {
        var missing = Path.Combine(_outside, "not-yet.bin");
        _server.Service.Probe = (stage, id) => { if (stage == "packing") Link(Path.Combine(JobDir(id), "String.wz"), missing, directory: false); };

        var job = await Pack();

        Assert.False(File.Exists(missing));
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
        // the move refuses to replace what is there (a link counts), so the job fails and the link is still a link to nothing
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.False(job.TryGetProperty("wz", out _));
    }

    [Fact]
    public async Task ALinkPlantedAsJobJson_AndItsTempFile_IsReplacedNotWrittenThrough()
    {
        var victimA = Path.Combine(_outside, "a.bin");
        var victimB = Path.Combine(_outside, "b.bin");
        File.WriteAllText(victimA, "KEEP-A");
        File.WriteAllText(victimB, "KEEP-B");
        _server.Service.Probe = (stage, id) =>
        {
            if (stage != "start") return;
            File.Delete(Path.Combine(JobDir(id), "job.json"));
            Link(Path.Combine(JobDir(id), "job.json"), victimA, directory: false);
            Link(Path.Combine(JobDir(id), "job.json.tmp"), victimB, directory: false);
        };

        var job = await Pack();

        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal("KEEP-A", File.ReadAllText(victimA));
        Assert.Equal("KEEP-B", File.ReadAllText(victimB));
        var record = Path.Combine(JobDir(job.GetProperty("jobId").GetString()!), "job.json");
        Assert.Null(new FileInfo(record).LinkTarget);   // a real file again
    }

    [Fact]
    public async Task AJobFolderReplacedByALink_IsNotFollowed_ByReadsOrVerify()
    {
        var job = await Pack();
        var id = job.GetProperty("jobId").GetString()!;
        var real = JobDir(id);
        var moved = Path.Combine(_outside, "moved-job");
        Directory.Move(real, moved);
        Link(real, moved, directory: true);

        await using var fresh = await PackerTestServer.StartAsync(_fx.Options());   // no memory of the job: it is read from disk
        Assert.Equal(HttpStatusCode.NotFound, (await fresh.Client.GetAsync("/v1/jobs/" + id)).StatusCode);
        var (status, _) = await fresh.Verify(id);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task ASymbolicLinkInPlaceOfTheWz_IsRefusedByVerify()
    {
        var job = await Pack();
        var id = job.GetProperty("jobId").GetString()!;
        var wz = Path.Combine(JobDir(id), "String.wz");
        var copy = Path.Combine(_outside, "String.wz");
        File.Move(wz, copy);
        Link(wz, copy, directory: false);

        var (status, body) = await _server.Verify(id);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("output_changed", PackerTestServer.ErrorCode(body));
    }

    [Fact]
    public async Task AJobRecordThatClaimsAnotherId_IsNotAccepted()
    {
        var a = (await Pack()).GetProperty("jobId").GetString()!;
        var fake = new string('b', 32);
        Directory.CreateDirectory(JobDir(fake));
        File.Copy(Path.Combine(JobDir(a), "job.json"), Path.Combine(JobDir(fake), "job.json"));

        await using var fresh = await PackerTestServer.StartAsync(_fx.Options());
        Assert.Equal(HttpStatusCode.NotFound, (await fresh.Client.GetAsync("/v1/jobs/" + fake)).StatusCode);
    }

    [Fact]
    public async Task ALinkPlantedInTheSnapshot_IsRefused_AndNothingIsWrittenThroughIt()
    {
        var target = Path.Combine(_outside, "snapshot-target");
        Directory.CreateDirectory(target);
        _server.Service.Probe = (stage, id) =>
        {
            if (stage == "packing") Link(Path.Combine(JobDir(id), ".work", "input", "img", "String"), target, directory: true);
        };

        var job = await Pack();

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("outbox_unsafe", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(JobDir(job.GetProperty("jobId").GetString()!)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task ALinkPlantedAsTheFinalInputFolder_IsNotWrittenThrough_AndTheFailedJobKeepsNoWz()
    {
        var target = Path.Combine(_outside, "input-target");
        Directory.CreateDirectory(target);
        _server.Service.Probe = (stage, id) => { if (stage == "packing") Link(Path.Combine(JobDir(id), "input"), target, directory: true); };

        var job = await Pack();

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.False(File.Exists(Path.Combine(JobDir(job.GetProperty("jobId").GetString()!), "String.wz")));   // the failed job does not keep a half-finished output
    }

    [Fact]
    public async Task ThePublishedFolderIsOnlyRead_EvenWhenThePackFails()
    {
        var published = PackerFixture.Snapshot(_fx.Published);
        _server.Service.Probe = (stage, _) => { if (stage == "packing") throw new InvalidOperationException("boom"); };
        var job = await Pack();
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal(published, PackerFixture.Snapshot(_fx.Published));
    }

    // ---- jobs outlive the process

    [Fact]
    public async Task AFinishedJob_CanBeReadAndVerified_ByANewProcess()
    {
        var job = await Pack();
        var id = job.GetProperty("jobId").GetString()!;

        await using var fresh = await PackerTestServer.StartAsync(_fx.Options());
        var again = JsonDocument.Parse(await fresh.Client.GetStringAsync("/v1/jobs/" + id)).RootElement;
        Assert.Equal("done", again.GetProperty("state").GetString());
        Assert.Equal(job.GetProperty("wz").GetProperty("sha256").GetString(), again.GetProperty("wz").GetProperty("sha256").GetString());
        var (_, verify) = await fresh.Verify(id);
        Assert.True(verify.GetProperty("identical").GetBoolean());
    }

    [Fact]
    public async Task ARunningRecordWithNoJobInMemory_IsReportedAsInterrupted()
    {
        var id = new string('c', 32);
        Directory.CreateDirectory(JobDir(id));
        var running = new PackerJobView(id, "running", "String", null, null, 1, new[] { new PackerFileResult("String/Pet.img", "owner", "set", null, null, null) }, Array.Empty<string>(), null);
        File.WriteAllText(Path.Combine(JobDir(id), "job.json"), JsonSerializer.Serialize(running, PackerJobStore.Json));

        var job = JsonDocument.Parse(await _server.Client.GetStringAsync("/v1/jobs/" + id)).RootElement;

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("interrupted", job.GetProperty("error").GetProperty("code").GetString());
        var (status, _) = await _server.Verify(id);
        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    [Fact]
    public async Task AGarbageJobRecord_IsJustNotFound()
    {
        var id = new string('d', 32);
        Directory.CreateDirectory(JobDir(id));
        File.WriteAllText(Path.Combine(JobDir(id), "job.json"), "{ not json");
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Client.GetAsync("/v1/jobs/" + id)).StatusCode);
        File.WriteAllBytes(Path.Combine(JobDir(id), "job.json"), new byte[9 * 1024 * 1024]);   // over the size cap
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Client.GetAsync("/v1/jobs/" + id)).StatusCode);
    }

    [Fact]
    public async Task OldFinishedJobs_StayReadableAfterTheyLeaveMemory()
    {
        await using var small = await PackerTestServer.StartAsync(new PackerOptions(PackerFixture.Token, _fx.Roots, _fx.Outbox, _fx.Published) { TempRoot = _fx.TempRoot, MaxJobsKept = 1 });
        var first = (await Pack(small)).GetProperty("jobId").GetString()!;
        var second = (await Pack(small)).GetProperty("jobId").GetString()!;
        var third = (await Pack(small)).GetProperty("jobId").GetString()!;

        foreach (var id in new[] { first, second, third })
            Assert.Equal("done", JsonDocument.Parse(await small.Client.GetStringAsync("/v1/jobs/" + id)).RootElement.GetProperty("state").GetString());
    }
}
#endif
