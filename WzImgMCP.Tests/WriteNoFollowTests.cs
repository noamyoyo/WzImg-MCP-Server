using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// PathGuard.Resolve and the write that follows are two steps: a symbolic link planted at the resolved path in
/// between must not be followed by the write.
/// </summary>
public sealed class WriteNoFollowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nofollow-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static bool TrySymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    [Fact]
    public void WritesANewFile_AndLeavesNoTemporaryFileBehind()
    {
        var path = Path.Combine(_dir, "new.bin");
        PathGuard.WriteFileNoFollow(path, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Equal(new[] { "new.bin" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void ReplacesAnExistingRegularFile()
    {
        var path = Path.Combine(_dir, "old.bin");
        File.WriteAllBytes(path, new byte[100]);
        PathGuard.WriteFileNoFollow(path, new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }

    [SkippableFact]
    public void RefusesASymlinkAtThePath_AndDoesNotWriteThroughIt()
    {
        var victim = Path.Combine(_dir, "victim.txt");
        File.WriteAllText(victim, "keep");
        var link = Path.Combine(_dir, "link.png");
        Skip.IfNot(TrySymlink(link, victim), "symbolic links cannot be created here");

        Assert.Throws<PathNotAllowedException>(() => PathGuard.WriteFileNoFollow(link, new byte[] { 1 }));
        Assert.Equal("keep", File.ReadAllText(victim));
        Assert.Equal(2, Directory.GetFileSystemEntries(_dir).Length);   // victim and link, no temporary file
    }

    [SkippableFact]
    public void RefusesADanglingSymlink_AndDoesNotCreateItsTarget()
    {
        var target = Path.Combine(_dir, "does-not-exist-yet");
        var link = Path.Combine(_dir, "dangling.png");
        Skip.IfNot(TrySymlink(link, target), "symbolic links cannot be created here");

        Assert.Throws<PathNotAllowedException>(() => PathGuard.WriteFileNoFollow(link, new byte[] { 1 }));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void WriteFileGuarded_OverwritesAnExistingFile()
    {
        PathGuard.WriteFileGuarded(_dir, "a.png", new byte[] { 1, 1, 1 });
        PathGuard.WriteFileGuarded(_dir, "a.png", new byte[] { 2 });
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(_dir, "a.png")));
        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }
}

/// <summary>The refusal says what happened: a symbolic link was found. It does not claim the path is outside a root that may not be set.</summary>
[Collection("AllowedRootEnv")]
public sealed class WriteNoFollowMessageTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _dir = Directory.CreateTempSubdirectory("nofollow-msg-").FullName;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalOfALinkInsideTheRoot_NamesTheLink_AndNeverSaysOutsideAllowedRoot(bool rootSet)
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, rootSet ? _dir : null);
        var real = Path.Combine(_dir, "real.txt");
        File.WriteAllText(real, "keep");
        var link = Path.Combine(_dir, "link.txt");
        try { File.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Skip.If(true, "symbolic links cannot be created here"); }

        var ex2 = Assert.Throws<PathNotAllowedException>(() => PathGuard.WriteFileNoFollow(link, new byte[] { 1 }));
        Assert.Contains("symbolic link", ex2.Message);
        Assert.Contains(link, ex2.Message);
        Assert.DoesNotContain("outside allowed root", ex2.Message);
        Assert.Equal("keep", File.ReadAllText(real));
    }
}
