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

        var app = Build(args, options, $"http://{HostUrl.ForUrl(host)}:{port}");

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WzImgMCP.Viewer");
        logger.LogInformation("WZ viewer (read only) starting on http://{Host}:{Port}", host, port);
        foreach (var name in ViewerOptions.SourceNames)
            logger.LogInformation("Source {Source}: {Root}", name, options.Roots[name]);

        await app.RunAsync();
        return 0;
    }

    /// <summary>Builds the host without starting it. <paramref name="resolver"/> is for tests; the default reads the options' roots.</summary>
    public static WebApplication Build(string[] args, ViewerOptions options, string url, ViewerPathResolver? resolver = null)
    {
        var builder = CreateBuilder(args, options, url, resolver);
        var app = builder.Build();
        Configure(app, options);
        return app;
    }

    /// <summary>Service registration only. Public so a test can list every registered service.</summary>
    public static WebApplicationBuilder CreateBuilder(string[] args, ViewerOptions options, string url, ViewerPathResolver? resolver = null)
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
        builder.Services.AddSingleton(resolver ?? new ViewerPathResolver(options.Roots));
        builder.Services.AddSingleton(new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests));
        return builder;
    }

    /// <summary>Middleware and the GET routes.</summary>
    public static void Configure(WebApplication app, ViewerOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WzImgMCP.Viewer");
        var slots = app.Services.GetRequiredService<SemaphoreSlim>();
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
            if (!await slots.WaitAsync(0))
            {
                context.Response.Headers.RetryAfter = "1";
                await WriteError(context, 503, "busy", "too many requests in progress; retry shortly");
                return;
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
                slots.Release();
            }
        });

        app.MapGet("/v1/health", (ViewerOptions o) => Results.Json(new
        {
            status = "ok",
            version = typeof(ViewerHost).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            sources = ViewerOptions.SourceNames.ToDictionary(n => n, n => IsReadableDirectory(o.Roots[n])),
        }, Json));

        app.MapGet("/v1/resolve", async (HttpContext context, ViewerPathResolver resolver) =>
        {
            var source = Single(context.Request.Query["source"]);
            var path = Single(context.Request.Query["path"]);
            var image = await resolver.ResolveAsync(source, path, context.RequestAborted);
            return Results.Json(new { source = image.Source, path = image.RelativePath, exists = true, sizeBytes = image.SizeBytes }, Json);
        }).AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                return await next(invocation);
            }
            catch (ViewerApiException ex)
            {
                return Results.Json(new { error = new { code = ex.Code, message = ex.Message } }, Json, statusCode: ex.Status);
            }
        });
    }

    /// <summary>Constant time comparison. Both values are hashed first, so the compared arrays always have the same length.</summary>
    private static bool TokenMatches(byte[] expectedDigest, Microsoft.Extensions.Primitives.StringValues supplied)
    {
        var value = supplied.Count == 1 ? supplied[0] ?? "" : "";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        // A missing, repeated or empty header can never match, but it still goes through the same comparison.
        return CryptographicOperations.FixedTimeEquals(expectedDigest, digest) & (supplied.Count == 1);
    }

    private static string? Single(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 1 ? values[0] : null;

    private static bool IsReadableDirectory(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Task WriteError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message } }, Json));
    }
}
