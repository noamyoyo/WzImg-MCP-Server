#if LINUX_BUILD
using System.Security.Cryptography;
using System.Text.Json;
using WzImgMCP.Packer;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;
using Xunit.Abstractions;

namespace WzImgMCP.Tests;

/// <summary>
/// OPT IN. Packs a REAL category and compares the result with the original .wz. Skipped unless <c>WZPACK_REAL_DATA</c> names a
/// folder that holds the original <c>&lt;Category&gt;.wz</c> files (it runs on omv6, where the game data is; there is none in the cloud).
/// <c>WZPACK_REAL_CATEGORY</c> picks the category (default <c>String</c>), <c>WZPACK_REAL_KEY</c> the key of the original (default <c>GMS</c>).
///
/// What it does: extract the original .wz into a person's copy, pack that copy through the packer over HTTP, ask verify to re-extract
/// the packed .wz and compare it with the copy (that is: with the original, value by value, picture by picture), and check the size
/// against the original (within 25 percent, the default of the "size sanity" check in the design). The byte difference between the
/// two .wz files is printed, not asserted: a repack need not reproduce the bytes of the vendor's tool.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackerRealDataTests
{
    public const string DataVar = "WZPACK_REAL_DATA";
    public const string CategoryVar = "WZPACK_REAL_CATEGORY";
    public const string KeyVar = "WZPACK_REAL_KEY";

    private readonly ITestOutputHelper _output;

    public PackerRealDataTests(ITestOutputHelper output) { _output = output; }

    [SkippableFact]
    public async Task PackOfARealCategory_IsSemanticallyIdenticalToTheOriginalWz()
    {
        var dataDir = Environment.GetEnvironmentVariable(DataVar);
        Skip.If(string.IsNullOrWhiteSpace(dataDir), $"{DataVar} is not set (opt-in test; it needs the original .wz files)");
        var category = Environment.GetEnvironmentVariable(CategoryVar) is { Length: > 0 } c ? c : "String";
        var key = Environment.GetEnvironmentVariable(KeyVar) is { Length: > 0 } k ? k : "GMS";
        var original = Path.Combine(dataDir!, category + ".wz");
        Skip.IfNot(File.Exists(original), $"{original} does not exist");

        var previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        using var fx = new PackerFixture();
        try
        {
            // 1. the original, extracted into the owner's copy
            var extract = new BatchTools(new WzSessionManager()).ExtractToImg(original, fx.Img("owner"), key, true, null, "v83", null, false);
            MarkdownTestHelper.AssertSuccess(extract);
            Assert.True(Directory.Exists(Path.Combine(fx.Img("owner"), category)), "the extraction made no category folder");
            var images = Directory.GetFiles(Path.Combine(fx.Img("owner"), category), "*.img", SearchOption.AllDirectories).Length;
            _output.WriteLine($"{category}: {images} images extracted");

            // 2. pack it through the packer service
            var options = new PackerOptions(PackerFixture.Token, fx.Roots, fx.Outbox)
            {
                TempRoot = fx.TempRoot,
                MaxImageBytes = 512L * 1024 * 1024,
                TimeBudget = TimeSpan.FromHours(2),
                MaxInputBytes = 64L * 1024 * 1024 * 1024,
                MaxWzBytes = 16L * 1024 * 1024 * 1024,
                MaxImages = 2_000_000,
            };
            await using var server = await PackerTestServer.StartAsync(options);
            server.Client.Timeout = TimeSpan.FromHours(2);
            var job = await server.PackAndWait("owner", category, seconds: 7200);
            Assert.True(job.GetProperty("state").GetString() == "done", job.ToString());
            var packed = job.GetProperty("wz").GetProperty("sizeBytes").GetInt64();
            var originalSize = new FileInfo(original).Length;
            _output.WriteLine($"original {originalSize} bytes, packed {packed} bytes ({(double)packed / originalSize:P1}); imgCount {job.GetProperty("imgCount").GetInt32()}; warnings {job.GetProperty("warnings").GetArrayLength()}");

            // 3. verify: re-extract the packed .wz and compare it with the copy
            var (_, verify) = await server.Verify(job.GetProperty("jobId").GetString()!);
            _output.WriteLine($"verify: identical {verify.GetProperty("identical").GetBoolean()}, files {verify.GetProperty("filesCompared").GetInt32()}, diffs {verify.GetProperty("diffs").GetArrayLength()}, truncated {verify.GetProperty("truncated").GetBoolean()}");
            foreach (var diff in verify.GetProperty("diffs").EnumerateArray().Take(20))
                _output.WriteLine($"  {diff.GetProperty("kind").GetString()} {diff.GetProperty("type").GetString()} {diff.GetProperty("path").GetString()}");
            Assert.True(verify.GetProperty("identical").GetBoolean(), verify.ToString());
            Assert.Equal(images, verify.GetProperty("filesCompared").GetInt32());

            // 4. size sanity against the original
            Assert.InRange((double)packed / originalSize, 0.75, 1.25);

            // 5. informational: are the two .wz files byte-identical?
            var packedFile = Path.Combine(fx.Outbox, job.GetProperty("jobId").GetString()!, category + ".wz");
            var same = SHA256.HashData(File.ReadAllBytes(packedFile)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(original)));
            _output.WriteLine($"byte-identical to the original .wz: {same}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, previousRoot);
        }
    }
}
#endif
