using System.IO.Pipes;
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
            // The client probes with server/discover first and falls back to initialize when the
            // probe times out (5 s by default). A slow first request on CI tripped that fallback,
            // which the relay mishandles (the probe's protocol header sticks, #36).
            // Wait as long as the whole connect may take, so these tests don't depend on timing.
            clientOptions: new McpClientOptions { DiscoverProbeTimeout = TimeSpan.FromSeconds(15) },
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
