#if LINUX_BUILD
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using WzImgMCP.Guides;
using WzImgMCP.Import;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>get_guide: the editor documents itself. The guides are embedded markdown, filled with the live limits, and may not talk about tools that do not exist.</summary>
[Collection("AllowedRootEnv")]
public sealed class GuideToolsTests
{
    private static readonly string[] Topics = { "start", "import-by-link" };

    /// <summary>Settings that differ from every default, so a guide that prints a fixed number is caught.</summary>
    private static ImportOptions Unusual() => new()
    {
        MaxDownloadBytes = 123L * 1024 * 1024,
        MaxStaged = 9,
        Retention = TimeSpan.FromDays(11),
        DownloadTimeout = TimeSpan.FromSeconds(4321),
        StallTimeout = TimeSpan.FromSeconds(77),
        FilesMaxCount = 67,
        FilesMaxUnpackedBytes = 45L * 1024 * 1024,
        FilesMaxRatio = 31,
        AllowedHosts = new[] { "files.example.org", "*.cdn.example.org" },
    };

    private static List<(Type Type, MethodInfo Method, string Name)> RegisteredTools()
        => typeof(GuideTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods().Select(m => (Type: t, Method: m, Name: m.GetCustomAttribute<McpServerToolAttribute>()?.Name)))
            .Where(x => x.Name != null)
            .Select(x => (x.Type, x.Method, x.Name!))
            .ToList();

    private static string Call(string? topic, ImportOptions? options = null)
        => GuideCatalog.Get(topic, options ?? new ImportOptions());

    // ---- the guides are in the assembly ----

    [Fact]
    public void EveryTopicIsEmbeddedInTheAssembly()
    {
        var resources = typeof(GuideCatalog).Assembly.GetManifestResourceNames();
        Assert.Equal(Topics.Order(), GuideCatalog.Topics.Select(t => t.Name).Order());
        foreach (var topic in Topics)
        {
            Assert.Contains($"WzImgMCP.Guides.{topic}.md", resources);
            Assert.False(string.IsNullOrWhiteSpace(GuideCatalog.ReadTemplate(topic)));
        }
        // no guide file that nobody can ask for
        var guideResources = resources.Where(r => r.StartsWith("WzImgMCP.Guides.", StringComparison.Ordinal) && r.EndsWith(".md", StringComparison.Ordinal)).ToList();
        Assert.Equal(Topics.Length, guideResources.Count);
    }

    [Fact]
    public void EveryGuideIsShortEnoughToRead()
    {
        foreach (var topic in Topics)
        {
            var lines = GuideCatalog.ReadTemplate(topic).Split('\n').Length;
            Assert.InRange(lines, 10, 150);
        }
    }

    // ---- listing, reading, unknown topic ----

    [Fact]
    public void NoTopic_ListsBothTopicsWithAWhenToUseLine()
    {
        foreach (var arg in new string?[] { null, "", "   " })
        {
            var text = Call(arg);
            foreach (var topic in GuideCatalog.Topics)
            {
                Assert.Contains($"`{topic.Name}`", text);
                Assert.Contains(topic.UseWhen, text);
                Assert.StartsWith("Use this", topic.UseWhen);
            }
        }
        Assert.Contains("start", Call(null));
        Assert.Contains("import-by-link", Call(null));
    }

    [Fact]
    public void EachTopic_ReturnsItsMarkdown()
    {
        foreach (var topic in Topics)
        {
            var text = Call(topic);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.StartsWith("# ", text);
            Assert.DoesNotContain("- success: false", text);
        }
        Assert.Contains("WZ Editing page", Call("start"));
        Assert.Contains("import_fetch", Call("import-by-link"));
        Assert.NotEqual(Call("start"), Call("import-by-link"));
    }

    [Fact]
    public void TopicNameIsForgiving_ButTheAnswerIsTheSame()
    {
        var expected = Call("import-by-link");
        Assert.Equal(expected, Call(" Import-By-Link "));
        Assert.Equal(expected, Call("import_by_link"));
    }

    [Fact]
    public void UnknownTopic_IsAClearError_ThatListsTheValidTopics()
    {
        var text = Call("no-such-guide");
        MarkdownTestHelper.AssertFailure(text);
        Assert.Contains("no-such-guide", text);
        foreach (var topic in Topics)
            Assert.Contains(topic, text);
        Assert.Contains("get_guide", text);
        // a very long name is cut, not echoed whole
        Assert.DoesNotContain(new string('x', 200), Call(new string('x', 200)));
    }

    // ---- live limits ----

