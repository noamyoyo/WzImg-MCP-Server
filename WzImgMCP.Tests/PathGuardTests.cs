using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// Tests for the WZIMG_ALLOWED_ROOT path allowlist. They change a process-wide environment
/// variable, so they must not run in parallel with each other.
/// </summary>
[CollectionDefinition("AllowedRootEnv", DisableParallelization = true)]
public class AllowedRootEnvCollection { }

[Collection("AllowedRootEnv")]
public sealed class PathGuardTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _tmp;
    private readonly string _root;
    private readonly string _outside;

    public PathGuardTests()
    {
        _tmp = Directory.CreateTempSubdirectory("pathguard-").FullName;
        _root = Directory.CreateDirectory(Path.Combine(_tmp, "data")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_tmp, "data2")).FullName;   // shares the "data" prefix
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previous);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public void Unset_ReturnsPathUnchanged()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        Assert.Equal("/etc/x", PathGuard.Resolve("/etc/x"));
        Assert.Equal("relative/dir", PathGuard.Resolve("relative/dir"));
        Assert.Null(PathGuard.AllowedRoot);
    }

    [Fact]
    public void Empty_TreatedAsUnset()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, "  ");
        Assert.Equal("/etc/x", PathGuard.Resolve("/etc/x"));
    }

    [Fact]
    public void InsideRoot_IsAllowed()
    {
        var p = Path.Combine(_root, "wz", "String.wz");
        Assert.Equal(p, PathGuard.Resolve(p));
        Assert.Equal(_root, PathGuard.Resolve(_root));
        Assert.Equal(_root, PathGuard.Resolve(_root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RelativePath_IsTakenRelativeToRoot()
    {
        Assert.Equal(Path.Combine(_root, "out", "x"), PathGuard.Resolve(Path.Combine("out", "x")));
    }

    [Theory]
    [InlineData("/etc/x")]
    [InlineData("/")]
    [InlineData("")]
    public void OutsideRoot_IsRejected(string path)
    {
        var ex = Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(path));
        Assert.Contains("path outside allowed root", ex.Message);
    }

    [Fact]
    public void DotDotTraversal_IsRejected()
    {
        Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(Path.Combine(_root, "..", "data2", "x")));
        Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(Path.Combine("..", "data2")));
    }

    [Fact]
    public void SiblingWithSharedPrefix_IsRejected()
    {
        Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(_outside));
        Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(Path.Combine(_outside, "x")));
    }

    [Fact]
    public void SymlinkInsideRootPointingOutside_IsRejected()
    {
        var link = Path.Combine(_root, "escape");
        try { Directory.CreateSymbolicLink(link, _outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // symlinks not permitted on this platform/account
        }

        Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(link));
        Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(Path.Combine(link, "new-dir", "file.wz")));
    }

    [Fact]
    public void SymlinkInsideRootPointingInside_IsAllowed()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var link = Path.Combine(_root, "alias");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Equal(target, PathGuard.Resolve(link));
    }

    [Fact]
    public void RootGivenAsSymlink_IsResolved()
    {
        var link = Path.Combine(_tmp, "rootlink");
        try { Directory.CreateSymbolicLink(link, _root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Environment.SetEnvironmentVariable(PathGuard.EnvVar, link);
        Assert.Equal(Path.Combine(_root, "a"), PathGuard.Resolve(Path.Combine(link, "a")));
        Assert.Throws<PathNotAllowedException>(() => PathGuard.Resolve(_outside));
    }

    [Fact]
    public void ExtractToImg_OutputOutsideRoot_ReturnsError_AndCreatesNothing()
    {
        var bad = Path.Combine(_outside, "created");
        var tools = new BatchTools(new WzSessionManager());

        var result = tools.ExtractToImg(Path.Combine(_root, "String.wz"), bad);

        Assert.Contains("path outside allowed root", result);
        Assert.False(Directory.Exists(bad));
    }

    [Fact]
    public void PackToWz_PathsOutsideRoot_AreRejected()
    {
        var tools = new BatchTools(new WzSessionManager());

        Assert.Contains("path outside allowed root", tools.PackToWz(_outside, Path.Combine(_root, "out")));
        Assert.Contains("path outside allowed root", tools.PackToWz(_root, Path.Combine(_outside, "out")));
        Assert.False(Directory.Exists(Path.Combine(_outside, "out")));
    }

    [Fact]
    public void InitDataSource_OutsideRoot_ReturnsError()
    {
        var tools = new FileTools(new WzSessionManager());
        Assert.Contains("path outside allowed root", tools.InitDataSource(_outside));
        Assert.Contains("path outside allowed root", tools.ScanImgDirectories("/etc"));
    }

    [Fact]
    public void ExtractToImg_WithoutRoot_IsNotRestricted()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        var tools = new BatchTools(new WzSessionManager());

        // No restriction: the failure is the missing input, not the allowlist.
        var result = tools.ExtractToImg(Path.Combine(_outside, "missing.wz"), Path.Combine(_outside, "out"));
        Assert.DoesNotContain("path outside allowed root", result);
        Assert.Contains("Path not found", result);
    }
}
