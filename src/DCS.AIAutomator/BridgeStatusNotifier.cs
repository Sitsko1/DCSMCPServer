using System;
using System.Collections.Generic;
using DCS.Scripting;

namespace DCS.AIAutomator;

/// <summary>
/// Translates <see cref="BridgeStatus"/> state transitions into user-facing notifications. The
/// status object exposes a single coarse <see cref="BridgeStatus.Changed"/> event, so this class
/// tracks the previous value of each observable and raises a notification only on an actual
/// transition (bridge start/stop/fault, DCS connect/disconnect, DCS not responding/recovered,
/// mission start). Pause/resume deliberately raise nothing — the annunciator shows it.
/// </summary>
public sealed class BridgeStatusNotifier
{
    private readonly BridgeStatus _status;
    private readonly NotificationService _notifications;

    private BridgeState _lastBridgeState;
    private bool _lastDcsConnected;
    private bool _lastDcsNotResponding;
    private string? _lastMissionName;

    // DCS script errors are already rate-limited per message in Lua (10 s); this caps toasts
    // further so a burst of different errors can't flood the screen.
    private static readonly TimeSpan ScriptErrorToastInterval = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, DateTimeOffset> _lastScriptErrorToast = new();

    public BridgeStatusNotifier(BridgeStatus status, NotificationService notifications)
    {
        _status = status;
        _notifications = notifications;

        _lastBridgeState = status.BridgeState;
        _lastDcsConnected = status.DcsConnected;
        _lastDcsNotResponding = status.DcsNotResponding;
        _lastMissionName = status.CurrentMission?.MissionName;

        status.Changed += OnStatusChanged;
        status.DcsScriptError += OnDcsScriptError;
    }

    // Raised on DcsConnection's background thread; NotificationService.Show marshals to the UI.
    private void OnDcsScriptError(object? sender, string message)
    {
        lock (_lastScriptErrorToast)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_lastScriptErrorToast.TryGetValue(message, out DateTimeOffset last) && now - last < ScriptErrorToastInterval) return;
            if (_lastScriptErrorToast.Count > 100) _lastScriptErrorToast.Clear();
            _lastScriptErrorToast[message] = now;
        }
        _notifications.Show("DCS script error", message, NotificationSeverity.Error);
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (_status.BridgeState != _lastBridgeState)
        {
            _lastBridgeState = _status.BridgeState;
            switch (_status.BridgeState)
            {
                case BridgeState.Starting:
                    _notifications.Show("Bridge starting", "Starting the MCP server...", NotificationSeverity.Info);
                    break;
                case BridgeState.Running:
                    _notifications.Show("Bridge running", $"MCP server listening at {_status.McpEndpoint}", NotificationSeverity.Success);
                    break;
                case BridgeState.Faulted:
                    _notifications.Show("Bridge faulted", "The MCP server failed to start. The log has the reason.", NotificationSeverity.Error);
                    break;
                case BridgeState.Stopped:
                    _notifications.Show("Bridge stopped", "The MCP server has stopped.", NotificationSeverity.Info);
                    break;
            }
        }

        if (_status.DcsConnected != _lastDcsConnected)
        {
            _lastDcsConnected = _status.DcsConnected;
            if (_status.DcsConnected)
            {
                _notifications.Show("DCS connected", $"Connected to {_status.DcsEndpoint}", NotificationSeverity.Success);
            }
            else
            {
                _notifications.Show("DCS disconnected", "Connection to DCS was lost.", NotificationSeverity.Warning);
            }
        }

        if (_status.DcsNotResponding != _lastDcsNotResponding)
        {
            _lastDcsNotResponding = _status.DcsNotResponding;
            if (_status.DcsNotResponding)
            {
                _notifications.Show("DCS not responding", "No data from DCS mid-mission. Showing last known values; waiting for it to recover.", NotificationSeverity.Warning);
            }
            else if (_status.DcsConnected)
            {
                // Recovered on the same connection (a disconnect clears the flag too, but that
                // already has its own "DCS disconnected" notification).
                _notifications.Show("DCS responding again", "Live data from DCS has resumed.", NotificationSeverity.Success);
            }
        }

        string? missionName = _status.CurrentMission?.MissionName;
        if (missionName != _lastMissionName)
        {
            _lastMissionName = missionName;
            if (missionName is not null)
            {
                _notifications.Show("Mission started", missionName, NotificationSeverity.Info);
            }
        }
    }
}