    [Fact]
    public void ImportGuide_PrintsTheLiveLimitsOfTheOptions()
    {
        var text = Call("import-by-link", Unusual());
        Assert.Contains("123 MB", text);          // max download
        Assert.Contains("4321 seconds", text);    // whole-download time
        Assert.Contains("77 seconds", text);      // stall time
        Assert.Contains("67 files", text);        // PNGs per zip
        Assert.Contains("45 MB", text);           // unpacked
        Assert.Contains("31 to 1", text);         // ratio
        Assert.Contains("9 imports", text);       // staged
        Assert.Contains("11 days", text);         // retention
        Assert.Contains("files.example.org, *.cdn.example.org", text);   // hosts
        Assert.Contains("4096 x 4096", text);     // PNG side (a fixed rule of import_png)
        Assert.Contains("20 MiB", text);
        // none of the defaults is left in the text
        Assert.DoesNotContain("300 MB", text);
        Assert.DoesNotContain("drive.google.com,", text);
        Assert.DoesNotContain("200 files", text);
    }

    [Fact]
    public void ImportGuide_FollowsTheDefaultsToo()
    {
        var o = new ImportOptions();
        var text = Call("import-by-link", o);
        Assert.Contains($"{o.MaxDownloadBytes / (1024 * 1024)} MB", text);
        Assert.Contains($"{o.FilesMaxCount} files", text);
        Assert.Contains(string.Join(", ", o.AllowedHosts), text);
        Assert.Contains($"{o.Retention.TotalDays:0} days", text);
    }

    [Fact]
    public void NoPlaceholderIsLeftOver_AndEveryPlaceholderIsKnown()
    {
        var known = GuideCatalog.Values(new ImportOptions()).Keys.ToHashSet();
        foreach (var topic in Topics)
        {
            Assert.DoesNotContain("{{", Call(topic, Unusual()));
            foreach (Match m in Regex.Matches(GuideCatalog.ReadTemplate(topic), @"\{\{([A-Za-z]+)\}\}"))
                Assert.Contains(m.Groups[1].Value, known);
        }
        // and every value the guides can use is used by some guide (no dead setting)
        var all = string.Concat(Topics.Select(GuideCatalog.ReadTemplate));
        foreach (var key in known)
            Assert.Contains("{{" + key + "}}", all);
    }

    // ---- the tool itself ----

    [Fact]
    public void TheToolIsRegisteredLikeTheOthers_AndReadOnly()
    {
        var tools = RegisteredTools();
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        var guide = Assert.Single(tools, t => t.Name == "get_guide");
        Assert.Equal(typeof(GuideTools), guide.Type);
        Assert.True(guide.Method.GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly);

        var p = Assert.Single(guide.Method.GetParameters());
        Assert.Equal("topic", p.Name);
        Assert.True(p.IsOptional);
        Assert.Equal(typeof(string), guide.Method.ReturnType);
    }

    [Fact]
    public void TheDescriptionSaysCallMeFirst_AndNamesTheTopics()
    {
        var guide = RegisteredTools().Single(t => t.Name == "get_guide").Method;
        var description = guide.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
        Assert.Contains("FIRST", description);
        Assert.Contains("import", description);
        Assert.Contains("pack", description);
        Assert.Contains("not sure", description);
        foreach (var topic in GuideCatalog.Topics)
            Assert.Contains(topic.Name, description);
    }

    [Fact]
    public void TheToolAnswersWithoutATree_AndWithoutAnAllowedRoot()
    {
        var before = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
            using var session = new WzSessionManager();
            using var service = new ImportService(session);
            Assert.False(session.IsInitialized);
            var tools = new GuideTools(service);
            Assert.Contains("import-by-link", tools.GetGuide());
            Assert.Contains("# ", tools.GetGuide("start"));
            MarkdownTestHelper.AssertFailure(tools.GetGuide("nope"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, before);
        }
    }

    [Fact]
    public void TheToolReadsTheLiveOptionsOfTheService()
    {
        using var session = new WzSessionManager();
        using var service = new ImportService(session, Unusual(), TimeProvider.System, new FakeWeb().Resolver, null);
        Assert.Contains("123 MB", new GuideTools(service).GetGuide("import-by-link"));
    }

    /// <summary>
    /// The owner and the friend editor run the same image and the same tool scan (WithToolsFromAssembly); only the data folder differs
    /// (WZIMG_ALLOWED_ROOT). Build the tool list the way Program does, once per profile, and ask for the tool.
    /// </summary>
    [Theory]
    [InlineData("/data/owner")]
    [InlineData("/data/friend")]
    public void TheToolIsOnOfferForTheOwnerAndTheFriend(string root)
    {
        var before = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, root);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<WzSessionManager>();
            services.AddSingleton<ImportService>();
            services.AddMcpServer().WithToolsFromAssembly(typeof(Program).Assembly);
            using var provider = services.BuildServiceProvider();

