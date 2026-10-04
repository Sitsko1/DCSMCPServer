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
/// script (see <see cref="DcsCommands"/>) and reports what DCS actually did. For now it only shows
/// the instruction on screen; tasking AI flights comes in #29-#31.
/// </summary>
[McpServerToolType]
public class AtcTools
{
    private readonly IDcsConnection _connection;

    public AtcTools(IDcsConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Seconds the instruction stays on screen.</summary>
    public const int MessageSeconds = 10;

    [McpServerTool(Name = "send_atc_instruction")]
    [Description("Shows an ATC instruction to the player as an on-screen message in DCS. It does not move or task any aircraft. Reports whether DCS displayed it.")]
    public async Task<string> SendAtcInstruction(
        [Description("The flight's callsign, e.g. 'Enfield 1-1'.")] string aircraft_callsign,
        [Description("The instruction.")] AtcAction action,
        [Description("Heading to fly, in degrees (1-360).")] double heading = 360,
        CancellationToken cancellationToken = default)
    {
        string text = $"ATC to {aircraft_callsign}: {Describe(action)}, fly heading {heading:000}";
        DcsCommandResult result = await _connection.ShowMessageAsync(text, MessageSeconds, cancellationToken);
        return result.Ok
            ? $"Shown on screen in DCS: \"{text}\""
            : $"Error: DCS didn't show the instruction: {result.Error}";
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
