#if LINUX_BUILD
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MapleLib.Img;
using MapleLib.WzLib;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>What the review of BUILD round 1 found (F1 to F7): version, trusted job.json, ordering, disk space and stale folders, incomplete packs, verify caps.</summary>
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

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void RewriteRecord(string id, Func<PackerJobView, PackerJobView> change)
    {
        var file = Path.Combine(JobDir(id), "job.json");
        var view = JsonSerializer.Deserialize<PackerJobView>(File.ReadAllText(file), PackerJobStore.Json)!;
        File.WriteAllText(file, JsonSerializer.Serialize(change(view), PackerJobStore.Json));
    }

    // ---- F1: the version is checked

    /// <summary>Packs the owner's String category with the library at another wz version and key, into <paramref name="dir"/>.</summary>
    private async Task<string> PackWith(string dir, short version, WzMapleVersion key)
    {
        Directory.CreateDirectory(dir);
        var result = await new WzPackingService().PackCategoriesAsync(_fx.Img("owner"), dir, new[] { "String" }, false, default, null, version, false, key);
        Assert.True(result.Success, result.ErrorMessage);
        return Path.Combine(dir, "String.wz");
    }

    [Theory]
    [InlineData((short)95, WzMapleVersion.GMS)]   // the right key, another version
    [InlineData((short)83, WzMapleVersion.EMS)]
    [InlineData((short)83, WzMapleVersion.BMS)]
    public async Task Verify_AWzOfAnotherVersionOrKey_IsNotIdentical_EvenWithItsRecordRewritten(short version, WzMapleVersion key)
    {
        var id = (await _server.PackAndWait()).GetProperty("jobId").GetString()!;
        var wrong = await PackWith(Path.Combine(_fx.Temp, "other"), version, key);
        File.Copy(wrong, Path.Combine(JobDir(id), "String.wz"), overwrite: true);
        RewriteRecord(id, v => v with { Wz = new PackerWz("String.wz", new FileInfo(wrong).Length, Sha(wrong)) });
        var fresh = await Start(_fx.Options());   // reads the rewritten record from disk

        var (status, body) = await fresh.Verify(id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("identical").GetBoolean(), body.ToString());
        var diff = Assert.Single(body.GetProperty("diffs").EnumerateArray().ToList());
        Assert.Equal("String.wz", diff.GetProperty("path").GetString());
        Assert.Equal("unreadable", diff.GetProperty("kind").GetString());
        Assert.Equal("wz", diff.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Verify_TheRightVersionAndKey_StillPasses()
    {
        var id = (await _server.PackAndWait()).GetProperty("jobId").GetString()!;
        var right = await PackWith(Path.Combine(_fx.Temp, "same"), 83, WzMapleVersion.GMS);
        File.Copy(right, Path.Combine(JobDir(id), "String.wz"), overwrite: true);
        RewriteRecord(id, v => v with { Wz = new PackerWz("String.wz", new FileInfo(right).Length, Sha(right)) });
        var fresh = await Start(_fx.Options());

        var (_, body) = await fresh.Verify(id);

        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());
    }

    // ---- F2: job.json is input

    [Theory]
    [InlineData("name-traversal")]
    [InlineData("name-absolute")]
    [InlineData("name-other-category")]
    [InlineData("category-traversal")]
    [InlineData("category-backups")]
    [InlineData("person-unknown")]
    [InlineData("state-unknown")]
    [InlineData("done-without-wz")]
    [InlineData("bad-sha")]
    [InlineData("negative-size")]
    public async Task ATamperedJobRecord_IsNotAccepted(string kind)
    {
        var id = (await _server.PackAndWait()).GetProperty("jobId").GetString()!;
        var elsewhere = Path.Combine(_fx.Temp, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var outsideWz = await PackWith(elsewhere, 83, WzMapleVersion.GMS);   // a valid .wz outside the job folder, with a matching hash
        PackerWz Wz(string name) => new(name, new FileInfo(outsideWz).Length, Sha(outsideWz));
        RewriteRecord(id, v => kind switch
        {
            "name-traversal" => v with { Wz = Wz("../../elsewhere/String.wz") },
            "name-absolute" => v with { Wz = Wz(outsideWz) },
            "name-other-category" => v with { Wz = Wz("Item.wz") },
            "category-traversal" => v with { Category = "../elsewhere" },
            "category-backups" => v with { Category = "Backups" },
            "person-unknown" => v with { Person = "stranger" },
            "state-unknown" => v with { State = "finished" },
            "done-without-wz" => v with { Wz = null },
            "bad-sha" => v with { Wz = v.Wz! with { Sha256 = "ABC" } },
            "negative-size" => v with { Wz = v.Wz! with { SizeBytes = -1 } },
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
        var job = await _server.PackAndWait();
        var id = job.GetProperty("jobId").GetString()!;
        _server.Service.Probe = (stage, _) =>
        {
            if (stage == "copied") File.WriteAllText(Path.Combine(JobDir(id), "String.wz"), "swapped after the hash was taken");
        };

        var (status, body) = await _server.Verify(id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());   // what was hashed is what was extracted
    }

    [Fact]
    public async Task Verify_ASwapBeforeTheCopy_IsRefused()
    {
        var id = (await _server.PackAndWait()).GetProperty("jobId").GetString()!;
        _server.Service.Probe = (stage, _) =>
        {
            if (stage == "verify") File.WriteAllText(Path.Combine(JobDir(id), "String.wz"), "swapped");
        };
        var (status, body) = await _server.Verify(id);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("output_changed", PackerTestServer.ErrorCode(body));
    }

    // ---- F3: "done" means finished

    [Fact]
    public async Task WhenAJobReadsDone_TheRecordIsFinal_AndTheSlotIsFree()
    {
        for (int i = 0; i < 15; i++)
        {
            var id = await _server.StartPack();
            var job = await _server.WaitForJob(id);
            Assert.Equal("done", job.GetProperty("state").GetString());

            Assert.False(_server.Gate.Busy, "the work slot was still taken when the job read done");
            var onDisk = JsonDocument.Parse(File.ReadAllText(Path.Combine(JobDir(id), "job.json"))).RootElement;
            Assert.Equal("done", onDisk.GetProperty("state").GetString());
            Assert.False(Directory.Exists(Path.Combine(JobDir(id), ".work")));
            var (status, _) = await _server.Verify(id);   // never 503 busy right after done
            Assert.Equal(HttpStatusCode.OK, status);
        }
    }

    [Fact]
    public async Task AFailedJob_AlsoReadsFailedOnlyWhenTheRecordIsFinalAndTheSlotFree()
    {
        File.WriteAllText(Path.Combine(_fx.Img("owner"), "String", "notes.txt"), "x");
        var id = await _server.StartPack();
        var job = await _server.WaitForJob(id);
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.False(_server.Gate.Busy);
        Assert.Equal("failed", JsonDocument.Parse(File.ReadAllText(Path.Combine(JobDir(id), "job.json"))).RootElement.GetProperty("state").GetString());
    }

    // ---- F4: space and stale folders

    [Fact]
    public async Task APackWithoutRoomInTheOutbox_Fails_BeforeTheLibraryWrites()
    {
        _server.Service.FreeSpace = _ => 10;
        var job = await _server.PackAndWait();
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("insufficient_space", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(JobDir(job.GetProperty("jobId").GetString()!)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task AVerifyWithoutRoomInTheTempFolder_Is503_AndLeavesNothing()
    {
        var id = (await _server.PackAndWait()).GetProperty("jobId").GetString()!;
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
        var capped = await Start(new PackerOptions(PackerFixture.Token, _fx.Roots, _fx.Outbox) { TempRoot = _fx.TempRoot, MaxJobFolders = 2 });
        await capped.PackAndWait();
        await capped.PackAndWait();
        var response = await capped.PostJson("/v1/pack", PackerTestServer.Pack());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("outbox_full", PackerTestServer.ErrorCode(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement));
        Assert.Equal(2, Directory.GetDirectories(_fx.Outbox).Length);
    }

    [Fact]
    public async Task CleanStale_RemovesLeftoversOfADeadProcess_AndNothingElse()
    {
        var id = (await _server.PackAndWait()).GetProperty("jobId").GetString()!;
        var stale = new string('e', 32);
        Directory.CreateDirectory(Path.Combine(JobDir(stale), ".work"));
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
    }

    // ---- F5: an incomplete pack is not "done"

    [Fact]
    public async Task APackThatPacksFewerImagesThanTheCategoryHas_IsNotDone()
    {
        // the library packs every file in practice (two names that differ only in case are both kept), so the library's answer is cut by a seam
        _server.Service.PackedProbe = packed => packed.ImagesPacked -= 1;

        var job = await _server.PackAndWait();

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("pack_failed", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("3 image file(s) found, 2 packed", job.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.False(job.TryGetProperty("wz", out _));
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(JobDir(job.GetProperty("jobId").GetString()!)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task TwoNamesThatDifferOnlyInCase_AreRefused_BecauseAWzCannotKeepThemApart()
    {
        // found while testing F5: the library packs both, but the re-extraction keeps one, so verify would call the pack incomplete
        _fx.WriteImg("owner", "String/Pet2.img", "one");
        _fx.WriteImg("owner", "String/pet2.img", "two");
        var job = await _server.PackAndWait();
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("unsupported_name", job.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- F6: the verify caps

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
        var id = (await _server.PackAndWait()).GetProperty("jobId").GetString()!;
        var (_, body) = await _server.Verify(id);
        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());
    }
}
#endif
