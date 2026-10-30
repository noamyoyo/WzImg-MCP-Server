#if LINUX_BUILD
using WzImgMCP.Packer;

namespace WzImgMCP.Tests;

/// <summary>Configuration of the packer: the token, the roots, the outbox and the caps. No server is started.</summary>
public sealed class PackerOptionsTests : IDisposable
{
    private const string GoodToken = "pack-kkkkkkkkkkkkkkkkkkkkkkkkkk-0123456789abcdef";
    private readonly string _temp = Directory.CreateTempSubdirectory("packer-options-").FullName;

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private Dictionary<string, string> Roots => PackerOptions.SourceNames.ToDictionary(n => n, n => Path.Combine(_temp, n));
    private string Outbox => Path.Combine(_temp, "outbox");
    private string Published => Path.Combine(_temp, "published");

    private Func<string, string?> Env(Dictionary<string, string?>? extra = null)
    {
        var map = new Dictionary<string, string?>
        {
            [PackerOptions.TokenEnvVar] = GoodToken,
            [PackerOptions.RootEnvPrefix + "CANON"] = Roots["canon"],
            [PackerOptions.RootEnvPrefix + "OWNER"] = Roots["owner"],
            [PackerOptions.RootEnvPrefix + "FRIEND"] = Roots["friend"],
            [PackerOptions.OutboxEnvVar] = Outbox,
            [PackerOptions.PublishedEnvVar] = Published,
        };
        if (extra != null) foreach (var (k, v) in extra) map[k] = v;
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var o = PackerOptions.FromEnvironment(Env());
        Assert.Equal(GoodToken, o.Token);
        Assert.Null(o.Warning);
        Assert.Equal(Roots["owner"], o.Roots["owner"]);
        Assert.Equal(Outbox, o.Outbox);
        Assert.Equal(Published, o.PublishedDir);
        Assert.Equal(Path.Combine(Published, "Item.wz"), o.PublishedFile("Item"));
        Assert.Equal(512L * 1024 * 1024, o.MaxInputBytes);
        Assert.Equal(1024L * 1024 * 1024, o.MaxWzBytes);
        Assert.Equal(2000, o.MaxFiles);
        Assert.Equal(TimeSpan.FromSeconds(900), o.TimeBudget);
        Assert.Equal(200, o.MaxDiffs);
        Assert.Equal(500, o.MaxJobFolders);
        Assert.Equal(8, o.MaxConcurrentRequests);
        Assert.Equal(13500, PackerOptions.DefaultPort);
        Assert.Equal("0.0.0.0", PackerOptions.DefaultHost);
    }

