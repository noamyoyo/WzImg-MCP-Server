using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;

namespace WzImgMCP.Packer;

/// <summary>
/// The packer service (<c>WzImgMCP.dll --packer</c>): a plain ASP.NET minimal API, modelled on <see cref="ViewerHost"/>. It never
/// registers the MCP server, a tool class or <c>WzSessionManager</c>. Every request needs the <c>X-Packer-Token</c> header
/// (checked first, in constant time, so an unauthenticated caller learns nothing about the routes). Routes:
/// <c>GET /v1/health</c>, <c>POST /v1/pack</c>, <c>GET /v1/jobs/{id}</c>, <c>POST /v1/verify</c>. Errors are
/// <c>{ "error": { "code", "message" } }</c>; no path and no secret is ever in an answer or a log line.
/// </summary>
public static class PackerHost
{
    private const int MaxBodyBytes = 4096;
    private const int MaxPackBodyBytes = 4 * 1024 * 1024;   // a pack lists up to PackerOptions.MaxFiles files

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Case sensitive, and a field the contract does not name is an error, so a typo is not silently ignored.
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private sealed record PackRequest(string? Category, List<PackRequestFile?>? Files, int? WzVersion, string? Key);
    private sealed record VerifyRequest(string? JobId);

    /// <summary>
    /// Entry point of the mode. Returns the process exit code: 0 after a normal stop, 2 when the configuration is invalid
    /// (the reason goes to standard error, never a path or the token).
    /// </summary>
    public static async Task<int> RunAsync(string[] args, string host, int port, Func<string, string?> getEnv)
    {
        PackerOptions options;
        try
        {
            options = PackerOptions.FromEnvironment(getEnv);
            options.CheckOutbox();
            options.CheckPublished();
        }
        catch (PackerConfigException ex)
        {
            Console.Error.WriteLine("packer: " + ex.Message);
            return 2;
        }

        if (options.Warning != null)
            Console.Error.WriteLine("packer: warning: " + options.Warning);

        // The packer's confinement is its own configuration (read-only roots, one outbox, a temp folder). The image's default
        // WZIMG_ALLOWED_ROOT=/data would refuse the outbox and the temp folder, so it is not used in this mode.
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, null);

        var app = Build(args, options, $"http://{HostUrl.ForUrl(host)}:{port}");
        app.Services.GetRequiredService<PackerService>().CleanStale();   // what a process that died left behind
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WzImgMCP.Packer");
        logger.LogInformation("WZ packer starting on http://{Host}:{Port}", host, port);
        foreach (var name in PackerOptions.SourceNames)
            logger.LogInformation("Source {Source}: {Readable}", name, ViewerPathResolver.IsReadableDirectory(Path.Combine(options.Roots[name], "img")) ? "readable" : "not readable");

        logger.LogInformation("Published folder: {Readable}", options.PublishedDir != null && ViewerPathResolver.IsReadableDirectory(options.PublishedDir) ? "readable" : "not readable");

