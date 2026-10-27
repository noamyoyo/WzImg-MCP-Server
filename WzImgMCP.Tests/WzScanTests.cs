using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>scan_img_directories must not follow symbolic links out of WZIMG_ALLOWED_ROOT.</summary>
[Collection("AllowedRootEnv")]
public sealed class WzScanTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _tmp = Directory.CreateTempSubdirectory("wzscan-").FullName;
    private readonly string _root;
    private readonly string _outside;

    public WzScanTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_tmp, "data")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_tmp, "outside")).FullName;
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previous);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static void WriteManifest(string dir, string version)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{\"version\": \"{version}\", \"displayName\": \"{version}\"}}");
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scan_DoesNotFollowSymlinksOutOfTheAllowedRoot(bool recursive)
    {
        Skip.If(OperatingSystem.IsWindows(), "needs symbolic links");
        WriteManifest(Path.Combine(_root, "good"), "inside");
        WriteManifest(Path.Combine(_outside, "evil"), "outside");
        WriteManifest(_outside, "outside-root");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link-to-outside"), _outside);
        // a real directory whose manifest.json is a link to a file outside the root
        Directory.CreateDirectory(Path.Combine(_root, "linked-manifest"));
        File.CreateSymbolicLink(Path.Combine(_root, "linked-manifest", "manifest.json"), Path.Combine(_outside, "manifest.json"));

        using var session = new WzSessionManager();
        var found = session.ScanForDataSources(_root, recursive);

        var paths = found.Select(f => f.Path).ToList();
        Assert.Contains(Path.Combine(_root, "good"), paths);
        Assert.DoesNotContain(paths, p => p.Contains("outside") || p.Contains("evil") || p.Contains("linked-manifest"));

        var markdown = new FileTools(session).ScanImgDirectories(_root, recursive);
        MarkdownTestHelper.AssertSuccess(markdown);
        Assert.DoesNotContain("evil", markdown);
        Assert.DoesNotContain("outside", markdown);
    }
}
