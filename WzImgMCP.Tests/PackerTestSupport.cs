#if LINUX_BUILD
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WzImgMCP.Packer;
using WzImgMCP.Viewer;

namespace WzImgMCP.Tests;

/// <summary>Temp area for the packer: the three source roots (each with an img folder), the published folder, the outbox and a temp root for verify.</summary>
internal sealed class PackerFixture : IDisposable
{
    public const string Token = "pack-kkkkkkkkkkkkkkkkkkkkkkkkkk-0123456789abcdef";
    public const string Category = "String";

    public string Temp { get; } = Directory.CreateTempSubdirectory("packer-test-").FullName;

    public PackerFixture()
    {
        foreach (var name in PackerOptions.SourceNames) Directory.CreateDirectory(Path.Combine(Temp, name, "img"));
        Directory.CreateDirectory(Outbox);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(Published);
    }

    public string Root(string source) => Path.Combine(Temp, source);
    public string Img(string source) => Path.Combine(Temp, source, "img");
    public string Outbox => Path.Combine(Temp, "outbox");
    public string Published => Path.Combine(Temp, "published");
    public string PublishedWz(string category = Category) => Path.Combine(Published, category + ".wz");
    public string TempRoot => Path.Combine(Temp, "tmp");
    public Dictionary<string, string> Roots => PackerOptions.SourceNames.ToDictionary(n => n, Root);

    public PackerOptions Options(Func<PackerOptions, PackerOptions>? tweak = null)
    {
        var options = new PackerOptions(Token, Roots, Outbox, Published) { TempRoot = TempRoot };
        return tweak?.Invoke(options) ?? options;
    }

    /// <summary>The default String category of a person: three images, one in a sub folder, each with two small canvases and one value that names the file.</summary>
    public void Populate(string person, string category = Category, string[]? relatives = null)
    {
        foreach (var rel in relatives ?? new[] { "Pet.img", "Eqp.img", "Sub/Deep.img" })
            WriteImg(person, category + "/" + rel, rel);
        ImgFixture.WriteManifest(Root(person), "GMS");
    }

    /// <summary>Writes the published <c>&lt;category&gt;.wz</c> (version 83, key GMS) with the default images or the ones given.</summary>
    public string BuildPublished(string category = Category, IEnumerable<string>? images = null, Func<string, Action<WzImage>>? content = null)
        => SyntheticWz.Build(PublishedWz(category), images, content);

    public string WriteImg(string person, string relativeToImg, string marker, Action<WzImage>? extra = null)
        => ImgFixture.Write(Root(person), relativeToImg, WzMapleVersion.BMS, img =>
        {
            img.AddProperty(new WzStringProperty("who", marker));
            extra?.Invoke(img);
        });

    /// <summary>Every file and folder below <paramref name="root"/> with its size and hash, for "nothing changed" checks.</summary>
    public static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, entry);
            map[rel] = Directory.Exists(entry) ? "<dir>" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
        }
        return map;
    }

    /// <summary>Snapshot of the named source roots together, keys prefixed with the source name.</summary>
    public Dictionary<string, string> SnapshotRoots(params string[] sources)
        => sources.SelectMany(s => Snapshot(Root(s)).Select(kv => (Key: s + "/" + kv.Key, kv.Value))).ToDictionary(x => x.Key, x => x.Value);

    public void Dispose() { try { Directory.Delete(Temp, true); } catch { } }
}

/// <summary>A started packer host with a client (token set), the log lines it wrote and helpers for the routes.</summary>
internal sealed class PackerTestServer : IAsyncDisposable
{
    public WebApplication App { get; private init; } = null!;
    public HttpClient Client { get; private init; } = null!;
    public ConcurrentQueue<string> Logs { get; private init; } = null!;
    public PackerOptions Options { get; private init; } = null!;
    public PackerService Service => App.Services.GetRequiredService<PackerService>();
    public PackerWorkGate Gate => App.Services.GetRequiredService<PackerWorkGate>();

    public static async Task<PackerTestServer> StartAsync(PackerOptions options)
    {
        var logs = new ConcurrentQueue<string>();
        var builder = PackerHost.CreateBuilder(Array.Empty<string>(), options, "http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Capture(logs));
        var app = builder.Build();
        PackerHost.Configure(app, options);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(PackerOptions.TokenHeader, options.Token);
        return new PackerTestServer { App = app, Client = client, Logs = logs, Options = options };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }

    public Task<HttpResponseMessage> PostJson(string url, object body)
        => Client.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    public Task<HttpResponseMessage> PostRaw(string url, string body, string contentType = "application/json")
        => Client.PostAsync(url, new StringContent(body, Encoding.UTF8, contentType));

    /// <summary>One file of a pack request.</summary>
    public static object Set(string img, string from = "owner") => new { img, from, op = "set" };
    public static object Remove(string img, string from = "owner") => new { img, from, op = "remove" };

    public static object Pack(IEnumerable<object> files, string category = PackerFixture.Category) => new { category, files = files.ToArray(), wzVersion = 83, key = "GMS" };

    /// <summary>POST /v1/pack and return the job id (the call must answer 202).</summary>
    public async Task<string> StartPack(params object[] files) => await StartPackIn(PackerFixture.Category, files);

    public async Task<string> StartPackIn(string category, params object[] files)
    {
        var response = await PostJson("/v1/pack", Pack(files, category));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("jobId").GetString()!;
    }

    /// <summary>Polls the job until it is done or failed.</summary>
    public async Task<JsonElement> WaitForJob(string jobId, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var response = await Client.GetAsync("/v1/jobs/" + jobId);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            var job = JsonDocument.Parse(text).RootElement.Clone();
            var state = job.GetProperty("state").GetString();
            if (state is "done" or "failed") return job;
            Assert.True(DateTime.UtcNow < deadline, "the job did not finish: " + text);
            await Task.Delay(50);
        }
    }

    /// <summary>Pack and wait; returns the finished job.</summary>
    public async Task<JsonElement> PackAndWait(params object[] files)
        => await WaitForJob(await StartPack(files));

    public async Task<(HttpStatusCode Status, JsonElement Body)> Verify(string jobId)
    {
        var response = await PostJson("/v1/verify", new { jobId });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    public static string ErrorCode(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

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
