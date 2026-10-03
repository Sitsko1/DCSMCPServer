using System;
using System.IO;
using System.Threading;
using DCS.Scripting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Windows.Storage;

namespace DCS.AIAutomator;

/// <summary>
/// Headless stdio relay for Claude Desktop (launched as <c>dcs-aiautomator.exe --mcp-relay</c>).
/// Runs with the app's package identity (it is the app's own exe, via the app execution alias), so
/// it reads the current port from the app's settings and the API key from the Credential Locker —
/// Claude Desktop's config holds neither. stdout carries only JSON-RPC: all diagnostics go to its
/// own log file, never the console.
/// </summary>
internal static class McpRelayMode
{
    public static int Run()
    {
        var settings = new SettingsService();
        using var logging = new DcsLogging(
            Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "Logs"),
            settings.LogLevel,
            settings.LogRetentionDays,
            fileNamePrefix: "dcs-aiautomator-relay-");
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(logging.Provider);
            builder.SetMinimumLevel(LogLevel.Trace); // DcsLogging's level switch decides
            builder.AddFilter("ModelContextProtocol", LogLevel.Warning); // SDK debug logging can include message bodies
        });
        ILogger log = loggerFactory.CreateLogger("DCS.Relay");

        // Never mint a key here: one the running app doesn't know about would just be a 401.
        string? apiKey = new SecretStore().TryGet(SecretStore.McpApiKey);
        if (apiKey is null)
        {
            log.LogError("No MCP API key yet; DCS.AIAutomator has never been run");
            Console.Error.WriteLine("DCS.AIAutomator has no API key yet. Open the app once, then retry.");
            return 1;
        }

        var endpoint = new Uri($"{settings.McpListenUrl}/mcp");
        log.LogInformation("Relay started for {Endpoint}", endpoint);
        var stdio = new StdioServerTransport("dcs-aiautomator", loggerFactory);
        McpStdioRelay.RunAsync(stdio, endpoint, apiKey, loggerFactory, CancellationToken.None).GetAwaiter().GetResult();
        log.LogInformation("Relay stopped (stdin closed)");
        return 0;
    }
}
