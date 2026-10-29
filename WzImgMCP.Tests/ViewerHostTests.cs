using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;

namespace WzImgMCP.Tests;

/// <summary>
/// The viewer service over real HTTP: the host is started on an ephemeral loopback port and called with HttpClient.
/// It reads WZIMG_ALLOWED_ROOT through the resolver, so it runs in the same non-parallel collection as the path guard tests.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class ViewerHostTests : IAsyncLifetime
{
    private static readonly string Token = "tok-" + new string('k', 28) + "-0123456789abcdef";   // 48 characters

    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _tmp = Directory.CreateTempSubdirectory("viewer-host-").FullName;
    private readonly List<Server> _servers = new();

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        foreach (var name in ViewerOptions.SourceNames)
            Directory.CreateDirectory(Path.Combine(_tmp, name, "img", "String"));
        File.WriteAllText(Path.Combine(_tmp, "owner", "img", "String", "Consume.img"), "0123456789");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private ViewerOptions Options(TimeSpan? timeout = null) => new(Token, ViewerOptions.SourceNames.ToDictionary(n => n, n => Path.Combine(_tmp, n)))
    {
        RequestTimeout = timeout ?? ViewerOptions.DefaultRequestTimeout,
    };

    private async Task<Server> Start(ViewerOptions? options = null, ViewerPathResolver? resolver = null)
    {
        var server = await Server.StartAsync(options ?? Options(), resolver);
        _servers.Add(server);
        return server;
    }

    /// <summary>A started viewer host plus a client for it and everything it logged.</summary>
    private sealed class Server : IAsyncDisposable
    {
        public WebApplication App { get; private init; } = null!;
        public HttpClient Client { get; private init; } = null!;
        public ConcurrentQueue<string> Logs { get; private init; } = null!;

        public static async Task<Server> StartAsync(ViewerOptions options, ViewerPathResolver? resolver)
        {
            var logs = new ConcurrentQueue<string>();
            var builder = ViewerHost.CreateBuilder(Array.Empty<string>(), options, "http://127.0.0.1:0", resolver);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new CaptureProvider(logs));
            var app = builder.Build();
            ViewerHost.Configure(app, options);
            await app.StartAsync();

            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(60) };
            return new Server { App = app, Client = client, Logs = logs };
        }

        public async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token = null, bool noToken = false)
        {
            var request = new HttpRequestMessage(method, url);
            if (!noToken)
                request.Headers.TryAddWithoutValidation(ViewerOptions.TokenHeader, token ?? Token);
            return await Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Get(string url, string? token = null, bool noToken = false) => Send(HttpMethod.Get, url, token, noToken);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class CaptureProvider : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _sink;
        public CaptureProvider(ConcurrentQueue<string> sink) { _sink = sink; }
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Enqueue(formatter(state, exception) + (exception == null ? "" : " | " + exception));
    }

    private static async Task<(string Code, string Message)> ErrorOf(HttpResponseMessage response)
    {
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        return (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await ErrorOf(response)).Code);
    }

    // ---- token

    [Fact]
    public async Task Token_Missing_Is401()
    {
        var s = await Start();
        await AssertError(await s.Get("/v1/health", noToken: true), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Token_Right_Is200()
    {
        var s = await Start();
        var response = await s.Get("/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Token, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("same-length")]
    [InlineData("shorter")]
    [InlineData("longer")]
    [InlineData("prefix")]
    [InlineData("empty")]
    [InlineData("upper-case")]
    public async Task Token_Wrong_Is401(string kind)
    {
        var s = await Start();
        var wrong = kind switch
        {
            "same-length" => new string('x', Token.Length),
            "shorter" => "short",
            "longer" => Token + Token,
            "prefix" => Token.Substring(0, Token.Length - 1),
            "upper-case" => Token.ToUpperInvariant(),
            _ => "",
        };
        Assert.NotEqual(Token, wrong);
        var response = await s.Get("/v1/health", wrong);
        await AssertError(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Token_RepeatedHeader_Is401()
    {
        var s = await Start();
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        request.Headers.TryAddWithoutValidation(ViewerOptions.TokenHeader, new[] { Token, Token });
        await AssertError(await s.Client.SendAsync(request), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Token_IsRequiredOnEveryRoute_AndBeforeAnythingElse()
    {
        var s = await Start();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
            foreach (var url in new[] { "/v1/health", "/v1/resolve?source=owner&path=String/Consume.img", "/nope", "/", "/sse" })
                await AssertError(await s.Send(method, url, noToken: true), HttpStatusCode.Unauthorized, "unauthorized");
    }

    // ---- startup refusal

    private static Func<string, string?> Env(string? token, Dictionary<string, string>? more = null)
        => name => name == ViewerOptions.TokenEnvVar ? token : more != null && more.TryGetValue(name, out var v) ? v : null;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789012345678901234567890")]   // 31 characters
    public async Task Startup_WithoutOrWithShortToken_ExitsWithErrorAndNoToken(string? token)
    {
        var previous = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        int exit;
        try
        {
            exit = await ViewerHost.RunAsync(Array.Empty<string>(), "127.0.0.1", 0, Env(token));
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.NotEqual(0, exit);
        var text = captured.ToString();
        Assert.Contains("WZ_VIEWER_TOKEN", text);
        if (!string.IsNullOrEmpty(token))
            Assert.DoesNotContain(token, text);
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123 456789")]     // inner space
    [InlineData("abcdefghijklmnopqrstuvwxyz0123\t456789")]    // inner tab
    [InlineData("abcdefghijklmnopqrstuvwxyz0123\n456789")]    // inner newline
    [InlineData("abcdefghijklmnopqrstuvwxyz0123\u0001456789")] // control character
    [InlineData("abcdefghijklmnopqrstuvwxyz0123\u00E9456789")] // non-ASCII
    [InlineData("abcdefghijklmnopqrstuvwxyz0123\u05D0456789")] // non-ASCII, Hebrew
    public async Task Startup_TokenWithForbiddenCharacter_ExitsWithErrorAndNoToken(string token)
    {
        Assert.Throws<ViewerConfigException>(() => ViewerOptions.FromEnvironment(Env(token)));

        var previous = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        int exit;
        try
        {
            exit = await ViewerHost.RunAsync(Array.Empty<string>(), "127.0.0.1", 0, Env(token));
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Equal(2, exit);
        Assert.Contains("WZ_VIEWER_TOKEN", captured.ToString());
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", captured.ToString());
    }

    [Fact]
    public void Options_Token_IsTrimmed_BeforeTheLengthCheck()
    {
        var core = "Zq7-" + new string('k', 28);   // 32 characters
        Assert.Equal(core, ViewerOptions.FromEnvironment(Env("  " + core + "\r\n")).Token);
        Assert.Throws<ViewerConfigException>(() => ViewerOptions.FromEnvironment(Env("  " + core.Substring(1) + "\n")));   // 31 after trimming
        Assert.Throws<ViewerConfigException>(() => ViewerOptions.FromEnvironment(Env("   \n")));
    }

    [Fact]
    public async Task Options_TrimmedToken_IsWhatTheHeaderMustCarry()
    {
        var core = "Zq7-" + new string('k', 28);
        var roots = ViewerOptions.SourceNames.ToDictionary(n => n, n => Path.Combine(_tmp, n));
        var options = ViewerOptions.FromEnvironment(name => name == ViewerOptions.TokenEnvVar ? core + "\n" : roots.GetValueOrDefault(name.Replace("WZ_VIEWER_ROOT_", "").ToLowerInvariant()));
        var s = await Start(options);
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/v1/health", core)).StatusCode);
    }

    [Fact]
    public void Options_WeakToken_GivesWarningWithoutTheToken()
    {
        var weak = ViewerOptions.FromEnvironment(Env(new string('a', 40)));
        Assert.NotNull(weak.Warning);
        Assert.Contains("WZ_VIEWER_TOKEN", weak.Warning);
        Assert.DoesNotContain("aaaa", weak.Warning);

        var sevenDistinct = ViewerOptions.FromEnvironment(Env("abcdefg" + new string('a', 30)));
        Assert.NotNull(sevenDistinct.Warning);

        var fine = ViewerOptions.FromEnvironment(Env("abcdefgh" + new string('a', 30)));   // 8 distinct characters
        Assert.Null(fine.Warning);
        Assert.Null(ViewerOptions.FromEnvironment(Env(Token)).Warning);
    }

    [Fact]
    public void Options_Token_Of32Characters_IsAccepted()
    {
        var options = ViewerOptions.FromEnvironment(Env(new string('a', 32)));
        Assert.Equal(32, options.Token.Length);
        Assert.Throws<ViewerConfigException>(() => ViewerOptions.FromEnvironment(Env(new string('a', 31))));
    }

    [Fact]
    public void Options_Roots_DefaultAndOverride()
    {
        var options = ViewerOptions.FromEnvironment(Env(new string('a', 40), new Dictionary<string, string>
        {
            ["WZ_VIEWER_ROOT_OWNER"] = "/somewhere/owner-tree",
            ["WZ_VIEWER_ROOT_FRIEND"] = "  ",
        }));
        Assert.Equal("/data/canon", options.Roots["canon"]);
        Assert.Equal("/somewhere/owner-tree", options.Roots["owner"]);
        Assert.Equal("/data/friend", options.Roots["friend"]);
    }

    // ---- health

    [Fact]
    public async Task Health_ReportsStatusVersionAndSources()
    {
        var s = await Start();
        var response = await s.Get("/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("version").GetString()));
        var sources = doc.RootElement.GetProperty("sources");
        foreach (var name in ViewerOptions.SourceNames)
            Assert.Equal(JsonValueKind.True, sources.GetProperty(name).ValueKind);
        Assert.Equal(3, sources.EnumerateObject().Count());
    }

    [Fact]
    public async Task Health_MissingOrNonDirectoryRoot_IsFalse()
    {
        Directory.Delete(Path.Combine(_tmp, "canon"), recursive: true);
        Directory.Delete(Path.Combine(_tmp, "friend"), recursive: true);
        File.WriteAllText(Path.Combine(_tmp, "friend"), "a file, not a directory");

        var s = await Start();
        using var doc = JsonDocument.Parse(await (await s.Get("/v1/health")).Content.ReadAsStringAsync());
        var sources = doc.RootElement.GetProperty("sources");
        Assert.False(sources.GetProperty("canon").GetBoolean());
        Assert.True(sources.GetProperty("owner").GetBoolean());
        Assert.False(sources.GetProperty("friend").GetBoolean());
    }

    [Fact]
    public async Task Health_RootWithoutImgFolder_IsFalse()
    {
        Directory.Delete(Path.Combine(_tmp, "canon", "img"), recursive: true);   // the root exists, what the resolver reads does not

        var s = await Start();
        using var doc = JsonDocument.Parse(await (await s.Get("/v1/health")).Content.ReadAsStringAsync());
        var sources = doc.RootElement.GetProperty("sources");
        Assert.False(sources.GetProperty("canon").GetBoolean());
        Assert.True(sources.GetProperty("owner").GetBoolean());
        Assert.True(sources.GetProperty("friend").GetBoolean());
    }

    // ---- /v1/resolve

    [Fact]
    public async Task Resolve_ExistingImage_ReturnsSize()
    {
        var s = await Start();
        var response = await s.Get("/v1/resolve?source=owner&path=String/Consume.img");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("owner", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("String/Consume.img", doc.RootElement.GetProperty("path").GetString());
        Assert.True(doc.RootElement.GetProperty("exists").GetBoolean());
        Assert.Equal(10, doc.RootElement.GetProperty("sizeBytes").GetInt64());
    }

    [Theory]
    [InlineData("/v1/resolve?source=owner&path=../String/Consume.img", 400, "bad_path")]
    [InlineData("/v1/resolve?source=owner&path=%2e%2e%2fString%2fConsume.img", 400, "bad_path")]   // decoded once by the server to ../
    [InlineData("/v1/resolve?source=owner&path=%252e%252e%252fString%252fConsume.img", 400, "bad_path")]   // double encoded stays '%'
    [InlineData("/v1/resolve?source=owner&path=String%5CConsume.img", 400, "bad_path")]
    [InlineData("/v1/resolve?source=owner&path=%2Fetc%2Fpasswd", 400, "bad_path")]
    [InlineData("/v1/resolve?source=owner&path=Backups/x.img", 400, "bad_path")]
    [InlineData("/v1/resolve?source=owner", 400, "bad_path")]
    [InlineData("/v1/resolve?source=owner&path=String/Consume.img&path=String/Consume.img", 400, "bad_path")]
    [InlineData("/v1/resolve?source=nobody&path=String/Consume.img", 400, "unknown_source")]
    [InlineData("/v1/resolve?path=String/Consume.img", 400, "unknown_source")]
    [InlineData("/v1/resolve?source=canon&path=String/Consume.img", 404, "not_found")]
    [InlineData("/v1/resolve?source=owner&path=String/Missing.img", 404, "not_found")]
    public async Task Resolve_Errors_AreTyped(string url, int status, string code)
    {
        var s = await Start();
        await AssertError(await s.Get(url), (HttpStatusCode)status, code);
    }

    // ---- methods, routes, bodies

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task NonGet_Is405_OnEveryRoute(string method)
    {
        var s = await Start();
        foreach (var url in new[] { "/v1/health", "/v1/resolve?source=owner&path=String/Consume.img", "/nope" })
        {
            var response = await s.Send(new HttpMethod(method), url);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Contains("GET", response.Content.Headers.Allow);
            if (method != "HEAD")
                Assert.Equal("method_not_allowed", (await ErrorOf(response)).Code);
        }
    }

    [Fact]
    public async Task UnknownRoute_Is404_Typed()
    {
        var s = await Start();
        foreach (var url in new[] { "/", "/nope", "/v1", "/v1/difff?person=owner", "/v2/health" })
            await AssertError(await s.Get(url), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task RequestBody_IsRefused()
    {
        var s = await Start();
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health") { Content = new StringContent("{\"a\":1}") };
        request.Headers.TryAddWithoutValidation(ViewerOptions.TokenHeader, Token);
        await AssertError(await s.Client.SendAsync(request), HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task EveryRegisteredRoute_AcceptsOnlyGet()
    {
        var s = await Start();
        var endpoints = s.App.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.NotEmpty(endpoints);

        var routes = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var route = Assert.IsType<RouteEndpoint>(endpoint);
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>();
            Assert.True(methods != null, $"route {route.RoutePattern.RawText} has no HTTP method restriction");
            Assert.Equal(new[] { "GET" }, methods!.HttpMethods);
            routes.Add(route.RoutePattern.RawText!);
        }

        Assert.Contains("/v1/health", routes);
        Assert.Contains("/v1/resolve", routes);
        Assert.All(routes, r => Assert.StartsWith("/v1/", r));
    }

    // ---- no MCP, no write path

    // A POST or DELETE gets 405 from the viewer's own method check whether or not a route exists, so such a request
    // proves nothing about MCP. What proves it: GET on the MCP paths is a plain 404 (a mapped MCP route would answer
    // otherwise), the endpoint list below holds only /v1/ routes, and the service graph has no MCP type.
    [Theory]
    [InlineData("/")]
    [InlineData("/sse")]
    [InlineData("/message")]
    public async Task NoMcpEndpoint_GetOnMcpPaths_Is404(string url)
    {
        var s = await Start();
        await AssertError(await s.Get(url), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task OnlyTheExpectedRoutesExist()
    {
        var s = await Start();
        var routes = s.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => ((RouteEndpoint)e).RoutePattern.RawText)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "/v1/canvas", "/v1/diff", "/v1/frames", "/v1/health", "/v1/inspect", "/v1/resolve" }, routes);
    }

    [Fact]
    public void ServiceGraph_HasNoMcpAndNoToolClasses()
    {
        var builder = ViewerHost.CreateBuilder(Array.Empty<string>(), Options(), "http://127.0.0.1:0", null);
        var mcpAssembly = typeof(ModelContextProtocol.Server.McpServerToolAttribute).Assembly;
        foreach (var d in builder.Services)
        {
            var types = new[] { d.ServiceType, d.ImplementationType, d.ImplementationInstance?.GetType() }.Where(t => t != null);
            foreach (var t in types)
            {
                Assert.False(t!.Namespace?.StartsWith("ModelContextProtocol") == true, $"MCP service registered: {t.FullName}");
                Assert.NotEqual(mcpAssembly, t.Assembly);
                Assert.False(t.Namespace?.StartsWith("WzImgMCP.Tools") == true || t.Namespace?.StartsWith("WzImgMCP.Server") == true,
                    $"editor service registered: {t.FullName}");
            }
        }
    }

    // ---- concurrency, timeout, errors, logging

    /// <summary>Resolver whose calls wait on a gate, so a test can hold requests in flight without sleeping.</summary>
    private sealed class GatedResolver : ViewerPathResolver
    {
        public readonly SemaphoreSlim Entered = new(0);
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GatedResolver(IReadOnlyDictionary<string, string> roots) : base(roots) { }

        public override async Task<ResolvedImage> ResolveAsync(string? source, string? path, CancellationToken cancellationToken)
        {
            Entered.Release();
            await Gate.Task;
            return await base.ResolveAsync(source, path, cancellationToken);
        }
    }

    [Fact]
    public async Task MoreConcurrentRequestsThanTheCap_GetBusy()
    {
        var options = Options();
        var resolver = new GatedResolver(options.Roots);
        var s = await Start(options, resolver);

        const string url = "/v1/resolve?source=owner&path=String/Consume.img";
        var inFlight = Enumerable.Range(0, options.MaxConcurrentRequests).Select(_ => s.Get(url)).ToList();
        for (int i = 0; i < options.MaxConcurrentRequests; i++)
            Assert.True(await resolver.Entered.WaitAsync(TimeSpan.FromSeconds(30)), "request did not arrive");

        var busy = await s.Get(url);
        await AssertError(busy, HttpStatusCode.ServiceUnavailable, "busy");
        Assert.True(busy.Headers.Contains("Retry-After"));

        resolver.Gate.SetResult();
        foreach (var task in inFlight)
            Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);

        // the slots are free again
        Assert.Equal(HttpStatusCode.OK, (await s.Get(url)).StatusCode);
    }

    [Fact]
    public async Task Health_AnswersWhileAllSlotsAreHeld_ButStillNeedsTheToken()
    {
        var options = Options();
        var resolver = new GatedResolver(options.Roots);
        var s = await Start(options, resolver);

        var inFlight = Enumerable.Range(0, options.MaxConcurrentRequests).Select(_ => s.Get("/v1/resolve?source=owner&path=String/Consume.img")).ToList();
        for (int i = 0; i < options.MaxConcurrentRequests; i++)
            Assert.True(await resolver.Entered.WaitAsync(TimeSpan.FromSeconds(30)), "request did not arrive");

        await AssertError(await s.Get("/v1/resolve?source=owner&path=String/Consume.img"), HttpStatusCode.ServiceUnavailable, "busy");
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/v1/health")).StatusCode);
        await AssertError(await s.Get("/v1/health", noToken: true), HttpStatusCode.Unauthorized, "unauthorized");

        resolver.Gate.SetResult();
        await Task.WhenAll(inFlight);
    }

    [Fact]
    public void Limiter_IsItsOwnServiceType_NotABareSemaphore()
    {
        var builder = ViewerHost.CreateBuilder(Array.Empty<string>(), Options(), "http://127.0.0.1:0", null);
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ViewerRequestLimiter));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(SemaphoreSlim));
    }

    [Fact]
    public async Task UnauthorizedRequests_DoNotTakeASlot()
    {
        var options = Options();
        var resolver = new GatedResolver(options.Roots);
        var s = await Start(options, resolver);

        var inFlight = Enumerable.Range(0, options.MaxConcurrentRequests).Select(_ => s.Get("/v1/resolve?source=owner&path=String/Consume.img")).ToList();
        for (int i = 0; i < 4; i++)
            Assert.True(await resolver.Entered.WaitAsync(TimeSpan.FromSeconds(30)));

        await AssertError(await s.Get("/v1/health", noToken: true), HttpStatusCode.Unauthorized, "unauthorized");
        resolver.Gate.SetResult();
        await Task.WhenAll(inFlight);
    }

    private sealed class ThrowingResolver : ViewerPathResolver
    {
        public ThrowingResolver(IReadOnlyDictionary<string, string> roots) : base(roots) { }

        public override Task<ResolvedImage> ResolveAsync(string? source, string? path, CancellationToken cancellationToken)
            => throw new InvalidOperationException("SECRET-DETAIL at /srv/private/place");
    }

    [Fact]
    public async Task UnhandledException_Is500_WithGenericBody_AndNothingLeaks()
    {
        var options = Options();
        var s = await Start(options, new ThrowingResolver(options.Roots));
        var response = await s.Get("/v1/resolve?source=owner&path=String/Consume.img");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var (code, message) = await ErrorOf(response);
        Assert.Equal("internal_error", code);
        Assert.DoesNotContain("SECRET-DETAIL", message);

        Assert.DoesNotContain("SECRET-DETAIL", string.Join("\n", s.Logs));
        Assert.Contains(s.Logs, l => l.Contains("GET /v1/resolve 500"));
    }

    private sealed class HangingResolver : ViewerPathResolver
    {
        public HangingResolver(IReadOnlyDictionary<string, string> roots) : base(roots) { }

        public override async Task<ResolvedImage> ResolveAsync(string? source, string? path, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task SlowRequest_IsCutOff_With504()
    {
        var options = Options(TimeSpan.FromMilliseconds(200));
        var s = await Start(options, new HangingResolver(options.Roots));
        await AssertError(await s.Get("/v1/resolve?source=owner&path=String/Consume.img"), HttpStatusCode.GatewayTimeout, "timeout");
        // the slot was released
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/v1/health")).StatusCode);
    }

    [Fact]
    public async Task Logs_HaveMethodRouteStatusAndTime_ButNeverQueryHeadersOrToken()
    {
        var s = await Start();
        await s.Get("/v1/resolve?source=owner&path=SECRETPATHVALUE/x.img");
        await s.Get("/v1/resolve?source=owner&path=String/Consume.img");
        await s.Get("/v1/health", "wrong-token-VALUE-123");
        await s.Get("/some/unknown/URL-PIECE");

        var logs = s.Logs.ToArray();
        Assert.Contains(logs, l => l.StartsWith("GET /v1/resolve 404 "));
        Assert.Contains(logs, l => l.StartsWith("GET /v1/resolve 200 "));
        Assert.Contains(logs, l => l.StartsWith("GET /v1/health 401 "));
        Assert.Contains(logs, l => l.StartsWith("GET (no route) 404 "));
        Assert.Contains(logs, l => l.Contains(" ms"));

        var all = string.Join("\n", logs);
        foreach (var secret in new[] { "SECRETPATHVALUE", "String/Consume.img", "source=owner", "wrong-token-VALUE-123", Token, "URL-PIECE", ViewerOptions.TokenHeader })
            Assert.DoesNotContain(secret, all);
    }
}
