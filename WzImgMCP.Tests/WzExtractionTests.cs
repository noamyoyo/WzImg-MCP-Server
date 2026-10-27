using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// Tests on real WZ data that do not need GDI+. They share the AllowedRootEnv collection because
/// PathGuardTests changes a process-wide environment variable that every tool path goes through.
/// </summary>
[Collection("AllowedRootEnv")]
public class WzExtractionTests
{
    [SkippableFact]
    public void ExtractFromGmsWz_WritesImagesAndManifest()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        using var tree = new TempTree();

        var result = WzTestData.Extract(Path.Combine(WzTestData.WzDir!, "String.wz"), tree.Path, "GMS");

        MarkdownTestHelper.AssertSuccess(result);
        Assert.True(File.Exists(Path.Combine(tree.Path, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(tree.Path, "String", "Pet.img")));
        Assert.True(MarkdownTestHelper.GetFirstInt(result, "images_extracted") > 0, result);
    }

    [SkippableTheory]
    [InlineData("String")]
    [InlineData("string")]
    [InlineData("STRING")]
    public void CategoryLookup_IsCaseInsensitive(string category)
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        using var tree = WzTestData.CopyOfExtractedString();
        using var session = new WzSessionManager();
        session.InitDataSource(tree.Path);

        var result = new PropertyTools(session).GetString(category, "Pet.img", "5000000/name");

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("true", WzTestData.Value(result, "found"));
        Assert.False(string.IsNullOrEmpty(WzTestData.Value(result, "value")));
    }
}
