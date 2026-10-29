using WzImgMCP.Utils;
using WzImgMCP.Viewer;

namespace WzImgMCP.Tests;

/// <summary>
/// The viewer's path resolver: strict text pattern first, then the file system checks (regular file, no symbolic
/// link, inside the source's img folder). It reads WZIMG_ALLOWED_ROOT through PathGuard, so it runs in the same
/// non-parallel collection as the other tests that set it.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerPathResolverTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _tmp = Directory.CreateTempSubdirectory("viewer-resolver-").FullName;
    private readonly string _canon;
    private readonly string _owner;
    private readonly ViewerPathResolver _resolver;

    public ViewerPathResolverTests()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _canon = Directory.CreateDirectory(Path.Combine(_tmp, "canon")).FullName;
        _owner = Directory.CreateDirectory(Path.Combine(_tmp, "owner")).FullName;
        var friend = Directory.CreateDirectory(Path.Combine(_tmp, "friend")).FullName;
        _resolver = new ViewerPathResolver(new Dictionary<string, string> { ["canon"] = _canon, ["owner"] = _owner, ["friend"] = friend });

        WriteImg(_canon, "String/Consume.img", "canon-consume");
        WriteImg(_owner, "String/Consume.img", "owner-consume!");
        WriteImg(_owner, "Map/Map/Map1/100000000.img", "map");
        WriteImg(_owner, "Mob/my file_1-a.v2.img", "spaces");
        WriteImg(_owner, "Backups/x.img", "backup");
        WriteImg(_owner, "Top.img", "top");
        Directory.CreateDirectory(Path.Combine(_owner, "img", "Dir", "x.img"));   // a directory named like an image
        WriteImg(friend, "String/Consume.img", "friend");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static void WriteImg(string root, string relative, string content)
    {
        var path = Path.Combine(root, "img", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static bool TrySymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    private static bool TryDirSymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    private ViewerApiException Fails(string? source, string? path)
        => Assert.Throws<ViewerApiException>(() => _resolver.Resolve(source, path));

    private static void AssertError(ViewerApiException ex, int status, string code)
    {
        Assert.Equal(code, ex.Code);
        Assert.Equal(status, ex.Status);
    }

    // ---- accepted

    [Theory]
    [InlineData("String/Consume.img")]
    [InlineData("Map/Map/Map1/100000000.img")]
    [InlineData("Mob/my file_1-a.v2.img")]    // space, '_', '-' and '.' inside a name
    [InlineData("Top.img")]                    // directly under img
    public void ValidPath_IsResolved(string path)
    {
        var r = _resolver.Resolve("owner", path);
        Assert.Equal("owner", r.Source);
        Assert.Equal(path, r.RelativePath);
        Assert.Equal(Path.Combine(_owner, "img", path), r.FullPath);
        Assert.Equal(new FileInfo(r.FullPath).Length, r.SizeBytes);
    }

    [Fact]
    public void EachSource_ReadsItsOwnRoot()
    {
        Assert.Equal(13, _resolver.Resolve("canon", "String/Consume.img").SizeBytes);
        Assert.Equal(14, _resolver.Resolve("owner", "String/Consume.img").SizeBytes);
        Assert.Equal(6, _resolver.Resolve("friend", "String/Consume.img").SizeBytes);
    }

    [Fact]
    public void Names_AreCaseSensitive_AndNotLowerCased()
    {
        AssertError(Fails("owner", "string/consume.img"), 404, "not_found");
        Assert.Equal("String/Consume.img", _resolver.Resolve("owner", "String/Consume.img").RelativePath);
    }

    [Fact]
    public void BackupsLookalikes_AreAllowed()
    {
        WriteImg(_owner, "Backups2/y.img", "y");
        WriteImg(_owner, "String/Backups.img", "z");
        WriteImg(_owner, "String/MyBackups/w.img", "w");
        Assert.Equal(1, _resolver.Resolve("owner", "Backups2/y.img").SizeBytes);
        Assert.Equal(1, _resolver.Resolve("owner", "String/Backups.img").SizeBytes);
        Assert.Equal(1, _resolver.Resolve("owner", "String/MyBackups/w.img").SizeBytes);
    }

    [SkippableFact]
    public void UnsearchableFolder_IsNotFound_NotAnException()
    {
        Skip.If(Environment.IsPrivilegedProcess, "root can search any folder");
        var locked = Path.Combine(_owner, "img", "Locked");
        WriteImg(_owner, "Locked/x.img", "x");
        try
        {
            File.SetUnixFileMode(locked, UnixFileMode.None);
            AssertError(Fails("owner", "Locked/x.img"), 404, "not_found");
            AssertError(Fails("owner", "Locked/deeper/x.img"), 404, "not_found");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UserWriteExec());   // so Dispose can delete it
        }
    }

    private static UnixFileMode UserWriteExec() => UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // ---- bad_path (text rules)

    [Theory]
    [InlineData("../String/Consume.img")]
    [InlineData("..")]
    [InlineData("String/../Consume.img")]
    [InlineData("String/..")]
    [InlineData("./String/Consume.img")]
    [InlineData("String/./Consume.img")]
    [InlineData("%2e%2e/String/Consume.img")]
    [InlineData("%2E%2E%2FString%2FConsume.img")]
    [InlineData("String%2FConsume.img")]
    [InlineData("String\\Consume.img")]
    [InlineData("..\\String\\Consume.img")]
    [InlineData("/String/Consume.img")]
    [InlineData("/etc/passwd")]
    [InlineData("/etc/passwd.img")]
    [InlineData("String//Consume.img")]
    [InlineData("String/Consume.img/")]
    [InlineData("String/Consume.txt")]
    [InlineData("String/Consume.IMG")]
    [InlineData("String/Consume")]
    [InlineData("String/")]
    [InlineData("Backups/x.img")]
    [InlineData("backups/x.img")]
    [InlineData("BACKUPS/x.img")]
    [InlineData("X/Backups/a.img")]            // Backups is refused as any segment, not only the first
    [InlineData("String/BACKUPS/a.img")]
    [InlineData("A/B/backups/c.img")]
    [InlineData(".x/y.img")]
    [InlineData("String/.hidden.img")]
    [InlineData("String/.img")]
    [InlineData("String/Con\u0001sume.img")]      // control character
    [InlineData("String/Con\nsume.img")]
    [InlineData("String/Consume.img\n")]          // a trailing newline must not slip past an end anchor
    [InlineData("String/Con\0sume.img")]
    [InlineData("String/C\u00F6nsume.img")]       // not ASCII
    [InlineData("C:/x.img")]
    [InlineData("String/a:b.img")]
    [InlineData("String/a*b.img")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectedText_IsBadPath(string? path)
    {
        AssertError(Fails("owner", path), 400, "bad_path");
    }

    [Fact]
    public void PathLength_260IsTheLimit()
    {
        string OfLength(int length)
        {
            var name = new string('a', 9);
            var sb = new System.Text.StringBuilder();
            while (sb.Length + name.Length + 1 < length - ".img".Length)
                sb.Append(name).Append('/');
            sb.Append(new string('b', length - ".img".Length - sb.Length)).Append(".img");
            Assert.Equal(length, sb.Length);
            return sb.ToString();
        }

        AssertError(Fails("owner", OfLength(261)), 400, "bad_path");
        AssertError(Fails("owner", OfLength(260)), 404, "not_found");   // passes the pattern, there is no such file
    }

    // ---- unknown_source

    [Theory]
    [InlineData("Canon")]
    [InlineData("other")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("../owner")]
    [InlineData("owner/")]
    public void UnknownSource_IsRejected_BeforeThePathIsLooked_At(string? source)
    {
        AssertError(Fails(source, "String/Consume.img"), 400, "unknown_source");
        AssertError(Fails(source, "../x"), 400, "unknown_source");
    }

    // ---- not_found

    [Fact]
    public void MissingFile_IsNotFound()
    {
        AssertError(Fails("owner", "String/Nope.img"), 404, "not_found");
        AssertError(Fails("owner", "Nope/Consume.img"), 404, "not_found");
    }

    [Fact]
    public void DirectoryNamedLikeAnImage_IsNotFound()
    {
        AssertError(Fails("owner", "Dir/x.img"), 404, "not_found");
    }

    [Fact]
    public void MissingImgFolder_IsNotFound()
    {
        Directory.Delete(Path.Combine(_canon, "img"), recursive: true);
        AssertError(Fails("canon", "String/Consume.img"), 404, "not_found");
    }

    [Fact]
    public void FileInOtherSource_IsNotFoundHere()
    {
        WriteImg(_canon, "OnlyCanon.img", "c");
        AssertError(Fails("owner", "OnlyCanon.img"), 404, "not_found");
    }

    // ---- symbolic links

    [SkippableFact]
    public void SymlinkedFile_IsRefused()
    {
        var link = Path.Combine(_owner, "img", "String", "Link.img");
        EnvRequire.Capability(TrySymlink(link, Path.Combine(_owner, "img", "String", "Consume.img")), "symbolic links cannot be created here");
        AssertError(Fails("owner", "String/Link.img"), 400, "bad_path");
    }

    [SkippableFact]
    public void SymlinkedParentDirectory_IsRefused()
    {
        var link = Path.Combine(_owner, "img", "Alias");
        EnvRequire.Capability(TryDirSymlink(link, Path.Combine(_owner, "img", "String")), "symbolic links cannot be created here");
        AssertError(Fails("owner", "Alias/Consume.img"), 400, "bad_path");
    }

    [SkippableFact]
    public void SymlinkToFileOutsideTheRoot_IsRefused()
    {
        var outside = Path.Combine(_tmp, "outside.img");
        File.WriteAllText(outside, "secret");
        var link = Path.Combine(_owner, "img", "String", "Out.img");
        EnvRequire.Capability(TrySymlink(link, outside), "symbolic links cannot be created here");
        AssertError(Fails("owner", "String/Out.img"), 400, "bad_path");
    }

    [SkippableFact]
    public void SymlinkedParentOutsideTheRoot_IsRefused()
    {
        var outsideDir = Directory.CreateDirectory(Path.Combine(_tmp, "outside-dir")).FullName;
        File.WriteAllText(Path.Combine(outsideDir, "x.img"), "secret");
        var link = Path.Combine(_owner, "img", "Escape");
        EnvRequire.Capability(TryDirSymlink(link, outsideDir), "symbolic links cannot be created here");
        AssertError(Fails("owner", "Escape/x.img"), 400, "bad_path");
    }

    [SkippableFact]
    public void DanglingSymlink_IsRefused()
    {
        var link = Path.Combine(_owner, "img", "String", "Dangling.img");
        EnvRequire.Capability(TrySymlink(link, Path.Combine(_tmp, "does-not-exist")), "symbolic links cannot be created here");
        AssertError(Fails("owner", "String/Dangling.img"), 400, "bad_path");
    }

    [SkippableFact]
    public void SymlinkedImgFolder_IsRefused()
    {
        var real = Path.Combine(_tmp, "real-img");
        Directory.CreateDirectory(Path.Combine(real, "String"));
        File.WriteAllText(Path.Combine(real, "String", "Consume.img"), "x");
        Directory.Delete(Path.Combine(_canon, "img"), recursive: true);
        EnvRequire.Capability(TryDirSymlink(Path.Combine(_canon, "img"), real), "symbolic links cannot be created here");
        AssertError(Fails("canon", "String/Consume.img"), 400, "bad_path");
    }

    [SkippableFact]
    public void SymlinkedRootItself_IsAllowed()
    {
        // The root is the mount point; only what is below it is checked for links.
        var linkedRoot = Path.Combine(_tmp, "root-link");
        EnvRequire.Capability(TryDirSymlink(linkedRoot, _owner), "symbolic links cannot be created here");
        var resolver = new ViewerPathResolver(new Dictionary<string, string> { ["owner"] = linkedRoot });
        Assert.Equal(14, resolver.Resolve("owner", "String/Consume.img").SizeBytes);
    }

    // ---- WZIMG_ALLOWED_ROOT

    [Fact]
    public void AllowedRoot_WhenSet_AppliesToTheResolvedFile()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _tmp);
        Assert.Equal(14, _resolver.Resolve("owner", "String/Consume.img").SizeBytes);

        var elsewhere = Directory.CreateDirectory(Path.Combine(_tmp, "elsewhere")).FullName;
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, elsewhere);
        AssertError(Fails("owner", "String/Consume.img"), 400, "bad_path");
    }
}
