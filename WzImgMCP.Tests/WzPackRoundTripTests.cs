using System.Text.RegularExpressions;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.Util;
using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// edit -> save -> pack_to_wz -> extract_to_img -> read back, including trees whose manifest encryption
/// does not match the key the .img files were written with (the packer tries other IVs per image).
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class WzPackRoundTripTests
{
    private const string Category = "String";
    private const string Image = "Pet.img";
    private const string NamePath = "5000000/name";

    private static string ReadName(string imgRoot)
    {
        using var session = new WzSessionManager();
        session.InitDataSource(imgRoot);
        var r = new PropertyTools(session).GetString(Category, Image, NamePath);
        MarkdownTestHelper.AssertSuccess(r);
        return WzTestData.Value(r, "value") ?? "";
    }

    private static int PropertyCount(string imgRoot)
    {
        using var session = new WzSessionManager();
        session.InitDataSource(imgRoot);
        return MarkdownTestHelper.GetFirstInt(new LifecycleTools(session).ParseImage(Category, Image), "property_count");
    }

    private static string Pack(string imgRoot, string outDir, string? versionKey)
    {
        var result = new BatchTools(new WzSessionManager())
            .PackToWz(imgRoot, outDir, 83, null, versionKey);
        MarkdownTestHelper.AssertSuccess(result);
        Assert.True(File.Exists(Path.Combine(outDir, "String.wz")), result);
        return Path.Combine(outDir, "String.wz");
    }

    private static string[] RelativeFiles(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray();

    /// <summary>Relative paths of files whose bytes differ (or exist on one side only), manifest.json excluded.</summary>
    private static List<string> Differences(string expectedRoot, string actualRoot)
    {
        var expected = RelativeFiles(expectedRoot).Where(f => f != "manifest.json").ToArray();
        var actual = RelativeFiles(actualRoot).Where(f => f != "manifest.json").ToArray();
        var diff = new List<string>();
        diff.AddRange(expected.Except(actual).Select(f => "missing: " + f));
        diff.AddRange(actual.Except(expected).Select(f => "extra: " + f));
        foreach (var f in expected.Intersect(actual))
        {
            if (!File.ReadAllBytes(Path.Combine(expectedRoot, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actualRoot, f))))
                diff.Add("differs: " + f);
        }
        return diff;
    }

    private static void SetManifestEncryption(string imgRoot, string encryption)
    {
        var path = Path.Combine(imgRoot, "manifest.json");
        var json = File.ReadAllText(path);
        const string pattern = "\"encryption\"\\s*:\\s*\"[^\"]*\"";
        Assert.Matches(pattern, json);
        File.WriteAllText(path, Regex.Replace(json, pattern, $"\"encryption\": \"{encryption}\""));
    }

    /// <summary>Re-serializes every .img of the String category with the IV of another key.</summary>
    private static void ReencodeImages(string imgRoot, WzMapleVersion from, WzMapleVersion to)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(imgRoot, Category), "*.img", SearchOption.AllDirectories))
        {
            byte[] bytes;
            var image = new WzImgDeserializer(false, false)
                .WzImageFromIMGFile(file, WzTool.GetIvByMapleVersion(from), Path.GetFileName(file), out bool ok);
            try
            {
                Assert.True(ok && image != null && image.ParseImage(), "could not read " + file);
                image!.Changed = true;
                using var ms = new MemoryStream();
                using (var writer = new WzBinaryWriter(ms, WzTool.GetIvByMapleVersion(to)))
                {
                    image.SaveImage(writer, true);
                    writer.Flush();
                    bytes = ms.ToArray();
                }
            }
            finally
            {
                image?.Dispose();
            }
            File.WriteAllBytes(file, bytes);
        }
    }

    [SkippableFact]
    public void EditSavePackExtract_ReadsBackTheEdit_AndOnlyTheEditedImageDiffers()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        using var tree = WzTestData.CopyOfExtractedString();
        using var packed = new TempTree();
        using var reextracted = new TempTree();
        var originalCount = PropertyCount(WzTestData.ExtractedStringRoot);

        using (var session = new WzSessionManager())
        {
            session.InitDataSource(tree.Path);
            var modify = new ModifyTools(session);
            MarkdownTestHelper.AssertSuccess(modify.SetString(Category, Image, NamePath, "ROUND-TRIP-EDIT"));
            MarkdownTestHelper.AssertSuccess(modify.SaveImage(Category, Image));
        }

        var wz = Pack(tree.Path, packed.Path, "GMS");
        MarkdownTestHelper.AssertSuccess(WzTestData.Extract(wz, reextracted.Path, "GMS"));

        Assert.Equal("ROUND-TRIP-EDIT", ReadName(reextracted.Path));
        Assert.Equal(originalCount, PropertyCount(reextracted.Path));
        Assert.Equal(new[] { "differs: String/Pet.img" }, Differences(WzTestData.ExtractedStringRoot, reextracted.Path));
    }

    [SkippableFact]
    public void PackWithoutEdits_ReproducesTheExtractedTree()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        using var tree = WzTestData.CopyOfExtractedString();
        using var packed = new TempTree();
        using var reextracted = new TempTree();

        var wz = Pack(tree.Path, packed.Path, "GMS");
        MarkdownTestHelper.AssertSuccess(WzTestData.Extract(wz, reextracted.Path, "GMS"));

        Assert.Empty(Differences(WzTestData.ExtractedStringRoot, reextracted.Path));
    }

    [SkippableFact]
    public void MixedKey_ManifestSaysGms_ButImagesAreBms_PacksUsingTheManifest()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        using var tree = WzTestData.CopyOfExtractedString();   // extracted .img files are BMS-encoded
        SetManifestEncryption(tree.Path, "GMS");
        using var packed = new TempTree();
        using var reextracted = new TempTree();

        var wz = Pack(tree.Path, packed.Path, versionKey: null);   // target key comes from the manifest
        MarkdownTestHelper.AssertSuccess(WzTestData.Extract(wz, reextracted.Path, "GMS"));

        Assert.Empty(Differences(WzTestData.ExtractedStringRoot, reextracted.Path));
    }

    [SkippableTheory]
    [InlineData("BMS", null)]    // manifest says BMS, images are GMS: the BMS read fails, the GMS fallback succeeds
    [InlineData("BMS", "GMS")]   // same trees, key override differs from the manifest
    public void MixedKey_ImagesAreGms_ButManifestSaysBms_FallbackReadsThem(string manifestKey, string? packKey)
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        using var tree = WzTestData.CopyOfExtractedString();
        ReencodeImages(tree.Path, WzMapleVersion.BMS, WzMapleVersion.GMS);
        SetManifestEncryption(tree.Path, manifestKey);
        using var packed = new TempTree();
        using var reextracted = new TempTree();

        var wz = Pack(tree.Path, packed.Path, packKey);
        MarkdownTestHelper.AssertSuccess(WzTestData.Extract(wz, reextracted.Path, packKey ?? manifestKey));

        Assert.Empty(Differences(WzTestData.ExtractedStringRoot, reextracted.Path));
    }
}
