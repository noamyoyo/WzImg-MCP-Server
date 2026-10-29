#if LINUX_BUILD
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using WzImgMCP.Viewer;

namespace WzImgMCP.Tests;

/// <summary>
/// For tests that need a FIFO or a symbolic link. When the environment cannot create one the test FAILS: a silent skip
/// would let the security guarantee go untested in CI. Set <c>WZ_TEST_ALLOW_SKIP=1</c> to skip instead (a developer
/// machine without symlinks).
/// </summary>
internal static class EnvRequire
{
    public const string AllowSkipVariable = "WZ_TEST_ALLOW_SKIP";

    public static bool SkipAllowed => Environment.GetEnvironmentVariable(AllowSkipVariable) == "1";

    public static void Capability(bool available, string reason)
    {
        if (available) return;
        if (SkipAllowed) throw new Xunit.SkipException(reason);
        Assert.Fail($"{reason}. This test must run (it checks a security guarantee); set {AllowSkipVariable}=1 to allow skipping it.");
    }
}

/// <summary>Builds small .img files with the library's own writer.</summary>
internal static class ImgFixture
{
    public static WzCanvasProperty Canvas(string name, int width = 4, int height = 4, byte shade = 200)
    {
        var canvas = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = shade; pixels[i + 1] = shade; pixels[i + 2] = shade; pixels[i + 3] = 255; }
        canvas.PngProperty.SetPixelsBgra32(pixels, width, height);
        canvas.AddProperty(new WzVectorProperty("origin", new WzIntProperty("X", 1), new WzIntProperty("Y", 2)));
        return canvas;
    }

    /// <summary>The default content: 2 strings, 1 int, 1 sub property with a child, 2 canvases (7 nodes at depth 1 are 5 top-level properties).</summary>
    public static byte[] Bytes(WzMapleVersion key, Action<WzImage>? fill = null, byte[]? customIv = null)
    {
        var img = new WzImage("fixture.img") { Changed = true };
        img.AddProperty(new WzStringProperty("name", "Fixture"));
        img.AddProperty(new WzStringProperty("desc", "A synthetic image"));
        img.AddProperty(new WzIntProperty("marker", 1));
        var info = new WzSubProperty("info");
        info.AddProperty(new WzIntProperty("id", 7));
        img.AddProperty(info);
        img.AddProperty(Canvas("0"));
        img.AddProperty(Canvas("1"));
        fill?.Invoke(img);

        using var stream = new MemoryStream();
        using (var writer = new WzBinaryWriter(stream, customIv ?? WzTool.GetIvByMapleVersion(key)))
            img.SaveImage(writer, true);
        return stream.ToArray();
    }

    /// <summary>An image with only what <paramref name="fill"/> adds (no default content).</summary>
    public static byte[] Build(Action<WzImage> fill, WzMapleVersion key = WzMapleVersion.BMS)
    {
        var img = new WzImage("fixture.img") { Changed = true };
        fill(img);
        using var stream = new MemoryStream();
        using (var writer = new WzBinaryWriter(stream, WzTool.GetIvByMapleVersion(key)))
            img.SaveImage(writer, true);
        return stream.ToArray();
    }

    /// <summary>A canvas whose pixels come from <paramref name="pixel"/> (b, g, r, a for each x, y).</summary>
    public static WzCanvasProperty PixelCanvas(string name, int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel,
        Microsoft.Xna.Framework.Graphics.SurfaceFormat? format = null)
    {
        var canvas = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                int i = (y * width + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = a;
            }
        canvas.PngProperty.SetPixelsBgra32(pixels, width, height, format ?? Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra32);   // lossless unless asked otherwise
        return canvas;
    }

    /// <summary>A tiny PCM WAV file, and a sound property made from it (the library keeps the file bytes).</summary>
    public static WzBinaryProperty Sound(string name, string directory, byte tone, int sampleRate = 8000)
    {
        var data = new byte[3200];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(tone + i % 7);
        var path = Path.Combine(directory, $"tone-{tone}-{sampleRate}.wav");
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write("RIFF"u8.ToArray()); w.Write(36 + data.Length); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(data.Length); w.Write(data);
        }
        return new WzBinaryProperty(name, path);
    }

    public static string Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, "img", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string Write(string root, string relative, WzMapleVersion key = WzMapleVersion.BMS, Action<WzImage>? fill = null)
        => Write(root, relative, Bytes(key, fill));

    public static void WriteManifest(string root, string encryption)
    {
        Directory.CreateDirectory(Path.Combine(root, "img"));
        File.WriteAllText(Path.Combine(root, "img", "manifest.json"), "{ \"version\": \"v83\", \"encryption\": \"" + encryption + "\" }");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    public static bool TryMakeFifo(string path)
    {
        try { return mkfifo(path, 0x1A4) == 0; }   // 0644
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public static bool TrySymlink(string link, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target); else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }
}

