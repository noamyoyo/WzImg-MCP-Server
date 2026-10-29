#if LINUX_BUILD
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

    private (ViewerImageLoader Loader, ViewerPathResolver Resolver, ViewerImageCache Cache) Make(ViewerOptions? options = null, IViewerFileSystem? fs = null, Action? clearErrors = null)
    {
        options ??= _tree.Options();
        fs ??= new PosixViewerFileSystem();
        var cache = new ViewerImageCache(options.CacheBytes);
        // never the real ErrorLogger by default: it is process wide and other tests use it
        return (new ViewerImageLoader(options, fs, cache, clearErrors ?? (() => { })), new ViewerPathResolver(options.Roots, fs), cache);
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
    public void Load_NoKeyFits_IsParseFailed_AndClearsTheLibraryErrors()
    {
        var bytes = ImgFixture.Bytes(WzMapleVersion.BMS, customIv: new byte[] { 1, 2, 3, 4 });   // a key the viewer does not know
        ImgFixture.Write(_tree.Root("owner"), "Custom.img", bytes);
        int cleared = 0;
        var (loader, resolver, cache) = Make(clearErrors: () => Interlocked.Increment(ref cleared));
        var ex = Fails(() => loader.Load(resolver.Resolve("owner", "Custom.img"), CancellationToken.None));
        AssertError(ex, 422, "parse_failed");
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void Load_ClearsTheLibraryErrorsAfterEveryParse_AlsoTheSuccessfulOnes_ButNotOnCacheHits()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        int cleared = 0;
        var (loader, resolver, _) = Make(clearErrors: () => Interlocked.Increment(ref cleared));
        var resolved = resolver.Resolve("owner", "A.img");
        loader.Load(resolved, CancellationToken.None).Dispose();
        Assert.Equal(1, cleared);
        loader.Load(resolved, CancellationToken.None).Dispose();   // cache hit: no parse
        Assert.Equal(1, cleared);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5000)]
    public void Load_GarbageFile_IsParseFailed(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        ImgFixture.Write(_tree.Root("owner"), "Junk.img", bytes);
        var (loader, resolver, _) = Make();
        AssertError(Fails(() => loader.Load(resolver.Resolve("owner", "Junk.img"), CancellationToken.None)), 422, "parse_failed");
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

    // ---- the file stamp (ctime and inode) and open failures

    [Fact]
    public void Rewrite_WithSameSizeAndRestoredMtime_IsNotServedFromTheCache()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img", ImgFixture.Build(i => i.AddProperty(new WzIntProperty("v", 1000))));
        var (loader, resolver, _) = Make();
        var mtime = File.GetLastWriteTimeUtc(path);
        string firstSha;
        using (var first = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None))
            firstSha = first.Loaded.Sha256;

        // one int replaced by another of the same encoded size, then the mtime put back (touch -r, rsync -t)
        var replacement = ImgFixture.Build(i => i.AddProperty(new WzIntProperty("v", 1001)));
        Assert.Equal(new FileInfo(path).Length, replacement.Length);
        File.WriteAllBytes(path, replacement);
        File.SetLastWriteTimeUtc(path, mtime);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));

        using var second = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.False(second.FromCache);
        Assert.NotEqual(firstSha, second.Loaded.Sha256);
        Assert.Equal(1001, ((WzIntProperty)second.Image["v"]!).Value);
    }

    [Fact]
    public void FileReplacedByAnotherInode_WithSameSizeMtimeAndCtime_IsAnotherKey()
    {
        var fs = new PosixViewerFileSystem();
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img");
        var a = fs.Stat(path)!.Value;
        var b = a with { Inode = a.Inode + 1 };
        Assert.NotEqual(new ImageKey("owner", "A.img", a.MtimeTicks, a.Size, "", a.CtimeTicks, a.Inode),
                        new ImageKey("owner", "A.img", b.MtimeTicks, b.Size, "", b.CtimeTicks, b.Inode));
        Assert.NotEqual(a, b);
        Assert.True(a.CtimeTicks > 0);
        Assert.True(a.Inode > 0);
    }

    [Fact]
    public void Stat_ReportsCtimeThatChangesWhenMtimeIsRestored()
    {
        var fs = new PosixViewerFileSystem();
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img");
        var before = fs.Stat(path)!.Value;
        Thread.Sleep(20);
        File.SetLastWriteTimeUtc(path, new DateTime(before.MtimeTicks + 621355968000000000L + 10_000_000L, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(path, new DateTime(before.MtimeTicks + 621355968000000000L, DateTimeKind.Utc));
        var after = fs.Stat(path)!.Value;
        Assert.Equal(before.MtimeTicks, after.MtimeTicks);
        Assert.NotEqual(before.CtimeTicks, after.CtimeTicks);
        Assert.NotEqual(before, after);
    }

    [Theory]
    [InlineData(4, 503, "busy")]       // EINTR on every attempt
    [InlineData(24, 503, "busy")]      // EMFILE
    [InlineData(13, 500, "internal_error")]   // EACCES
    [InlineData(5, 500, "internal_error")]    // EIO
    [InlineData(2, 404, "not_found")]  // ENOENT
    public void OpenFailure_IsClassifiedByErrno_NotAlwaysNotFound(int errno, int status, string code)
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img");
        int calls = 0;
        var fs = new PosixViewerFileSystem(opener: (p, flags) => { calls++; return (-1, errno); });
        AssertError(Fails(() => fs.Read(path, 1 << 20, null)), status, code);
        Assert.Equal(errno == 4 ? 8 : 1, calls);   // an interrupted open is retried 8 times, nothing else is retried
    }

    [Fact]
    public void OpenInterruptedFewerTimesThanTheLimit_Succeeds()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img");
        int calls = 0;
        var fs = new PosixViewerFileSystem(opener: (p, flags) => ++calls < 4 ? (-1, 4) : OpenViaLibc(p, flags));
        Assert.NotEmpty(fs.Read(path, 1 << 20, null).Bytes);
        Assert.Equal(4, calls);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int libcOpen([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);

    private static (int, int) OpenViaLibc(string path, int flags)
    {
        int fd = libcOpen(path, flags);
        return (fd, fd < 0 ? System.Runtime.InteropServices.Marshal.GetLastPInvokeError() : 0);
    }

    [Fact]
    public void EnvRequire_FailsWhenTheCapabilityIsMissing_AndSkipsOnlyWithTheVariable()
    {
        var previous = Environment.GetEnvironmentVariable(EnvRequire.AllowSkipVariable);
        try
        {
            EnvRequire.Capability(true, "available");   // nothing happens

            Environment.SetEnvironmentVariable(EnvRequire.AllowSkipVariable, null);
            Assert.ThrowsAny<Exception>(() => EnvRequire.Capability(false, "symbolic links cannot be created here"));
            Assert.Throws<Xunit.Sdk.FailException>(() => EnvRequire.Capability(false, "mkfifo is not available"));

            Environment.SetEnvironmentVariable(EnvRequire.AllowSkipVariable, "0");
            Assert.Throws<Xunit.Sdk.FailException>(() => EnvRequire.Capability(false, "x"));

            Environment.SetEnvironmentVariable(EnvRequire.AllowSkipVariable, "1");
            Assert.Throws<Xunit.SkipException>(() => EnvRequire.Capability(false, "symbolic links cannot be created here"));
        }
        finally { Environment.SetEnvironmentVariable(EnvRequire.AllowSkipVariable, previous); }
    }

    // ---- the safe open

    [SkippableFact]
    public void Fifo_IsRefusedWithoutBlocking()
    {
        var fifo = Path.Combine(_tree.Root("owner"), "img", "Pipe.img");
        EnvRequire.Capability(ImgFixture.TryMakeFifo(fifo), "mkfifo is not available");
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
        EnvRequire.Capability(ImgFixture.TrySymlink(link, real, directory: false), "symbolic links cannot be created here");
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
        EnvRequire.Capability(ImgFixture.TrySymlink(dir, other, directory: true), "symbolic links cannot be created here");

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

    // ---- single flight

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Yield();
        }
    }

    [Fact]
    public async Task SingleFlight_ConcurrentLoadsOfTheSameFile_ShareOneParse()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var (loader, resolver, cache) = Make(fs: fs);
        var resolved = resolver.Resolve("owner", "A.img");

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => loader.Load(resolved, CancellationToken.None))).ToList();
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitUntil(() => loader.Joins == 3);   // the other three are waiting for the first
        gate.Set();
        var leases = await Task.WhenAll(tasks);

        Assert.Equal(1, fs.Reads);
        Assert.Equal(1, loader.Parses);
        Assert.Equal(1, cache.Count);
        Assert.All(leases, l => Assert.Same(leases[0].Loaded, l.Loaded));
        var loaded = leases[0].Loaded;
        foreach (var l in leases) l.Dispose();
        Assert.False(loaded.Disposed, "still cached");
    }

    [Fact]
    public async Task SingleFlight_ResultTooBigToCache_IsStillSharedAndDisposedWithTheLastLease()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var (loader, resolver, cache) = Make(_tree.Options(cacheBytes: 100), fs);
        var resolved = resolver.Resolve("owner", "A.img");

        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() => loader.Load(resolved, CancellationToken.None))).ToList();
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitUntil(() => loader.Joins == 2);
        gate.Set();
        var leases = await Task.WhenAll(tasks);

        Assert.Equal(0, cache.Count);
        Assert.Equal(1, loader.Parses);
        var loaded = leases[0].Loaded;
        Assert.All(leases, l => Assert.Same(loaded, l.Loaded));
        leases[0].Dispose();
        leases[1].Dispose();
        Assert.False(loaded.Disposed);
        leases[2].Dispose();
        Assert.True(loaded.Disposed, "every reference, the flight's included, has been given back");
    }

    [Fact]
    public async Task SingleFlight_WaiterThatIsCancelled_LeavesTheParseRunning()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var (loader, resolver, _) = Make(fs: fs);
        var resolved = resolver.Resolve("owner", "A.img");

        var creator = Task.Run(() => loader.Load(resolved, CancellationToken.None));
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)));
        using var cts = new CancellationTokenSource();
        var waiter = Task.Run(() => loader.Load(resolved, cts.Token));
        await WaitUntil(() => loader.Joins == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        gate.Set();
        using var lease = await creator;
        Assert.Equal(1, fs.Reads);
        Assert.False(lease.Loaded.Disposed);
    }

    [Fact]
    public async Task SingleFlight_CreatorThatIsCancelled_DoesNotFailTheWaiter()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var (loader, resolver, _) = Make(fs: fs);
        var resolved = resolver.Resolve("owner", "A.img");

        using var cts = new CancellationTokenSource();
        var creator = Task.Run(() => loader.Load(resolved, cts.Token));
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)));
        var waiter = Task.Run(() => loader.Load(resolved, CancellationToken.None));
        await WaitUntil(() => loader.Joins == 1);

        cts.Cancel();      // the creator's request goes away ...
        gate.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);
        using var lease = await waiter;   // ... the waiter parses for itself
        Assert.NotNull(lease.Image);
        Assert.Equal(2, fs.Reads);
        Assert.Equal(1, loader.Parses);
    }

    [Fact]
    public async Task SingleFlight_ParseFailure_IsTheSameTypedErrorForEveryone()
    {
        ImgFixture.Write(_tree.Root("owner"), "Junk.img", new byte[3000]);
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem { BlockReads = gate };
        var (loader, resolver, _) = Make(fs: fs);
        var resolved = resolver.Resolve("owner", "Junk.img");

        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() => loader.Load(resolved, CancellationToken.None))).ToList();
        Assert.True(await fs.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitUntil(() => loader.Joins == 2);
        gate.Set();
        foreach (var t in tasks)
            AssertError(await Assert.ThrowsAsync<ViewerApiException>(() => t), 422, "parse_failed");
        Assert.Equal(1, loader.Parses);
    }

    // ---- fail closed

    [Fact]
    public void Load_WithoutTheIdentityTheResolverRecorded_IsRefusedBeforeAnyRead()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var fs = new FakeFileSystem { IdentityOverride = _ => null };
        var (loader, resolver, _) = Make(fs: fs);
        var resolved = resolver.Resolve("owner", "A.img");
        Assert.Null(resolved.Identity);
        AssertError(Fails(() => loader.Load(resolved, CancellationToken.None)), 400, "bad_path");
        Assert.Equal(0, fs.Reads);
    }

    [Fact]
    public void Read_CompareWithTheProcLink_AcceptsOnlyTheCheckedPath()
    {
        var path = ImgFixture.Write(_tree.Root("owner"), "A.img");
        var identity = new PosixViewerFileSystem().Identity(path);
        Assert.NotNull(identity);

        Assert.Equal(new FileInfo(path).Length, new PosixViewerFileSystem(_ => path).Read(path, 1 << 20, identity).Bytes.Length);                 // matches
        AssertError(Fails(() => new PosixViewerFileSystem(_ => path + "x").Read(path, 1 << 20, identity)), 400, "bad_path");                      // points elsewhere
        AssertError(Fails(() => new PosixViewerFileSystem(_ => null).Read(path, 1 << 20, identity)), 400, "bad_path");                            // /proc unreadable: fail closed
        AssertError(Fails(() => new PosixViewerFileSystem(_ => throw new UnauthorizedAccessException()).Read(path, 1 << 20, identity)), 400, "bad_path");
        AssertError(Fails(() => new PosixViewerFileSystem(_ => throw new IOException()).Read(path, 1 << 20, identity)), 400, "bad_path");
        // no expected identity (the manifest): the link is not compared at all
        Assert.NotEmpty(new PosixViewerFileSystem(_ => null).Read(path, 1 << 20, null).Bytes);
    }

    // ---- memory

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_OutOfMemory_Is413_AndTheCacheIsEmptied(bool insufficient)
    {
        ImgFixture.Write(_tree.Root("owner"), "Keep.img");
        ImgFixture.Write(_tree.Root("owner"), "Boom.img");
        var (loader, resolver, cache) = Make();
        loader.Load(resolver.Resolve("owner", "Keep.img"), CancellationToken.None).Dispose();
        Assert.Equal(1, cache.Count);

        loader.ParseProbe = _ => throw (insufficient ? new InsufficientMemoryException("detail that must not leak") : new OutOfMemoryException("detail that must not leak"));
        var ex = Fails(() => loader.Load(resolver.Resolve("owner", "Boom.img"), CancellationToken.None));
        AssertError(ex, 413, "too_large");
        Assert.DoesNotContain("detail", ex.Message);
        Assert.Equal(0, cache.Count);

        loader.ParseProbe = null;   // the loader is still usable
        using var again = loader.Load(resolver.Resolve("owner", "Boom.img"), CancellationToken.None);
        Assert.NotNull(again.Image);
    }

    [Fact]
    public void Read_OutOfMemory_Is413()
    {
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var fs = new FakeFileSystem { Rewrite = (_, _) => throw new OutOfMemoryException() };
        var (loader, resolver, _) = Make(fs: fs);
        AssertError(Fails(() => loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None)), 413, "too_large");
    }

    // ---- manifest in the key

    [Fact]
    public void Cache_ChangedManifestKey_IsANewKey()
    {
        ImgFixture.WriteManifest(_tree.Root("owner"), "BMS");
        ImgFixture.Write(_tree.Root("owner"), "A.img");
        var (loader, resolver, cache) = Make();
        using var first = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.Equal("BMS", first.Loaded.Key.Manifest);

        File.WriteAllText(Path.Combine(_tree.Root("owner"), "img", "manifest.json"), "{ \"encryption\": \"GMS\", \"padding\": \"to change the size\" }");
        using var second = loader.Load(resolver.Resolve("owner", "A.img"), CancellationToken.None);
        Assert.False(second.FromCache);
        Assert.Equal("GMS", second.Loaded.Key.Manifest);
        Assert.Equal(1, cache.Count);
        Assert.Equal("BMS", second.Loaded.KeyUsed);   // the file is still BMS: the order changed, the answer did not
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
            [ViewerOptions.MaxFileMbEnvVar] = "7", [ViewerOptions.MaxDecodesEnvVar] = "3", [ViewerOptions.CacheMbEnvVar] = "50", [ViewerOptions.LinkBudgetMbEnvVar] = "5",
        };
        var o = ViewerOptions.FromEnvironment(n => Env(set, n));
        Assert.Equal(7L * 1024 * 1024, o.MaxFileBytes);
        Assert.Equal(3, o.MaxDecodes);
        Assert.Equal(50L * 1024 * 1024, o.CacheBytes);
        Assert.Equal(5L * 1024 * 1024, o.LinkBudgetBytes);

        var defaults = ViewerOptions.FromEnvironment(n => n == ViewerOptions.TokenEnvVar ? token : null);
        Assert.Equal(64L * 1024 * 1024, defaults.MaxFileBytes);
        Assert.Equal(64L * 1024 * 1024, defaults.LinkBudgetBytes);
        Assert.Equal(2, defaults.MaxDecodes);
        Assert.Equal(256L * 1024 * 1024, defaults.CacheBytes);

        foreach (var bad in new[] { "abc", "-1", "0", "1.5", "99999999999", "" })
        {
            var b = ViewerOptions.FromEnvironment(n => n == ViewerOptions.TokenEnvVar ? token
                : n is ViewerOptions.MaxFileMbEnvVar or ViewerOptions.MaxDecodesEnvVar ? bad : null);
            Assert.Equal(64L * 1024 * 1024, b.MaxFileBytes);
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
