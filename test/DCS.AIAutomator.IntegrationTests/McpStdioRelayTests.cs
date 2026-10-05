using System.IO.Pipes;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// Drives McpStdioRelay the way Claude Desktop would: an MCP client talks to the relay over a pair
/// of pipes (standing in for the relay's stdin/stdout), and the relay forwards to a real in-process
/// DcsMcpBridgeHost over HTTP with the bearer key.
/// </summary>
public class McpStdioRelayTests : IAsyncLifetime
{
    // Own port: xUnit runs test classes in parallel, and McpServerIntegrationTests uses 5271.
    private const string ListenUrl = "http://127.0.0.1:5273";
    private const string ApiKey = "RelayTestApiKey_0123456789abcdef";
    private const string DcsLinkSecret = "RelayTestLinkSecret_0123456789";
    private const int UnusedDcsPort = 1025;

    private readonly DcsMcpBridgeHost _host = new();
    private readonly CancellationTokenSource _stop = new();

    public async Task InitializeAsync() =>
        await _host.StartAsync(ListenUrl, dcsPort: UnusedDcsPort, apiKey: ApiKey, dcsLinkSecret: DcsLinkSecret);

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await _host.DisposeAsync();
    }

    // Starts the relay on one end of two pipes and returns an MCP client on the other end.
    private async Task<McpClient> ConnectThroughRelayAsync(string endpoint, string apiKey)
    {
        var clientToRelay = new AnonymousPipeServerStream(PipeDirection.Out);
        var relayIn = new AnonymousPipeClientStream(PipeDirection.In, clientToRelay.ClientSafePipeHandle);
        var relayToClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, relayToClient.ClientSafePipeHandle);

        var relaySide = new StreamServerTransport(relayIn, relayToClient, "relay-under-test");
        _ = McpStdioRelay.RunAsync(relaySide, new Uri(endpoint), apiKey, loggerFactory: null, _stop.Token);

        return await McpClient.CreateAsync(
            new StreamClientTransport(serverInput: clientToRelay, serverOutput: clientIn),
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
    }

    [Fact]
    public async Task RelaysInitializeToolsListAndToolCalls_ToTheRunningApp()
    {
        await using McpClient client = await ConnectThroughRelayAsync($"{ListenUrl}/mcp", ApiKey);

        IList<McpClientTool> tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "get_aircraft_state");

        CallToolResult result = await client.CallToolAsync("get_aircraft_state", new Dictionary<string, object?>());
        Assert.StartsWith("No active aircraft", result.Content.OfType<TextContentBlock>().First().Text);
    }

    [Fact]
    public async Task AnInitialize_AfterASuccessfulServerDiscoverProbe_StillWorks()
    {
        // #36: the SDK client probes with server/discover (2026-07-28) and, if the probe times out on
        // its side (a slow first request: seen on CI), falls back to initialize (2025-11-25) on the
        // same connection. The probe may still reach the app and succeed; the SDK's HTTP transport
        // then caches 2026-07-28 and sends it as the MCP-Protocol-Version header of the fallback
        // initialize, which the app rejects. Raw messages make that order deterministic.
        var (toRelay, fromRelay) = StartRawRelay($"{ListenUrl}/mcp", ApiKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await toRelay.SendMessageAsync(new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = RequestMethods.ServerDiscover,
            Params = JsonNode.Parse("""
                {"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28",
                          "io.modelcontextprotocol/clientInfo":{"name":"relay-test","version":"1"},
                          "io.modelcontextprotocol/clientCapabilities":{}}}
                """),
        }, timeout.Token);
        Assert.IsType<JsonRpcResponse>(await ReadReplyAsync(fromRelay, new RequestId(1), timeout.Token)); // the probe succeeded

        await toRelay.SendMessageAsync(new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = RequestMethods.Initialize,
            Params = JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"relay-test","version":"1"}}"""),
        }, timeout.Token);
        JsonRpcMessage reply = await ReadReplyAsync(fromRelay, new RequestId(2), timeout.Token);

        Assert.True(reply is JsonRpcResponse, $"initialize failed: {(reply as JsonRpcError)?.Error.Message}");
    }

    private static async Task<JsonRpcMessage> ReadReplyAsync(ITransport from, RequestId id, CancellationToken cancellationToken)
    {
        await foreach (JsonRpcMessage message in from.MessageReader.ReadAllAsync(cancellationToken))
        {
            if (message is JsonRpcResponse { Id: var r } && r.Equals(id)) return message;
            if (message is JsonRpcError { Id: var e } && e.Equals(id)) return message;
        }
        throw new InvalidOperationException($"the relay closed without replying to {id}");
    }

    // Starts the relay on two pipes and returns the raw client end of them (no SDK client logic).
    private (ITransport ToRelay, ITransport FromRelay) StartRawRelay(string endpoint, string apiKey)
    {
        var clientToRelay = new AnonymousPipeServerStream(PipeDirection.Out);
        var relayIn = new AnonymousPipeClientStream(PipeDirection.In, clientToRelay.ClientSafePipeHandle);
        var relayToClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, relayToClient.ClientSafePipeHandle);

        _ = McpStdioRelay.RunAsync(new StreamServerTransport(relayIn, relayToClient, "relay-under-test"),
            new Uri(endpoint), apiKey, loggerFactory: null, _stop.Token);

        var client = new StreamServerTransport(clientIn, clientToRelay, "raw-client");
        return (client, client);
    }

    [Fact]
    public async Task AppNotRunning_FailsFastWithAClearMessage_InsteadOfHanging()
    {
        var started = DateTime.UtcNow;

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            ConnectThroughRelayAsync("http://127.0.0.1:5279/mcp", ApiKey)); // nothing listens there

        Assert.Contains("not running", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "must not hang");
    }

    [Fact]
    public async Task WrongApiKey_IsReportedAsSuch()
    {
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            ConnectThroughRelayAsync($"{ListenUrl}/mcp", "not-the-key"));

        Assert.Contains("API key", ex.Message);
    }
}
