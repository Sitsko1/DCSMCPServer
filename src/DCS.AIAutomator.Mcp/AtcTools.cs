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
/// script (see <see cref="DcsCommands"/>) and reports what DCS actually did. Vectors task the
/// addressed AI flight; every instruction is also shown on screen. Hold, Orbit and ClearToLand
/// are message-only until #30/#31.
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
                 "addressed AI flight onto the (magnetic) heading, at the given altitude or its current one. " +
                 "Hold, Orbit and ClearToLand are only shown on screen for now. Address flights by callsign " +
                 "('Enfield 1-1') or group name, as list_ai_flights shows them. Reports what DCS actually did.")]
    public async Task<string> SendAtcInstruction(
        [Description("The flight's callsign ('Enfield 1-1') or group name, as list_ai_flights shows them.")] string aircraft_callsign,
        [Description("The instruction.")] AtcAction action,
        [Description("Magnetic heading to fly, in degrees (1-360).")] double heading = 360,
        [Description("Altitude to fly (Vectors only), in the user's units: feet or meters, as get_aircraft_state reports. Omit to keep the current altitude.")] double? altitude = null,
        CancellationToken cancellationToken = default)
    {
        UnitSystem units = _status.Units;
        if (heading is < 0 or > 360) return "Error: heading must be between 0 and 360 degrees.";
        double? altitudeMeters = altitude is double a ? (units == UnitSystem.Imperial ? a / UnitConversion.MetersToFeet(1) : a) : null;
        if (altitudeMeters is < DcsCommands.MinVectorAltitudeMeters or > DcsCommands.MaxVectorAltitudeMeters)
        {
            return $"Error: altitude must be between {AircraftStateFormatter.Altitude(DcsCommands.MinVectorAltitudeMeters, units)} " +
                   $"and {AircraftStateFormatter.Altitude(DcsCommands.MaxVectorAltitudeMeters, units)}.";
        }

        string outcome;
        if (action == AtcAction.Vectors)
        {
            var (list, flights) = await _connection.ListFlightsAsync(cancellationToken);
            if (!list.Ok) return $"Error: couldn't look up the flight: {list.Error}";
            var (flight, error) = AiFlight.Resolve(flights, aircraft_callsign);
            if (flight is null) return $"Error: {error}";

            if (flight.IsPlayer)
            {
                outcome = $"{flight.Callsign} (group \"{flight.GroupName}\") is the player's flight, so it wasn't tasked.";
            }
            else
            {
                var (result, vector) = await _connection.VectorAsync(flight.GroupName, heading, altitudeMeters, cancellationToken);
                if (!result.Ok) return $"Error: DCS didn't vector {flight.Callsign}: {result.Error}";
                outcome = $"{flight.Callsign} (group \"{flight.GroupName}\") is turning to heading {heading:000} " +
                          $"at {AircraftStateFormatter.Altitude(vector?.AltitudeMslMeters, units)} MSL." +
                          (vector?.MagneticVariationDegrees is null
                              ? " There was no player aircraft to measure magnetic variation from, so the heading was flown as true."
                              : "");
            }
        }
        else
        {
            outcome = $"{action} doesn't task aircraft yet, so no flight was moved.";
        }

        string text = $"ATC to {aircraft_callsign}: {Describe(action)}, fly heading {heading:000}" +
                      (altitudeMeters is double m ? $", altitude {AircraftStateFormatter.Altitude(m, units)}" : "");
        DcsCommandResult shown = await _connection.ShowMessageAsync(text, MessageSeconds, cancellationToken);
        return shown.Ok
            ? $"{outcome} Shown on screen in DCS: \"{text}\""
            : $"{outcome} The on-screen message failed: {shown.Error}";
    }

    private static string Describe(AtcAction action) => action switch
    {
        AtcAction.Vectors => "vectors",
        AtcAction.ClearToLand => "cleared to land",
        AtcAction.Hold => "hold",
        AtcAction.Orbit => "orbit",
        _ => action.ToString(),
    };
}