            var offered = provider.GetServices<McpServerTool>().ToList();
            Assert.NotEmpty(offered);
            Assert.Contains(offered, t => t.ProtocolTool.Name == "get_guide");
            // nothing in the registration drops tools by profile: the list is the full list of the assembly
            Assert.Equal(RegisteredTools().Select(t => t.Name).Order(), offered.Select(t => t.ProtocolTool.Name).Order());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, before);
        }
    }

    // ---- the guides may not claim what the code does not do ----

    private static readonly Regex SnakeWord = new(@"(?<![A-Za-z0-9_\-.])[a-z][a-z0-9]*(?:_[a-z0-9]+)+(?![A-Za-z0-9_\-])", RegexOptions.Compiled);
    private static readonly Regex CamelWord = new(@"(?<![A-Za-z0-9_])[a-z]+[A-Z][A-Za-z0-9]*(?![A-Za-z0-9_])", RegexOptions.Compiled);

    private static string SourceFolder([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "WzImgMCP");

    /// <summary>Every error code the import code can raise, read from its source. Empty when the source is not next to the tests.</summary>
    private static HashSet<string> ErrorCodesInTheCode()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var folder = Path.Combine(SourceFolder(), "Import");
        if (!Directory.Exists(folder)) return codes;
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "new ImportException\\(\"([a-z_]+)\""))
                codes.Add(m.Groups[1].Value);
        return codes;
    }

    private static readonly string[] ErrorCodesTheGuidesName =
    {
        "url_not_allowed", "not_a_file", "http_error", "too_large", "busy", "too_many_staged", "category_unknown", "category_refused",
        "not_gms", "unsafe_zip", "not_a_png", "unsupported_type", "hash_mismatch", "token_invalid", "tree_changed", "unsaved_edits",
        "lock_failed", "interrupted_import_pending", "not_found",
    };

    [Fact]
    public void EveryToolNamedInAGuideIsARegisteredTool()
    {
        var tools = RegisteredTools().Select(t => t.Name).ToHashSet();
        var codes = ErrorCodesTheGuidesName.ToHashSet();
        foreach (var topic in Topics)
        {
            var named = SnakeWord.Matches(Call(topic)).Select(m => m.Value).Distinct().ToList();
            Assert.NotEmpty(named);
            foreach (var word in named)
                Assert.True(tools.Contains(word) || codes.Contains(word), $"guide '{topic}' names '{word}', which is neither a registered tool nor a known error code");
        }
        // the guides together do talk about the tools of the import flow
        var all = string.Concat(Topics.Select(t => Call(t)));
        foreach (var name in new[] { "import_fetch", "import_status", "import_preview", "import_apply", "import_undo", "import_png", "set_canvas_origin", "save_image",
                     "discard_changes", "delete_property", "validate_image", "clear_cache", "get_guide", "init_data_source" })
        {
            Assert.Contains(name, tools);
            Assert.Contains(name, all);
        }
    }

    [Fact]
    public void EveryErrorCodeNamedInAGuideIsRaisedByTheCode()
    {
        var inCode = ErrorCodesInTheCode();
        if (inCode.Count == 0) return;   // the source is not next to the tests (a published test run): nothing to compare with
        foreach (var code in ErrorCodesTheGuidesName)
            Assert.Contains(code, inCode);
    }

    [Fact]
    public void EveryParameterNamedInAGuideBelongsToARegisteredTool()
    {
        var parameters = RegisteredTools().SelectMany(t => t.Method.GetParameters()).Select(p => p.Name!).ToHashSet();
        foreach (var topic in Topics)
            foreach (var word in CamelWord.Matches(Call(topic)).Select(m => m.Value).Distinct())
                Assert.True(parameters.Contains(word), $"guide '{topic}' names the parameter '{word}', which no registered tool has");
    }

    [Fact]
    public void ParametersWrittenAsNameEqualsValueAreRealParametersOfTheToolBeforeThem()
    {
        // e.g. import_fetch(url, kind="wz", expectedSha256?, category?) and import_png(category, image, parentPath, name, pngPath)
        var tools = RegisteredTools().ToDictionary(t => t.Name, t => t.Method.GetParameters().Select(p => p.Name!).ToHashSet());
        foreach (var topic in Topics)
            foreach (Match call in Regex.Matches(Call(topic), @"\b([a-z]+(?:_[a-z0-9]+)+)\(([^)\n]*)\)"))
            {
                if (!tools.TryGetValue(call.Groups[1].Value, out var real)) continue;
                foreach (var arg in call.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = Regex.Match(arg, @"^[A-Za-z0-9]+").Value;
                    if (name.Length == 0) continue;
                    Assert.True(real.Contains(name), $"guide '{topic}': {call.Groups[1].Value}(...) names '{name}', which is not a parameter of that tool");
                }
            }
    }

    [Fact]
    public void TheGuidesStateTheFixedRulesOfTheCode()
    {
        // the rules that are constants in the code, not settings
        Assert.Equal(4096, WzImgMCP.Imaging.CanvasImaging.MaxImportDimension);
        Assert.Equal(20L * 1024 * 1024, WzImgMCP.Imaging.CanvasImaging.MaxImportBytes);
        var text = Call("import-by-link");
        Assert.Contains("GMS", text);
        Assert.Contains("List.wz", text);
        Assert.Contains("Sound.wz", text);
        Assert.Contains("never deletes", text);
        Assert.Contains("Anyone with the link", text);
    }
}
#endif
