using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using ModelContextProtocol.Server;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP;

class Program
{
    static async Task Main(string[] args)
    {
        ServerSettings.Apply();   // WZIMG_PRE_BIG_BANG: no DXT in the automatic format choice

        // Parse transport mode from arguments
        var transport = ParseTransportMode(args);
        var port = ParsePort(args);
        var host = ParseHost(args);

        if (transport == TransportMode.Http)
        {
            await RunHttpServer(args, host, port);
        }
        else
        {
            await RunStdioServer(args);
        }
    }

    private static async Task RunStdioServer(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Configure logging to stderr for stdio transport
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Register services
        builder.Services.AddSingleton<WzSessionManager>();

        // Configure MCP server with stdio transport
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "harepacker-mcp",
                    Version = "1.0.0"
                };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();
        StartMemoryGuard(host.Services, host.Services.GetRequiredService<ILogger<Program>>());
        await host.RunAsync();
    }

    private static async Task RunHttpServer(string[] args, string host, int port)
    {
        host = HostUrl.ForUrl(host);   // an IPv6 address such as :: must be in brackets inside a URL

        var builder = WebApplication.CreateBuilder(args);

        // Configure logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Configure URLs
        builder.WebHost.UseUrls($"http://{host}:{port}");

        // Register services
        builder.Services.AddSingleton<WzSessionManager>();

        // Session idle timeout (WZIMG_SESSION_IDLE_SECONDS, default 7 days). The library closes a session that has had no
        // request in flight for this long; its default is 2 h, which on Linux lasts only 72 s (see SessionIdleSettings).
        var idleSeconds = SessionIdleSettings.Parse(Environment.GetEnvironmentVariable(SessionIdleSettings.EnvVar), out var idleWarning);
        var idleTimeout = SessionIdleSettings.ToLibraryTimeout(idleSeconds);

        // Configure MCP server with HTTP/SSE transport
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "harepacker-mcp",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(o =>
            {
                o.IdleTimeout = idleTimeout;
                o.MaxIdleSessionCount = SessionIdleSettings.MaxIdleSessionCount;
            })
            .WithToolsFromAssembly();

        var app = builder.Build();

        // Log startup info
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("HaRepacker MCP Server starting on http://{Host}:{Port}", host, port);
        logger.LogInformation("Streamable HTTP endpoint: POST http://{Host}:{Port}/", host, port);
        logger.LogInformation("Legacy SSE endpoint: GET http://{Host}:{Port}/sse", host, port);
        logger.LogInformation("Legacy message endpoint: POST http://{Host}:{Port}/message", host, port);
        if (idleWarning != null)
            logger.LogWarning("{Warning}", idleWarning);
        logger.LogInformation("Session idle timeout: {Seconds} s (library IdleTimeout ticks {Ticks}, timestamp frequency {Frequency}/s); max idle sessions: {MaxIdle}",
            idleSeconds, idleTimeout.Ticks, TimeProvider.System.TimestampFrequency, SessionIdleSettings.MaxIdleSessionCount);
        logger.LogInformation("Default data folder (opened by the first tool call that needs it): {Path}",
            app.Services.GetRequiredService<WzSessionManager>().DefaultDataPath);

        StartMemoryGuard(app.Services, logger);

        // Map MCP endpoints (supports both Streamable HTTP and legacy SSE)
        app.MapMcp();

        await app.RunAsync();
    }

    /// <summary>
    /// Logs the memory and scan settings and starts the timer of the memory guard (it trims the cache and the managed heap
    /// when the heap is above WZIMG_MEMORY_SOFT_LIMIT_MB or the server has been idle for WZIMG_IDLE_TRIM_SECONDS).
    /// </summary>
    private static void StartMemoryGuard(IServiceProvider services, ILogger logger)
    {
        var session = services.GetRequiredService<WzSessionManager>();
        var guard = session.MemoryGuard.Settings;
        var scan = session.ScanLimits;
        logger.LogInformation(
            "Memory: image cache limit {CacheMb} MB (WZIMG_CACHE_MB); trim at managed heap above {SoftMb} MB or after {IdleSeconds} s without a tool call (0 = off); " +
            "scans: at most {ScanMax} images, {Budget} s budget (0 = none); bulk tools: at most {BulkMax} images unless force",
            ServerSettings.CacheMb, guard.SoftLimitBytes / 1048576, (int)guard.IdleTrim.TotalSeconds,
            scan.MaxImages, (int)scan.Budget.TotalSeconds, scan.BulkMaxImages);
        session.StartMemoryGuard(message => logger.LogInformation("{Message}", message));
    }

    private static TransportMode ParseTransportMode(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();

            // Support --transport=http or --transport http
            if (arg == "--transport" && i + 1 < args.Length)
            {
                return args[i + 1].ToLowerInvariant() switch
                {
                    "http" or "sse" or "web" => TransportMode.Http,
                    _ => TransportMode.Stdio
                };
            }

            if (arg.StartsWith("--transport="))
            {
                var value = arg.Substring("--transport=".Length);
                return value switch
                {
                    "http" or "sse" or "web" => TransportMode.Http,
                    _ => TransportMode.Stdio
                };
            }

            // Shorthand flags
            if (arg == "--http" || arg == "-h" || arg == "--sse" || arg == "--web")
            {
                return TransportMode.Http;
            }

            if (arg == "--stdio" || arg == "-s")
            {
                return TransportMode.Stdio;
            }
        }

        // Default to stdio for backwards compatibility
        return TransportMode.Stdio;
    }

    /// <summary>
    /// Address the HTTP transport binds to. Defaults to localhost; set --host 0.0.0.0 (or the
    /// WZIMG_HOST environment variable) to listen on all interfaces, e.g. inside a container.
    /// </summary>
    private static string ParseHost(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--host", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];

            if (args[i].StartsWith("--host=", StringComparison.OrdinalIgnoreCase))
                return args[i].Substring("--host=".Length);
        }

        var fromEnv = Environment.GetEnvironmentVariable("WZIMG_HOST");
        return string.IsNullOrWhiteSpace(fromEnv) ? "localhost" : fromEnv;
    }

    private static int ParsePort(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();

            // Support --port=8080 or --port 8080
            if (arg == "--port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var port))
                    return port;
            }

            if (arg.StartsWith("--port="))
            {
                var value = arg.Substring("--port=".Length);
                if (int.TryParse(value, out var port))
                    return port;
            }

            // Short form -p
            if (arg == "-p" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var port))
                    return port;
            }
        }

        // Default port
        return 13339;
    }

    private enum TransportMode
    {
        Stdio,
        Http
    }
}
