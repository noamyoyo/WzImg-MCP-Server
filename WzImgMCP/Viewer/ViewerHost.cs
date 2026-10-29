using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WzImgMCP.Utils;

namespace WzImgMCP.Viewer;

/// <summary>
/// The read-only viewer service (<c>WzImgMCP.dll --viewer</c>): a plain ASP.NET minimal API with GET endpoints only.
/// It never registers the MCP server, a tool class or <c>WzSessionManager</c>, so no code that writes a file is
/// reachable through it. Every request needs the <c>X-Viewer-Token</c> header.
/// </summary>
public static class ViewerHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Entry point of the mode. Returns the process exit code: 0 after a normal stop, 2 when the configuration is
    /// invalid (for example the token is missing or too short), with the reason on standard error.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, string host, int port, Func<string, string?> getEnv)
    {
        ViewerOptions options;
        try
        {
            options = ViewerOptions.FromEnvironment(getEnv);
        }
        catch (ViewerConfigException ex)
        {
            Console.Error.WriteLine("viewer: " + ex.Message);
            return 2;
        }

        if (options.Warning != null)
            Console.Error.WriteLine("viewer: warning: " + options.Warning);

        var app = Build(args, options, $"http://{HostUrl.ForUrl(host)}:{port}");

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WzImgMCP.Viewer");
        logger.LogInformation("WZ viewer (read only) starting on http://{Host}:{Port}", host, port);
        foreach (var name in ViewerOptions.SourceNames)
            logger.LogInformation("Source {Source}: {Root}", name, options.Roots[name]);

        await app.RunAsync();
        return 0;
    }

    /// <summary>Builds the host without starting it. <paramref name="resolver"/> is for tests; the default reads the options' roots.</summary>
    public static WebApplication Build(string[] args, ViewerOptions options, string url, ViewerPathResolver? resolver = null, IViewerFileSystem? fileSystem = null)
    {
        var builder = CreateBuilder(args, options, url, resolver, fileSystem);
        var app = builder.Build();
        Configure(app, options);
        return app;
    }

    /// <summary>Service registration only. Public so a test can list every registered service.</summary>
    public static WebApplicationBuilder CreateBuilder(string[] args, ViewerOptions options, string url, ViewerPathResolver? resolver = null, IViewerFileSystem? fileSystem = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        // The framework's own request log lines contain the full URL with the query string; this service logs its own line.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);

        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 0;   // no request body is ever read
        });

        builder.Services.AddSingleton(options);
        var files = fileSystem ?? new PosixViewerFileSystem();
        builder.Services.AddSingleton(files);
        builder.Services.AddSingleton(resolver ?? new ViewerPathResolver(options.Roots, files));
        builder.Services.AddSingleton(new ViewerImageCache(options.CacheBytes));
        builder.Services.AddSingleton(sp => new ViewerImageLoader(options, sp.GetRequiredService<IViewerFileSystem>(), sp.GetRequiredService<ViewerImageCache>()));
#if LINUX_BUILD
        builder.Services.AddSingleton(sp => new Canvas.ViewerCanvasService(sp.GetRequiredService<ViewerImageLoader>(), options.MaxPictureBytes));
