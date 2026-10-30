#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// What verify catches: a .wz that differs from the published file outside the list, a listed image that is not what went in, a
/// published file or an input snapshot that is no longer the one the pack used. Each "wrong" .wz is made with the library and put
/// in place of the packed one with its record rewritten (so only the comparison can catch it). Synthetic data only.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackerVerifyTests : IAsyncLifetime
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

    /// <summary>Packs a replacement of Pet.img and the removal of Cash.img; returns the job id.</summary>
    private async Task<string> Packed()
    {
        _fx.WriteImg("owner", "String/Pet.img", "OWNER-PET");
        var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"), PackerTestServer.Remove("String/Cash.img"));
        Assert.Equal("done", job.GetProperty("state").GetString());
        return job.GetProperty("jobId").GetString()!;
    }

    private void RewriteRecord(string id, Func<PackerJobView, PackerJobView> change)
    {
        var file = Path.Combine(JobDir(id), "job.json");
        var view = JsonSerializer.Deserialize<PackerJobView>(File.ReadAllText(file), PackerJobStore.Json)!;
        File.WriteAllText(file, JsonSerializer.Serialize(change(view), PackerJobStore.Json));
    }

    /// <summary>Puts <paramref name="wz"/> in place of the packed file and rewrites the record to match it; returns a new server that reads that record from disk.</summary>
    private async Task<PackerTestServer> Swap(string id, string wz, PackerOptions? options = null)
    {
        File.Copy(wz, Path.Combine(JobDir(id), "String.wz"), overwrite: true);
        RewriteRecord(id, v => v with { Wz = new PackerWz("String.wz", new FileInfo(wz).Length, SyntheticWz.Sha256(wz)) });
        return await Start(options ?? _fx.Options());
    }

    private string Wrong(string name, Action<WzFile> change) => SyntheticWz.Modify(_fx.PublishedWz(), Path.Combine(_fx.Temp, "wrong", name, "String.wz"), change);

    private static List<(string Path, string Kind, string Type)> Diffs(JsonElement body)
        => body.GetProperty("diffs").EnumerateArray().Select(d => (d.GetProperty("path").GetString()!, d.GetProperty("kind").GetString()!, d.GetProperty("type").GetString()!)).ToList();

    // ---- the .wz differs from the published file where it should not

    [Fact]
    public async Task ATamperedByteInAnUnlistedImage_IsCaught()
    {
        var id = await Packed();
        var wz = Path.Combine(_fx.Temp, "tampered", "String.wz");
        Directory.CreateDirectory(Path.GetDirectoryName(wz)!);
        File.Copy(Path.Combine(JobDir(id), "String.wz"), wz);
        var eqp = SyntheticWz.Blocks(wz)["Eqp.img"];
        var bytes = File.ReadAllBytes(wz);
        bytes[eqp.Offset + eqp.Size / 2] ^= 0x5A;   // one byte inside the stored block of an image that was not listed
        File.WriteAllBytes(wz, bytes);
        var fresh = await Swap(id, wz);

        var (status, body) = await fresh.Verify(id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.False(body.GetProperty("unchangedIdentical").GetBoolean());
        Assert.True(body.GetProperty("onlyListedDiffer").GetBoolean());
        Assert.Equal(new[] { ("String/Eqp.img", "changed", "image") }, Diffs(body));
    }

    [Fact]
    public async Task AnUnlistedImageThatIsGone_AndOneThatIsNew_AreCaught()
    {
        var id = await Packed();
        var wz = Wrong("gone-and-new", w =>
        {
            SyntheticWz.Find(w, "Eqp.img").Remove();
            SyntheticWz.Replace(w, "Pet.img", SyntheticWz.Content("OWNER-PET"));
            SyntheticWz.Find(w, "Cash.img").Remove();
            var extra = new WzImage("Extra.img") { Changed = true };
            SyntheticWz.Content("extra")(extra);
            w.WzDirectory.AddImage(extra);
        });
        var fresh = await Swap(id, wz);

        var (_, body) = await fresh.Verify(id);

        Assert.False(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.False(body.GetProperty("unchangedIdentical").GetBoolean());
        Assert.False(body.GetProperty("onlyListedDiffer").GetBoolean());
        var diffs = Diffs(body);
        Assert.Contains(("String/Eqp.img", "removed", "image"), diffs);
        Assert.Contains(("String/Extra.img", "added", "image"), diffs);
    }

    [Fact]
    public async Task AListedRemoveThatIsStillThere_AndAListedSetThatIsMissing_AreCaught()
    {
        var id = await Packed();
        var wz = Wrong("not-done", w => SyntheticWz.Find(w, "Pet.img").Remove());   // Cash.img was to go, Pet.img was to be set
        var fresh = await Swap(id, wz);

        var (_, body) = await fresh.Verify(id);

        Assert.False(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.True(body.GetProperty("unchangedIdentical").GetBoolean());
        Assert.False(body.GetProperty("onlyListedDiffer").GetBoolean());
        var diffs = Diffs(body);
        Assert.Contains(("String/Cash.img", "not_removed", "image"), diffs);
        Assert.Contains(("String/Pet.img", "missing", "image"), diffs);
    }

    // ---- a listed image is not what went in

    [Fact]
    public async Task AListedImageWithAnotherValue_IsCaughtByTheComparisonWithTheSnapshot()
    {
        var id = await Packed();
        var wz = Wrong("other-value", w =>
        {
            SyntheticWz.Replace(w, "Pet.img", img => { SyntheticWz.Content("SOMETHING-ELSE")(img); });
            SyntheticWz.Find(w, "Cash.img").Remove();
        });
        var fresh = await Swap(id, wz);

        var (status, body) = await fresh.Verify(id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.True(body.GetProperty("unchangedIdentical").GetBoolean());   // the rest is as stored
        Assert.True(body.GetProperty("onlyListedDiffer").GetBoolean());
        Assert.Contains(Diffs(body), d => d.Path.StartsWith("String/Pet.img") && d.Kind == "changed");
    }

    [Fact]
    public async Task APackedWzInPlaceOfTheRecordedOne_WithoutTheRecordRewritten_IsRefused()
    {
        var id = await Packed();
        var wz = Wrong("unrecorded", w => SyntheticWz.Find(w, "Cash.img").Remove());
        File.Copy(wz, Path.Combine(JobDir(id), "String.wz"), overwrite: true);

        var (status, body) = await _server.Verify(id);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("output_changed", PackerTestServer.ErrorCode(body));
    }

    // ---- the files the pack was made from

    [Fact]
    public async Task APublishedFileThatChangedAfterThePack_Is409()
    {
        var id = await Packed();
        SyntheticWz.Modify(_fx.PublishedWz(), Path.Combine(_fx.Temp, "newer", "String.wz"), w => SyntheticWz.Find(w, "Z/Only.img").Remove());
        File.Copy(Path.Combine(_fx.Temp, "newer", "String.wz"), _fx.PublishedWz(), overwrite: true);

        var (status, body) = await _server.Verify(id);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("published_changed", PackerTestServer.ErrorCode(body));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.TempRoot));
    }

    [Fact]
    public async Task APublishedFileThatIsGone_Is404()
    {
        var id = await Packed();
        File.Delete(_fx.PublishedWz());
        var (status, body) = await _server.Verify(id);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", PackerTestServer.ErrorCode(body));
    }

    [Fact]
    public async Task ATamperedInputSnapshot_Is409()
    {
        var id = await Packed();
        File.AppendAllText(Path.Combine(JobDir(id), "input", "img", "String", "Pet.img"), "x");
        var (status, body) = await _server.Verify(id);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("input_changed", PackerTestServer.ErrorCode(body));
    }

    [Fact]
    public async Task AMissingInputSnapshot_AndASnapshotFileThatIsALink_Are409()
    {
        var id = await Packed();
        var file = Path.Combine(JobDir(id), "input", "img", "String", "Pet.img");
        var copy = Path.Combine(_fx.Temp, "snapshot-copy.img");
        File.Move(file, copy);
        Assert.Equal("input_changed", PackerTestServer.ErrorCode((await _server.Verify(id)).Body));   // gone

        EnvRequire.Capability(ImgFixture.TrySymlink(file, copy, directory: false), "symbolic links cannot be created here");
        var (status, body) = await _server.Verify(id);   // a link to identical bytes
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("input_changed", PackerTestServer.ErrorCode(body));
    }

    // ---- limits and typed errors

    [Fact]
    public async Task Verify_ListsAtMostMaxDiffs_AndSaysTruncated()
    {
        var id = await Packed();
        var wz = Wrong("many-gone", w =>
        {
            SyntheticWz.Replace(w, "Pet.img", SyntheticWz.Content("OWNER-PET"));
            SyntheticWz.Find(w, "Cash.img").Remove();
            foreach (var path in new[] { "Eqp.img", "Sub/Deep.img", "Sub/Other.img", "Z/Only.img" }) SyntheticWz.Find(w, path).Remove();
        });
        var fresh = await Swap(id, wz, new PackerOptions(PackerFixture.Token, _fx.Roots, _fx.Outbox, _fx.Published) { TempRoot = _fx.TempRoot, MaxDiffs = 2 });

        var (_, body) = await fresh.Verify(id);

        Assert.False(body.GetProperty("identical").GetBoolean());
        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, body.GetProperty("diffs").GetArrayLength());
    }

    [Fact]
    public async Task Verify_OfAnUnknownMalformedOrUnfinishedJob_IsATypedError()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Verify(new string('a', 32))).Status);

        var failed = await _server.PackAndWait(PackerTestServer.Remove("String/Nothing.img"));
        var (status, body) = await _server.Verify(failed.GetProperty("jobId").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("job_not_done", PackerTestServer.ErrorCode(body));

        var response = await _server.PostRaw("/v1/verify", "{\"jobId\":42}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APackWithOnlyRemoves_NeedsNoSnapshot_AndVerifiesIdentical()
    {
        var job = await _server.PackAndWait(PackerTestServer.Remove("String/Cash.img"));
        var id = job.GetProperty("jobId").GetString()!;
        Directory.Delete(Path.Combine(JobDir(id), "input"), true);   // nothing in it was needed

        var (_, body) = await _server.Verify(id);

        Assert.True(body.GetProperty("identical").GetBoolean(), body.ToString());
        Assert.Equal(0, body.GetProperty("filesCompared").GetInt32());
    }
}
#endif
