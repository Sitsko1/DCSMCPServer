using System.Diagnostics;
using System.Text.Json;
using DCS.Scripting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// Composes and runs the MCP server (HTTP transport) plus the DCS connection, in-process, for
/// any host (WinUI, tests, a future console launcher) to start and observe via
/// <see cref="Status"/>. Chosen over stdio specifically so the hosting app can be a persistent,
/// always-on process with a UI — stdio would mean MCP clients spawn/kill this process per
/// session, which doesn't fit a status window.
/// </summary>
public sealed class DcsMcpBridgeHost : IAsyncDisposable
{
    // Optional constructor param (defaults to a fresh instance) so a caller that restarts the
    // host across settings changes can pass the same BridgeStatus through and keep its UI
    // subscription alive, instead of it going stale on every new DcsMcpBridgeHost.
    public DcsMcpBridgeHost(BridgeStatus? status = null)
    {
        Status = status ?? new BridgeStatus();
    }

    public BridgeStatus Status { get; }

    private WebApplication? _app;

    // Read per request by the bearer check, so SetApiKey takes effect without a restart.
    private volatile string _apiKey = "";

    /// <summary>
    /// Replaces the MCP API key immediately: requests with the old key get 401 from now on. No
    /// restart, so the DCS connection isn't dropped.
    /// </summary>
    public void SetApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) throw new ArgumentException("An MCP API key is required.", nameof(apiKey));
        _apiKey = apiKey;
    }

    public async Task StartAsync(
        string listenUrl = "http://127.0.0.1:5270",
        string dcsIp = "127.0.0.1",
        int dcsPort = 1024,
        CancellationToken cancellationToken = default,
        ILoggerProvider? loggerProvider = null,
        string? apiKey = null,
        string? dcsLinkSecret = null)
    {
        // No "auth off" mode (maintainer decision, #7): both secrets are required.
        SetApiKey(apiKey ?? "");
        if (!Secrets.IsWellFormed(dcsLinkSecret))
        {
            throw new ArgumentException("A well-formed DCS link secret is required.", nameof(dcsLinkSecret));
        }

        Status.BridgeState = BridgeState.Starting;
        try
        {
            var builder = WebApplication.CreateBuilder();

            // Never a console provider: a WinUI app has no console attached. With a provider (the
            // app's DcsLogging), it decides the level; without one (tests), debug output only.
            builder.Logging.ClearProviders();
            if (loggerProvider is not null)
            {
                builder.Logging.AddProvider(loggerProvider); // registered as an instance, so a host restart doesn't dispose it
                builder.Logging.SetMinimumLevel(LogLevel.Trace);
                builder.Logging.AddFilter("Microsoft", LogLevel.Warning); // per-request ASP.NET chatter
                builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
            }
            else
            {
                builder.Logging.AddDebug();
            }

            builder.Services.AddDcsScripting(Status, dcsIp, dcsPort, dcsLinkSecret!);

            // AtcAction has no source-generated JSON metadata in the SDK's default (reflection-free,
            // AOT-safe) serializer options — merge in AtcJsonContext for it.
            var atcToolSerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolverChain = { McpJsonUtilities.DefaultOptions.TypeInfoResolver!, AtcJsonContext.Default },
            };

            builder.Services
                .AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<AtcTools>(atcToolSerializerOptions)
                .WithTools<AircraftTools>()
                .WithRequestFilters(filters => filters.AddCallToolFilter(LogToolCall));

            builder.WebHost.UseUrls(listenUrl);

            _app = builder.Build();

            // Bearer API key on every request, checked before any MCP handling (constant time).
            _app.Use(async (context, next) =>
            {
                if (!Secrets.BearerMatches(context.Request.Headers.Authorization, _apiKey))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                await next(context);
            });
            _app.MapMcp("/mcp");

            await _app.StartAsync(cancellationToken);
            Status.McpEndpoint = $"{listenUrl.TrimEnd('/')}/mcp";
            Status.DcsEndpoint = $"{dcsIp}:{dcsPort}";
            Status.BridgeState = BridgeState.Running;
            _app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DCS.Bridge").LogInformation(
                "MCP server listening at {McpEndpoint}; DCS at {DcsEndpoint}", Status.McpEndpoint, Status.DcsEndpoint);
        }
        catch
        {
            Status.BridgeState = BridgeState.Faulted;
            throw;
        }
    }

    /// <summary>
    /// Logs every MCP tool call: name, outcome and duration. Never the arguments — they're
    /// user/LLM free text, some of it ending up as Lua sent to DCS (see #4).
    /// </summary>
    private static McpRequestHandler<CallToolRequestParams, CallToolResult> LogToolCall(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, cancellationToken) =>
    {
        ILogger logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("DCS.Tools") ?? NullLogger.Instance;
        string tool = context.Params?.Name ?? "(unknown)";
        long started = Stopwatch.GetTimestamp();
        try
        {
            CallToolResult result = await next(context, cancellationToken);
            logger.LogInformation("Tool {Tool} {Outcome} in {ElapsedMs:0} ms",
                tool, result.IsError == true ? "failed" : "succeeded", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool {Tool} threw after {ElapsedMs:0} ms", tool, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    };

    // Stops and fully tears down the current WebApplication so StartAsync can be called again
    // (e.g. after a settings change) without leaking the previous instance.
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }
        Status.BridgeState = BridgeState.Stopped;
        Status.McpEndpoint = "—";
        Status.DcsEndpoint = "—";
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