    [Fact]
    public void BlankRoot_KeepsDataSlashSource()
    {
        var o = PackerOptions.FromEnvironment(Env(new() { [PackerOptions.RootEnvPrefix + "FRIEND"] = " " }));
        Assert.Equal("/data/friend", o.Roots["friend"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingToken_RefusesToStart(string? token)
    {
        var ex = Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [PackerOptions.TokenEnvVar] = token })));
        Assert.Contains("WZ_PACKER_TOKEN", ex.Message);
    }

    [Fact]
    public void ShortToken_RefusesToStart_AndTheMessageDoesNotHoldTheToken()
    {
        var token = "short-token-0123456789abcdef";   // 28 characters
        var ex = Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [PackerOptions.TokenEnvVar] = token })));
        Assert.DoesNotContain(token, ex.Message);
    }

    [Theory]
    [InlineData("has space in it 0123456789abcdef0123456789")]
    [InlineData("non-ascii-é-0123456789abcdef0123456789abcdef")]
    [InlineData("control\tchar-0123456789abcdef0123456789abcdef")]
    public void TokenWithForbiddenCharacters_RefusesToStart(string token)
        => Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [PackerOptions.TokenEnvVar] = token })));

    [Fact]
    public void WeakToken_StartsWithAWarning_ThatDoesNotHoldTheToken()
    {
        var token = new string('a', 20) + new string('b', 20);
        var o = PackerOptions.FromEnvironment(Env(new() { [PackerOptions.TokenEnvVar] = token }));
        Assert.NotNull(o.Warning);
        Assert.DoesNotContain(token, o.Warning);
    }

    [Fact]
    public void SurroundingWhitespaceOfTheToken_IsTrimmed()
        => Assert.Equal(GoodToken, PackerOptions.FromEnvironment(Env(new() { [PackerOptions.TokenEnvVar] = GoodToken + "\n" })).Token);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/outbox")]
    public void OutboxMustBeSetAndAbsolute(string? outbox)
        => Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [PackerOptions.OutboxEnvVar] = outbox })));

    [Fact]
    public void Outbox_MustNotOverlapAReadOnlyRoot()
    {
        foreach (var inside in new[] { Roots["owner"], Path.Combine(Roots["owner"], "out"), _temp })
        {
            var ex = Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [PackerOptions.OutboxEnvVar] = inside })));
            Assert.DoesNotContain(_temp, ex.Message);   // no path in a message
        }
    }

    [Theory]
    [InlineData("WZ_PACKER_MAX_REQUESTS", "0")]
    [InlineData("WZ_PACKER_MAX_REQUESTS", "33")]
    [InlineData("WZ_PACKER_MAX_INPUT_MB", "abc")]
    [InlineData("WZ_PACKER_MAX_INPUT_MB", "-1")]
    [InlineData("WZ_PACKER_MAX_WZ_MB", "0")]
    [InlineData("WZ_PACKER_MAX_FILES", "1.5")]
    [InlineData("WZ_PACKER_MAX_FILES", "0")]
    [InlineData("WZ_PACKER_MAX_FILES", "20001")]
    [InlineData("WZ_PACKER_TIME_BUDGET_SECONDS", "5")]
    [InlineData("WZ_PACKER_TIME_BUDGET_SECONDS", "99999")]
    [InlineData("WZ_PACKER_MAX_DIFFS", "0")]
    [InlineData("WZ_PACKER_MAX_JOB_FOLDERS", "0")]
    [InlineData("WZ_PACKER_MAX_IMG_MB", "5000")]
    [InlineData("WZ_PACKER_TMP", "relative")]
    public void BadCapValues_RefuseToStart_InsteadOfFallingBack(string name, string value)
    {
        var ex = Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [name] = value })));
        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void GoodCapValues_AreRead()
    {
        var o = PackerOptions.FromEnvironment(Env(new()
        {
            ["WZ_PACKER_MAX_REQUESTS"] = "2", ["WZ_PACKER_MAX_INPUT_MB"] = "10", ["WZ_PACKER_MAX_WZ_MB"] = "5", ["WZ_PACKER_MAX_FILES"] = "7",
            ["WZ_PACKER_MAX_IMG_MB"] = "3", ["WZ_PACKER_TIME_BUDGET_SECONDS"] = "60", ["WZ_PACKER_MAX_DIFFS"] = "9", ["WZ_PACKER_MAX_JOB_FOLDERS"] = "11", ["WZ_PACKER_TMP"] = "/dev/shm/",
        }));
        Assert.Equal(2, o.MaxConcurrentRequests);
        Assert.Equal(10L * 1024 * 1024, o.MaxInputBytes);
        Assert.Equal(5L * 1024 * 1024, o.MaxWzBytes);
        Assert.Equal(7, o.MaxFiles);
        Assert.Equal(3L * 1024 * 1024, o.MaxImageBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), o.TimeBudget);
        Assert.Equal(9, o.MaxDiffs);
        Assert.Equal(11, o.MaxJobFolders);
        Assert.Equal("/dev/shm", o.TempRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("relative/published")]
    public void PublishedFolder_MustBeSetAndAbsolute(string? published)
    {
        var ex = Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [PackerOptions.PublishedEnvVar] = published })));
        Assert.Contains("WZ_PACKER_PUBLISHED_DIR", ex.Message);
    }

    [Fact]
    public void PublishedFolder_MustNotOverlapTheOutboxOrARoot()
    {
        foreach (var bad in new[] { Outbox, Path.Combine(Outbox, "pub"), Roots["owner"], Path.Combine(Roots["friend"], "pub"), _temp })
        {
            var ex = Assert.Throws<PackerConfigException>(() => PackerOptions.FromEnvironment(Env(new() { [PackerOptions.PublishedEnvVar] = bad })));
            Assert.DoesNotContain(_temp, ex.Message);
        }
    }

    [Fact]
    public void CheckPublished_AcceptsARealFolder_AndRefusesAMissingOneAndALink()
    {
        var options = PackerOptions.FromEnvironment(Env());
        Assert.Throws<PackerConfigException>(options.CheckPublished);   // not created yet

        Directory.CreateDirectory(Published);
        options.CheckPublished();
        Directory.Delete(Published);

        var real = Path.Combine(_temp, "real");
        Directory.CreateDirectory(real);
        EnvRequire.Capability(ImgFixture.TrySymlink(Published, real, directory: true), "symbolic links cannot be created here");
        var ex = Assert.Throws<PackerConfigException>(options.CheckPublished);
        Assert.DoesNotContain(_temp, ex.Message);
    }

    [Fact]
    public void CheckOutbox_AcceptsARealWritableFolder_AndLeavesNothingBehind()
    {
        Directory.CreateDirectory(Outbox);
        PackerOptions.FromEnvironment(Env()).CheckOutbox();
        Assert.Empty(Directory.GetFileSystemEntries(Outbox));
    }

    [Fact]
    public void CheckOutbox_RefusesAMissingFolder_AndASymbolicLink()
    {
        var options = PackerOptions.FromEnvironment(Env());
        Assert.Throws<PackerConfigException>(options.CheckOutbox);   // not created yet

        var real = Path.Combine(_temp, "real");
        Directory.CreateDirectory(real);
        EnvRequire.Capability(ImgFixture.TrySymlink(Outbox, real, directory: true), "symbolic links cannot be created here");
        var ex = Assert.Throws<PackerConfigException>(options.CheckOutbox);
        Assert.DoesNotContain(_temp, ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(real));   // nothing was probed through the link
    }
}
#endif
