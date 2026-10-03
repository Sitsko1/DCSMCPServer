using DCS.Scripting;
using Microsoft.Extensions.Logging;
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

    // Likewise a DCS port nothing listens on: the default 1024 is taken whenever DCS itself is
    // running with the deployed script, which made the "DCS is down" test connect to real DCS.
    private const int UnusedDcsPort = 1025;

    private const string ApiKey = "IntegrationTestApiKey_0123456789abcdef";
    private const string DcsLinkSecret = "IntegrationTestLinkSecret_0123456789";

    private readonly CapturingLoggerProvider _logs = new();
    private DcsMcpBridgeHost _host = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = new DcsMcpBridgeHost();
        await _host.StartAsync(ListenUrl, dcsPort: UnusedDcsPort, loggerProvider: _logs, apiKey: ApiKey, dcsLinkSecret: DcsLinkSecret);
        _client = await ConnectAsync(ApiKey);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }

    private static Task<McpClient> ConnectAsync(string apiKey) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{ListenUrl}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
        }));

    // A minimal MCP initialize request, posted raw so the HTTP status is visible.
    private static async Task<HttpResponseMessage> PostInitializeAsync(string? authorization)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ListenUrl}/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await http.SendAsync(request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-key")]
    [InlineData("IntegrationTestApiKey_0123456789abcdef")] // right key, no "Bearer" scheme
    public async Task Requests_WithoutTheRightBearerKey_Get401(string? authorization)
    {
        using HttpResponseMessage response = await PostInitializeAsync(authorization);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requests_WithTheRightBearerKey_AreServed()
    {
        using HttpResponseMessage response = await PostInitializeAsync($"Bearer {ApiKey}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RotatingTheKey_RejectsTheOldOne_Immediately()
    {
        const string newKey = "RotatedApiKey_abcdefghijklmnopqrstuvwxyz";
        _host.SetApiKey(newKey);

        using HttpResponseMessage oldKey = await PostInitializeAsync($"Bearer {ApiKey}");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, oldKey.StatusCode);

        await using McpClient rotated = await ConnectAsync(newKey);
        Assert.Contains(await rotated.ListToolsAsync(), t => t.Name == "get_aircraft_state");
    }

    [Fact]
    public async Task StartingWithoutAnApiKey_IsRefused()
    {
        // No "auth off" mode (maintainer decision).
        await using var host = new DcsMcpBridgeHost();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.StartAsync("http://127.0.0.1:5272", dcsPort: UnusedDcsPort, apiKey: "", dcsLinkSecret: DcsLinkSecret));
    }

    [Fact]
    public async Task TheApiKeyAndLinkSecret_AreNeverLogged()
    {
        await CallGetAircraftStateAsync();
        (await PostInitializeAsync("Bearer wrong-key")).Dispose();
        (await PostInitializeAsync($"Bearer {ApiKey}")).Dispose();

        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(ApiKey) || e.Message.Contains(DcsLinkSecret));
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

        // No real DCS instance is listening on 127.0.0.1:1025 in the test environment — this is
        // the correct, expected response, not a failure.
        Assert.Equal("Error: DCS interface is down.", resultText);
    }

    [Fact]
    public async Task ListTools_IncludesGetAircraftState()
    {
        IList<McpClientTool> tools = await _client.ListToolsAsync();

        Assert.Contains(tools, t => t.Name == "get_aircraft_state");
    }

    [Fact]
    public async Task CallTool_GetAircraftState_WithNoAircraft_SaysSoInsteadOfZeros()
    {
        string text = await CallGetAircraftStateAsync();

        Assert.StartsWith("No active aircraft", text);
        Assert.DoesNotContain("0 ft", text);
    }

    [Theory]
    [InlineData(UnitSystem.Imperial, "9,843 ft MSL", "194 kt")]
    [InlineData(UnitSystem.Metric, "3,000 m MSL", "360 km/h")]
    public async Task CallTool_GetAircraftState_ReturnsSnapshotInConfiguredUnits(
        UnitSystem units, string expectedAltitude, string expectedIas)
    {
        // State injected straight into the shared BridgeStatus, as DcsConnection would set it.
        _host.Status.Units = units;
        _host.Status.CurrentMission = new MissionInfo("Quick Start", "Caucasus", "F/A-18C");
        _host.Status.Aircraft = new AircraftState(
            Latitude: 41.5, Longitude: 41.75, AltitudeMslMeters: 3000, AltitudeAglMeters: 2900,
            IndicatedAirspeedMps: 100, TrueAirspeedMps: 110, Mach: 0.33, VerticalSpeedMps: 0,
            MagneticHeadingRadians: Math.PI, Failures: ["GearFailure"]);

        string text = await CallGetAircraftStateAsync();

        Assert.Contains("F/A-18C", text);
        Assert.Contains(expectedAltitude, text);
        Assert.Contains(expectedIas, text);
        Assert.Contains("180°", text);
        Assert.Contains("Gear failure", text);
    }

    [Fact]
    public async Task CallTool_GetAircraftState_WhenDcsNotResponding_SaysSoWithDataAge()
    {
        InjectAircraft();
        _host.Status.LastTelemetryUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(12);
        _host.Status.DcsNotResponding = true;

        string text = await CallGetAircraftStateAsync();

        Assert.StartsWith("DCS not responding", text);
        Assert.Matches(@"last update 1\d s ago", text); // 12 s, allowing for test latency
        Assert.Contains("stale", text);
        Assert.Contains("F/A-18C", text); // held values still shown, clearly labelled
    }

    [Fact]
    public async Task CallTool_GetAircraftState_WhenPaused_NotesIt()
    {
        InjectAircraft();
        _host.Status.DcsPaused = true;

        string text = await CallGetAircraftStateAsync();

        Assert.StartsWith("DCS is paused", text);
        Assert.Contains("F/A-18C", text);
    }

    [Fact]
    public async Task ToolCalls_AreLogged_WithNameAndOutcome()
    {
        await CallGetAircraftStateAsync();

        Assert.Contains(_logs.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("get_aircraft_state") && e.Message.Contains("succeeded"));
    }

    [Fact]
    public async Task ToolArguments_AreNeverLogged()
    {
        // Tool arguments are user/LLM free text that ends up in Lua sent to DCS (see #4) —
        // they must not land in log files.
        await _client.CallToolAsync("send_atc_instruction", new Dictionary<string, object?>
        {
            ["aircraft_callsign"] = "Sentinel Callsign 9-9",
            ["action"] = "Vectors",
            ["heading"] = 90,
        });

        Assert.Contains(_logs.Entries, e => e.Message.Contains("send_atc_instruction")); // the call itself is logged
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains("Sentinel Callsign"));
    }

    private void InjectAircraft()
    {
        _host.Status.CurrentMission = new MissionInfo("Quick Start", "Caucasus", "F/A-18C");
        _host.Status.Aircraft = new AircraftState(1, 2, 3000, 2900, 100, 110, 0.33, 0, 0, []);
    }

    private async Task<string> CallGetAircraftStateAsync()
    {
        CallToolResult result = await _client.CallToolAsync("get_aircraft_state", new Dictionary<string, object?>());
        return result.Content.OfType<TextContentBlock>().First().Text;
    }
}
