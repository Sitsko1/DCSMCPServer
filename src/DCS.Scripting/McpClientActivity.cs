namespace DCS.Scripting;

public enum McpClientState
{
    NoClients,
    Active,
    Idle,
    AuthFailed,
}

public sealed record McpClientSnapshot(McpClientState State, IReadOnlyList<string> ClientNames, DateTimeOffset? LastSeenUtc);

/// <summary>
/// Which MCP clients (AI agents) have reached the bridge, and when. The server is stateless, so
/// there's no connect/disconnect to observe: a client is <see cref="McpClientState.Active"/>
/// while it has made a request within <see cref="ActiveWindow"/>, and
/// <see cref="McpClientState.Idle"/> after that. A rejected key (401) shows as
/// <see cref="McpClientState.AuthFailed"/> until a request succeeds or the window passes. Time is
/// passed in, so callers re-evaluate on a timer. Thread-safe: recorded from request threads.
/// </summary>
public sealed class McpClientActivity
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSeenByName = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastAuthFailureUtc;

    /// <summary>Raised on the recording (request) thread after every record or reset.</summary>
    public event EventHandler? Changed;

    /// <param name="clientName">The client's MCP <c>clientInfo.name</c>; null when this request didn't
    /// carry it (counts as activity, adds no name).</param>
    public void RecordRequest(string? clientName, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            // ponytail: grows by one entry per distinct client name; a handful in practice.
            if (!string.IsNullOrEmpty(clientName)) _lastSeenByName[clientName] = nowUtc;
            _lastSuccessUtc = nowUtc;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordAuthFailure(DateTimeOffset nowUtc)
    {
        lock (_gate) _lastAuthFailureUtc = nowUtc;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets everything (bridge restart).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _lastSeenByName.Clear();
            _lastSuccessUtc = null;
            _lastAuthFailureUtc = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public McpClientSnapshot Snapshot(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            bool authFailed = _lastAuthFailureUtc is { } failed
                && nowUtc - failed <= ActiveWindow
                && (_lastSuccessUtc is null || _lastSuccessUtc < failed);

            McpClientState state =
                authFailed ? McpClientState.AuthFailed
                : _lastSuccessUtc is null ? McpClientState.NoClients
                : nowUtc - _lastSuccessUtc <= ActiveWindow ? McpClientState.Active
                : McpClientState.Idle;

            string[] names = _lastSeenByName.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
            return new McpClientSnapshot(state, names, _lastSuccessUtc);
        }
    }
}
