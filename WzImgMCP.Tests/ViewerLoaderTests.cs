#if LINUX_BUILD
using MapleLib.Helpers;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;

namespace WzImgMCP.Tests;

/// <summary>
/// The safe reader, the parse with key fallback, the stable-read retry and the parsed-image cache, on synthetic .img
/// files made with the library's own writer. Uses the resolver (PathGuard), so it shares the non-parallel collection.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerLoaderTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly ViewerTree _tree = new();

    public ViewerLoaderTests()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _tree.Dispose();
    }

    private (ViewerImageLoader Loader, ViewerPathResolver Resolver, ViewerImageCache Cache) Make(ViewerOptions? options = null, IViewerFileSystem? fs = null)
    {
        options ??= _tree.Options();
        fs ??= new PosixViewerFileSystem();
        var cache = new ViewerImageCache(options.CacheBytes);
        return (new ViewerImageLoader(options, fs, cache), new ViewerPathResolver(options.Roots, fs), cache);
    }

    private static ViewerApiException Fails(Action action) => Assert.Throws<ViewerApiException>(action);

    private static void AssertError(ViewerApiException ex, int status, string code)
    {
        Assert.Equal(code, ex.Code);
        Assert.Equal(status, ex.Status);
        Assert.DoesNotContain("/tmp", ex.Message);   // never an absolute path
    }

    // ---- parse and keys

    [Fact]
    public void Load_ParsesASyntheticImage()
    {
        ImgFixture.Write(_tree.Root("owner"), "String/Fixture.img");
        var (loader, resolver, _) = Make();
        using var lease = loader.Load(resolver.Resolve("owner", "String/Fixture.img"), CancellationToken.None);
        Assert.Equal("BMS", lease.Loaded.KeyUsed);
        Assert.False(lease.FromCache);
        Assert.Equal(6, lease.Image.WzProperties.Count);
        Assert.Equal(new FileInfo(Path.Combine(_tree.Root("owner"), "img", "String", "Fixture.img")).Length, lease.Loaded.FileBytes);
        Assert.True(lease.Loaded.Cost > lease.Loaded.FileBytes);
    }

    [Theory]
    [InlineData(WzMapleVersion.GMS)]
    [InlineData(WzMapleVersion.EMS)]
    [InlineData(WzMapleVersion.BMS)]
    public void Load_FindsTheKeyTheFileWasWrittenWith(WzMapleVersion written)
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img", written);
        var (loader, resolver, _) = Make();   // no manifest: default order BMS, GMS, EMS
        using var lease = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.Equal(written.ToString(), lease.Loaded.KeyUsed);
        Assert.Equal(6, lease.Image.WzProperties.Count);
    }

    [Fact]
    public void Load_ManifestKeyDoesNotHaveToMatch()
    {
        ImgFixture.WriteManifest(_tree.Root("owner"), "GMS");        // the manifest says GMS ...
        ImgFixture.Write(_tree.Root("owner"), "A.img", WzMapleVersion.EMS);   // ... the file is EMS
        var (loader, resolver, _) = Make();
        using var lease = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.Equal("EMS", lease.Loaded.KeyUsed);
    }

    [Fact]
    public void Load_ManifestKeyIsUsedWhenItMatches()
    {
        ImgFixture.WriteManifest(_tree.Root("owner"), "gms");
        ImgFixture.Write(_tree.Root("owner"), "A.img", WzMapleVersion.GMS);
        var (loader, resolver, _) = Make();
        using var lease = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.Equal("GMS", lease.Loaded.KeyUsed);
    }

    [Fact]
    public void KeyOrder_PreferredFirst_ThenBmsGmsEms_EachOnce()
    {
        Assert.Equal(new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS }, ViewerImageLoader.KeyOrder(null));
        Assert.Equal(new[] { WzMapleVersion.GMS, WzMapleVersion.BMS, WzMapleVersion.EMS }, ViewerImageLoader.KeyOrder(WzMapleVersion.GMS));
        Assert.Equal(new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS }, ViewerImageLoader.KeyOrder(WzMapleVersion.BMS));
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("CUSTOM")]
    [InlineData("GENERATE")]
    [InlineData("")]
    public void Load_UnusableManifestKey_FallsBackToTheDefaultOrder(string encryption)
    {
        ImgFixture.WriteManifest(_tree.Root("owner"), encryption);
        ImgFixture.Write(_tree.Root("owner"), "A.img", WzMapleVersion.GMS);
        var (loader, resolver, _) = Make();
        using var lease = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.Equal("GMS", lease.Loaded.KeyUsed);
    }

    [Fact]
    public void Load_NoKeyFits_IsParseFailed_AndLeavesNoLoggedErrors()
    {
        var bytes = ImgFixture.Bytes(WzMapleVersion.BMS, customIv: new byte[] { 1, 2, 3, 4 });   // a key the viewer does not know
        ImgFixture.Write(_tree.Root("owner"), "Custom.img", bytes);
        var (loader, resolver, cache) = Make();
        var ex = Fails(() => loader.Load(resolver.Resolve("owner", "Custom.img"), CancellationToken.None));
        AssertError(ex, 422, "parse_failed");
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, ErrorLogger.NumberOfErrorsPresent());
    }

    [Fact]
    public void Load_TruncatedFile_IsParseFailed()
    {
        var bytes = ImgFixture.Bytes(WzMapleVersion.BMS);
        ImgFixture.Write(_tree.Root("owner"), "Cut.img", bytes.Take(bytes.Length / 2).ToArray());
        var (loader, resolver, _) = Make();
        AssertError(Fails(() => loader.Load(resolver.Resolve("owner", "Cut.img"), CancellationToken.None)), 422, "parse_failed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5000)]
    public void Load_GarbageFile_IsParseFailed_AndLeavesNoLoggedErrors(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        ImgFixture.Write(_tree.Root("owner"), "Junk.img", bytes);
        var (loader, resolver, _) = Make();
        AssertError(Fails(() => loader.Load(resolver.Resolve("owner", "Junk.img"), CancellationToken.None)), 422, "parse_failed");
        Assert.Equal(0, ErrorLogger.NumberOfErrorsPresent());
    }

    [Fact]
    public void Load_Cancelled_StopsBeforeParsing()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var (loader, resolver, cache) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => loader.Load(resolver.Resolve("owner", "A.img"), cts.Token));
        Assert.Equal(0, cache.Count);
    }

    // ---- size cap

    [Fact]
    public void Load_FileOverTheCap_IsTooLarge_AndNotRead()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "Big.img");
        var size = new FileInfo(path).Length;
        var fs = new FakeFileSystem();
        var (loader, resolver, _) = Make(_tree.Options(maxFileBytes: size - 1), fs);
        AssertError(Fails(() => loader.Load(resolver.Resolve("owner", "Big.img"), CancellationToken.None)), 413, "too_large");
        Assert.Equal(0, fs.Reads);

        // exactly the cap is fine
        var (loader2, resolver2, _) = Make(_tree.Options(maxFileBytes: size));
        using var lease = loader2.Load(resolver2.Resolve("owner", "Big.img"), CancellationToken.None);
        Assert.NotNull(lease.Image);
    }

    [Fact]
    public void FileSystem_Read_EnforcesTheCapItself()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "Big.img");
        var fs = new PosixViewerFileSystem();
        AssertError(Fails(() => fs.Read(path, new FileInfo(path).Length - 1, null)), 413, "too_large");
        Assert.Equal(new FileInfo(path).Length, fs.Read(path, new FileInfo(path).Length, null).Bytes.Length);
    }

    // ---- stable read

    [Fact]
    public void Load_FileChangedWhileRead_IsReadAgainOnce()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var fs = new FakeFileSystem { Rewrite = (n, real) => n == 1 ? real with { After = real.After with { MtimeTicks = real.After.MtimeTicks + 1 } } : real };
        var (loader, resolver, _) = Make(fs: fs);
        using var lease = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.Equal(2, fs.Reads);
        Assert.NotNull(lease.Image);
    }

    [Fact]
    public void Load_FileStillChanging_IsFileBusy()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var fs = new FakeFileSystem { Rewrite = (n, real) => real with { After = real.After with { Size = real.After.Size + n } } };
        var (loader, resolver, cache) = Make(fs: fs);
        AssertError(Fails(() => loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None)), 409, "file_busy");
        Assert.Equal(2, fs.Reads);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Load_ShortRead_CountsAsChanged()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var fs = new FakeFileSystem { Rewrite = (n, real) => real with { Bytes = real.Bytes.Take(real.Bytes.Length - 1).ToArray() } };
        var (loader, resolver, _) = Make(fs: fs);
        AssertError(Fails(() => loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None)), 409, "file_busy");
    }

    // ---- the safe open

    [SkippableFact]
    public void Fifo_IsRefusedWithoutBlocking()
    {
        var fifo = Path.Combine(_tree.Root("owner"), "img", "Pipe.img");
        Skip.IfNot(ImgFixture.TryMakeFifo(fifo), "mkfifo is not available");
        var (loader, resolver, _) = Make();

        ViewerApiException? error = null;
        var worker = Task.Run(() => error = Assert.Throws<ViewerApiException>(() => loader.Load(resolver.Resolve("owner", "Pipe.img"), CancellationToken.None)));
        Assert.True(worker.Wait(TimeSpan.FromSeconds(20)), "reading a FIFO blocked");
        AssertError(error!, 400, "bad_path");

        // the file system itself, straight at the FIFO
        var direct = Task.Run(() => Assert.Throws<ViewerApiException>(() => new PosixViewerFileSystem().Read(fifo, 1 << 20, null)));
        Assert.True(direct.Wait(TimeSpan.FromSeconds(20)), "reading a FIFO blocked");
        AssertError(direct.Result, 400, "bad_path");
    }

    [SkippableFact]
    public void FinalSymlink_IsRefusedByTheReader()
    {
        var real = ImgFixture.Write(_tree.Root("owner"), "Real.img");
        var link = Path.Combine(_tree.Root("owner"), "img", "Link.img");
        Skip.IfNot(ImgFixture.TrySymlink(link, real, directory: false), "symbolic links cannot be created here");
        AssertError(Fails(() => new PosixViewerFileSystem().Read(link, 1 << 20, null)), 400, "bad_path");
    }

    [SkippableFact]
    public void ParentSwappedForASymlink_AfterTheCheck_IsDetected()
    {
        ImgFixture.Write(_tree.Root("owner"), "Dir/A.img");
        var (loader, resolver, _) = Make();
        var resolved = resolver.Resolve("owner", "Dir/A.img");   // checked while nothing is wrong
        Assert.NotNull(resolved.Identity);

        // an attacker replaces the folder by a link to another folder that has a file of the same name
        var other = Path.Combine(_tree.Temp, "elsewhere");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "A.img"), ImgFixture.Bytes(WzMapleVersion.BMS));
        var dir = Path.Combine(_tree.Root("owner"), "img", "Dir");
        Directory.Delete(dir, true);
        Skip.IfNot(ImgFixture.TrySymlink(dir, other, directory: true), "symbolic links cannot be created here");

        AssertError(Fails(() => loader.Load(resolved, CancellationToken.None)), 400, "bad_path");
    }

    [Fact]
    public void FileReplacedAfterTheCheck_IsDetectedByInodeCompare()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img");
        var (loader, resolver, _) = Make();
        var resolved = resolver.Resolve("owner", "A.img");

        var replacement = path + ".new";
        File.WriteAllBytes(replacement, ImgFixture.Bytes(WzMapleVersion.BMS));
        File.Move(replacement, path, overwrite: true);   // same name, different inode

        AssertError(Fails(() => loader.Load(resolved, CancellationToken.None)), 400, "bad_path");
    }

    [Fact]
    public void Directory_IsRefusedByTheReader()
    {
        var dir = Path.Combine(_tree.Root("owner"), "img", "D.img");
        Directory.CreateDirectory(dir);
        AssertError(Fails(() => new PosixViewerFileSystem().Read(dir, 1 << 20, null)), 400, "bad_path");
    }

    [Fact]
    public void MissingFile_IsNotFound_WithoutThePath()
    {
        var ex = Fails(() => new PosixViewerFileSystem().Read(Path.Combine(_tree.Temp, "nope.img"), 1 << 20, null));
        AssertError(ex, 404, "not_found");
        Assert.DoesNotContain("nope", ex.Message);
    }

    // ---- cache

    [Fact]
    public void Cache_SecondLoadIsAHit_AndTheSameImage()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var fs = new FakeFileSystem();
        var (loader, resolver, cache) = Make(fs: fs);
        var resolved = resolver.Resolve("owner", "A.img");
        using var first = loader.Load(resolved, CancellationToken.None);
        using var second = loader.Load(resolved, CancellationToken.None);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Same(first.Loaded, second.Loaded);
        Assert.Equal(1, fs.Reads);
        Assert.Equal(1, cache.Count);
        Assert.Equal(first.Loaded.Cost, cache.Bytes);
    }

    [Fact]
    public void Cache_ChangedMtime_IsANewKey_AndTheOldEntryGoes()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img");
        var (loader, resolver, cache) = Make();
        LoadedImage oldOne;
        using (var first = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None))
            oldOne = first.Loaded;

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));   // same bytes, newer mtime
        using var second = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.False(second.FromCache);
        Assert.NotSame(oldOne, second.Loaded);
        Assert.NotEqual(oldOne.Key.MtimeTicks, second.Loaded.Key.MtimeTicks);
        Assert.Equal(1, cache.Count);
        Assert.True(oldOne.Disposed);
    }

    [Fact]
    public void Cache_ChangedSize_IsANewKey()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var (loader, resolver, cache) = Make();
        using var first = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        ImgFixture.Write(_tree.Root("owner"), "A.img", WzMapleVersion.BMS, img => img.AddProperty(new WzStringProperty("more", "a longer file now")));
        using var second = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.False(second.FromCache);
        Assert.Equal(7, second.Image.WzProperties.Count);
        Assert.Equal(6, first.Image.WzProperties.Count);   // the lease held across the change still works
    }

    [Fact]
    public void Cache_SourcesNeverShareEntries()
    {
        ImgFixture.Write(_tree.Root("canon"), "Mob/B.img", WzMapleVersion.BMS, img => ((WzIntProperty)img["marker"]).Value = 100);
        ImgFixture.Write(_tree.Root("owner"), "Mob/B.img", WzMapleVersion.BMS, img => ((WzIntProperty)img["marker"]).Value = 200);
        var (loader, resolver, cache) = Make();
        using var canon = loader.Load(resolver.Resolve("canon", "Mob/B.img"), CancellationToken.None);
        using var owner = loader.Load(resolver.Resolve("owner", "Mob/B.img"), CancellationToken.None);
        Assert.NotSame(canon.Loaded, owner.Loaded);
        Assert.False(owner.FromCache);
        Assert.Equal(2, cache.Count);
        Assert.Equal(100, ((WzIntProperty)canon.Image["marker"]).Value);
        Assert.Equal(200, ((WzIntProperty)owner.Image["marker"]).Value);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_AndDisposesOnlyWhenNoLeaseHoldsIt()
    {
        foreach (var n in new[] { "A", "B", "C" })
            ImgFixture.Write(_tree.Root("owner"), n + ".img");
        // room for two entries
        var probe = Make();
        long cost;
        using (var l = probe.Loader.Load(probe.Resolver.Resolve("owner", "A.img"), CancellationToken.None)) cost = l.Loaded.Cost;
        var (loader, resolver, cache) = Make(_tree.Options(cacheBytes: cost * 2 + cost / 2));

        var a = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);   // keep a lease on A
        LoadedImage b, c;
        using (var lb = loader.Load(resolver.Resolve("owner", "B.img"), CancellationToken.None)) b = lb.Loaded;
        using (var lc = loader.Load(resolver.Resolve("owner", "C.img"), CancellationToken.None)) c = lc.Loaded;   // A is the oldest: evicted

        Assert.Equal(2, cache.Count);
        Assert.False(a.Loaded.Disposed, "evicted but still leased: must stay alive");
        Assert.Equal(6, a.Image.WzProperties.Count);
        Assert.False(b.Disposed);
        Assert.False(c.Disposed);

        var loadedA = a.Loaded;
        a.Dispose();
        Assert.True(loadedA.Disposed, "last lease gone: disposed");
        a.Dispose();   // twice is fine

        // a hit refreshes B, so the next eviction takes C
        using (loader.Load(resolver.Resolve("owner", "B.img"), CancellationToken.None)) { }
        using (loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None)) { }
        Assert.True(c.Disposed);
        Assert.False(b.Disposed);
        Assert.True(cache.Bytes <= cost * 2 + cost / 2);
    }

    [Fact]
    public void Cache_EntryBiggerThanTheCap_IsNotCached_AndDisposedWithItsLease()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var (loader, resolver, cache) = Make(_tree.Options(cacheBytes: 100));
        var lease = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        var loaded = lease.Loaded;
        Assert.Equal(0, cache.Count);
        Assert.False(loaded.Disposed);
        lease.Dispose();
        Assert.True(loaded.Disposed);
    }

    [Fact]
    public void Cache_Clear_DisposesUnleasedEntries()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var (loader, resolver, cache) = Make();
        LoadedImage loaded;
        using (var lease = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None)) loaded = lease.Loaded;
        Assert.False(loaded.Disposed);
        cache.Clear();
        Assert.True(loaded.Disposed);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void Estimator_GrowsWithTheTree_AndNeverUndercountsTheFile()
    {
        var small = ImgFixture.Write(_tree.Root("owner"), "S.img");
        ImgFixture.Write(_tree.Root("owner"), "L.img", WzMapleVersion.BMS, img =>
        {
            for (int i = 0; i < 500; i++) img.AddProperty(new WzStringProperty("name" + i, new string('x', 40)));
        });
        var (loader, resolver, _) = Make();
        using var s = loader.Load(resolver.Resolve("owner", "S.img"), CancellationToken.None);
        using var l = loader.Load(resolver.Resolve("owner", "L.img"), CancellationToken.None);
        Assert.True(s.Loaded.Cost >= s.Loaded.FileBytes);
        Assert.True(l.Loaded.Cost > s.Loaded.Cost + 500 * 100);
    }

    // ---- options

    [Fact]
    public void Options_ReadTheNewEnvVars_AndIgnoreBadValues()
    {
        string? Env(Dictionary<string, string> d, string n) => d.GetValueOrDefault(n);
        var token = new string('a', 20) + "bcdefghijklmnop";
        var set = new Dictionary<string, string>
        {
            [ViewerOptions.TokenEnvVar] = token,
            [ViewerOptions.MaxFileMbEnvVar] = "7", [ViewerOptions.MaxDecodesEnvVar] = "3", [ViewerOptions.CacheMbEnvVar] = "50",
        };
        var o = ViewerOptions.FromEnvironment(n => Env(set, n));
        Assert.Equal(7L * 1024 * 1024, o.MaxFileBytes);
        Assert.Equal(3, o.MaxDecodes);
        Assert.Equal(50L * 1024 * 1024, o.CacheBytes);

        var defaults = ViewerOptions.FromEnvironment(n => n == ViewerOptions.TokenEnvVar ? token : null);
        Assert.Equal(128L * 1024 * 1024, defaults.MaxFileBytes);
        Assert.Equal(2, defaults.MaxDecodes);
        Assert.Equal(256L * 1024 * 1024, defaults.CacheBytes);

        foreach (var bad in new[] { "abc", "-1", "0", "1.5", "99999999999", "" })
        {
            var b = ViewerOptions.FromEnvironment(n => n == ViewerOptions.TokenEnvVar ? token
                : n is ViewerOptions.MaxFileMbEnvVar or ViewerOptions.MaxDecodesEnvVar ? bad : null);
            Assert.Equal(128L * 1024 * 1024, b.MaxFileBytes);
            Assert.Equal(2, b.MaxDecodes);
        }
    }

    [Fact]
    public void Resolver_RecordsTheFileIdentity()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var resolver = new ViewerPathResolver(_tree.Roots);
        var resolved = resolver.Resolve("owner", "A.img");
        Assert.NotNull(resolved.Identity);
        Assert.Equal(resolved.Identity, new PosixViewerFileSystem().Identity(resolved.FullPath));
    }
}
#endif
