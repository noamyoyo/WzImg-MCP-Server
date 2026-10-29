using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// The data folder (WZIMG_DEFAULT_DATA_PATH, default /data/img) is opened by the first tool call that needs it.
/// Changes WZIMG_ALLOWED_ROOT in one test, so it runs in the same non-parallel collection as the path guard tests.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class AutoDataSourceTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly TestFixture _fixture = new();       // a tree with manifest.json, Character/Test.img, Map/Map0/...
    private readonly List<IDisposable> _extra = new();

    public AutoDataSourceTests()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        foreach (var d in _extra) d.Dispose();
        _fixture.Dispose();
    }

    private WzSessionManager NewSession(string? path)
    {
        var s = new WzSessionManager(path);
        _extra.Add(s);
        return s;
    }

    [Fact]
    public void FirstCallWithoutInit_OpensTheDefaultFolder()
    {
        var session = NewSession(_fixture.TestDataPath);
        Assert.False(session.AutoInitialized);
        var tools = new FileTools(session);

        var categories = tools.ListCategories();
        MarkdownTestHelper.AssertSuccess(categories);
        Assert.Contains("character", categories, StringComparison.OrdinalIgnoreCase);

        var info = tools.GetDataSourceInfo();
        MarkdownTestHelper.AssertSuccess(info);
        Assert.Equal("true", MarkdownTestHelper.GetFirstValue(info, "auto_initialized"), ignoreCase: true);
    }

    [Fact]
    public void AutoOpen_WorksForOtherToolClasses()
    {
        var session = NewSession(_fixture.TestDataPath);
        var result = new PropertyTools(session).GetString("Character", "Test.img", "testString");
        MarkdownTestHelper.AssertSuccess(result);
        Assert.Contains("Hello World", result);
    }

    [Fact]
    public void ExplicitInit_Wins_AndIsNotAutoInitialized()
    {
        var other = Directory.CreateTempSubdirectory("wzauto-other-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(other, "manifest.json"), "{\"version\":\"1\",\"displayName\":\"Other\"}");
            Directory.CreateDirectory(Path.Combine(other, "Mob"));

            var session = NewSession(_fixture.TestDataPath);   // the default would be the fixture tree
            var tools = new FileTools(session);
            MarkdownTestHelper.AssertSuccess(tools.InitDataSource(other));

            var categories = tools.ListCategories();
            Assert.Contains("mob", categories, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("character", categories, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("false", MarkdownTestHelper.GetFirstValue(tools.GetDataSourceInfo(), "auto_initialized"), ignoreCase: true);
        }
        finally { try { Directory.Delete(other, true); } catch { } }
    }

    [Fact]
    public void ExplicitInit_AfterAutoOpen_SwitchesFolder()
    {
        var other = Directory.CreateTempSubdirectory("wzauto-other-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(other, "manifest.json"), "{\"version\":\"1\"}");
            Directory.CreateDirectory(Path.Combine(other, "Mob"));
            var session = NewSession(_fixture.TestDataPath);
            var tools = new FileTools(session);
            Assert.True(session.IsInitialized);
            Assert.True(session.AutoInitialized);
            MarkdownTestHelper.AssertSuccess(tools.InitDataSource(other));
            Assert.False(session.AutoInitialized);
            Assert.Contains("mob", tools.ListCategories(), StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(other, true); } catch { } }
    }

    [Fact]
    public void MissingFolder_GivesTheUsualError_AndRetriesLater()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wzauto-missing-" + Guid.NewGuid().ToString("N"));
        var session = NewSession(missing);
        var tools = new FileTools(session);

        var result = tools.ListCategories();
        MarkdownTestHelper.AssertFailure(result);
        Assert.Contains("No data source initialized", result);
        Assert.False(session.IsInitialized);

        // the folder appears later (for example after an extraction): the next call opens it
        try
        {
            Directory.CreateDirectory(Path.Combine(missing, "Mob"));
            File.WriteAllText(Path.Combine(missing, "manifest.json"), "{\"version\":\"1\"}");
            MarkdownTestHelper.AssertSuccess(tools.ListCategories());
        }
        finally { try { Directory.Delete(missing, true); } catch { } }
    }

    [Fact]
    public void FolderWithoutManifest_GivesTheUsualError()
    {
        var dir = Directory.CreateTempSubdirectory("wzauto-nomanifest-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Mob"));
            var result = new FileTools(NewSession(dir)).ListCategories();
            MarkdownTestHelper.AssertFailure(result);
            Assert.Contains("No data source initialized", result);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void DefaultFolderOutsideAllowedRoot_IsNotOpened()
    {
        var root = Directory.CreateTempSubdirectory("wzauto-root-").FullName;
        try
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, root);   // the fixture tree is outside this root
            var result = new FileTools(NewSession(_fixture.TestDataPath)).ListCategories();
            MarkdownTestHelper.AssertFailure(result);
            Assert.Contains("No data source initialized", result);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ConcurrentFirstCalls_OpenTheFolderOnce()
    {
        var session = NewSession(_fixture.TestDataPath);
        var tools = new FileTools(session);
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return tools.ListCategories();
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => MarkdownTestHelper.AssertSuccess(r));
        var first = session.DataSource;
        Assert.Same(first, session.DataSource);   // one data source object, not one per caller
        Assert.True(session.AutoInitialized);
    }

    [Theory]
    [InlineData(null, "/data/img")]
    [InlineData("", "/data/img")]
    [InlineData("  ", "/data/img")]
    public void DefaultPath_FallsBackToDataImg(string? configured, string expected)
    {
        var previous = Environment.GetEnvironmentVariable(WzSessionManager.DefaultDataPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(WzSessionManager.DefaultDataPathEnvVar, configured);
            Assert.Equal(expected, new WzSessionManager().DefaultDataPath);
            Environment.SetEnvironmentVariable(WzSessionManager.DefaultDataPathEnvVar, "/data/other");
            Assert.Equal("/data/other", new WzSessionManager().DefaultDataPath);
        }
        finally { Environment.SetEnvironmentVariable(WzSessionManager.DefaultDataPathEnvVar, previous); }
    }
}
