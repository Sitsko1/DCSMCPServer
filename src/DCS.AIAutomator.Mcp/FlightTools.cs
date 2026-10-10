using System.ComponentModel;
using DCS.AIAutomator.Core;
using ModelContextProtocol.Server;

namespace DCS.AIAutomator.Mcp;

/// <summary>
/// Discovery of AI flights in the running mission, so an LLM knows which callsigns and group
/// names exist before it addresses one (send_atc_instruction, AI tasking in #29-#31).
/// </summary>
[McpServerToolType]
public class FlightTools
{
    private readonly IDcsConnection _connection;
    private readonly BridgeStatus _status;

    public FlightTools(IDcsConnection connection, BridgeStatus status)
    {
        _connection = connection;
        _status = status;
    }

    [McpServerTool(Name = "list_ai_flights")]
    [Description("Lists the AI air groups (aircraft and helicopters) in the running DCS mission: " +
                 "flight callsign, mission-editor group name, live/initial aircraft count, aircraft type and " +
                 "coalition, then each live aircraft's callsign, position and altitude, in the user's chosen units. " +
                 "Groups and aircraft with a human player are marked as such. Instructions address a whole flight.")]
    public async Task<string> ListAiFlights(CancellationToken cancellationToken = default)
    {
        var (result, flights) = await _connection.ListFlightsAsync(cancellationToken);
        if (!result.Ok) return $"Error: couldn't list AI flights: {result.Error}";
        if (flights.Count == 0) return "No AI flights in the mission.";

        UnitSystem u = _status.Units;
        return string.Join('\n', flights.Select(f =>
            $"{f.FlightCallsign} (group \"{f.GroupName}\"){Player(f.IsPlayer)}: " +
            $"{f.Members.Count}/{f.InitialSize?.ToString() ?? "?"}, {f.Type}, {f.Coalition}" +
            string.Concat(f.Members.Select(m =>
                $"\n  {m.Callsign}{Player(m.IsPlayer)}: {AircraftStateFormatter.Position(m.Latitude, m.Longitude)}, " +
                $"{AircraftStateFormatter.Altitude(m.AltitudeMslMeters, u)} MSL"))));

        static string Player(bool isPlayer) => isPlayer ? " [player]" : "";
    }
}
