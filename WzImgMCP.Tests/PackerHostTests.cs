#if LINUX_BUILD
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WzImgMCP.Packer;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>The packer over real HTTP: token, routes, methods, bodies, no MCP, no secrets in answers or logs.</summary>
[Collection("AllowedRootEnv")]
public sealed class PackerHostTests : IAsyncLifetime
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly PackerFixture _fx = new();
    private PackerTestServer _server = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);
        _fx.BuildPublished();
        _fx.Populate("owner");
        _server = await PackerTestServer.StartAsync(_fx.Options());
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        _fx.Dispose();
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token = null, bool noToken = false, string? body = null, string contentType = "application/json")
    {
        using var plain = new HttpClient { BaseAddress = _server.Client.BaseAddress };
        var request = new HttpRequestMessage(method, url);
        if (!noToken) request.Headers.TryAddWithoutValidation(PackerOptions.TokenHeader, token ?? PackerFixture.Token);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await plain.SendAsync(request);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, text);
        using var doc = JsonDocument.Parse(text);
        Assert.True(code == doc.RootElement.GetProperty("error").GetProperty("code").GetString(), text);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetProperty("message").GetString()));
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
    }

    // ---- token

    public static IEnumerable<object[]> AllRoutes => new[]
    {
        new object[] { "GET", "/v1/health" },
        new object[] { "GET", "/v1/jobs/" + new string('a', 32) },
        new object[] { "POST", "/v1/pack" },
        new object[] { "POST", "/v1/verify" },
        new object[] { "GET", "/v1/nothing-here" },
        new object[] { "DELETE", "/v1/pack" },
    };

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task NoToken_OnAnyRoute_Is401(string method, string url)
        => await AssertError(await Send(new HttpMethod(method), url, noToken: true), HttpStatusCode.Unauthorized, "unauthorized");

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task WrongToken_OnAnyRoute_Is401(string method, string url)
        => await AssertError(await Send(new HttpMethod(method), url, token: PackerFixture.Token + "x"), HttpStatusCode.Unauthorized, "unauthorized");

    [Fact]
    public async Task ViewerTokenHeader_IsNotAccepted()
    {
        using var plain = new HttpClient { BaseAddress = _server.Client.BaseAddress };
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        request.Headers.TryAddWithoutValidation("X-Viewer-Token", PackerFixture.Token);
        await AssertError(await plain.SendAsync(request), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task TheTokenTwice_Is401()
    {
        using var plain = new HttpClient { BaseAddress = _server.Client.BaseAddress };
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        request.Headers.TryAddWithoutValidation(PackerOptions.TokenHeader, new[] { PackerFixture.Token, PackerFixture.Token });
        await AssertError(await plain.SendAsync(request), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task ATokenOfTheSameLength_ThatDiffersInOneCharacter_Is401()
    {
        var wrong = PackerFixture.Token[..^1] + (PackerFixture.Token[^1] == 'f' ? 'e' : 'f');
        Assert.Equal(PackerFixture.Token.Length, wrong.Length);
        await AssertError(await Send(HttpMethod.Get, "/v1/health", token: wrong), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task UnauthorizedAnswer_DoesNotRevealWhichRoutesExist()
    {
        var real = await (await Send(HttpMethod.Get, "/v1/health", noToken: true)).Content.ReadAsStringAsync();
        var fake = await (await Send(HttpMethod.Get, "/v1/not-a-route", noToken: true)).Content.ReadAsStringAsync();
        Assert.Equal(real, fake);
    }

    // ---- routes and methods

    [Fact]
    public async Task OnlyTheFiveExpectedRoutesExist()
    {
        var routes = _server.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => ((RouteEndpoint)e).RoutePattern.RawText)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "/v1/health", "/v1/jobs/{id}", "/v1/pack", "/v1/published", "/v1/verify" }, routes);
    }

    [Fact]
    public void EveryRoute_HasExactlyTheMethodsItNeeds()
    {
        var expected = new Dictionary<string, string> { ["/v1/health"] = "GET", ["/v1/jobs/{id}"] = "GET", ["/v1/pack"] = "POST", ["/v1/published"] = "GET", ["/v1/verify"] = "POST" };
        foreach (var endpoint in _server.App.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            var route = Assert.IsType<RouteEndpoint>(endpoint);
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>();
            Assert.True(methods != null, $"route {route.RoutePattern.RawText} has no HTTP method restriction");
            Assert.Equal(new[] { expected[route.RoutePattern.RawText!] }, methods!.HttpMethods);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/sse")]
    [InlineData("/message")]
    [InlineData("/mcp")]
    [InlineData("/v1/resolve")]
    [InlineData("/v1/diff")]
    [InlineData("/v1/canvas")]
    public async Task NoMcpAndNoViewerRoute_GetIsAPlain404(string url)
        => await AssertError(await Send(HttpMethod.Get, url), HttpStatusCode.NotFound, "not_found");

    [Theory]
    [InlineData("POST", "/v1/health", "GET")]
    [InlineData("DELETE", "/v1/health", "GET")]
    [InlineData("PUT", "/v1/pack", "POST")]
    [InlineData("GET", "/v1/pack", "POST")]
    [InlineData("GET", "/v1/verify", "POST")]
    [InlineData("POST", "/v1/jobs/00000000000000000000000000000000", "GET")]
    [InlineData("HEAD", "/v1/health", "GET")]
    public async Task WrongMethod_Is405_WithAnAllowHeader(string method, string url, string allow)
    {
        var response = await Send(new HttpMethod(method), url);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(allow, response.Content.Headers.Allow.Single());
        if (method != "HEAD")
            await AssertError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
    }

    [Fact]
    public async Task AGetWithABody_Is400()
        => await AssertError(await Send(HttpMethod.Get, "/v1/health", body: "{}"), HttpStatusCode.BadRequest, "bad_request");

    [Fact]
    public void ServiceGraph_HasNoMcpAndNoToolClasses()
    {
        var builder = PackerHost.CreateBuilder(Array.Empty<string>(), _fx.Options(), "http://127.0.0.1:0");
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

    // ---- health

    [Fact]
    public async Task Health_ReportsSourcesOutboxAndPublishedFolder_WithoutAnyPath()
    {
        var response = await Send(HttpMethod.Get, "/v1/health");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("sources").GetProperty("owner").GetBoolean());
        Assert.True(root.GetProperty("outbox").GetBoolean());
        Assert.True(root.GetProperty("published").GetBoolean());
        Assert.False(root.GetProperty("busy").GetBoolean());
        Assert.DoesNotContain(_fx.Temp, text);
        Assert.Null(response.Headers.Server.FirstOrDefault());
    }

    // ---- bodies

    private const string OneFile = """[{"img":"String/Pet.img","from":"owner","op":"set"}]""";

    private static string PackJson(string category = "\"String\"", string files = OneFile, string version = "83", string key = "\"GMS\"", string extra = "")
        => $$"""{"category":{{category}},"files":{{files}},"wzVersion":{{version}},"key":{{key}}{{extra}}}""";

    [Fact]
    public async Task PackBody_Problems_AreTypedErrors()
    {
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: ""), HttpStatusCode.BadRequest, "bad_request");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: "not json"), HttpStatusCode.BadRequest, "bad_request");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: "null"), HttpStatusCode.BadRequest, "bad_request");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: "[1,2]"), HttpStatusCode.BadRequest, "bad_request");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: PackJson(extra: ",\"extra\":1")), HttpStatusCode.BadRequest, "bad_request");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: PackJson(extra: ",\"person\":\"owner\"")), HttpStatusCode.BadRequest, "bad_request");   // the phase A field is gone
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: PackJson().Replace("\"category\"", "\"Category\"")), HttpStatusCode.BadRequest, "bad_request");   // names are case sensitive
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: PackJson(version: "\"83\"")), HttpStatusCode.BadRequest, "bad_request");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: PackJson(), contentType: "text/plain"), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: "{\"pad\":\"" + new string('x', 4 * 1024 * 1024 + 600) + "\"}"), HttpStatusCode.RequestEntityTooLarge, "too_large");
    }

    [Theory]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83}""", "bad_request")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":64,"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"BMS"}""", "bad_request")]
    [InlineData("""{"files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"../friend","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"a/b","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"..","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":".hidden","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"/etc","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"Strïng","files":[{"img":"String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"Backups","files":[{"img":"Backups/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_category")]
    [InlineData("""{"category":"String","wzVersion":83,"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[],"wzVersion":83,"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[null],"wzVersion":83,"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"stranger","op":"set"}],"wzVersion":83,"key":"GMS"}""", "unknown_source")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"canon","op":"set"}],"wzVersion":83,"key":"GMS"}""", "unknown_source")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","op":"set"}],"wzVersion":83,"key":"GMS"}""", "unknown_source")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner","op":"merge"}],"wzVersion":83,"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner"}],"wzVersion":83,"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[{"from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner","op":"set","x":1}],"wzVersion":83,"key":"GMS"}""", "bad_request")]
    [InlineData("""{"category":"String","files":[{"img":"Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"Item/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String/../Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"/String/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String\\Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.txt","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String/.hidden/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String/Backups/Pet.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pët.img","from":"owner","op":"set"}],"wzVersion":83,"key":"GMS"}""", "bad_path")]
    [InlineData("""{"category":"String","files":[{"img":"String/Pet.img","from":"owner","op":"set"},{"img":"String/Pet.img","from":"friend","op":"remove"}],"wzVersion":83,"key":"GMS"}""", "bad_request")]
    public async Task PackRequest_WithABadField_Is400_AndCreatesNothing(string body, string code)
    {
        var before = PackerFixture.Snapshot(_fx.Temp);
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: body), HttpStatusCode.BadRequest, code);
        Assert.Equal(before, PackerFixture.Snapshot(_fx.Temp));   // no job folder, nothing anywhere
    }

    [Fact]
    public async Task APackThatListsTooManyFiles_Is400_AndCreatesNothing()
    {
        var capped = await PackerTestServer.StartAsync(_fx.Options(o => new PackerOptions(PackerFixture.Token, _fx.Roots, _fx.Outbox, _fx.Published) { TempRoot = _fx.TempRoot, MaxFiles = 2 }));
        await using var _ = capped;
        var files = string.Join(',', Enumerable.Range(0, 3).Select(i => $$"""{"img":"String/F{{i}}.img","from":"owner","op":"remove"}"""));
        var before = PackerFixture.Snapshot(_fx.Temp);
        var response = await capped.PostRaw("/v1/pack", PackJson(files: "[" + files + "]"));
        await AssertError(response, HttpStatusCode.BadRequest, "too_many_files");
        Assert.Equal(before, PackerFixture.Snapshot(_fx.Temp));
    }

    [Theory]
    [InlineData("NoSuchCategory", 404, "not_found")]
    public async Task APackOfACategoryWithNoPublishedFile_Is404(string category, int status, string code)
    {
        var body = PackJson(category: $"\"{category}\"", files: $$"""[{"img":"{{category}}/Pet.img","from":"owner","op":"remove"}]""");
        await AssertError(await Send(HttpMethod.Post, "/v1/pack", body: body), (HttpStatusCode)status, code);
    }

    [Fact]
    public async Task VerifyBody_Problems_AreTypedErrors()
    {
        await AssertError(await Send(HttpMethod.Post, "/v1/verify", body: "{}"), HttpStatusCode.NotFound, "not_found");
        await AssertError(await Send(HttpMethod.Post, "/v1/verify", body: """{"jobId":"x"}"""), HttpStatusCode.NotFound, "not_found");
        await AssertError(await Send(HttpMethod.Post, "/v1/verify", body: """{"jobId":"../../x"}"""), HttpStatusCode.NotFound, "not_found");
        await AssertError(await Send(HttpMethod.Post, "/v1/verify", body: $$"""{"jobId":"{{new string('a', 32)}}","extra":1}"""), HttpStatusCode.BadRequest, "bad_request");
        await AssertError(await Send(HttpMethod.Post, "/v1/verify", body: $$"""{"jobId":"{{new string('a', 32)}}","person":"owner"}"""), HttpStatusCode.BadRequest, "bad_request");   // a verify is about the pack, not a person
        await AssertError(await Send(HttpMethod.Post, "/v1/verify", body: "{\"pad\":\"" + new string('x', 5000) + "\"}"), HttpStatusCode.RequestEntityTooLarge, "too_large");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("zz")]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public async Task UnknownOrMalformedJobId_Is404(string id)
        => await AssertError(await Send(HttpMethod.Get, "/v1/jobs/" + id), HttpStatusCode.NotFound, "not_found");

    // ---- no secrets in answers or logs

    [Fact]
    public async Task AnswersAndLogs_NeverHoldTheTokenOrAPath()
    {
        _fx.WriteImg("owner", "String/Pet.img", "x");
        var job = await _server.PackAndWait(PackerTestServer.Set("String/Pet.img"));
        var id = job.GetProperty("jobId").GetString()!;
        var answers = new List<string>
        {
            job.ToString(),
            (await _server.Verify(id)).Body.ToString(),
            await (await _server.Client.GetAsync("/v1/health")).Content.ReadAsStringAsync(),
            await (await _server.PostJson("/v1/pack", PackerTestServer.Pack(new[] { PackerTestServer.Remove("NoSuchCategory/Pet.img") }, "NoSuchCategory"))).Content.ReadAsStringAsync(),
            await (await _server.Client.GetAsync("/v1/nothing")).Content.ReadAsStringAsync(),
        };
        // a bad token must not be echoed either
        answers.Add(await (await Send(HttpMethod.Get, "/v1/health", token: "wrong-token-that-is-long-enough-0123456789")).Content.ReadAsStringAsync());

        foreach (var text in answers)
        {
            Assert.DoesNotContain(PackerFixture.Token, text);
            Assert.DoesNotContain(_fx.Temp, text);
            Assert.DoesNotContain("wrong-token-that-is-long-enough", text);
        }
        Assert.NotEmpty(_server.Logs);
        foreach (var line in _server.Logs)
        {
            Assert.DoesNotContain(PackerFixture.Token, line);
            Assert.DoesNotContain(_fx.Temp, line);
            Assert.DoesNotContain("wrong-token-that-is-long-enough", line);
        }
    }

    [Fact]
    public async Task EveryRequestIsLogged_WithMethodRouteAndStatusOnly()
    {
        await _server.Client.GetAsync("/v1/jobs/" + new string('a', 32) + "?secret=1");
        var line = Assert.Single(_server.Logs, l => l.Contains("/v1/jobs/{id}"));
        Assert.Contains("GET", line);
        Assert.Contains("404", line);
        Assert.DoesNotContain("secret", line);
        Assert.DoesNotContain(new string('a', 32), line);
    }
}
#endif
