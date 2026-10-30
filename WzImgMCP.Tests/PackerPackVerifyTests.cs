#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// Pack a change set into a published .wz over real HTTP, then verify it. The published file is a synthetic String.wz (version 83,
/// key GMS, seven images in three folders); the people's copies hold zero-IV .img files. Synthetic data only.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackerPackVerifyTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly PackerFixture _fx = new();
    private PackerTestServer _server = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _fx.BuildPublished();
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

    private async Task<JsonElement> Done(params object[] files)
    {
        var job = await _server.PackAndWait(files);
        Assert.True(job.GetProperty("state").GetString() == "done", job.ToString());
        return job;
    }

    private string OutWz(JsonElement job) => Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!, "String.wz");

    private async Task<JsonElement> VerifyOk(JsonElement job)
    {
        var (status, body) = await _server.Verify(job.GetProperty("jobId").GetString()!);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.True(body.GetProperty("unchangedIdentical").GetBoolean(), body.ToString());
        Assert.True(body.GetProperty("onlyListedDiffer").GetBoolean(), body.ToString());
        Assert.Equal(83, body.GetProperty("version").GetInt32());
        Assert.Equal("GMS", body.GetProperty("key").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        return body;
    }

    private static string StringOf(WzImage image, string name)
    {
        Assert.True(image.ParseImage());
        return ((WzStringProperty)image[name]!).Value;
    }

    private static WzImage Image(WzFile wz, string path)
    {
        var parts = path.Split('/');
        var dir = wz.WzDirectory;
        foreach (var part in parts[..^1]) dir = dir.GetDirectoryByName(part)!;
        return dir.GetImageByName(parts[^1])!;
    }

    // ---- replace

    [Fact]
    public async Task Replace_ProducesTheWz_AndTheJobAnswersTheContract()
    {
        var source = _fx.WriteImg("owner", "String/Pet.img", "OWNER-PET");
        var job = await Done(PackerTestServer.Set("String/Pet.img"));

        Assert.Equal("String", job.GetProperty("category").GetString());
        Assert.False(job.TryGetProperty("person", out _));
        var wz = job.GetProperty("wz");
        Assert.Equal("String.wz", wz.GetProperty("name").GetString());
        Assert.Equal(new FileInfo(OutWz(job)).Length, wz.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(SyntheticWz.Sha256(OutWz(job)), wz.GetProperty("sha256").GetString());
        var published = job.GetProperty("published");
        Assert.Equal("String.wz", published.GetProperty("name").GetString());
        Assert.Equal(new FileInfo(_fx.PublishedWz()).Length, published.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(SyntheticWz.Sha256(_fx.PublishedWz()), published.GetProperty("sha256").GetString());
        Assert.Equal(1, job.GetProperty("imgCount").GetInt32());
        Assert.Equal(0, job.GetProperty("warnings").GetArrayLength());
        Assert.False(job.TryGetProperty("error", out _));

        var file = Assert.Single(job.GetProperty("files").EnumerateArray().ToList());
        Assert.Equal("String/Pet.img", file.GetProperty("img").GetString());
        Assert.Equal("owner", file.GetProperty("from").GetString());
        Assert.Equal("set", file.GetProperty("op").GetString());
        Assert.Equal("replaced", file.GetProperty("result").GetString());
        Assert.Equal(SyntheticWz.Sha256(source), file.GetProperty("sha256").GetString());
        Assert.Equal(new FileInfo(source).Length, file.GetProperty("sizeBytes").GetInt64());

        // the input snapshot holds the exact bytes that were read
        var snapshot = Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!, "input", "img", "String", "Pet.img");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(snapshot));
    }

    [Fact]
    public async Task Replace_OneImage_EveryOtherImageIsRawIdentical_AndTheOrderIsKept()
    {
        var publishedHash = SyntheticWz.Sha256(_fx.PublishedWz());
        _fx.WriteImg("owner", "String/Sub/Deep.img", "OWNER-DEEP");
        var job = await Done(PackerTestServer.Set("String/Sub/Deep.img"));

        var before = SyntheticWz.Blocks(_fx.PublishedWz());
        var after = SyntheticWz.Blocks(OutWz(job));
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, block) in before)
        {
            if (path == "Sub/Deep.img") Assert.NotEqual(block.Sha256, after[path].Sha256);
            else Assert.Equal(block.Sha256, after[path].Sha256);
        }
        Assert.Equal(SyntheticWz.StoredOrder(_fx.PublishedWz()), SyntheticWz.StoredOrder(OutWz(job)));
        Assert.Equal(publishedHash, SyntheticWz.Sha256(_fx.PublishedWz()));   // the published file is never touched

        using var wz = SyntheticWz.Open(OutWz(job));
        Assert.Equal("OWNER-DEEP", StringOf(Image(wz, "Sub/Deep.img"), "who"));
        Assert.Equal("Pet.img", StringOf(Image(wz, "Pet.img"), "who"));   // an untouched one still reads
    }

    [Fact]
    public async Task Replace_ThenVerify_IsIdenticalToThePublishedFileExceptTheListedImage()
    {
        _fx.WriteImg("owner", "String/Pet.img", "OWNER-PET");
        var job = await Done(PackerTestServer.Set("String/Pet.img"));

        var body = await VerifyOk(job);

        Assert.Equal(1, body.GetProperty("filesCompared").GetInt32());
        Assert.Equal(6, body.GetProperty("unchangedCompared").GetInt32());   // the other six images of the seven
        Assert.Equal(0, body.GetProperty("diffs").GetArrayLength());
    }

    [Fact]
    public async Task Replace_TakesTheFileFromThePersonWhoChangedIt()
    {
        _fx.WriteImg("owner", "String/Pet.img", "OWNER-PET");
        _fx.WriteImg("friend", "String/Pet.img", "FRIEND-PET");
        _fx.WriteImg("friend", "String/Eqp.img", "FRIEND-EQP");
        var job = await Done(PackerTestServer.Set("String/Pet.img", "friend"), PackerTestServer.Set("String/Eqp.img", "friend"));

        using var wz = SyntheticWz.Open(OutWz(job));
        Assert.Equal("FRIEND-PET", StringOf(Image(wz, "Pet.img"), "who"));
        Assert.Equal("FRIEND-EQP", StringOf(Image(wz, "Eqp.img"), "who"));
        await VerifyOk(job);
    }

    [Fact]
    public async Task Replace_AnImageWithCanvasAndSound_KeepsThemAndVerifiesIdentical()
    {
        // the published Eqp.img holds a sound and two canvases of its own; the replacement has others
        var sounds = Directory.CreateTempSubdirectory("pk-sound-").FullName;
        try
        {
            _fx.BuildPublished(content: rel => img =>
            {
                SyntheticWz.Content(rel)(img);
                if (rel == "Eqp.img") img.AddProperty(ImgFixture.Sound("snd", sounds, 10));
            });
            _fx.WriteImg("owner", "String/Eqp.img", "NEW-EQP", img =>
            {
                img.AddProperty(ImgFixture.Sound("snd", sounds, 77, 16000));
                img.AddProperty(ImgFixture.PixelCanvas("big", 40, 24, (x, y) => ((byte)(x * 6), (byte)(y * 9), 3, 200)));
            });

            var job = await Done(PackerTestServer.Set("String/Eqp.img"));

            using (var wz = SyntheticWz.Open(OutWz(job)))
            {
                var eqp = Image(wz, "Eqp.img");
                Assert.True(eqp.ParseImage());
                Assert.IsType<WzBinaryProperty>(eqp["snd"]);
                Assert.IsType<WzCanvasProperty>(eqp["big"]);
                Assert.Equal("NEW-EQP", ((WzStringProperty)eqp["who"]!).Value);
            }
            await VerifyOk(job);
        }
        finally
        {
            try { Directory.Delete(sounds, true); } catch { }
        }
    }

    // ---- add

    [Fact]
    public async Task Add_AppendsTheImageAtTheEndOfItsFolder_AndCreatesMissingFolders()
    {
        _fx.WriteImg("owner", "String/Sub/Deeper/Added.img", "ADDED");
        _fx.WriteImg("owner", "String/New/Fresh.img", "FRESH");
        _fx.WriteImg("owner", "String/Top.img", "TOP");
        var job = await Done(PackerTestServer.Set("String/Sub/Deeper/Added.img"), PackerTestServer.Set("String/New/Fresh.img"), PackerTestServer.Set("String/Top.img"));

        Assert.All(job.GetProperty("files").EnumerateArray(), f => Assert.Equal("added", f.GetProperty("result").GetString()));
        var before = SyntheticWz.Blocks(_fx.PublishedWz());
        var after = SyntheticWz.Blocks(OutWz(job));
        Assert.Equal(before.Count + 3, after.Count);
        foreach (var (path, block) in before) Assert.Equal(block.Sha256, after[path].Sha256);

        var order = SyntheticWz.StoredOrder(OutWz(job));
        Assert.True(order.IndexOf("Sub/Deeper/Last.img") < order.IndexOf("Sub/Deeper/Added.img"));   // after the images that were there
        Assert.Equal(SyntheticWz.StoredOrder(_fx.PublishedWz()).Where(order.Contains), order.Where(SyntheticWz.StoredOrder(_fx.PublishedWz()).Contains));   // the old ones in the old order
        using (var wz = SyntheticWz.Open(OutWz(job)))
        {
            Assert.Equal("ADDED", StringOf(Image(wz, "Sub/Deeper/Added.img"), "who"));
            Assert.Equal("FRESH", StringOf(Image(wz, "New/Fresh.img"), "who"));
            Assert.Equal("TOP", StringOf(Image(wz, "Top.img"), "who"));
        }
        var body = await VerifyOk(job);
        Assert.Equal(3, body.GetProperty("filesCompared").GetInt32());
    }

    // ---- remove

    [Fact]
    public async Task Remove_TakesOnlyTheListedImageOut()
    {
        var job = await Done(PackerTestServer.Remove("String/Sub/Other.img"), PackerTestServer.Remove("String/Cash.img", "friend"));

        Assert.All(job.GetProperty("files").EnumerateArray(), f =>
        {
            Assert.Equal("removed", f.GetProperty("result").GetString());
            Assert.False(f.TryGetProperty("sha256", out _));
        });
        var before = SyntheticWz.Blocks(_fx.PublishedWz());
        var after = SyntheticWz.Blocks(OutWz(job));
        Assert.Equal(before.Keys.Except(new[] { "Sub/Other.img", "Cash.img" }).OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, block) in after) Assert.Equal(block.Sha256, before[path].Sha256);
        var body = await VerifyOk(job);
        Assert.Equal(0, body.GetProperty("filesCompared").GetInt32());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!, "input", "img")));   // no input image, nothing in the snapshot
    }

    [Fact]
    public async Task Remove_TheLastImageOfAFolder_StillGivesAFileThatOpensAndVerifies()
    {
        var job = await Done(PackerTestServer.Remove("String/Z/Only.img"));

        using var wz = SyntheticWz.Open(OutWz(job));
        Assert.Null(wz.WzDirectory.GetDirectoryByName("Z")?.GetImageByName("Only.img"));
        Assert.Equal(6, SyntheticWz.Blocks(OutWz(job)).Count);
        await VerifyOk(job);
    }

    [Fact]
    public async Task Remove_OfAnImageThePublishedFileLacks_FailsTheJob()
    {
        var job = await _server.PackAndWait(PackerTestServer.Remove("String/Nothing.img"));

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("image_not_found", job.GetProperty("error").GetProperty("code").GetString());
        Assert.False(job.TryGetProperty("wz", out _));
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!)).Select(Path.GetFileName).ToArray());
    }

    // ---- all three together

    [Fact]
    public async Task ReplaceAddAndRemoveTogether_ChangeOnlyTheListedImages()
    {
        _fx.WriteImg("owner", "String/Pet.img", "OWNER-PET");
        _fx.WriteImg("friend", "String/Sub/Deep.img", "FRIEND-DEEP");
        _fx.WriteImg("owner", "String/Brand/New.img", "BRAND-NEW");
        var job = await Done(
            PackerTestServer.Set("String/Pet.img"), PackerTestServer.Set("String/Sub/Deep.img", "friend"), PackerTestServer.Set("String/Brand/New.img"),
            PackerTestServer.Remove("String/Cash.img"), PackerTestServer.Remove("String/Sub/Deeper/Last.img", "friend"));

        Assert.Equal(new[] { "replaced", "replaced", "added", "removed", "removed" },
            job.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("result").GetString()).ToArray());
        Assert.Equal(5, job.GetProperty("imgCount").GetInt32());

        var before = SyntheticWz.Blocks(_fx.PublishedWz());
        var after = SyntheticWz.Blocks(OutWz(job));
        var untouched = before.Keys.Except(new[] { "Pet.img", "Sub/Deep.img", "Cash.img", "Sub/Deeper/Last.img" }).ToList();
        Assert.Equal(3, untouched.Count);   // Eqp, Sub/Other, Z/Only
        foreach (var path in untouched) Assert.Equal(before[path].Sha256, after[path].Sha256);
        Assert.Equal(before.Count - 2 + 1, after.Count);

        var body = await VerifyOk(job);
        Assert.Equal(3, body.GetProperty("filesCompared").GetInt32());
        Assert.Equal(3, body.GetProperty("unchangedCompared").GetInt32());
    }

    [Fact]
    public async Task APackThatListsTheSameImageTwice_OrWithoutACategoryPrefix_IsRefusedBeforeAnythingIsMade()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var before = PackerFixture.Snapshot(_fx.Temp);
        foreach (var files in new[]
        {
            new[] { PackerTestServer.Set("String/Pet.img"), PackerTestServer.Remove("String/Pet.img") },
            new[] { PackerTestServer.Set("String/Pet.img"), PackerTestServer.Set("String/pet.img") },
            new[] { PackerTestServer.Set("Pet.img") },
            new[] { PackerTestServer.Set("Item/Pet.img") },
        })
        {
            var response = await _server.PostJson("/v1/pack", PackerTestServer.Pack(files));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(before, PackerFixture.Snapshot(_fx.Temp));
    }

    // ---- the keys

    [Fact]
    public async Task AnImgWrittenWithAnotherKey_FailsTheJob_AndNothingIsCopiedAsItIs()
    {
        var gmsImg = SyntheticWz.ImgBytes(SyntheticWz.Content("GMS-KEYED"), WzMapleVersion.GMS);
        ImgFixture.Write(_fx.Root("owner"), "String/Pet.img", gmsImg);
        var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"));

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("bad_image", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("String/Pet.img", job.GetProperty("error").GetProperty("message").GetString());
        Assert.False(job.TryGetProperty("wz", out _));
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task TheGmsFileIsMadeFromZeroKeyInput_NotACopyOfIt()
    {
        var source = _fx.WriteImg("owner", "String/Pet.img", "ZERO-KEY");
        var job = await Done(PackerTestServer.Set("String/Pet.img"));

        using var wz = SyntheticWz.Open(OutWz(job));
        var block = Image(wz, "Pet.img");
        var blocks = SyntheticWz.Blocks(OutWz(job));
        var stored = new byte[block.BlockSize];
        using (var fs = File.OpenRead(OutWz(job))) { fs.Position = block.Offset; fs.ReadExactly(stored); }
        Assert.NotEqual(File.ReadAllBytes(source), stored);   // another key, so other bytes
        Assert.True(blocks.ContainsKey("Pet.img"));
        Assert.Equal("ZERO-KEY", StringOf(block, "who"));
    }

    // ---- damaged input

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("empty")]
    public async Task ADamagedImage_FailsTheJob_WithoutAPath_AndLeavesNoWz(string kind)
    {
        var good = SyntheticWz.ImgBytes(SyntheticWz.Content("GOOD"));
        var bytes = kind switch
        {
            "garbage" => Enumerable.Range(0, 400).Select(i => (byte)(i * 31 + 7)).ToArray(),
            "truncated" => good[..(good.Length / 2)],
            _ => Array.Empty<byte>(),
        };
        ImgFixture.Write(_fx.Root("owner"), "String/Pet.img", bytes);

        var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"));

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("bad_image", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(_fx.Temp, job.ToString());
        Assert.False(job.TryGetProperty("wz", out _));
        Assert.False(File.Exists(OutWz(job)));
        Assert.Equal(new[] { "job.json" }, Directory.GetFileSystemEntries(Path.Combine(_fx.Outbox, job.GetProperty("jobId").GetString()!)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task APublishedFileThatIsNotAWz_FailsTheJob_AndThePublishedFileIsUntouched()
    {
        File.WriteAllText(_fx.PublishedWz(), "this is not a wz file at all");
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"));

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("published_unreadable", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("this is not a wz file at all", File.ReadAllText(_fx.PublishedWz()));
    }

    [Fact]
    public async Task APublishedFileOfAnotherVersionOrKey_IsRefused()
    {
        foreach (var (version, key) in new[] { ((short)95, WzMapleVersion.GMS), ((short)83, WzMapleVersion.EMS) })
        {
            using (var wz = new WzFile(version, key) { Name = "String.wz" })
            {
                var image = new WzImage("Pet.img") { Changed = true };
                SyntheticWz.Content("p")(image);
                wz.WzDirectory.AddImage(image);
                File.Delete(_fx.PublishedWz());
                wz.SaveToDisk(_fx.PublishedWz());
            }
            _fx.WriteImg("owner", "String/Pet.img", "x");
            var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"));
            Assert.Equal("failed", job.GetProperty("state").GetString());
            Assert.Equal("published_unreadable", job.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task TwoImagesInThePublishedFileThatDifferOnlyInCase_AreRefused()
    {
        using (var wz = new WzFile(83, WzMapleVersion.GMS) { Name = "String.wz" })
        {
            foreach (var name in new[] { "Pet.img", "pet.img" })
            {
                var image = new WzImage(name) { Changed = true };
                SyntheticWz.Content(name)(image);
                wz.WzDirectory.AddImage(image);
            }
            File.Delete(_fx.PublishedWz());
            wz.SaveToDisk(_fx.PublishedWz());
        }
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("ambiguous_name", job.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task APublishedFileThatChangesWhileThePackRuns_IsDetected()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        _server.Service.Probe = (stage, _) =>
        {
            if (stage == "packing") File.AppendAllText(_fx.PublishedWz(), "tail");   // changes the size, so the stamp
        };
        var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("published_changed", job.GetProperty("error").GetProperty("code").GetString());
        Assert.False(job.TryGetProperty("wz", out _));
    }
}
#endif
