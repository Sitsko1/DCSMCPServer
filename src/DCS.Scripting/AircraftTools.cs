using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DCS.Scripting;

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

        UnitSystem u = _status.Units;
        return string.Join('\n',
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
    }
}
