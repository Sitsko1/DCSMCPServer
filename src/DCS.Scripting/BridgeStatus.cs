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

    /// <summary>
    /// An error the DCS-side Hooks script reported about itself (already rate-limited there).
    /// Lets the app surface it as a notification without the library knowing about the UI.
    /// Raised on DcsConnection's background thread.
    /// </summary>
    public event EventHandler<string>? DcsScriptError;

    internal void RaiseDcsScriptError(string message) => DcsScriptError?.Invoke(this, message);

    public BridgeStatus()
    {
        McpClients.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Which AI agents have reached the MCP server, and when (recorded by DcsMcpBridgeHost). Its
    /// changes raise <see cref="Changed"/>; Active fades to Idle with time alone, so a UI showing it
    /// also re-renders on a timer.
    /// </summary>
    public McpClientActivity McpClients { get; } = new();

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

    private bool _dcsAuthFailed;
    /// <summary>
    /// DCS's Hooks script rejected the app's link secret, or is an older script that doesn't do
    /// the handshake at all. Either way the fix is "redeploy Lua scripts". Survives the reconnect
    /// loop (each retry fails the same way); cleared by a successful handshake or when DCS can't
    /// be reached at all.
    /// </summary>
    public bool DcsAuthFailed
    {
        get => _dcsAuthFailed;
        set
        {
            if (_dcsAuthFailed == value) return;
            _dcsAuthFailed = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _dcsPaused;
    /// <summary>The DCS simulation is paused (reported by the Hooks script's pause/resume callbacks).</summary>
    public bool DcsPaused
    {
        get => _dcsPaused;
        set
        {
            if (_dcsPaused == value) return;
            _dcsPaused = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _dcsNotResponding;
    /// <summary>
    /// Connected, mid-mission and unpaused, but no line from DCS within the timeout — i.e. DCS is
    /// frozen/hung. The socket stays open (it may recover); last known state is held, not cleared.
    /// </summary>
    public bool DcsNotResponding
    {
        get => _dcsNotResponding;
        set
        {
            if (_dcsNotResponding == value) return;
            _dcsNotResponding = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>When the last line of any kind arrived from DCS; null before the first one.
    /// Deliberately doesn't raise <see cref="Changed"/> (it updates several times a second).</summary>
    public DateTimeOffset? LastTelemetryUtc { get; set; }

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
