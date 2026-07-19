using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

public class BatchToolsTests : IClassFixture<TestFixture>
{
    private readonly TestFixture _fixture;
    private readonly BatchTools _tools;

    public BatchToolsTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        _tools = new BatchTools(fixture.Session);
    }

    [Fact]
    public void PackToWz_CreatesWzFileFromImgFilesystem()
    {
        var packDir = Path.Combine(Path.GetTempPath(), "wz_pack_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pack = _tools.PackToWz(_fixture.TestDataPath, packDir, wzVersion: 83, category: "Character");
            MarkdownTestHelper.AssertSuccess(pack);
            Assert.True(File.Exists(Path.Combine(packDir, "Character.wz")), "Character.wz not created");
        }
        finally
        {
            if (Directory.Exists(packDir)) Directory.Delete(packDir, true);
        }
    }

    [Fact]
    public void ExtractToImg_RoundTripsPackedWz()
    {
        var packDir = Path.Combine(Path.GetTempPath(), "wz_pack_" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(Path.GetTempPath(), "wz_extract_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pack = _tools.PackToWz(_fixture.TestDataPath, packDir, wzVersion: 83, category: "Character");
            MarkdownTestHelper.AssertSuccess(pack);

            var extract = _tools.ExtractToImg(Path.Combine(packDir, "Character.wz"), extractDir, versionKey: "BMS");
            MarkdownTestHelper.AssertSuccess(extract);
            Assert.True(File.Exists(Path.Combine(extractDir, "Character", "Test.img")),
                "extracted img filesystem missing Character/Test.img");
        }
        finally
        {
            if (Directory.Exists(packDir)) Directory.Delete(packDir, true);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        }
    }

    [Fact]
    public void BatchSearchAndExport_Work()
    {
        var search = _tools.BatchSearch("test*", categories: "Character", searchType: "name", maxResults: 20);
        MarkdownTestHelper.AssertSuccess(search);
        Assert.Contains("result_count", search, StringComparison.OrdinalIgnoreCase);

        var outDir = Path.Combine(Path.GetTempPath(), "wz_batch_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var export = _tools.BatchExportImages("Character", outDir, format: "png", maxImages: 5);
            MarkdownTestHelper.AssertSuccess(export);
            Assert.Contains("exported_count", export, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }
}
