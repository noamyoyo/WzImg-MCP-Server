#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// What the reviews of the first packer found, kept for the v2 packer: the version and key are checked, job.json is input, "done"
/// means finished, disk space and stale folders, the verify caps. Synthetic data only.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackerReviewFixTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly PackerFixture _fx = new();
    private readonly List<PackerTestServer> _servers = new();
    private PackerTestServer _server = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _fx.BuildPublished();
        _fx.Populate("owner");
        _server = await Start(_fx.Options());
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _fx.Dispose();
    }

    private async Task<PackerTestServer> Start(PackerOptions options)
    {
        var s = await PackerTestServer.StartAsync(options);
        _servers.Add(s);
        return s;
    }

    private string JobDir(string id) => Path.Combine(_fx.Outbox, id);

    private Task<JsonElement> Pack() => _server.PackAndWait(PackerTestServer.Set("String/Pet.img"), PackerTestServer.Remove("String/Cash.img"));

    private void RewriteRecord(string id, Func<PackerJobView, PackerJobView> change)
    {
        var file = Path.Combine(JobDir(id), "job.json");
        var view = JsonSerializer.Deserialize<PackerJobView>(File.ReadAllText(file), PackerJobStore.Json)!;
        File.WriteAllText(file, JsonSerializer.Serialize(change(view), PackerJobStore.Json));
    }

    // ---- the version and the key are checked

    [Theory]
    [InlineData((short)95, WzMapleVersion.GMS)]   // the right key, another version
    [InlineData((short)83, WzMapleVersion.EMS)]
    [InlineData((short)83, WzMapleVersion.BMS)]
    public async Task Verify_AWzOfAnotherVersionOrKey_IsNotIdentical_EvenWithItsRecordRewritten(short version, WzMapleVersion key)
    {
        var id = (await Pack()).GetProperty("jobId").GetString()!;
        var wrong = SyntheticWz.Build(Path.Combine(_fx.Temp, "other", "String.wz"), version: version, key: key);
        File.Copy(wrong, Path.Combine(JobDir(id), "String.wz"), overwrite: true);
        RewriteRecord(id, v => v with { Wz = new PackerWz("String.wz", new FileInfo(wrong).Length, SyntheticWz.Sha256(wrong)) });
        var fresh = await Start(_fx.Options());   // reads the rewritten record from disk

        var (status, body) = await fresh.Verify(id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.False(body.GetProperty("unchangedIdentical").GetBoolean());
        Assert.Equal(0, body.GetProperty("version").GetInt32());
        var diff = Assert.Single(body.GetProperty("diffs").EnumerateArray().ToList());
        Assert.Equal("String.wz", diff.GetProperty("path").GetString());
        Assert.Equal("unreadable", diff.GetProperty("kind").GetString());
        Assert.Equal("wz", diff.GetProperty("type").GetString());
    }

    // ---- job.json is input

    [Theory]
    [InlineData("name-traversal")]
    [InlineData("name-absolute")]
    [InlineData("name-other-category")]
    [InlineData("category-traversal")]
    [InlineData("category-backups")]
    [InlineData("state-unknown")]
    [InlineData("done-without-wz")]
    [InlineData("done-without-published")]
    [InlineData("bad-sha")]
    [InlineData("bad-published-sha")]
    [InlineData("negative-size")]
    [InlineData("no-files")]
    [InlineData("file-other-category")]
    [InlineData("file-traversal")]
    [InlineData("file-bad-from")]
    [InlineData("file-bad-op")]
    [InlineData("file-duplicate")]
    [InlineData("file-set-without-hash")]
    [InlineData("file-remove-with-hash")]
    [InlineData("file-without-result")]
    [InlineData("file-bad-result")]
    public async Task ATamperedJobRecord_IsNotAccepted(string kind)
    {
        var id = (await Pack()).GetProperty("jobId").GetString()!;
        var outsideWz = SyntheticWz.Build(Path.Combine(_fx.Temp, "elsewhere", "String.wz"));   // a valid .wz outside the job folder, with a matching hash
        PackerWz Wz(string name) => new(name, new FileInfo(outsideWz).Length, SyntheticWz.Sha256(outsideWz));
        PackerJobView WithFile(PackerJobView v, Func<PackerFileResult, PackerFileResult> change) => v with { Files = v.Files.Select((f, i) => i == 0 ? change(f) : f).ToList() };
        RewriteRecord(id, v => kind switch
        {
            "name-traversal" => v with { Wz = Wz("../../elsewhere/String.wz") },
            "name-absolute" => v with { Wz = Wz(outsideWz) },
            "name-other-category" => v with { Wz = Wz("Item.wz") },
            "category-traversal" => v with { Category = "../elsewhere" },
            "category-backups" => v with { Category = "Backups" },
            "state-unknown" => v with { State = "finished" },
            "done-without-wz" => v with { Wz = null },
            "done-without-published" => v with { Published = null },
            "bad-sha" => v with { Wz = v.Wz! with { Sha256 = "ABC" } },
            "bad-published-sha" => v with { Published = v.Published! with { Sha256 = "ABC" } },
            "negative-size" => v with { Wz = v.Wz! with { SizeBytes = -1 } },
            "no-files" => v with { Files = Array.Empty<PackerFileResult>() },
            "file-other-category" => WithFile(v, f => f with { Img = "Item/Pet.img" }),
            "file-traversal" => WithFile(v, f => f with { Img = "String/../../elsewhere/Pet.img" }),
            "file-bad-from" => WithFile(v, f => f with { From = "stranger" }),
            "file-bad-op" => WithFile(v, f => f with { Op = "merge" }),
            "file-duplicate" => v with { Files = v.Files.Append(v.Files[0]).ToList() },
            "file-set-without-hash" => WithFile(v, f => f with { Sha256 = null }),
            "file-remove-with-hash" => v with { Files = v.Files.Select(f => f.Op == "remove" ? f with { Sha256 = new string('a', 64) } : f).ToList() },
            "file-without-result" => WithFile(v, f => f with { Result = null }),
            "file-bad-result" => WithFile(v, f => f with { Result = "exploded" }),
            _ => v,
        });
        var fresh = await Start(_fx.Options());   // no memory of the job: it is read from disk

        Assert.Equal(HttpStatusCode.NotFound, (await fresh.Client.GetAsync("/v1/jobs/" + id)).StatusCode);
        var (status, body) = await fresh.Verify(id);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", PackerTestServer.ErrorCode(body));
    }

    [Fact]
    public async Task Verify_UsesTheCopy_NotTheFileInTheOutbox_SoASwapAfterTheHashChangesNothing()
    {
        var id = (await Pack()).GetProperty("jobId").GetString()!;
        _server.Service.Probe = (stage, _) =>
        {
            if (stage == "copied") File.WriteAllText(Path.Combine(JobDir(id), "String.wz"), "swapped after the hash was taken");
        };

        var (status, body) = await _server.Verify(id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());   // what was hashed is what was compared
    }

    [Fact]
    public async Task Verify_ASwapBeforeTheCopy_IsRefused()
    {
        var id = (await Pack()).GetProperty("jobId").GetString()!;
        _server.Service.Probe = (stage, _) =>
        {
            if (stage == "verify") File.WriteAllText(Path.Combine(JobDir(id), "String.wz"), "swapped");
        };
        var (status, body) = await _server.Verify(id);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("output_changed", PackerTestServer.ErrorCode(body));
    }

    // ---- "done" means finished

    [Fact]
    public async Task WhenAJobReadsDone_TheRecordIsFinal_AndTheSlotIsFree()
    {
        for (int i = 0; i < 15; i++)
        {
            var id = await _server.StartPack(PackerTestServer.Set("String/Pet.img"));
            var job = await _server.WaitForJob(id);
            Assert.Equal("done", job.GetProperty("state").GetString());

            Assert.False(_server.Gate.Busy, "the work slot was still taken when the job read done");
            var onDisk = JsonDocument.Parse(File.ReadAllText(Path.Combine(JobDir(id), "job.json"))).RootElement;
            Assert.Equal("done", onDisk.GetProperty("state").GetString());
            Assert.Equal("replaced", onDisk.GetProperty("files")[0].GetProperty("result").GetString());
            Assert.False(Directory.Exists(Path.Combine(JobDir(id), ".work")));
            var (status, _) = await _server.Verify(id);   // never 503 busy right after done
            Assert.Equal(HttpStatusCode.OK, status);
        }
    }

    [Fact]
    public async Task AFailedJob_AlsoReadsFailedOnlyWhenTheRecordIsFinalAndTheSlotFree()
    {
        var id = await _server.StartPack(PackerTestServer.Remove("String/Nothing.img"));
        var job = await _server.WaitForJob(id);
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.False(_server.Gate.Busy);
        Assert.Equal("failed", JsonDocument.Parse(File.ReadAllText(Path.Combine(JobDir(id), "job.json"))).RootElement.GetProperty("state").GetString());
    }

    // ---- space and stale folders

    [Fact]
    public async Task APackWithoutRoomInTheOutbox_Fails_BeforeTheLibraryWrites()
    {
        _server.Service.FreeSpace = _ => 10;
        var job = await Pack();
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("insufficient_space", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(JobDir(job.GetProperty("jobId").GetString()!)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task AVerifyWithoutRoomInTheTempFolder_Is503_AndLeavesNothing()
    {
        var id = (await Pack()).GetProperty("jobId").GetString()!;
        _server.Service.FreeSpace = _ => 10;
        var (status, body) = await _server.Verify(id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("insufficient_space", PackerTestServer.ErrorCode(body));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.TempRoot));
        Assert.False(_server.Gate.Busy);
    }

    [Fact]
    public void TheDefaultFreeSpaceCheck_WorksOnARealFolder()
    {
        var free = _server.Service.FreeSpace(_fx.Outbox);
        Assert.True(free > 0);
    }

    [Fact]
    public async Task TheOutboxHoldsAtMostMaxJobFolders()
    {
        var capped = await Start(new PackerOptions(PackerFixture.Token, _fx.Roots, _fx.Outbox, _fx.Published) { TempRoot = _fx.TempRoot, MaxJobFolders = 2 });
        await capped.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        await capped.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        var response = await capped.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Set("String/Pet.img") }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("outbox_full", PackerTestServer.ErrorCode(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement));
        Assert.Equal(2, Directory.GetDirectories(_fx.Outbox).Length);
    }

    [Fact]
    public async Task CleanStale_RemovesLeftoversOfADeadProcess_AndNothingElse()
    {
        var id = (await Pack()).GetProperty("jobId").GetString()!;
        var stale = new string('e', 32);
        Directory.CreateDirectory(Path.Combine(JobDir(stale), ".work", "input"));
        File.WriteAllText(Path.Combine(JobDir(stale), ".work", "String.wz"), "half");
        File.WriteAllText(Path.Combine(JobDir(stale), "job.json"), "{}");
        var linked = new string('f', 32);
        var outside = Path.Combine(_fx.Temp, "outside-work");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "KEEP");
        Directory.CreateDirectory(JobDir(linked));
        EnvRequire.Capability(ImgFixture.TrySymlink(Path.Combine(JobDir(linked), ".work"), outside, directory: true), "symbolic links cannot be created here");
        Directory.CreateDirectory(Path.Combine(_fx.TempRoot, "wzpacker-0123456789abcdef", "img"));
        File.WriteAllText(Path.Combine(_fx.TempRoot, "wzpacker-0123456789abcdef", "img", "x.img"), "x");
        Directory.CreateDirectory(Path.Combine(_fx.TempRoot, "somebody-else"));
        Directory.CreateDirectory(Path.Combine(_fx.TempRoot, "wzpacker-not-ours"));

        _server.Service.CleanStale();

        Assert.False(Directory.Exists(Path.Combine(JobDir(stale), ".work")));
        Assert.True(File.Exists(Path.Combine(JobDir(stale), "job.json")));
        Assert.Null(new DirectoryInfo(Path.Combine(JobDir(linked), ".work")).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(JobDir(linked), ".work")));   // the link is gone ...
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(outside, "keep.txt")));   // ... and its target was not touched
        Assert.False(Directory.Exists(Path.Combine(_fx.TempRoot, "wzpacker-0123456789abcdef")));
        Assert.True(Directory.Exists(Path.Combine(_fx.TempRoot, "somebody-else")));
        Assert.True(Directory.Exists(Path.Combine(_fx.TempRoot, "wzpacker-not-ours")));
        Assert.True(File.Exists(Path.Combine(JobDir(id), "String.wz")));   // finished jobs stay
        Assert.True(Directory.Exists(Path.Combine(JobDir(id), "input")));
    }

    // ---- the verify caps

    [Fact]
    public void VerifyDiffs_AreNotLimitedToTwoHundredCanvasesOr256MiB()
    {
        var options = _server.Service.ViewerOptionsFor(_fx.Roots);
        Assert.True(options.MaxCanvasDecodes >= 100_000);
        Assert.True(options.MaxDecodedBytes >= 64L * 1024 * 1024 * 1024);
        Assert.Equal(_server.Options.MaxImageBytes, options.MaxFileBytes);
    }

    [Fact]
    public async Task AnImageWithManyCanvases_VerifiesIdentical()
    {
        _fx.WriteImg("owner", "String/Many.img", "many", img =>
        {
            for (int i = 0; i < 260; i++)
                img.AddProperty(ImgFixture.PixelCanvas("c" + i, 8, 8, (x, y) => ((byte)(i + x), (byte)(i * 3 + y), (byte)(x * y), 255)));
        });
        var id = (await _server.PackAndWait(PackerTestServer.Set("String/Many.img"))).GetProperty("jobId").GetString()!;
        var (_, body) = await _server.Verify(id);
        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());
    }
}
#endif
