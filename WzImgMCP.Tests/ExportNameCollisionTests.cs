using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// File and directory names derived from image and canvas names must stay unique, or one export silently
/// overwrites another.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ExportNameCollisionTests : IClassFixture<TestFixture>, IDisposable
{
    private readonly TestFixture _fixture;
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _sandbox = Directory.CreateTempSubdirectory("name-collision-").FullName;

    public ExportNameCollisionTests(TestFixture fixture)
    {
        _fixture = fixture;
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_sandbox, true); } catch { }
    }

    /// <summary>A data source whose Character category holds the same image file name in different sub directories.</summary>
    private WzSessionManager SessionWithImages(params string[] relativeImagePaths)
    {
        var tree = Directory.CreateDirectory(Path.Combine(_sandbox, "tree")).FullName;
        File.Copy(Path.Combine(_fixture.TestDataPath, "manifest.json"), Path.Combine(tree, "manifest.json"));
        var source = Path.Combine(_fixture.TestDataPath, "Character", "Test.img");
        foreach (var rel in relativeImagePaths)
        {
            var target = Path.Combine(tree, "Character", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        var session = new WzSessionManager();
        session.InitDataSource(tree);
        return session;
    }

    [Fact]
    public void BatchExportImages_SameImageFileNameInTwoDirectories_DoesNotOverwrite()
    {
        using var session = SessionWithImages("a/x.img", "b/x.img");
        var outDir = Path.Combine(_sandbox, "out");

        var r = new BatchTools(session).BatchExportImages("Character", outDir);
        MarkdownTestHelper.AssertSuccess(r);

        var perImage = Directory.GetFileSystemEntries(Path.Combine(outDir, "Character")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "x", "x_2" }, perImage);                     // two image directories, not one shared "x"
        var filesPerDirectory = Directory.GetDirectories(Path.Combine(outDir, "Character"))
            .Select(d => Directory.GetFiles(d).Length).ToArray();
        Assert.Equal(filesPerDirectory[0], filesPerDirectory[1]);           // each image was exported completely
        Assert.Equal(MarkdownTestHelper.GetFirstInt(r, "exported_count"), filesPerDirectory.Sum());
    }

    [Fact]
    public void BatchExportImages_ImageNamesThatSanitiseToTheSameName_DoNotOverwrite()
    {
        using var session = SessionWithImages("a_b.img", "a:b.img");      // ':' is replaced by '_'
        var outDir = Path.Combine(_sandbox, "out2");

        MarkdownTestHelper.AssertSuccess(new BatchTools(session).BatchExportImages("Character", outDir));

        Assert.Equal(2, Directory.GetDirectories(Path.Combine(outDir, "Character")).Length);
    }
}
