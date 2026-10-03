using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// Relays MCP messages between a stdio-side transport (Claude Desktop launches the app's own exe
/// with <c>--mcp-relay</c>, because <c>claude_desktop_config.json</c> only supports stdio servers)
/// and the running app's HTTP endpoint, adding the bearer API key.
/// <para>
/// <b>A dumb pipe, deliberately:</b> it hosts no tools and has no DCS logic — that's what keeps it
/// from being the deleted stdio-exe bridge (see CLAUDE.md). Messages are forwarded unchanged and
/// never logged. When the app can't be reached, each request gets a JSON-RPC error saying why,
/// so the client fails fast instead of hanging.
/// </para>
/// </summary>
public static class McpStdioRelay
{
    public const int AppUnavailableErrorCode = -32000;

    /// <summary>Runs until the stdio side closes (or cancellation).</summary>
    public static async Task RunAsync(ITransport stdioSide, Uri endpoint, string apiKey,
        ILoggerFactory? loggerFactory, CancellationToken cancellationToken)
    {
        ILogger logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DCS.Relay");
        await using var http = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
        }, loggerFactory ?? NullLoggerFactory.Instance);

        ITransport? appSide = null;
        Task? appToStdio = null;
        try
        {
            await foreach (JsonRpcMessage message in stdioSide.MessageReader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    if (appSide is null)
                    {
                        appSide = await http.ConnectAsync(cancellationToken);
                        appToStdio = PumpAsync(appSide, stdioSide, cancellationToken);
                    }
                    await appSide.SendMessageAsync(message, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    string reason = Describe(ex, endpoint);
                    logger.LogWarning("Relay could not forward to the app: {Reason}", reason);
                    if (message is JsonRpcRequest request)
                    {
                        await stdioSide.SendMessageAsync(new JsonRpcError
                        {
                            Id = request.Id,
                            Error = new JsonRpcErrorDetail { Code = AppUnavailableErrorCode, Message = reason },
                        }, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            if (appSide is not null) await appSide.DisposeAsync();
            if (appToStdio is not null) await Task.WhenAny(appToStdio, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static async Task PumpAsync(ITransport from, ITransport to, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (JsonRpcMessage message in from.MessageReader.ReadAllAsync(cancellationToken))
            {
                await to.SendMessageAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Actionable text for the user, from whatever the HTTP side threw.</summary>
    private static string Describe(Exception ex, Uri endpoint)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } || e.Message.Contains("401"))
            {
                return "DCS.AIAutomator rejected the API key (401). Reconnect this client in DCS.AIAutomator → Settings → AI agents.";
            }
            if (e is HttpRequestException { StatusCode: null })
            {
                return $"DCS.AIAutomator is not running (nothing listening at {endpoint}). Start the app, then retry.";
            }
        }
        return $"Could not reach DCS.AIAutomator at {endpoint}: {ex.Message}";
    }
}
