namespace DCS.Scripting;

public enum BridgeState
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

/// <summary>
/// Live, observable state of the bridge: whether the MCP server itself is up, whether DCS is
/// connected, and the current mission (if any). Mutated from background tasks (the MCP host,
/// DcsConnection); consumers on a UI thread must marshal <see cref="Changed"/> handling
/// themselves (e.g. via DispatcherQueue) — this class does no thread marshaling of its own.
/// </summary>
public sealed class BridgeStatus
{
    public event EventHandler? Changed;

    private BridgeState _bridgeState = BridgeState.Stopped;
    public BridgeState BridgeState
    {
        get => _bridgeState;
        set
        {
            if (_bridgeState == value) return;
            _bridgeState = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _dcsConnected;
    public bool DcsConnected
    {
        get => _dcsConnected;
        set
        {
            if (_dcsConnected == value) return;
            _dcsConnected = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private MissionInfo? _currentMission;
    public MissionInfo? CurrentMission
    {
        get => _currentMission;
        set
        {
            if (_currentMission == value) return;
            _currentMission = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private AircraftState? _aircraft;
    /// <summary>The player aircraft's latest state (~5 Hz); null when there's no player aircraft.</summary>
    public AircraftState? Aircraft
    {
        get => _aircraft;
        set
        {
            if (_aircraft == value) return;
            _aircraft = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private UnitSystem _units = UnitSystem.Imperial;
    /// <summary>
    /// Display units for aircraft state, for the UI and the get_aircraft_state tool. Set by the
    /// app from its settings; lives here (not in the host's startup parameters) so changing it
    /// takes effect immediately, without restarting the bridge or reconnecting to DCS.
    /// </summary>
    public UnitSystem Units
    {
        get => _units;
        set
        {
            if (_units == value) return;
            _units = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string _mcpEndpoint = "—";
    /// <summary>The MCP server's actual listen URL + route, e.g. "http://127.0.0.1:5270/mcp". Set by DcsMcpBridgeHost.StartAsync so the UI never hardcodes it.</summary>
    public string McpEndpoint
    {
        get => _mcpEndpoint;
        set
        {
            if (_mcpEndpoint == value) return;
            _mcpEndpoint = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string _dcsEndpoint = "—";
    /// <summary>The DCS socket address the bridge is configured to connect to, e.g. "127.0.0.1:1024".</summary>
    public string DcsEndpoint
    {
        get => _dcsEndpoint;
        set
        {
            if (_dcsEndpoint == value) return;
            _dcsEndpoint = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
