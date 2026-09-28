using DCS.Scripting;
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

    private DcsMcpBridgeHost _host = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = new DcsMcpBridgeHost();
        await _host.StartAsync(ListenUrl, dcsPort: UnusedDcsPort);

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

    private async Task<string> CallGetAircraftStateAsync()
    {
        CallToolResult result = await _client.CallToolAsync("get_aircraft_state", new Dictionary<string, object?>());
        return result.Content.OfType<TextContentBlock>().First().Text;
    }
}