#endif
        builder.Services.AddSingleton(new ViewerDecodeLimiter(options.MaxDecodes));
        builder.Services.AddSingleton(sp => new Diff.ViewerBaseMap(options, sp.GetRequiredService<IViewerFileSystem>()));
        builder.Services.AddSingleton<Diff.ViewerDiffService>();
        builder.Services.AddSingleton(sp => new ViewerWorkRunner(sp.GetRequiredService<ViewerDecodeLimiter>(), sp.GetRequiredService<ViewerPathResolver>(),
            sp.GetRequiredService<ViewerImageLoader>(), options.LinkBudgetBytes));
        builder.Services.AddSingleton(new ViewerRequestLimiter(options.MaxConcurrentRequests));
        return builder;
    }

    /// <summary>Middleware and the GET routes.</summary>
    public static void Configure(WebApplication app, ViewerOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WzImgMCP.Viewer");
        var limiter = app.Services.GetRequiredService<ViewerRequestLimiter>();
        ViewerLinkContext.Install();   // MapleLib's static hook for _outlink targets; answers null outside a viewer request
        var tokenDigest = SHA256.HashData(Encoding.UTF8.GetBytes(options.Token));

        // Outermost: log one line per request (method, route template, status, time) and turn any unhandled
        // exception into a generic 500. Neither the query string, a header nor the exception text is written anywhere.
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

        // Token, method, route and body checks, in this order, so an unauthenticated caller learns nothing about the routes.
        app.Use(async (context, next) =>
        {
            if (!TokenMatches(tokenDigest, context.Request.Headers[ViewerOptions.TokenHeader]))
            {
                await WriteError(context, 401, "unauthorized", $"missing or wrong {ViewerOptions.TokenHeader} header");
                return;
            }
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.Headers.Allow = "GET";
                await WriteError(context, 405, "method_not_allowed", "only GET is allowed");
                return;
            }
            if (context.GetEndpoint() == null)
            {
                await WriteError(context, 404, "not_found", "no such route");
                return;
            }
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                await WriteError(context, 400, "bad_request", "request bodies are not accepted");
                return;
            }
            await next();
        });

        // Concurrency cap and request timeout.
        app.Use(async (context, next) =>
        {
            // Health is cheap and is what a container health check calls: it must still answer while every slot is busy.
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
            timeout.CancelAfter(options.RequestTimeout);
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

        app.MapGet("/v1/health", (ViewerOptions o) => Results.Json(new
        {
            status = "ok",
            version = typeof(ViewerHost).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            sources = ViewerOptions.SourceNames.ToDictionary(n => n, n => ViewerPathResolver.IsReadableDirectory(Path.Combine(o.Roots[n], "img"))),   // what the resolver reads
        }, Json)).WithMetadata(new NoRequestSlot());

        app.MapGet("/v1/resolve", async (HttpContext context, ViewerPathResolver resolver) =>
        {
            var image = await resolver.ResolveAsync(Single(context.Request.Query["source"]), Single(context.Request.Query["path"]), context.RequestAborted);
            return Results.Json(new { source = image.Source, path = image.RelativePath, exists = true, sizeBytes = image.SizeBytes }, Json);
        }).AddEndpointFilter(TypedErrors);

        app.MapGet("/v1/inspect", async (HttpContext context, ViewerPathResolver resolver, ViewerImageLoader loader, ViewerWorkRunner runner) =>
        {
            var image = await resolver.ResolveAsync(Single(context.Request.Query["source"]), Single(context.Request.Query["path"]), context.RequestAborted);
            var slot = context.Items[RequestSlotKey] as ViewerRequestSlot;
            var ct = context.RequestAborted;
            var result = await runner.RunAsync(image.Source, slot, ct, links => ViewerInspector.Inspect(loader, image, links, ct));
            return Results.Json(result, Json);
        }).AddEndpointFilter(TypedErrors);

        app.MapGet("/v1/diff", async (HttpContext context, ViewerPathResolver resolver, Diff.ViewerDiffService diff, ViewerWorkRunner runner) =>
        {
            var person = Single(context.Request.Query["person"]);
            if (person is not ("owner" or "friend"))
                throw new ViewerApiException(400, "unknown_source", "person must be owner or friend");
            var path = Single(context.Request.Query["path"]);
            var ct = context.RequestAborted;
            var canon = await ResolveOrNull(resolver, "canon", path, ct);
            var mine = await ResolveOrNull(resolver, person, path, ct);
            var slot = context.Items[RequestSlotKey] as ViewerRequestSlot;
            var result = await runner.RunAsync(person, slot, ct, _ => diff.Diff(person, canon, mine, ct));
            return Results.Json(result, Json);
        }).AddEndpointFilter(TypedErrors);

#if LINUX_BUILD
        MapCanvasRoutes(app);
#endif
    }

#if LINUX_BUILD
    /// <summary>
    /// <c>/v1/canvas</c> (one canvas as PNG) and <c>/v1/frames</c> (the frames of an animation, as JSON). Both resolve the
    /// path with the same rules as <c>/v1/diff</c>, and run their parse and pixel work through the work runner.
    /// </summary>
    private static void MapCanvasRoutes(WebApplication app)
    {
        app.MapGet("/v1/canvas", async (HttpContext context, ViewerPathResolver resolver, IViewerFileSystem files, Canvas.ViewerCanvasService canvases, ViewerWorkRunner runner) =>
        {
            var query = context.Request.Query;
            var ct = context.RequestAborted;
            var image = await resolver.ResolveAsync(Single(query["source"]), Single(query["path"]), ct);
            var nodeText = Single(query["node"]);
            var node = Canvas.ViewerCanvasService.ParseNode(nodeText);
            if (query["maxSide"].Count > 1)
                throw new ViewerApiException(400, "bad_request", "maxSide must be given once");
            int maxSide = Canvas.ViewerCanvasService.ParseMaxSide(Single(query["maxSide"]), out var maxSideText);

            // Revalidation: the ETag depends on the file stamp, the node, maxSide and the renderer, so a match needs no parse and no decode.
            var stamp = files.Stat(image.FullPath) ?? throw new ViewerApiException(404, "not_found", "image not found");
            var etag = Canvas.ViewerCanvasService.ETagFor(image.Source, image.RelativePath, stamp, nodeText!, maxSideText);
            if (Canvas.ViewerCanvasService.IfNoneMatches(context.Request.Headers.IfNoneMatch.ToString(), etag))
            {
                context.Response.Headers.ETag = etag;
                context.Response.Headers.CacheControl = "private, no-cache";
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var slot = context.Items[RequestSlotKey] as ViewerRequestSlot;
            var render = await runner.RunAsync(image.Source, slot, ct, links => canvases.Render(image, node, nodeText!, maxSide, maxSideText, links, ct));

            var headers = context.Response.Headers;
            if (render.ETag != null)
            {
                headers.ETag = render.ETag;
                headers.CacheControl = "private, no-cache";   // keep it, but revalidate every time
            }
            else
                headers.CacheControl = "private, no-store";   // the pixels come from another image: the file stamp cannot validate them
            headers["X-Canvas-Width"] = render.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            headers["X-Canvas-Height"] = render.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            headers["X-Canvas-Origin-X"] = render.OriginX.ToString(System.Globalization.CultureInfo.InvariantCulture);
            headers["X-Canvas-Origin-Y"] = render.OriginY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (render.DelayMs.HasValue)
                headers["X-Canvas-Delay"] = render.DelayMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            headers["X-Image-Width"] = render.OutputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
            headers["X-Image-Height"] = render.OutputHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Bytes(render.Png, "image/png");
        }).AddEndpointFilter(TypedErrors);

        app.MapGet("/v1/frames", async (HttpContext context, ViewerPathResolver resolver, Canvas.ViewerCanvasService canvases, ViewerWorkRunner runner) =>
        {
            var query = context.Request.Query;
            var ct = context.RequestAborted;
            var image = await resolver.ResolveAsync(Single(query["source"]), Single(query["path"]), ct);
            var nodeText = Single(query["node"]);
            var node = Canvas.ViewerCanvasService.ParseNode(nodeText);
            var slot = context.Items[RequestSlotKey] as ViewerRequestSlot;
            var result = await runner.RunAsync(image.Source, slot, ct, links => canvases.Frames(image, node, nodeText!, links, ct));
            return Results.Json(result, Json);
        }).AddEndpointFilter(TypedErrors);
    }
#endif

    /// <summary>Resolves a path in one source; a missing file is null, any other problem is thrown.</summary>
    private static async Task<ResolvedImage?> ResolveOrNull(ViewerPathResolver resolver, string source, string? path, CancellationToken cancellationToken)
    {
        try { return await resolver.ResolveAsync(source, path, cancellationToken); }
        catch (ViewerApiException ex) when (ex.Code == "not_found") { return null; }
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

    internal static readonly object RequestSlotKey = new();

    /// <summary>Constant time comparison. Both values are hashed first, so the compared arrays always have the same length.</summary>
    private static bool TokenMatches(byte[] expectedDigest, Microsoft.Extensions.Primitives.StringValues supplied)
    {
        var value = supplied.Count == 1 ? supplied[0] ?? "" : "";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        // A missing, repeated or empty header can never match, but it still goes through the same comparison.
        return CryptographicOperations.FixedTimeEquals(expectedDigest, digest) & (supplied.Count == 1);
    }

    private static string? Single(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 1 ? values[0] : null;

    private static Task WriteError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message } }, Json));
    }
}
