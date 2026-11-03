#if LINUX_BUILD
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Import;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A web server in memory: routes by full address, every request recorded, no network.</summary>
internal sealed class FakeWeb : HttpMessageHandler
{
    public ConcurrentQueue<Uri> Requests { get; } = new();
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes = new();
    public Dictionary<string, IPAddress[]> Dns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IPAddress[] DefaultAddress { get; set; } = { IPAddress.Parse("142.250.74.110") };

    public HostResolver Resolver => (host, _) => Task.FromResult(Dns.TryGetValue(host, out var ips) ? ips : DefaultAddress);

    public void Route(string url, Func<HttpResponseMessage> response) => _routes[Key(url)] = (_, _) => Task.FromResult(response());
    public void Route(string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _routes[Key(url)] = response;
    public void ServeFile(string url, byte[] bytes, string? fileName, string contentType = "application/octet-stream")
        => Route(url, () => File(bytes, fileName, contentType));
    public void Redirect(string url, string location, int status = 302)
        => Route(url, () =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)status);
            r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return r;
        });

    public static HttpResponseMessage File(byte[] bytes, string? fileName, string contentType = "application/octet-stream")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (fileName != null)
            r.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "\"" + fileName + "\"" };
        return r;
    }

    public static HttpResponseMessage Html(string html)
        => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static string Key(string url) => new Uri(url).GetLeftPart(UriPartial.Query);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        if (_routes.TryGetValue(Key(request.RequestUri!.ToString()), out var route))
            return await route(request, cancellationToken).ConfigureAwait(false);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// One test's world: an allowed root with a tree in it (img/, a Skill category read like the real one), an import service with a fake
/// web and a manual clock, and the editor session. The allowed-root environment variable is set for the life of the object.
/// </summary>
internal sealed class ImportEnv : IDisposable
{
    public const string DriveFileId = "1AbCdEfGhIjKlMnOpQrStUv";
    public const string ViewLink = "https://drive.google.com/file/d/" + DriveFileId + "/view?usp=sharing";
    public const string DirectLink = "https://drive.usercontent.google.com/download?id=" + DriveFileId + "&export=download&confirm=t";

    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    public string Temp { get; } = Directory.CreateTempSubdirectory("import-test-").FullName;
    public string Root => Path.Combine(Temp, "data");
    public string Tree => Path.Combine(Root, "img");
    public string Inbox => Path.Combine(Root, "inbox");
    public string LockFile => Path.Combine(Root, ".import-lock");
    public ManualTime Clock { get; } = new();
    public FakeWeb Web { get; } = new();
    public WzSessionManager Session { get; }
    public ImportService Service { get; private set; }
    public ImportOptions Options { get; }

    public ImportEnv(Func<ImportOptions, ImportOptions>? tweak = null, string? copyTreeFrom = null, bool setRoot = true, Action<string>? prepareTree = null)
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, setRoot ? Root : null);
        if (copyTreeFrom != null)
            WzTestData.CopyDirectory(copyTreeFrom, Tree);
        prepareTree?.Invoke(Tree);
        Options = tweak?.Invoke(new ImportOptions()) ?? new ImportOptions();
        Session = new WzSessionManager();
        if (copyTreeFrom != null && setRoot)
            Session.InitDataSource(Tree);
        Service = NewService();
    }

    public ImportService NewService() => Service = new ImportService(Session, Options, Clock, Web.Resolver, () => Web);

    public async Task<ImportStatusResult> FetchAndWait(string url, string? kind = "wz", string? sha = null, string? category = null, int seconds = 60)
    {
        var fetched = Service.Fetch(url, kind, sha, category);
        var fetch = Assert.IsType<ImportFetchResult>(fetched);
        await Service.WaitForJobAsync(fetch.Id!, TimeSpan.FromSeconds(seconds));
        return Assert.IsType<ImportStatusResult>(Service.GetStatus(fetch.Id));
    }

    public string DirOf(string id) => Path.Combine(Inbox, id);

    public ImportPreviewResult Preview(string id) => Assert.IsType<ImportPreviewResult>(Service.Preview(id));

    public void Dispose()
    {
        Service.Dispose();
        Session.Dispose();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(Temp, true); } catch { }
    }

    public static T Failure<T>(ImportResultBase result, string code) where T : ImportResultBase
    {
        Assert.False(result.Success, "expected a refusal but the call succeeded");
        Assert.Equal(code, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        return (T)result;
    }

    public static void AssertRefused(ImportResultBase result, string code)
    {
        Assert.False(result.Success, "expected a refusal but the call succeeded: " + result.Error);
        Assert.Equal(code, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    /// <summary>Every file below a folder with its hash, for "nothing changed" checks. Folders are listed too.</summary>
    public static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return map;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, entry)] = Directory.Exists(entry) ? "<dir>" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
        return map;
    }
}