        await app.RunAsync();
        return 0;
    }

    public static WebApplication Build(string[] args, PackerOptions options, string url, PosixViewerFileSystem? fileSystem = null)
    {
        var builder = CreateBuilder(args, options, url, fileSystem);
        var app = builder.Build();
        Configure(app, options);
        return app;
    }

    /// <summary>Service registration only. Public so a test can list every registered service.</summary>
    public static WebApplicationBuilder CreateBuilder(string[] args, PackerOptions options, string url, PosixViewerFileSystem? fileSystem = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        // The framework's own request log lines contain the full URL; this service logs its own line.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);

        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxPackBodyBytes + 1024;   // the handlers refuse anything over their own limit with a typed error
        });

        builder.Services.AddSingleton(options);
        var files = fileSystem ?? new PosixViewerFileSystem();
        builder.Services.AddSingleton(files);
        builder.Services.AddSingleton(new PackerWorkGate());
        builder.Services.AddSingleton(sp => new PackerJobStore(options, sp.GetRequiredService<PosixViewerFileSystem>()));
        builder.Services.AddSingleton(sp => new PackerService(options, sp.GetRequiredService<PosixViewerFileSystem>(), sp.GetRequiredService<PackerWorkGate>(),
            sp.GetRequiredService<PackerJobStore>(), sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton(new ViewerRequestLimiter(options.MaxConcurrentRequests));
        return builder;
    }

    public static void Configure(WebApplication app, PackerOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WzImgMCP.Packer");
        var limiter = app.Services.GetRequiredService<ViewerRequestLimiter>();
        var tokenDigest = SHA256.HashData(Encoding.UTF8.GetBytes(options.Token));

        // Outermost: one log line per request (method, route template, status, time) and a generic 500 for anything unhandled.
        // Neither the query string, a header, a body nor the exception text is written anywhere.
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    await WriteError(context, 500, "internal_error", "internal error");
                }
                logger.LogError("Unhandled exception of type {ExceptionType}", ex.GetType().Name);
            }
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
            logger.LogInformation("{Method} {Route} {Status} {ElapsedMs} ms",
                context.Request.Method, route, context.Response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        });

        // Token first, then route and method, so an unauthenticated caller learns nothing about the routes.
        app.Use(async (context, next) =>
        {
            if (!TokenMatches(tokenDigest, context.Request.Headers[PackerOptions.TokenHeader]))
            {
                await WriteError(context, 401, "unauthorized", $"missing or wrong {PackerOptions.TokenHeader} header");
                return;
            }
            var endpoint = context.GetEndpoint();
            if (endpoint == null)
            {
                await WriteError(context, 404, "not_found", "no such route");
                return;
            }
            // A known path with another method: the router substitutes an endpoint called "405 HTTP Method Not Supported".
            if (endpoint.DisplayName?.StartsWith("405", StringComparison.Ordinal) == true)
            {
                context.Response.Headers.Allow = AllowFor(context.Request.Path);
                await WriteError(context, 405, "method_not_allowed", "method not allowed for this route");
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method) && (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            {
                await WriteError(context, 400, "bad_request", "this request takes no body");
                return;
            }
            await next();
        });

        // Concurrency cap and request timeout. Verify may run as long as the time budget; the rest is quick.
        app.Use(async (context, next) =>
        {
            ViewerRequestSlot? slot = null;
            if (context.GetEndpoint()?.Metadata.GetMetadata<NoRequestSlot>() == null)
            {
                slot = limiter.TryEnter();
                if (slot == null)
                {
                    context.Response.Headers.RetryAfter = "1";
                    await WriteError(context, 503, "busy", "too many requests in progress; retry shortly");
                    return;
                }
                context.Items[RequestSlotKey] = slot;
            }
            var clientAborted = context.RequestAborted;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(clientAborted);
            timeout.CancelAfter(context.GetEndpoint()?.Metadata.GetMetadata<LongRunning>() != null ? options.TimeBudget + TimeSpan.FromSeconds(5) : options.RequestTimeout);
            context.RequestAborted = timeout.Token;
            try
            {
                await next();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !clientAborted.IsCancellationRequested && !context.Response.HasStarted)
            {
                context.Response.Clear();
                await WriteError(context, 504, "timeout", "the request took too long");
            }
            catch (OperationCanceledException) when (clientAborted.IsCancellationRequested)
            {
                // the client went away; nothing to answer
            }
            finally
            {
                context.RequestAborted = clientAborted;
                slot?.ReleaseUnlessHeld();   // stays taken while uncancellable work of this request is still running
            }
        });

        app.MapGet("/v1/health", (PackerOptions o, PackerWorkGate gate) => Results.Json(new
        {
            status = "ok",
            version = typeof(PackerHost).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            sources = PackerOptions.SourceNames.ToDictionary(n => n, n => ViewerPathResolver.IsReadableDirectory(Path.Combine(o.Roots[n], "img"))),
            outbox = Directory.Exists(o.Outbox),
            published = o.PublishedDir != null && ViewerPathResolver.IsReadableDirectory(o.PublishedDir),
            busy = gate.Busy,
        }, Json)).WithMetadata(new NoRequestSlot());

        app.MapPost("/v1/pack", async (HttpContext context, PackerService service, PackerOptions o) =>
        {
            var request = await ReadBody<PackRequest>(context, MaxPackBodyBytes);
            var category = PackerTree.RequireCategory(request.Category);
            if (request.WzVersion != PackerService.WzVersion)
                throw new ViewerApiException(400, "bad_request", $"wzVersion must be {PackerService.WzVersion}");
            if (request.Key != PackerService.Key)
                throw new ViewerApiException(400, "bad_request", $"key must be {PackerService.Key}");
            var files = PackerFiles.Validate(category, request.Files, o.MaxFiles);
            var job = service.StartPack(category, files);
            return Results.Json(new { jobId = job.JobId }, Json, statusCode: StatusCodes.Status202Accepted);
        }).AddEndpointFilter(TypedErrors);

        // The sha256 of the packer's read-only copy of the published file: the publisher compares it with the manifest and its client folder.
        app.MapGet("/v1/published", async (HttpContext context, PackerService service) =>
        {
            var category = PackerTree.RequireCategory(context.Request.Query["category"].Count == 1 ? context.Request.Query["category"][0] : null);
            var ct = context.RequestAborted;
            var (sha, size) = await Task.Run(() => service.PublishedInfo(category, ct), ct);
            return Results.Json(new { category, wz = category + ".wz", sha256 = sha, sizeBytes = size }, Json);
        }).WithMetadata(new LongRunning()).AddEndpointFilter(TypedErrors);

        app.MapGet("/v1/jobs/{id}", (string id, PackerJobStore jobs) =>
        {
            var job = jobs.Find(id) ?? throw new ViewerApiException(404, "not_found", "no such job");
            return Results.Json(job, Json);
        }).AddEndpointFilter(TypedErrors);

        app.MapPost("/v1/verify", async (HttpContext context, PackerService service, PackerWorkGate gate) =>
        {
            var request = await ReadBody<VerifyRequest>(context, MaxBodyBytes);
            if (!PackerJobStore.IsJobId(request.JobId))
                throw new ViewerApiException(404, "not_found", "no such job");
            var ct = context.RequestAborted;

            var lease = gate.TryEnter() ?? throw new ViewerApiException(503, "busy", "a pack or verify is already running; retry shortly");
            Task<PackerVerifyResult> task;
            try
            {
                // The library cannot cancel a parse, so the work runs on its own thread, and the slot and the gate stay taken until it has really ended.
                task = Task.Run(() =>
                {
                    try { return service.Verify(request.JobId!, ct); }
                    finally { lease.Dispose(); }
                }, CancellationToken.None);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            (context.Items[RequestSlotKey] as ViewerRequestSlot)?.HoldUntil(task);
            var result = await task.WaitAsync(ct);
            return Results.Json(result, Json);
        }).WithMetadata(new LongRunning()).AddEndpointFilter(TypedErrors);
    }

    private static string AllowFor(PathString path)
        => path.Value is "/v1/pack" or "/v1/verify" ? "POST" : "GET";

    /// <summary>Reads and checks a JSON body: application/json, at most <paramref name="maxBytes"/>, no unknown field.</summary>
    private static async Task<T> ReadBody<T>(HttpContext context, int maxBytes) where T : class
    {
        var request = context.Request;
        if (request.ContentLength is { } declared && declared > maxBytes)
            throw new ViewerApiException(413, "too_large", "the request body is too large");
        if (request.ContentType == null || !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new ViewerApiException(415, "unsupported_media_type", "send application/json");

        using var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int n = await request.Body.ReadAsync(chunk.AsMemory(), context.RequestAborted);
            if (n == 0) break;
            total += n;
            if (total > maxBytes)
                throw new ViewerApiException(413, "too_large", "the request body is too large");
            body.Write(chunk, 0, n);
        }
        var buffer = body.GetBuffer();
        if (total == 0)
            throw new ViewerApiException(400, "bad_request", "a JSON body is required");
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, total), BodyJson) ?? throw new ViewerApiException(400, "bad_request", "a JSON object is required");
        }
        catch (JsonException)
        {
            throw new ViewerApiException(400, "bad_request", "the body is not the JSON this route takes");
        }
    }

    /// <summary>Turns a <see cref="ViewerApiException"/> from a handler into its typed JSON answer.</summary>
    private static async ValueTask<object?> TypedErrors(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        try
        {
            return await next(invocation);
        }
        catch (ViewerApiException ex)
        {
            return Results.Json(new { error = new { code = ex.Code, message = ex.Message } }, Json, statusCode: ex.Status);
        }
    }

    /// <summary>Endpoint marker: the request does not count against the concurrency cap (the token is still required).</summary>
    private sealed class NoRequestSlot { }

    /// <summary>Endpoint marker: the request may run as long as the time budget.</summary>
    private sealed class LongRunning { }

    internal static readonly object RequestSlotKey = new();

    /// <summary>Constant time comparison. Both values are hashed first, so the compared arrays always have the same length.</summary>
    private static bool TokenMatches(byte[] expectedDigest, Microsoft.Extensions.Primitives.StringValues supplied)
    {
        var value = supplied.Count == 1 ? supplied[0] ?? "" : "";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return CryptographicOperations.FixedTimeEquals(expectedDigest, digest) & (supplied.Count == 1);
    }

    private static Task WriteError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message } }, Json));
    }
}
