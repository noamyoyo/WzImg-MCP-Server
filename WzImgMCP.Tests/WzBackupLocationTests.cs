using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// Where save_image puts the backup of the file it replaces: inside the data source when
/// WZIMG_ALLOWED_ROOT is set, next to it (the Windows default) otherwise.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class WzBackupLocationTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly TempTree _root = new();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previous);
        _root.Dispose();
    }

    /// <summary>Edits and saves Pet.img twice (the backup is made when an existing file is replaced).</summary>
    private string SaveTwice(string dataSourceDir)
    {
        WzTestData.CopyDirectory(WzTestData.ExtractedStringRoot, dataSourceDir);
        using var session = new WzSessionManager();
        session.InitDataSource(dataSourceDir);
        var modify = new ModifyTools(session);
        for (int i = 0; i < 2; i++)
        {
            MarkdownTestHelper.AssertSuccess(modify.SetString("String", "Pet.img", "5000000/name", "BACKUP-" + i));
            MarkdownTestHelper.AssertSuccess(modify.SaveImage("String", "Pet.img"));
        }
        return dataSourceDir;
    }

    private static string[] FilesUnder(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : [];

    [SkippableFact]
    public void WithAllowedRoot_BackupsStayInsideTheDataSource()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _root.Path);
        var dataSource = SaveTwice(Path.Combine(_root.Path, "v83"));

        var inside = FilesUnder(Path.Combine(dataSource, "Backups"));
        Assert.Contains(inside, f => Path.GetFileName(f).StartsWith("Pet.img", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(FilesUnder(Path.Combine(_root.Path, "Backups")));      // not a sibling of the data source

        // and the backups are not mistaken for a category
        using var session = new WzSessionManager();
        session.InitDataSource(dataSource);
        Assert.DoesNotContain("Backups", new FileTools(session).ListCategories());
    }

    [SkippableFact]
    public void WithoutAllowedRoot_BackupsStayNextToTheDataSource()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        var dataSource = SaveTwice(Path.Combine(_root.Path, "v83"));

        Assert.Contains(FilesUnder(Path.Combine(_root.Path, "Backups")), f => Path.GetFileName(f).StartsWith("Pet.img", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(FilesUnder(Path.Combine(dataSource, "Backups")));
    }
}
