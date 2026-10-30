#if LINUX_BUILD
using System.Diagnostics;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using WzImgMCP.Packer;
using WzImgMCP.Utils;
using Xunit.Abstractions;

namespace WzImgMCP.Tests;

/// <summary>
/// OPT IN. Repeats the packer's two guarantees on a REAL category. Skipped unless <c>WZPACK_REAL_DATA</c> names a folder that holds the
/// original <c>&lt;Category&gt;.wz</c> files (it runs on omv6, where the game data is; there is none in the cloud). Nothing in that
/// folder is written: the packer only reads it. <c>WZPACK_REAL_CATEGORY</c> picks the category (default <c>String</c>),
/// <c>WZPACK_REAL_IMG</c> the image to replace (a path inside the .wz such as <c>Sub/Deep.img</c>; default: the first image),
/// <c>WZPACK_REAL_OUT</c> a folder with room for two copies of the category (default: the system temp folder).
///
/// 1. Round trip: open the .wz (version 83, key GMS), save it with no change to another path, and require the same sha256 and the same
///    raw stored block for every image.
/// 2. One-image replacement through the packer over HTTP: take one image out of the .wz as an extracted (zero key) .img, pack it back
///    into the published .wz, and ask verify. Every other image must be raw identical, only the listed one may differ, and the listed
///    one must equal what went in.
/// Reports the time and the peak working set of the process after each step (the packer runs in 4 GB).
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class PackerRealDataTests
{
    public const string DataVar = "WZPACK_REAL_DATA";
    public const string CategoryVar = "WZPACK_REAL_CATEGORY";
    public const string ImgVar = "WZPACK_REAL_IMG";
    public const string OutVar = "WZPACK_REAL_OUT";

    private readonly ITestOutputHelper _output;

    public PackerRealDataTests(ITestOutputHelper output) { _output = output; }

    private void Report(string step, Stopwatch clock)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        _output.WriteLine($"{step}: {clock.Elapsed.TotalSeconds:F1} s, peak working set {process.PeakWorkingSet64 / (1024 * 1024)} MB, now {process.WorkingSet64 / (1024 * 1024)} MB");
    }

    /// <summary>A new empty folder, below <c>WZPACK_REAL_OUT</c> when that is set (the real files are big), else in the system temp folder.</summary>
    private static string WorkFolder(string prefix)
    {
        var parent = Environment.GetEnvironmentVariable(OutVar);
        if (string.IsNullOrWhiteSpace(parent)) return Directory.CreateTempSubdirectory(prefix).FullName;
        var path = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? Original(out string category)
    {
        var dataDir = Environment.GetEnvironmentVariable(DataVar);
        category = Environment.GetEnvironmentVariable(CategoryVar) is { Length: > 0 } c ? c : "String";
        return string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, category + ".wz");
    }

    [SkippableFact]
    public void RoundTripOfARealCategory_IsByteIdentical_AndEveryRawBlockIsEqual()
    {
        var original = Original(out var category);
        Skip.If(original == null, $"{DataVar} is not set (opt-in test; it needs the original .wz files)");
        Skip.IfNot(File.Exists(original), $"{original} does not exist");

        var outDir = WorkFolder("wzpack-rt-");
        try
        {
            var copy = Path.Combine(outDir, category + ".wz");
            var clock = Stopwatch.StartNew();
            using (var wz = new WzFile(original!, 83, WzMapleVersion.GMS))
            {
                Skip.IfNot(wz.ParseWzFile() == WzFileParseStatus.Success && wz.Version == 83, "the original does not open as version 83 with the key GMS");
                Report("open", clock);
                clock.Restart();
                wz.SaveToDisk(copy);
                Report("save with no change", clock);
            }

            clock.Restart();
            var before = SyntheticWz.Blocks(original!);
            var after = SyntheticWz.Blocks(copy);
            Report("raw blocks of both files", clock);
            _output.WriteLine($"{category}: {before.Count} images, {new FileInfo(original!).Length} bytes");

            Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
            var different = before.Where(kv => after[kv.Key].Sha256 != kv.Value.Sha256).Select(kv => kv.Key).Take(10).ToList();
            Assert.True(different.Count == 0, "raw blocks differ: " + string.Join(", ", different));
            Assert.Equal(SyntheticWz.Sha256(original!), SyntheticWz.Sha256(copy));
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { }
        }
    }

    [SkippableFact]
    public async Task OneImageReplacedInARealCategory_LeavesEveryOtherImageRawIdentical()
    {
        var original = Original(out var category);
        Skip.If(original == null, $"{DataVar} is not set (opt-in test; it needs the original .wz files)");
        Skip.IfNot(File.Exists(original), $"{original} does not exist");

        var previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        var outDir = WorkFolder("wzpack-one-");
        using var fx = new PackerFixture();
        try
        {
            var clock = Stopwatch.StartNew();

            // 1. one image of the real file, taken out as an extracted .img (zero key) into the owner's copy
            string path;
            using (var wz = new WzFile(original!, 83, WzMapleVersion.GMS))
            {
                Skip.IfNot(wz.ParseWzFile() == WzFileParseStatus.Success && wz.Version == 83, "the original does not open as version 83 with the key GMS");
                var wanted = Environment.GetEnvironmentVariable(ImgVar);
                var all = new List<string>();
                void Walk(WzDirectory dir, string prefix)
                {
                    foreach (var image in dir.WzImages) all.Add(prefix + image.Name);
                    foreach (var sub in dir.WzDirectories) Walk(sub, prefix + sub.Name + "/");
                }
                Walk(wz.WzDirectory, "");
                path = string.IsNullOrEmpty(wanted) ? all.OrderBy(p => p, StringComparer.Ordinal).First() : wanted;
                Assert.Contains(path, all);
                var image = SyntheticWz.Find(wz, path);
                Assert.True(image.ParseImage());
                var bytes = WzImgSerializer.CreateForImgExtraction().SerializeImage(image);
                ImgFixture.Write(fx.Root("owner"), category + "/" + path, bytes);
                _output.WriteLine($"{category}: replacing {path} ({bytes.Length} bytes), {all.Count} images in the file");
            }
            Report("take one image out", clock);

            // 2. pack it back into the real published file, which is only read
            var options = new PackerOptions(PackerFixture.Token, fx.Roots, fx.Outbox, Path.GetDirectoryName(original!)!)
            {
                TempRoot = Path.Combine(outDir, "tmp"),
                MaxImageBytes = 512L * 1024 * 1024,
                TimeBudget = TimeSpan.FromHours(2),
                MaxInputBytes = 4L * 1024 * 1024 * 1024,
                MaxWzBytes = 16L * 1024 * 1024 * 1024,
            };
            Directory.CreateDirectory(options.TempRoot);
            await using var server = await PackerTestServer.StartAsync(options);
            server.Client.Timeout = TimeSpan.FromHours(2);
            var publishedHash = SyntheticWz.Sha256(original!);
            clock.Restart();
            var job = await server.WaitForJob(await server.StartPackIn(category, PackerTestServer.Set(category + "/" + path)), seconds: 7200);
            Assert.True(job.GetProperty("state").GetString() == "done", job.ToString());
            Report("pack", clock);
            var packed = job.GetProperty("wz").GetProperty("sizeBytes").GetInt64();
            _output.WriteLine($"original {new FileInfo(original!).Length} bytes, packed {packed} bytes; result {job.GetProperty("files")[0].GetProperty("result").GetString()}");

            // 3. verify
            clock.Restart();
            var (_, verify) = await server.Verify(job.GetProperty("jobId").GetString()!);
            Report("verify", clock);
            _output.WriteLine($"verify: identical {verify.GetProperty("identical").GetBoolean()}, unchangedIdentical {verify.GetProperty("unchangedIdentical").GetBoolean()}, "
                + $"onlyListedDiffer {verify.GetProperty("onlyListedDiffer").GetBoolean()}, unchanged compared {verify.GetProperty("unchangedCompared").GetInt32()}, "
                + $"listed compared {verify.GetProperty("filesCompared").GetInt32()}, diffs {verify.GetProperty("diffs").GetArrayLength()}");
            foreach (var diff in verify.GetProperty("diffs").EnumerateArray().Take(20))
                _output.WriteLine($"  {diff.GetProperty("kind").GetString()} {diff.GetProperty("type").GetString()} {diff.GetProperty("path").GetString()}");
            Assert.True(verify.GetProperty("identical").GetBoolean(), verify.ToString());
            Assert.Equal(83, verify.GetProperty("version").GetInt32());
            Assert.Equal("GMS", verify.GetProperty("key").GetString());

            // 4. nothing but the listed image differs, checked again here with the raw blocks
            var newWz = Path.Combine(fx.Outbox, job.GetProperty("jobId").GetString()!, category + ".wz");
            var before = SyntheticWz.Blocks(original!);
            var after = SyntheticWz.Blocks(newWz);
            Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
            var changed = before.Where(kv => after[kv.Key].Sha256 != kv.Value.Sha256).Select(kv => kv.Key).ToList();
            _output.WriteLine($"images whose raw block differs: {changed.Count} ({string.Join(", ", changed.Take(5))})");
            Assert.All(changed, c => Assert.Equal(path, c));
            Assert.Equal(publishedHash, SyntheticWz.Sha256(original!));   // the published file was only read
            Assert.InRange((double)packed / new FileInfo(original!).Length, 0.75, 1.25);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, previousRoot);
            try { Directory.Delete(outDir, true); } catch { }
        }
    }
}
#endif
