using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// export_to_md writes "&lt;outputPath&gt;.md": the path guard has to check that file, not the path as the caller wrote it.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ExportMarkdownPathTests : IClassFixture<TestFixture>, IDisposable
{
    private const string Cat = "Character";
    private const string Img = "Test.img";

    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _sandbox = Directory.CreateTempSubdirectory("md-path-").FullName;
    private readonly string _root;
    private readonly string _outside;
    private readonly ExportTools _export;

    public ExportMarkdownPathTests(TestFixture fixture)
    {
        fixture.InitializeDataSource();
        _root = Directory.CreateDirectory(Path.Combine(_sandbox, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_sandbox, "outside")).FullName;
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root);
        _export = new ExportTools(fixture.Session);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_sandbox, true); } catch { }
    }

    [Theory]
    [InlineData("report", "report.md")]
    [InlineData("report.txt", "report.md")]
    [InlineData("report.MD", "report.MD")]
    [InlineData("sub/dir/report.png", "sub/dir/report.md")]
    public void WritesTheMdFileInsideTheRoot(string given, string expectedRelative)
    {
        var r = _export.ExportToMd(Cat, Img, null, 2, Path.Combine(_root, given));
        MarkdownTestHelper.AssertSuccess(r);
        var expected = Path.Combine(_root, expectedRelative);
        Assert.True(File.Exists(expected), r);
        Assert.Equal(expected, MarkdownTestHelper.GetFirstValue(r, "output_path"));
    }

    [SkippableFact]
    public void RefusesAnMdFileThatIsASymlinkLeadingOutside_WhileThePathAsGivenIsHarmless()
    {
        var victim = Path.Combine(_outside, "victim.md");
        File.WriteAllText(victim, "keep");
        try { File.CreateSymbolicLink(Path.Combine(_root, "doc.md"), victim); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip.If(true, "symbolic links cannot be created here (platform or account does not allow them)");
        }

        // "doc" and "doc.txt" both turn into "doc.md": the guard must see the symlink.
        foreach (var given in new[] { "doc", "doc.txt" })
        {
            var r = _export.ExportToMd(Cat, Img, null, 2, Path.Combine(_root, given));
            MarkdownTestHelper.AssertFailure(r);
            Assert.Contains("resolves to", r);   // refused by the path guard on the final .md path, not only by the no-follow write
        }
        Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Theory]
    [InlineData("../outside/doc")]
    [InlineData("../outside/doc.txt")]
    public void RefusesAPathOutsideTheRoot_AndCreatesNothing(string relativeToRoot)
    {
        var r = _export.ExportToMd(Cat, Img, null, 2, Path.Combine(_root, relativeToRoot));
        MarkdownTestHelper.AssertFailure(r);
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }
}
