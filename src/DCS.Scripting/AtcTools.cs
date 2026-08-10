using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace DCS.Scripting;

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
/// ATC (air traffic control) tool for DCS World, backed by a live socket connection to
/// DCS's Export.lua. Port of the hand-rolled CustomAtcToolHandler onto the ModelContextProtocol SDK.
/// </summary>
[McpServerToolType]
public class AtcTools
{
    private readonly IDcsConnection _connection;

    public AtcTools(IDcsConnection connection)
    {
        _connection = connection;
    }

    [McpServerTool(Name = "send_atc_instruction")]
    [Description("Transmits dynamic flight adjustments, route updates, and safe routing guidance vectors to aircraft.")]
    public string SendAtcInstruction(
        [Description("The exact flight indicator group callsign (e.g., 'Enfield 1-1').")] string aircraft_callsign,
        [Description("The explicit flight operation action.")] AtcAction action,
        [Description("The physical compass direction degrees heading parameters to fly.")] double heading = 360)
    {
        string lua = $"trigger.action.outText(\"ATC to {aircraft_callsign}: Perform {action} fly heading {heading:000}\", 10)\n";
        return _connection.SendLuaCommand(lua)
            ? "ATC instruction broadcasted successfully."
            : "Error: DCS interface is down.";
    }
}
