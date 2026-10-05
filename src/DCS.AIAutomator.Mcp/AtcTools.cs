using System.ComponentModel;
using System.Text.Json.Serialization;
using DCS.AIAutomator.Core;
using ModelContextProtocol.Server;

namespace DCS.AIAutomator.Mcp;

[JsonConverter(typeof(JsonStringEnumConverter<AtcAction>))]
public enum AtcAction
{
    Vectors,
    ClearToLand,
    Hold,
    Orbit
}

// AtcAction has no source-generated JSON metadata in the SDK's default (reflection-free,
// PublishAot-safe) serializer options — this context supplies it. Merged into the options
// passed to .WithTools<AtcTools>() in the bridge host.
[JsonSerializable(typeof(AtcAction))]
public partial class AtcJsonContext : JsonSerializerContext
{
}

/// <summary>
/// ATC (air traffic control) tool for DCS World. Sends structured commands to the DCS Hooks
/// script (see <see cref="DcsCommands"/>) and reports what DCS actually did. Every action tasks
/// the addressed AI flight (never the player's own), and every instruction is also shown on screen.
/// </summary>
[McpServerToolType]
public class AtcTools
{
    private readonly IDcsConnection _connection;
    private readonly BridgeStatus _status;

    public AtcTools(IDcsConnection connection, BridgeStatus status)
    {
        _connection = connection;
        _status = status;
    }

    /// <summary>Seconds the instruction stays on screen.</summary>
    public const int MessageSeconds = 10;

    [McpServerTool(Name = "send_atc_instruction")]
    [Description("Gives an ATC instruction to a flight in DCS and shows it on screen. Vectors turns the " +
                 "addressed AI flight onto the (magnetic) heading. Orbit circles over its present position. " +
                 "Hold flies a racetrack at its present position with the inbound leg on the (magnetic) heading. " +
                 "These three use the given altitude or the current one. ClearToLand lands it at the named friendly " +
                 "airfield, or the nearest friendly one. " +
                 "Address flights by callsign ('Enfield 1-1') or group name, as list_ai_flights shows them. " +
                 "Reports what DCS actually did.")]
    public async Task<string> SendAtcInstruction(
        [Description("The flight's callsign ('Enfield 1-1') or group name, as list_ai_flights shows them.")] string aircraft_callsign,
        [Description("The instruction.")] AtcAction action,
        [Description("Magnetic heading in degrees (1-360): the heading to fly for Vectors, the inbound leg for Hold. Not used by Orbit.")] double heading = 360,
        [Description("Altitude (Vectors, Orbit, Hold), in the user's units: feet or meters, as get_aircraft_state reports. Omit to keep the current altitude.")] double? altitude = null,
        [Description("Airfield to land at (ClearToLand), e.g. 'Kutaisi'; case-insensitive, a unique part of the name is enough. Omit for the nearest friendly airfield.")] string? airbase = null,
        CancellationToken cancellationToken = default)
    {
        UnitSystem units = _status.Units;
        if (heading is < 0 or > 360) return "Error: heading must be between 0 and 360 degrees.";
        double? altitudeMeters = altitude is double a ? (units == UnitSystem.Imperial ? a / UnitConversion.MetersToFeet(1) : a) : null;
        if (altitudeMeters is < DcsCommands.MinTaskAltitudeMeters or > DcsCommands.MaxTaskAltitudeMeters)
        {
            return $"Error: altitude must be between {AircraftStateFormatter.Altitude(DcsCommands.MinTaskAltitudeMeters, units)} " +
                   $"and {AircraftStateFormatter.Altitude(DcsCommands.MaxTaskAltitudeMeters, units)}.";
        }

        string outcome;
        string? landedAt = null;
        var (list, flights) = await _connection.ListFlightsAsync(cancellationToken);
        if (!list.Ok) return $"Error: couldn't look up the flight: {list.Error}";
        var (flight, error) = AiFlight.Resolve(flights, aircraft_callsign);
        if (flight is null) return $"Error: {error}";

        string who = $"{flight.Callsign} (group \"{flight.GroupName}\")";
        if (flight.IsPlayer)
        {
            outcome = $"{who} is the player's flight, so it wasn't tasked.";
        }
        else
        {
            var (result, task) = action switch
            {
                AtcAction.Vectors => await _connection.VectorAsync(flight.GroupName, heading, altitudeMeters, cancellationToken),
                AtcAction.Orbit => await _connection.OrbitAsync(flight.GroupName, altitudeMeters, cancellationToken),
                AtcAction.Hold => await _connection.HoldAsync(flight.GroupName, heading, altitudeMeters, cancellationToken),
                _ => await _connection.LandAsync(flight.GroupName, airbase, cancellationToken),
            };
            if (!result.Ok) return $"Error: DCS didn't task {flight.Callsign}: {result.Error}";
            string at = $"at {AircraftStateFormatter.Altitude(task?.AltitudeMslMeters, units)} MSL";
            outcome = action switch
            {
                AtcAction.Vectors => $"{who} is turning to heading {heading:000} {at}.",
                AtcAction.Orbit => $"{who} is orbiting its present position {at}.",
                AtcAction.Hold => $"{who} is holding at its present position, inbound heading {heading:000}, {at}.",
                _ => $"{who} is cleared to land at {task?.Airbase ?? "an airfield"} " +
                     $"({AircraftStateFormatter.Distance(task?.DistanceMeters, units)} away).",
            };
            landedAt = task?.Airbase;
            if (action is AtcAction.Vectors or AtcAction.Hold && task?.MagneticVariationDegrees is null)
                outcome += " There was no player aircraft to measure magnetic variation from, so the heading was flown as true.";
        }

        string text = $"ATC to {aircraft_callsign}: " + action switch
        {
            AtcAction.Vectors => $"vectors, fly heading {heading:000}",
            AtcAction.Orbit => "orbit present position",
            AtcAction.Hold => $"hold at present position, inbound heading {heading:000}",
            _ => landedAt is null ? "cleared to land" : $"cleared to land {landedAt}",
        } + (altitudeMeters is double m && action != AtcAction.ClearToLand ? $", altitude {AircraftStateFormatter.Altitude(m, units)}" : "");
        DcsCommandResult shown = await _connection.ShowMessageAsync(text, MessageSeconds, cancellationToken);
        return shown.Ok
            ? $"{outcome} Shown on screen in DCS: \"{text}\""
            : $"{outcome} The on-screen message failed: {shown.Error}";
    }
}
