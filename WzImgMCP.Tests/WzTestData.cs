using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// Test data for the tests that need real WZ files and no GDI+ (so they also run on Linux).
/// Point WZIMG_TEST_WZ_PATH at a directory containing String.wz (GMS key, version 83). The tests
/// skip when it is not set. String.wz is extracted once per test run into a temp directory; every
/// test works on its own copy of that tree, so the originals are never modified.
/// </summary>
public static class WzTestData
{
    public const string EnvVar = "WZIMG_TEST_WZ_PATH";

    public static string? WzDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "String.wz")) ? dir : null;
        }
    }

    public static bool Available => WzDir != null;

    public const string SkipReason = EnvVar + " is not set to a directory containing String.wz";

    private static readonly Lazy<string> SharedExtraction = new(() =>
    {
        var root = Directory.CreateTempSubdirectory("wzimg-shared-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, true); } catch { } };
        var result = Extract(Path.Combine(WzDir!, "String.wz"), root, "GMS");
        MarkdownTestHelper.AssertSuccess(result);
        return root;
    });

    /// <summary>The shared extraction of String.wz. Read only: tests must work on a copy.</summary>
    public static string ExtractedStringRoot => SharedExtraction.Value;

    /// <summary>Runs extract_to_img on a WZ file with the given key (GMS, BMS, ...) as version v83.</summary>
    public static string Extract(string wzFile, string outputDir, string versionKey)
        => new BatchTools(new WzSessionManager())
            .ExtractToImg(wzFile, outputDir, versionKey, true, null, "v83", null, false);

    /// <summary>A private writable copy of the extracted String.wz tree (manifest.json + String/*.img).</summary>
    public static TempTree CopyOfExtractedString()
    {
        var tree = new TempTree();
        CopyDirectory(SharedExtraction.Value, tree.Path);
        return tree;
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    /// <summary>The value after "- key:" in the first matching line of a tool's markdown result.</summary>
    public static string? Value(string markdown, string key) => MarkdownTestHelper.GetFirstValue(markdown, key);
}

public sealed class TempTree : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("wzimg-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
