using System;
using DCS.Scripting;

namespace DCS.AIAutomator;

/// <summary>
/// Translates <see cref="BridgeStatus"/> state transitions into user-facing notifications. The
/// status object exposes a single coarse <see cref="BridgeStatus.Changed"/> event, so this class
/// tracks the previous value of each observable and raises a notification only on an actual
/// transition (bridge start/stop/fault, DCS connect/disconnect, mission start).
/// </summary>
public sealed class BridgeStatusNotifier
{
    private readonly BridgeStatus _status;
    private readonly NotificationService _notifications;

    private BridgeState _lastBridgeState;
    private bool _lastDcsConnected;
    private string? _lastMissionName;

    public BridgeStatusNotifier(BridgeStatus status, NotificationService notifications)
    {
        _status = status;
        _notifications = notifications;

        _lastBridgeState = status.BridgeState;
        _lastDcsConnected = status.DcsConnected;
        _lastMissionName = status.CurrentMission?.MissionName;

        status.Changed += OnStatusChanged;
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
                    _notifications.Show("Bridge faulted", "The MCP server failed to start.", NotificationSeverity.Error);
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
