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
                 "callsign, mission-editor group name, aircraft type, coalition, position and altitude, " +
                 "in the user's chosen units. Groups with a human player are marked as such.")]
    public async Task<string> ListAiFlights(CancellationToken cancellationToken = default)
    {
        var (result, flights) = await _connection.ListFlightsAsync(cancellationToken);
        if (!result.Ok) return $"Error: couldn't list AI flights: {result.Error}";
        if (flights.Count == 0) return "No AI flights in the mission.";

        UnitSystem u = _status.Units;
        return string.Join('\n', flights.Select(f =>
            $"{f.Callsign} (group \"{f.GroupName}\"){(f.IsPlayer ? " [player]" : "")}: {f.Type}, {f.Coalition}, " +
            $"{AircraftStateFormatter.Position(f.Latitude, f.Longitude)}, " +
            $"{AircraftStateFormatter.Altitude(f.AltitudeMslMeters, u)} MSL"));
    }
}