/// <summary>The real file system with hooks: count and block reads, or change what a read reports.</summary>
internal sealed class FakeFileSystem : IViewerFileSystem
{
    private readonly PosixViewerFileSystem _inner = new();
    public int Reads;
    public SemaphoreSlim ReadEntered { get; } = new(0);
    public ManualResetEventSlim? BlockReads { get; set; }
    public Func<int, FileRead, FileRead>? Rewrite { get; set; }

    public FileStamp? Stat(string path) => _inner.Stat(path);
    public Func<string, PathState?>? ProbeOverride { get; set; }
    public PathState Probe(string path) => ProbeOverride?.Invoke(path) ?? _inner.Probe(path);
    public Func<string, FileIdentity?>? IdentityOverride { get; set; }
    public FileIdentity? Identity(string path) => IdentityOverride != null ? IdentityOverride(path) : _inner.Identity(path);

    public FileRead Read(string path, long maxBytes, FileIdentity? expected)
    {
        int n = Interlocked.Increment(ref Reads);
        ReadEntered.Release();
        BlockReads?.Wait();
        var real = _inner.Read(path, maxBytes, expected);
        return Rewrite?.Invoke(n, real) ?? real;
    }
}

/// <summary>Temp tree with the three source roots, each with an img folder.</summary>
internal sealed class ViewerTree : IDisposable
{
    public string Temp { get; } = Directory.CreateTempSubdirectory("viewer-v2-").FullName;
    public ViewerTree()
    {
        foreach (var n in ViewerOptions.SourceNames) Directory.CreateDirectory(Path.Combine(Temp, n, "img"));
    }
    public string Root(string source) => Path.Combine(Temp, source);
    public Dictionary<string, string> Roots => ViewerOptions.SourceNames.ToDictionary(n => n, Root);
    public const string Token = "tok-kkkkkkkkkkkkkkkkkkkkkkkkkkkk-0123456789abcdef";
    public ViewerOptions Options(long? maxFileBytes = null, long? cacheBytes = null, TimeSpan? timeout = null, int? maxRequests = null, int? maxDecodes = null)
        => new(Token, Roots)
        {
            MaxFileBytes = maxFileBytes ?? ViewerOptions.DefaultMaxFileMb * 1024L * 1024L,
            CacheBytes = cacheBytes ?? ViewerOptions.DefaultCacheMb * 1024L * 1024L,
            RequestTimeout = timeout ?? ViewerOptions.DefaultRequestTimeout,
            MaxConcurrentRequests = maxRequests ?? ViewerOptions.DefaultMaxConcurrentRequests,
            MaxDecodes = maxDecodes ?? ViewerOptions.DefaultMaxDecodes,
        };
    public void Dispose() { try { Directory.Delete(Temp, true); } catch { } }
}

/// <summary>A started viewer host with a client and a log capture.</summary>
internal sealed class ViewerTestServer : IAsyncDisposable
{
    public WebApplication App { get; private init; } = null!;
    public HttpClient Client { get; private init; } = null!;
    public ConcurrentQueue<string> Logs { get; private init; } = null!;
    public string Token { get; private init; } = null!;

    public static async Task<ViewerTestServer> StartAsync(ViewerOptions options, IViewerFileSystem? fileSystem = null)
    {
        var logs = new ConcurrentQueue<string>();
        var builder = ViewerHost.CreateBuilder(Array.Empty<string>(), options, "http://127.0.0.1:0", null, fileSystem);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Capture(logs));
        var app = builder.Build();
        ViewerHost.Configure(app, options);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(ViewerOptions.TokenHeader, options.Token);
        return new ViewerTestServer { App = app, Client = client, Logs = logs, Token = options.Token };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }

    private sealed class Capture : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _sink;
        public Capture(ConcurrentQueue<string> sink) { _sink = sink; }
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Enqueue(formatter(state, exception));
    }
}
#endif
