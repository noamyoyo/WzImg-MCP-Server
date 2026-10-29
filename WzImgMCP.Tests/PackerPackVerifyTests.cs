#if LINUX_BUILD
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>Pack a synthetic category over real HTTP, re-extract and compare. Synthetic data only.</summary>
[Collection("AllowedRootEnv")]
public sealed class PackerPackVerifyTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly PackerFixture _fx = new();
    private PackerTestServer _server = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _fx.Populate("owner");
        _fx.Populate("friend");
        _server = await PackerTestServer.StartAsync(_fx.Options());
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _fx.Dispose();
    }

    [Fact]
    public async Task Pack_ProducesTheWz_AndTheJobAnswersTheContract()
    {
        var job = await _server.PackAndWait();

        Assert.Equal("done", job.GetProperty("state").GetString());
        var wz = job.GetProperty("wz");
        Assert.Equal("String.wz", wz.GetProperty("name").GetString());
        var file = Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!, "String.wz");
        Assert.True(File.Exists(file));
        Assert.Equal(new FileInfo(file).Length, wz.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), wz.GetProperty("sha256").GetString());
        Assert.Equal(3, job.GetProperty("imgCount").GetInt32());
        Assert.Equal(0, job.GetProperty("warnings").GetArrayLength());
        Assert.False(job.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task PackThenVerify_ReExtractedWzIsIdenticalToThePersonsCopy()
    {
        var job = await _server.PackAndWait();
        var (status, body) = await _server.Verify(job.GetProperty("jobId").GetString()!);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.Equal(3, body.GetProperty("filesCompared").GetInt32());
        Assert.Equal(0, body.GetProperty("diffs").GetArrayLength());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task PackedWz_OpensWithVersion83AndKeyGms_AndHoldsTheEditedValue()
    {
        _fx.WriteImg("owner", "String/Pet.img", "EDITED-VALUE");
        var job = await _server.PackAndWait();
        var wzPath = Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!, "String.wz");

        using var wz = new WzFile(wzPath, 83, WzMapleVersion.GMS);
        Assert.Equal(WzFileParseStatus.Success, wz.ParseWzFile());
        var pet = wz.WzDirectory.WzImages.Single(i => i.Name == "Pet.img");
        Assert.True(pet.ParseImage());
        Assert.Equal("EDITED-VALUE", ((WzStringProperty)pet["who"]!).Value);
    }

    [Fact]
    public async Task Verify_FindsAValueEditedAfterThePack()
    {
        var job = await _server.PackAndWait();
        _fx.WriteImg("owner", "String/Pet.img", "CHANGED-AFTER-PACK");   // the tree no longer matches the packed .wz

        var (status, body) = await _server.Verify(job.GetProperty("jobId").GetString()!);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("identical").GetBoolean());
        var diff = Assert.Single(body.GetProperty("diffs").EnumerateArray().ToList());
        Assert.Equal("String/Pet.img:who", diff.GetProperty("path").GetString());
        Assert.Equal("changed", diff.GetProperty("kind").GetString());
        Assert.Equal("string", diff.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Verify_ReportsAnImageTheWzLacks_AndOneItHasExtra()
    {
        var job = await _server.PackAndWait();
        File.Delete(Path.Combine(_fx.Img("owner"), "String", "Eqp.img"));              // in the .wz, no longer in the tree
        _fx.WriteImg("owner", "String/New.img", "late");                                // in the tree, not in the .wz

        var (_, body) = await _server.Verify(job.GetProperty("jobId").GetString()!);

        Assert.False(body.GetProperty("identical").GetBoolean());
        var diffs = body.GetProperty("diffs").EnumerateArray().Select(d => (d.GetProperty("path").GetString(), d.GetProperty("kind").GetString(), d.GetProperty("type").GetString())).ToList();
        Assert.Contains(("String/Eqp.img", "added", "image"), diffs);     // the .wz has it, the tree does not
        Assert.Contains(("String/New.img", "removed", "image"), diffs);   // the tree has it, the .wz does not
        Assert.Equal(2, diffs.Count);
    }

    [Fact]
    public async Task Verify_ADifferentValidWzInPlaceOfThePacked_IsRefused()
    {
        var job = await _server.PackAndWait();
        var id = job.GetProperty("jobId").GetString()!;

        // a second, valid .wz from other content, put where the first one was
        _fx.WriteImg("owner", "String/Pet.img", "OTHER");
        var other = await _server.PackAndWait();
        var otherFile = Path.Combine(_fx.Outbox, other.GetProperty("jobId").GetString()!, "String.wz");
        File.Copy(otherFile, Path.Combine(_fx.Outbox, id, "String.wz"), overwrite: true);

        var (status, body) = await _server.Verify(id);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("output_changed", PackerTestServer.ErrorCode(body));
    }

    [Fact]
    public async Task Verify_ADeliberatelyWrongWz_WithItsRecordRewritten_IsCaughtByTheComparison()
    {
        // Someone swaps the .wz AND rewrites job.json so the hash matches: only the semantic comparison can still catch it.
        var good = await _server.PackAndWait();
        var goodId = good.GetProperty("jobId").GetString()!;
        _fx.WriteImg("owner", "String/Pet.img", "WRONG");
        var wrong = await _server.PackAndWait();
        var wrongFile = Path.Combine(_fx.Outbox, wrong.GetProperty("jobId").GetString()!, "String.wz");
        var target = Path.Combine(_fx.Outbox, goodId, "String.wz");
        File.Copy(wrongFile, target, overwrite: true);
        var jobFile = Path.Combine(_fx.Outbox, goodId, "job.json");
        var json = JsonSerializer.Deserialize<PackerJobView>(File.ReadAllText(jobFile), PackerJobStore.Json)!;
        File.WriteAllText(jobFile, JsonSerializer.Serialize(json with
        {
            Wz = new PackerWz("String.wz", new FileInfo(target).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant()),
        }, PackerJobStore.Json));
        await using var restarted = await PackerTestServer.StartAsync(_fx.Options());   // a new process reads job.json from disk
        _fx.WriteImg("owner", "String/Pet.img", "Pet.img");                       // the tree says what the first pack packed

        var response = await restarted.PostJson("/v1/verify", new { jobId = goodId, person = "owner" });
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("identical").GetBoolean());
        Assert.Contains(body.GetProperty("diffs").EnumerateArray(), d => d.GetProperty("path").GetString() == "String/Pet.img:who");
    }

    [Fact]
    public async Task Verify_IsAboutThePersonsCopy_ThatIsNamed()
    {
        var job = await _server.PackAndWait("owner");
        _fx.WriteImg("friend", "String/Pet.img", "friend-differs");

        var (_, forOwner) = await _server.Verify(job.GetProperty("jobId").GetString()!, "owner");
        var (_, forFriend) = await _server.Verify(job.GetProperty("jobId").GetString()!, "friend");

        Assert.True(forOwner.GetProperty("identical").GetBoolean());
        Assert.False(forFriend.GetProperty("identical").GetBoolean());
    }

    [Fact]
    public async Task Verify_ListsAtMostMaxDiffs_AndSaysTruncated()
    {
        var names = Enumerable.Range(0, 6).Select(i => $"F{i}.img").ToArray();
        _fx.Populate("owner", "Many", names);
        var job = await _server.PackAndWait("owner", "Many");
        foreach (var n in names) _fx.WriteImg("owner", "Many/" + n, "changed-" + n);   // six images now differ

        await using var small = await PackerTestServer.StartAsync(_fx.Options(o => new PackerOptions(PackerFixture.Token, _fx.Roots, _fx.Outbox) { TempRoot = _fx.TempRoot, MaxDiffs = 2 }));
        var response = await small.PostJson("/v1/verify", new { jobId = job.GetProperty("jobId").GetString(), person = "owner" });
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, body.GetProperty("diffs").GetArrayLength());
        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.False(body.GetProperty("identical").GetBoolean());
    }

    [Fact]
    public async Task Verify_OfAnUnknownMalformedOrUnfinishedJob_IsATypedError()
    {
        var (s1, b1) = await _server.Verify(new string('a', 32));
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal("not_found", PackerTestServer.ErrorCode(b1));

        var (s2, b2) = await _server.Verify("../../etc");
        Assert.Equal(HttpStatusCode.NotFound, s2);

        var (s3, b3) = await _server.Verify(new string('a', 32), "stranger");
        Assert.Equal(HttpStatusCode.BadRequest, s3);
        Assert.Equal("unknown_source", PackerTestServer.ErrorCode(b3));

        // a failed job has no .wz
        Directory.Delete(Path.Combine(_fx.Img("owner"), "String"), true);
        Directory.CreateDirectory(Path.Combine(_fx.Img("owner"), "String"));
        var failed = await _server.PackAndWait();
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        var (s4, b4) = await _server.Verify(failed.GetProperty("jobId").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, s4);
        Assert.Equal("job_not_done", PackerTestServer.ErrorCode(b4));
    }
}
#endif
