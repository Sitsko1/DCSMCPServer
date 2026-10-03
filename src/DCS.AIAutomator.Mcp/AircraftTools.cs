using System.ComponentModel;
using DCS.AIAutomator.Core;
using ModelContextProtocol.Server;

namespace DCS.AIAutomator.Mcp;

/// <summary>
/// Read-only aircraft tools. Reads the latest snapshot from <see cref="BridgeStatus"/> (fed by
/// DcsConnection's telemetry) and never talks to DCS itself, so it depends only on the status
/// model — keep it that way (dependencies stay one-directional).
/// </summary>
[McpServerToolType]
public class AircraftTools
{
    private readonly BridgeStatus _status;

    public AircraftTools(BridgeStatus status)
    {
        _status = status;
    }

    [McpServerTool(Name = "get_aircraft_state")]
    [Description("Returns the player's current aircraft in DCS: type, position, altitude, speeds, " +
                 "vertical speed, magnetic heading and active malfunctions, in the user's chosen " +
                 "units (unit names are included with every value).")]
    public string GetAircraftState()
    {
        MissionInfo? mission = _status.CurrentMission;
        AircraftState? a = _status.Aircraft;
        if (mission is null || a is null)
        {
            return "No active aircraft: DCS is not connected, no mission is running, or the player " +
                   "is not in an aircraft (spectating or dead).";
        }

        // Never pass held data off as live.
        string? caveat = null;
        if (_status.DcsNotResponding)
        {
            int ageSeconds = _status.LastTelemetryUtc is { } last
                ? (int)(DateTimeOffset.UtcNow - last).TotalSeconds
                : -1;
            caveat = ageSeconds >= 0
                ? $"DCS not responding; last update {ageSeconds} s ago. The values below are stale (last known)."
                : "DCS not responding. The values below are stale (last known).";
        }
        else if (_status.DcsPaused)
        {
            caveat = "DCS is paused; the values below are held from when it paused.";
        }

        UnitSystem u = _status.Units;
        string state = string.Join('\n',
            $"Aircraft: {mission.Aircraft}",
            $"Position: {AircraftStateFormatter.Position(a.Latitude, a.Longitude)}",
            $"Altitude: {AircraftStateFormatter.Altitude(a.AltitudeMslMeters, u)} MSL, {AircraftStateFormatter.Altitude(a.AltitudeAglMeters, u)} AGL",
            $"Indicated airspeed: {AircraftStateFormatter.Speed(a.IndicatedAirspeedMps, u)}",
            $"True airspeed: {AircraftStateFormatter.Speed(a.TrueAirspeedMps, u)}",
            $"Mach: {AircraftStateFormatter.Mach(a.Mach)}",
            $"Vertical speed: {AircraftStateFormatter.VerticalSpeed(a.VerticalSpeedMps, u)}",
            $"Magnetic heading: {AircraftStateFormatter.Heading(a.MagneticHeadingRadians)}",
            $"Malfunctions: {AircraftStateFormatter.Failures(a.Failures)}",
            $"Units: {(u == UnitSystem.Imperial ? "imperial (ft, kt, ft/min)" : "metric (m, km/h, m/s)")}");
        return caveat is null ? state : caveat + "\n" + state;
    }
}
