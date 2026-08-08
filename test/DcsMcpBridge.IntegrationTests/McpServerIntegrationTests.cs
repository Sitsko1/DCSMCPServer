using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// Starts the real DcsMcpBridgeHost in-process (HTTP transport) and drives it with the SDK's own
/// McpClient, exactly like a real MCP client would connect. This is the in-process successor to
/// the old subprocess-based test, from back when the server was a stdio exe.
/// </summary>
public class McpServerIntegrationTests : IAsyncLifetime
{
    // A distinct port from the WinUI app's default (5270), so a running dev instance of
    // DCS.AIAutomator doesn't collide with the test run.
    private const string ListenUrl = "http://127.0.0.1:5271";

    private DcsMcpBridgeHost _host = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = new DcsMcpBridgeHost();
        await _host.StartAsync(ListenUrl);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{ListenUrl}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        });
        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task ListTools_IncludesSendAtcInstruction()
    {
        IList<McpClientTool> tools = await _client.ListToolsAsync();

        Assert.Contains(tools, t => t.Name == "send_atc_instruction");
    }

    [Fact]
    public async Task CallTool_SendAtcInstruction_ReportsDcsDown()
    {
        CallToolResult result = await _client.CallToolAsync(
            "send_atc_instruction",
            new Dictionary<string, object?>
            {
                ["aircraft_callsign"] = "Enfield 1-1",
                ["action"] = "Vectors",
                ["heading"] = 270,
            });

        string? resultText = result.Content.OfType<TextContentBlock>().First().Text;

        // No real DCS instance is listening on 127.0.0.1:1024 in the test environment — this is
        // the correct, expected response, not a failure.
        Assert.Equal("Error: DCS interface is down.", resultText);
    }
}
