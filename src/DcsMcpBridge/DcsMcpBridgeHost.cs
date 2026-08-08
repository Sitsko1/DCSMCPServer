using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

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

    public async Task StartAsync(
        string listenUrl = "http://127.0.0.1:5270",
        string dcsIp = "127.0.0.1",
        int dcsPort = 1024,
        CancellationToken cancellationToken = default)
    {
        Status.BridgeState = BridgeState.Starting;
        try
        {
            var builder = WebApplication.CreateBuilder();

            // No console attached when hosted inside a WinUI app; log to the debug output instead.
            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();

            builder.Services.AddSingleton(Status);
            builder.Services.AddSingleton(sp => new DcsConnection(
                sp.GetRequiredService<ILogger<DcsConnection>>(),
                sp.GetRequiredService<BridgeStatus>(),
                dcsIp,
                dcsPort));
            builder.Services.AddSingleton<IDcsConnection>(sp => sp.GetRequiredService<DcsConnection>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DcsConnection>());

            // AtcAction has no source-generated JSON metadata in the SDK's default (reflection-free,
            // AOT-safe) serializer options — merge in AtcJsonContext for it.
            var atcToolSerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolverChain = { McpJsonUtilities.DefaultOptions.TypeInfoResolver!, AtcJsonContext.Default },
            };

            builder.Services
                .AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<AtcTools>(atcToolSerializerOptions);

            builder.WebHost.UseUrls(listenUrl);

            _app = builder.Build();
            _app.MapMcp("/mcp");

            await _app.StartAsync(cancellationToken);
            Status.McpEndpoint = $"{listenUrl.TrimEnd('/')}/mcp";
            Status.DcsEndpoint = $"{dcsIp}:{dcsPort}";
            Status.BridgeState = BridgeState.Running;
        }
        catch
        {
            Status.BridgeState = BridgeState.Faulted;
            throw;
        }
    }

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