/// <summary>
/// Real .wz files for the import tests, made once: version 1 of a Skill category (extracted into a tree) and version 2 of it.
/// Version 2 against that tree: A unchanged, B changed (one value changed, one added, one removed), C new, C2 new in an existing folder
/// whose name differs in capitals; D is in the tree only. Also a BMS-key copy (not GMS) and a Mob.wz.
/// </summary>
public sealed class ImportWzData : IDisposable
{
    public string Temp { get; } = Directory.CreateTempSubdirectory("import-wzdata-").FullName;
    /// <summary>The extracted version 1 tree (manifest.json, Skill/...), plus D.img added by hand and the folder renamed to SUB.</summary>
    public string TreeV1 => Path.Combine(Temp, "tree-v1");
    public byte[] V2Wz { get; private set; } = Array.Empty<byte>();
    public byte[] V2WzBms { get; private set; } = Array.Empty<byte>();
    public byte[] MobWz { get; private set; } = Array.Empty<byte>();

    public static byte[] ImgBytes(string who, Action<WzImage>? extra = null)
        => ImgFixture.Bytes(WzMapleVersion.BMS, img =>
        {
            img.AddProperty(new WzStringProperty("who", who));
            extra?.Invoke(img);
        });

    public ImportWzData()
    {
        var v1 = Path.Combine(Temp, "src-v1");
        var v2 = Path.Combine(Temp, "src-v2");
        WriteSkill(v1, v1: true);
        WriteSkill(v2, v1: false);

        var out1 = Pack(v1, "GMS", "Skill");
        var out2 = Pack(v2, "GMS", "Skill");
        V2Wz = File.ReadAllBytes(Path.Combine(out2, "Skill.wz"));
        var outBms = Pack(v2, "BMS", "Skill");
        V2WzBms = File.ReadAllBytes(Path.Combine(outBms, "Skill.wz"));

        var mob = Path.Combine(Temp, "src-mob");
        ImgFixture.Write(mob, "Mob/1.img", WzMapleVersion.BMS, null);
        ImgFixture.WriteManifest(mob, "BMS");
        MobWz = File.ReadAllBytes(Path.Combine(Pack(mob, "GMS", "Mob"), "Mob.wz"));

        // the tree: version 1 extracted by the library, like the real trees are
        var result = new WzExtractionService().ExtractAsync(out1, TreeV1, "v83", "test tree", WzMapleVersion.GMS, new List<string> { "Skill" }, false)
            .GetAwaiter().GetResult();
        if (!result.Success) throw new InvalidOperationException("could not extract the test tree");
        File.WriteAllBytes(Path.Combine(TreeV1, "Skill", "D.img"), ImgBytes("D-tree-only"));
        // a folder spelled differently from the upload's
        Directory.Move(Path.Combine(TreeV1, "Skill", "Sub"), Path.Combine(TreeV1, "Skill", "SUB"));
        // categories that must be refused by name
        foreach (var name in new[] { "Mob", "Sound", "List" })
            Directory.CreateDirectory(Path.Combine(TreeV1, name));
        File.WriteAllBytes(Path.Combine(TreeV1, "Mob", "1.img"), ImgBytes("mob"));
    }

    private static void WriteSkill(string root, bool v1)
    {
        ImgFixture.WriteManifest(root, "BMS");
        ImgFixture.Write(root, "Skill/A.img", ImgBytes("A"));
        if (v1)
            ImgFixture.Write(root, "Skill/B.img", ImgBytes("B-old", img => img.AddProperty(new WzIntProperty("old", 1))));
        else
            ImgFixture.Write(root, "Skill/B.img", ImgBytes("B-new", img => img.AddProperty(new WzIntProperty("extra", 5))));
        ImgFixture.Write(root, "Skill/Sub/X.img", ImgBytes("X-" + (v1 ? "old" : "new")));
        if (!v1)
        {
            ImgFixture.Write(root, "Skill/C.img", ImgBytes("C-new"));
            ImgFixture.Write(root, "Skill/Sub/Y.img", ImgBytes("Y-new"));
        }
    }

    private string Pack(string imgRoot, string key, string category)
    {
        var output = Path.Combine(Temp, "wz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var result = new WzPackingService().PackCategoriesAsync(Path.Combine(imgRoot, "img"), output, new List<string> { category }, false,
            overridePatchVersion: 83, separateCanvas: false,
            overrideEncryption: Enum.Parse<WzMapleVersion>(key)).GetAwaiter().GetResult();
        if (!result.Success || !File.Exists(Path.Combine(output, category + ".wz")))
            throw new InvalidOperationException("could not pack the test wz: " + result.ErrorMessage);
        return output;
    }

    public void Dispose() { try { Directory.Delete(Temp, true); } catch { } }
}

/// <summary>PNG and zip builders for the loose-files tests.</summary>
internal static class PngMaker
{
    public static byte[] Png(int width = 2, int height = 2)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 10; pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        return EncodeRaw(pixels, width, height);
    }

    private static byte[] EncodeRaw(byte[] bgra, int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A zip with these entries (name, bytes). The mode of each entry can be set for the symbolic link test.</summary>
    public static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, Action<ZipArchiveEntry>? tweak = null, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                using (var s = entry.Open()) s.Write(data, 0, data.Length);
                tweak?.Invoke(entry);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A PNG header followed by many zeros: valid enough to pass the signature check, compresses to almost nothing.</summary>
    public static byte[] PngLookalikeOfZeros(int length)
    {
        var data = new byte[length];
        Png().AsSpan(0, 8).CopyTo(data);
        return data;
    }
}
#endif
