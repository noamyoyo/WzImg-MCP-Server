#if LINUX_BUILD
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Extensions.DependencyInjection;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;
using WzImgMCP.Viewer.Diff;

namespace WzImgMCP.Tests;

/// <summary>GET /v1/diff: file-level states, base hashes, typed errors, slots and leases.</summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerDiffEndpointTests : IAsyncLifetime
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

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Img(int marker, byte hidden = 0, int extraNodes = 0)
        => ImgFixture.Build(img =>
        {
            img.AddProperty(new WzIntProperty("marker", marker));
            img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, (x, y) => x < 4 ? (hidden, hidden, hidden, (byte)0) : ((byte)90, (byte)90, (byte)90, (byte)255)));
            for (int i = 0; i < extraNodes; i++) img.AddProperty(new WzIntProperty($"e{i:000}", i));
        });

    private void WriteBase(string person, string path, byte[] baseBytes)
        => File.WriteAllText(Path.Combine(_tree.Root(person), ".base.json"),
            "{ \"createdUtc\": \"2026-10-27T17:30:53Z\", \"fileCount\": 1, \"files\": { \"" + path + "\": \"" + Sha(baseBytes) + "\" }, \"release\": \"r0\" }");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Get(ViewerTestServer s, string url)
    {
        var response = await s.Client.GetAsync(url);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    // ---- states

    [Fact]
    public async Task Unchanged_SameBytes_AndTheBaseHashSaysNotEdited()
    {
        var bytes = Img(1);
        ImgFixture.Write(_tree.Root("canon"), "String/Pet.img", bytes);
        ImgFixture.Write(_tree.Root("owner"), "String/Pet.img", bytes);
        WriteBase("owner", "String/Pet.img", bytes);
        var s = await Start();

        var (status, body) = await Get(s, "/v1/diff?person=owner&path=String/Pet.img");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("unchanged", body.GetProperty("state").GetString());
        Assert.Equal("identical", body.GetProperty("semantic").GetString());
        Assert.Equal(Sha(bytes), body.GetProperty("before").GetProperty("sha256").GetString());
        Assert.Equal(Sha(bytes), body.GetProperty("now").GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("now").GetProperty("sizeBytes").GetInt64());
        Assert.True(body.GetProperty("before").GetProperty("matchesBase").GetBoolean());
        Assert.True(body.GetProperty("now").GetProperty("matchesBase").GetBoolean());
        Assert.Equal(0, body.GetProperty("changes").GetArrayLength());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Changed_ListsTheChange_AndMatchesBaseSaysTheCopyWasEdited()
    {
        var canon = Img(1);
        ImgFixture.Write(_tree.Root("canon"), "String/Pet.img", canon);
        ImgFixture.Write(_tree.Root("owner"), "String/Pet.img", Img(2));
        WriteBase("owner", "String/Pet.img", canon);   // the copy was made from this canon
        var s = await Start();

        var (status, body) = await Get(s, "/v1/diff?person=owner&path=String/Pet.img");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("changed", body.GetProperty("state").GetString());
        Assert.Equal("different", body.GetProperty("semantic").GetString());
        Assert.True(body.GetProperty("before").GetProperty("matchesBase").GetBoolean());    // canon still is what the copy started from
        Assert.False(body.GetProperty("now").GetProperty("matchesBase").GetBoolean());      // the copy has been edited
        var change = Assert.Single(body.GetProperty("changes").EnumerateArray());
        Assert.Equal("marker", change.GetProperty("path").GetString());
        Assert.Equal("changed", change.GetProperty("kind").GetString());
        Assert.Equal("int", change.GetProperty("type").GetString());
        Assert.Equal("1", change.GetProperty("before").GetString());
        Assert.Equal("2", change.GetProperty("after").GetString());
        Assert.Equal(1, body.GetProperty("summary").GetProperty("changed").GetInt32());
        Assert.Equal(0, body.GetProperty("canvases").GetArrayLength());
    }

    [Fact]
    public async Task ResavedWithNothingVisible_IsChangedButSemanticallyIdentical()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1, hidden: 7));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(1, hidden: 99));   // other bytes, the same picture
        var s = await Start();

        var (_, body) = await Get(s, "/v1/diff?person=owner&path=A.img");
        Assert.Equal("changed", body.GetProperty("state").GetString());
        Assert.Equal("identical", body.GetProperty("semantic").GetString());
        Assert.Equal(0, body.GetProperty("changes").GetArrayLength());
        Assert.Equal("recompressed", body.GetProperty("canvases")[0].GetProperty("change").GetString());
        Assert.Equal(1, body.GetProperty("summary").GetProperty("canvasesRecompressed").GetInt32());
    }

    [Fact]
    public async Task AddedAndRemoved_AreFileLevelStates()
    {
        ImgFixture.Write(_tree.Root("owner"), "OnlyMine.img", Img(1));
        ImgFixture.Write(_tree.Root("canon"), "OnlyCanon.img", Img(2));
        var s = await Start();

        var (_, added) = await Get(s, "/v1/diff?person=owner&path=OnlyMine.img");
        Assert.Equal("added", added.GetProperty("state").GetString());
        Assert.Equal("different", added.GetProperty("semantic").GetString());
        Assert.False(added.GetProperty("before").GetProperty("exists").GetBoolean());
        Assert.True(added.GetProperty("now").GetProperty("exists").GetBoolean());
        Assert.False(added.GetProperty("before").TryGetProperty("sha256", out _));
        Assert.Equal(JsonValueKind.Null, added.GetProperty("before").GetProperty("matchesBase").ValueKind);

        var (_, removed) = await Get(s, "/v1/diff?person=owner&path=OnlyCanon.img");
        Assert.Equal("removed", removed.GetProperty("state").GetString());
        Assert.True(removed.GetProperty("before").GetProperty("exists").GetBoolean());
        Assert.False(removed.GetProperty("now").GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task MatchesBase_IsNull_WithoutAMapOrWithoutAnEntry()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2));
        ImgFixture.Write(_tree.Root("canon"), "B.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "B.img", Img(2));
        WriteBase("owner", "B.img", Img(1));   // a map, but without A.img
        var s = await Start();

        var (_, a) = await Get(s, "/v1/diff?person=owner&path=A.img");
        Assert.Equal(JsonValueKind.Null, a.GetProperty("now").GetProperty("matchesBase").ValueKind);
        var (_, friend) = await Get(s, "/v1/diff?person=friend&path=A.img");   // friend has no file, no map
        Assert.Equal(JsonValueKind.Null, friend.GetProperty("before").GetProperty("matchesBase").ValueKind);
        var (_, b) = await Get(s, "/v1/diff?person=owner&path=B.img");
        Assert.False(b.GetProperty("now").GetProperty("matchesBase").GetBoolean());
    }

    [Fact]
    public async Task BrokenBaseMap_IsTreatedAsUnknown_NotAnError()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2));
        File.WriteAllText(Path.Combine(_tree.Root("owner"), ".base.json"), "{ this is not json");
        var s = await Start();
        var (status, body) = await Get(s, "/v1/diff?person=owner&path=A.img");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("now").GetProperty("matchesBase").ValueKind);
    }

    [Fact]
    public async Task ResponseShape_HasEveryField()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2));
        var s = await Start();
        var (_, body) = await Get(s, "/v1/diff?person=owner&path=A.img");
        Assert.Equal(new[] { "baseTrust", "before", "canvases", "changes", "now", "semantic", "state", "summary", "truncated", "warnings" },
            body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "exists", "matchesBase", "sha256", "sizeBytes" },
            body.GetProperty("now").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "added", "canvasesChanged", "canvasesRecompressed", "changed", "removed" },
            body.GetProperty("summary").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SameRequestTwice_GivesTheSameBody_AlsoFromTheCache()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1, extraNodes: 30));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2, extraNodes: 20));
        var s = await Start();
        var first = await s.Client.GetStringAsync("/v1/diff?person=owner&path=A.img");
        var second = await s.Client.GetStringAsync("/v1/diff?person=owner&path=A.img");
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Truncation_ShowsInTheResponse()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1, extraNodes: 5));
        ImgFixture.Write(_tree.Root("owner"), "A.img", ImgFixture.Build(img => { for (int i = 0; i < 5; i++) img.AddProperty(new WzIntProperty($"e{i:000}", i + 100)); }));
        var options = new ViewerOptions(ViewerTree.Token, _tree.Roots) { MaxChanges = 3 };
        var s = await Start(options);
        var (_, body) = await Get(s, "/v1/diff?person=owner&path=A.img");
        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, body.GetProperty("changes").GetArrayLength());
        Assert.Contains(body.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("more than 3 changes"));
    }

    [Fact]
    public async Task CanvasDecodeBudget_FromTheOptions_IsHonoured()
    {
        byte[] Make(byte shade) => ImgFixture.Build(img => img.AddProperty(ImgFixture.PixelCanvas("c", 8, 8, (x, y) => (shade, shade, shade, (byte)255))));
        ImgFixture.Write(_tree.Root("canon"), "A.img", Make(10));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Make(200));
        var s = await Start(new ViewerOptions(ViewerTree.Token, _tree.Roots) { MaxCanvasDecodes = 0 });
        var (_, body) = await Get(s, "/v1/diff?person=owner&path=A.img");
        Assert.Equal("pixels_unverified", body.GetProperty("changes")[0].GetProperty("detail").GetString());
        Assert.NotEqual(0, body.GetProperty("warnings").GetArrayLength());
    }

    // ---- errors and auth

    private async Task AssertError(ViewerTestServer s, string url, HttpStatusCode status, string code)
    {
        var response = await s.Client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(code, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(_tree.Temp, text);
    }

    [Fact]
    public async Task Errors_AreTyped()
    {
        ImgFixture.Write(_tree.Root("canon"), "Good.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "Junk.img", new byte[3000]);
        ImgFixture.Write(_tree.Root("canon"), "Junk.img", Img(1));
        var s = await Start();

        await AssertError(s, "/v1/diff?person=canon&path=Good.img", HttpStatusCode.BadRequest, "unknown_source");
        await AssertError(s, "/v1/diff?path=Good.img", HttpStatusCode.BadRequest, "unknown_source");
        await AssertError(s, "/v1/diff?person=nobody&path=Good.img", HttpStatusCode.BadRequest, "unknown_source");
        await AssertError(s, "/v1/diff?person=owner", HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, "/v1/diff?person=owner&path=../Good.img", HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, "/v1/diff?person=owner&path=Backups/Good.img", HttpStatusCode.BadRequest, "bad_path");
        await AssertError(s, "/v1/diff?person=owner&path=Missing.img", HttpStatusCode.NotFound, "not_found");
        await AssertError(s, "/v1/diff?person=owner&path=Junk.img", HttpStatusCode.UnprocessableEntity, "parse_failed");
    }

    [Fact]
    public async Task TooLarge_And_FileBusy_AreTyped()
    {
        var bytes = Img(1);
        ImgFixture.Write(_tree.Root("canon"), "A.img", bytes);
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2));
        var small = await Start(new ViewerOptions(ViewerTree.Token, _tree.Roots) { MaxFileBytes = bytes.Length - 1 });
        await AssertError(small, "/v1/diff?person=owner&path=A.img", HttpStatusCode.RequestEntityTooLarge, "too_large");

        var fs = new FakeFileSystem { Rewrite = (n, real) => real with { After = real.After with { Size = real.After.Size + n } } };
        var busy = await Start(fs: fs);
        await AssertError(busy, "/v1/diff?person=owner&path=A.img", HttpStatusCode.Conflict, "file_busy");
    }

    [Fact]
    public async Task NeedsTheToken_AndOnlyGet()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2));
        var s = await Start();
        using var bare = new HttpClient { BaseAddress = s.Client.BaseAddress };
        var noToken = await bare.GetAsync("/v1/diff?person=owner&path=A.img");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        var post = await s.Client.PostAsync("/v1/diff?person=owner&path=A.img", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task Log_HasNoPathOrPerson()
    {
        ImgFixture.Write(_tree.Root("canon"), "SecretName.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "SecretName.img", Img(2));
        var s = await Start();
        await s.Client.GetAsync("/v1/diff?person=owner&path=SecretName.img");
        var logs = string.Join("\n", s.Logs);
        Assert.Contains("GET /v1/diff 200", logs);
        Assert.DoesNotContain("SecretName", logs);
        Assert.DoesNotContain("person=", logs);
    }

    // ---- leases and slots

    [Fact]
    public async Task AfterAnError_NoLeaseIsLeftHeld()
    {
        ImgFixture.Write(_tree.Root("canon"), "Junk.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "Junk.img", new byte[3000]);
        var s = await Start();
        var loader = s.App.Services.GetRequiredService<ViewerImageLoader>();
        var resolver = s.App.Services.GetRequiredService<ViewerPathResolver>();

        LoadedImage canon;
        using (var lease = loader.Load(resolver.Resolve("canon", "Junk.img"), CancellationToken.None))
            canon = lease.Loaded;
        await AssertError(s, "/v1/diff?person=owner&path=Junk.img", HttpStatusCode.UnprocessableEntity, "parse_failed");   // canon leased inside, owner failed

        Assert.False(canon.Disposed);   // still cached ...
        loader.Cache.Clear();
        Assert.True(canon.Disposed);    // ... and nobody holds it any more: the diff gave its lease back
    }

    [Fact]
    public async Task AfterASuccess_NoLeaseIsLeftHeld_AndTheDecodeSlotIsFree()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2));
        var s = await Start(_tree.Options(maxDecodes: 1));
        var loader = s.App.Services.GetRequiredService<ViewerImageLoader>();
        var resolver = s.App.Services.GetRequiredService<ViewerPathResolver>();
        LoadedImage canon, mine;
        using (var a = loader.Load(resolver.Resolve("canon", "A.img"), CancellationToken.None)) canon = a.Loaded;
        using (var b = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None)) mine = b.Loaded;

        for (int i = 0; i < 3; i++)   // with one decode slot, a leaked slot would hang the second call
            Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/v1/diff?person=owner&path=A.img")).StatusCode);

        loader.Cache.Clear();
        Assert.True(canon.Disposed);
        Assert.True(mine.Disposed);
    }

    [Fact]
    public async Task TimedOutDiff_KeepsItsSlotUntilTheWorkEnds_ThenEverythingIsFree()
    {
        ImgFixture.Write(_tree.Root("canon"), "A.img", Img(1));
        ImgFixture.Write(_tree.Root("owner"), "A.img", Img(2));
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var s = await Start(_tree.Options(timeout: TimeSpan.FromMilliseconds(400), maxRequests: 1, maxDecodes: 1), fs);

        await AssertError(s, "/v1/diff?person=owner&path=A.img", HttpStatusCode.GatewayTimeout, "timeout");
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertError(s, "/v1/resolve?source=owner&path=A.img", HttpStatusCode.ServiceUnavailable, "busy");   // slot still taken

        gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode last;
        do { last = (await s.Client.GetAsync("/v1/resolve?source=owner&path=A.img")).StatusCode; }
        while (last == HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < deadline);
        Assert.Equal(HttpStatusCode.OK, last);

        // and the decode slot is free too: a full diff works
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/v1/diff?person=owner&path=A.img")).StatusCode);
        var loader = s.App.Services.GetRequiredService<ViewerImageLoader>();
        loader.Cache.Clear();
    }

    // ---- options

    [Fact]
    public void Options_ReadTheDiffEnvVars_AndIgnoreBadValues()
    {
        var token = "abcdefgh" + new string('a', 30);
        var o = ViewerOptions.FromEnvironment(n => n switch
        {
            ViewerOptions.TokenEnvVar => token,
            ViewerOptions.MaxChangesEnvVar => "50",
            ViewerOptions.MaxCanvasDecodesEnvVar => "7",
            ViewerOptions.DiffBudgetSecondsEnvVar => "3",
            _ => null,
        });
        Assert.Equal((50, 7, TimeSpan.FromSeconds(3)), (o.MaxChanges, o.MaxCanvasDecodes, o.DiffBudget));

        var d = ViewerOptions.FromEnvironment(n => n == ViewerOptions.TokenEnvVar ? token : null);
        Assert.Equal((2000, 200, TimeSpan.FromSeconds(25)), (d.MaxChanges, d.MaxCanvasDecodes, d.DiffBudget));

        var bad = ViewerOptions.FromEnvironment(n => n == ViewerOptions.TokenEnvVar ? token : "abc");
        Assert.Equal((2000, 200, TimeSpan.FromSeconds(25)), (bad.MaxChanges, bad.MaxCanvasDecodes, bad.DiffBudget));
    }

    [Fact]
    public void BaseMap_RereadsWhenTheFileChanges()
    {
        var options = _tree.Options();
        var map = new ViewerBaseMap(options, new PosixViewerFileSystem());
        Assert.Null(map.Get("owner", "A.img"));
        File.WriteAllText(Path.Combine(_tree.Root("owner"), ".base.json"), "{ \"files\": { \"A.img\": \"ABC123\" } }");
        Assert.Equal("abc123", map.Get("owner", "A.img"));   // lower-cased
        Assert.Null(map.Get("owner", "B.img"));
        File.WriteAllText(Path.Combine(_tree.Root("owner"), ".base.json"), "{ \"files\": { \"B.img\": \"def456\", \"padding\": \"to change the size\" } }");
        Assert.Equal("def456", map.Get("owner", "B.img"));
        Assert.Null(map.Get("owner", "A.img"));
        Assert.Null(map.Get("nobody", "A.img"));
    }

    // ---- base map: caching of bad maps, key normalisation, advisory flag

    private ViewerBaseMap NewMap() => new(_tree.Options(), new PosixViewerFileSystem());
    private string BasePath(string person) => Path.Combine(_tree.Root(person), ".base.json");

    [Fact]
    public void BaseMap_MalformedMap_IsReadOnceUntilItChanges()
    {
        var map = NewMap();
        File.WriteAllText(BasePath("owner"), "{ this is not json");
        Assert.Null(map.Get("owner", "A.img"));
        Assert.Null(map.Get("owner", "A.img"));
        Assert.Null(map.Get("owner", "B.img"));
        Assert.Equal(1, map.Reads);

        File.WriteAllText(BasePath("owner"), "{ \"files\": { \"A.img\": \"abc\" } }");   // a new stamp: read again
        Assert.Equal("abc", map.Get("owner", "A.img"));
        Assert.Equal(2, map.Reads);
        Assert.Equal("abc", map.Get("owner", "A.img"));
        Assert.Equal(2, map.Reads);
    }

    [Fact]
    public void BaseMap_OversizedMap_IsNotReadAndNotRetried()
    {
        var map = NewMap();
        using (var f = File.Create(BasePath("owner")))
            f.SetLength(64L * 1024 * 1024 + 1);   // sparse
        Assert.Null(map.Get("owner", "A.img"));
        Assert.Null(map.Get("owner", "A.img"));
        Assert.Equal(0, map.Reads);   // the stamp's size is enough to refuse it
    }

    [Fact]
    public void BaseMap_KeysAreNormalised_CaseSlashesAndLeadingDot()
    {
        var map = NewMap();
        File.WriteAllText(BasePath("owner"),
            "{ \"files\": { \"String\\\\Pet.img\": \"AA11\", \"./Map/Back.img\": \"bb22\", \"/Mob/X.img\": \"cc33\" } }");
        Assert.Equal("aa11", map.Get("owner", "String/Pet.img"));
        Assert.Equal("aa11", map.Get("owner", "string/pet.img"));
        Assert.Equal("bb22", map.Get("owner", "Map/Back.img"));
        Assert.Equal("cc33", map.Get("owner", "Mob/X.img"));
    }

    [Fact]
    public void BaseMap_TwoSpellingsWithDifferentHashes_AreUnknown()
    {
        var map = NewMap();
        File.WriteAllText(BasePath("owner"), "{ \"files\": { \"A.img\": \"11\", \"a.img\": \"22\", \"B.img\": \"33\", \"b.IMG\": \"33\" } }");
        Assert.Null(map.Get("owner", "A.img"));
        Assert.Equal("33", map.Get("owner", "B.img"));
    }

    [Fact]
    public async Task Response_SaysTheBaseHashIsAdvisory_AndAnEditorWrittenHashLooksUnedited()
    {
        var canon = Img(1);
        var edited = Img(2);
        ImgFixture.Write(_tree.Root("canon"), "String/Pet.img", canon);
        ImgFixture.Write(_tree.Root("owner"), "String/Pet.img", edited);
        WriteBase("owner", "String/Pet.img", edited);   // the person records the hash of their own edit
        var s = await Start();

        var (_, body) = await Get(s, "/v1/diff?person=owner&path=String/Pet.img");
        Assert.Equal("advisory", body.GetProperty("baseTrust").GetString());
        Assert.True(body.GetProperty("now").GetProperty("matchesBase").GetBoolean());   // looks "not edited" ...
        Assert.Equal("changed", body.GetProperty("state").GetString());                  // ... but the diff against canon says otherwise
        Assert.NotEqual(body.GetProperty("before").GetProperty("sha256").GetString(), body.GetProperty("now").GetProperty("sha256").GetString());
    }
}
#endif
