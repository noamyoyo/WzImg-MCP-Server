#if LINUX_BUILD
using System.Reflection;
using ModelContextProtocol.Server;
using WzImgMCP.Import;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>The five MCP tools: they are registered, they answer in the usual markdown form, and they use the same service.</summary>
[Collection("AllowedRootEnv")]
public sealed class ImportToolsTests
{
    private static readonly string[] Names = { "import_fetch", "import_status", "import_preview", "import_apply", "import_undo" };

    [Fact]
    public void TheFiveToolsAreRegisteredLikeTheOthers()
    {
        var found = typeof(ImportTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n != null).Order().ToArray();
        Assert.Equal(Names.Order().ToArray(), found);
        Assert.NotNull(typeof(ImportTools).GetCustomAttribute<McpServerToolTypeAttribute>());

        // the assembly scan the server uses finds them, next to all the old tools, with unique names
        var all = typeof(ImportTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n != null).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Contains("import_png", all);
        foreach (var name in Names) Assert.Contains(name, all);
    }

    [Fact]
    public void TheServiceCanBeBuiltByDependencyInjection()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<WzImgMCP.Server.WzSessionManager>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<ImportService>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient<ImportTools>(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        Assert.NotNull(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ImportTools>(provider));
    }

    [Fact]
    public async Task Tools_AnswerInMarkdown_AndShareTheService()
    {
        using var env = new ImportEnv();
        var tools = new ImportTools(env.Service);
        env.Web.ServeFile(ImportEnv.DirectLink, PngMaker.Png(), "a.png");

        var fetch = tools.ImportFetch(ImportEnv.ViewLink, "files");
        MarkdownTestHelper.AssertSuccess(fetch);
        var id = MarkdownTestHelper.GetFirstValue(fetch, "id")!;
        await env.Service.WaitForJobAsync(id, TimeSpan.FromSeconds(30));

        var status = tools.ImportStatus(id);
        MarkdownTestHelper.AssertSuccess(status);
        Assert.Equal("ready", MarkdownTestHelper.GetFirstValue(status, "status"));
        Assert.Contains("a.png", status);

        var refused = tools.ImportPreview(id);   // loose files have no preview
        MarkdownTestHelper.AssertFailure(refused);
        Assert.Equal("wrong_kind", MarkdownTestHelper.GetFirstValue(refused, "error_code"));

        var apply = tools.ImportApply(id, "token");
        Assert.Equal("wrong_kind", MarkdownTestHelper.GetFirstValue(apply, "error_code"));

        var undo = tools.ImportUndo(id);
        MarkdownTestHelper.AssertSuccess(undo);
        Assert.Equal("discarded", MarkdownTestHelper.GetFirstValue(undo, "outcome"));
    }

    [Fact]
    public void Refusals_ComeAsCodeAndSentence()
    {
        using var env = new ImportEnv();
        var tools = new ImportTools(env.Service);
        var text = tools.ImportFetch("http://drive.google.com/x", "wz");
        MarkdownTestHelper.AssertFailure(text);
        Assert.Equal("url_not_allowed", MarkdownTestHelper.GetFirstValue(text, "error_code"));
        Assert.Contains("secure", MarkdownTestHelper.GetFirstValue(text, "error")!);
        MarkdownTestHelper.AssertFailure(tools.ImportStatus("nope"));
    }
}
#endif
