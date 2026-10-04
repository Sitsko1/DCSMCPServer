using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCS.AIAutomator.Core;

/// <summary>
/// Wire contract for DCS-side telemetry: one JSON object per line, written by the Hooks script
/// (LuaHooksScriptGenerator). Keep the two in sync by hand and bump
/// LuaHooksScriptGenerator.ProtocolVersion on any change; the unit tests' DcsWireSamples check
/// both sides' field names against shared sample lines.
/// </summary>
public sealed class DcsTelemetryMessage
{
    /// <summary>
    /// Present only on mission reports. Must stay nullable: heartbeat/pause lines omit it, and
    /// reading "absent" as false would clear the mission on every heartbeat.
    /// </summary>
    [JsonPropertyName("missionActive")]
    public bool? MissionActive { get; set; }

    /// <summary>Keep-alive sent ~1/s on real time during simulation; its arrival is all that matters.</summary>
    [JsonPropertyName("heartbeat")]
    public bool? Heartbeat { get; set; }

    /// <summary>Sent on simulation pause (true) and resume (false).</summary>
    [JsonPropertyName("paused")]
    public bool? Paused { get; set; }

    /// <summary>A message from the Hooks script's own log (mcpBridgeLog), forwarded to the app.</summary>
    [JsonPropertyName("log")]
    public DcsLogTelemetry? Log { get; set; }

    /// <summary>The Hooks script accepted the app's link secret (reply to its AUTH line).</summary>
    [JsonPropertyName("authOk")]
    public bool? AuthOk { get; set; }

    /// <summary>The script's wire contract version, sent with authOk; absent from scripts that
    /// predate versioning. See LuaHooksScriptGenerator.ProtocolVersion.</summary>
    [JsonPropertyName("protocol")]
    public int? Protocol { get; set; }

    /// <summary>The script's reply to one command the app sent, matched by id.</summary>
    [JsonPropertyName("commandResult")]
    public DcsCommandResultTelemetry? CommandResult { get; set; }

    /// <summary>The Hooks script rejected the app's link secret; it closes the connection next.</summary>
    [JsonPropertyName("authError")]
    public bool? AuthError { get; set; }

    [JsonPropertyName("missionName")]
    public string? MissionName { get; set; }

    [JsonPropertyName("terrain")]
    public string? Terrain { get; set; }

    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; set; }

    /// <summary>Absent when there's no player aircraft (spectator, dead, menus).</summary>
    [JsonPropertyName("ownship")]
    public OwnshipTelemetry? Ownship { get; set; }
}

public sealed class DcsCommandResultTelemetry
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
}

public sealed class DcsLogTelemetry
{
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>Player aircraft state in DCS's SI units; see <see cref="AircraftState"/>.</summary>
public sealed class OwnshipTelemetry
{
    [JsonPropertyName("lat")] public double? Lat { get; set; }
    [JsonPropertyName("lon")] public double? Lon { get; set; }
    [JsonPropertyName("altMsl")] public double? AltMsl { get; set; }
    [JsonPropertyName("altAgl")] public double? AltAgl { get; set; }
    [JsonPropertyName("ias")] public double? Ias { get; set; }
    [JsonPropertyName("tas")] public double? Tas { get; set; }
    [JsonPropertyName("mach")] public double? Mach { get; set; }
    [JsonPropertyName("vs")] public double? Vs { get; set; }
    [JsonPropertyName("hdg")] public double? Hdg { get; set; }
    [JsonPropertyName("failures")] public List<string>? Failures { get; set; }
}

[JsonSerializable(typeof(DcsTelemetryMessage))]
[JsonSerializable(typeof(List<AiFlightTelemetry>))]
[JsonSerializable(typeof(VectorResultTelemetry))]
internal partial class DcsTelemetryJsonContext : JsonSerializerContext
{
}

/// <summary>One parsed line from DCS.</summary>
/// <param name="IsMissionReport">The line carried "missionActive" — only these change the mission/aircraft state.</param>
/// <param name="Mission">For a mission report: the active mission, or null for "no mission".</param>
/// <param name="Aircraft">For a mission report: the player aircraft, or null when there isn't one.</param>
/// <param name="Paused">Set by pause/resume lines; null when the line says nothing about pausing.</param>
/// <param name="Log">A forwarded Hooks-script log message, if this line carries one.</param>
/// <param name="AuthOk">The script accepted the link secret.</param>
/// <param name="AuthError">The script rejected the link secret.</param>
/// <param name="Protocol">The script's wire contract version (sent with AuthOk); null if it didn't say.</param>
/// <param name="CommandResult">The reply to a command, with the id it answers.</param>
public sealed record DcsLine(
    bool IsMissionReport, MissionInfo? Mission, AircraftState? Aircraft, bool? Paused,
    DcsLogEntry? Log = null, bool AuthOk = false, bool AuthError = false, int? Protocol = null,
    (long Id, DcsCommandResult Result)? CommandResult = null);

/// <param name="Level">"info", "warning" or "error", as the Hooks script reports it.</param>
public sealed record DcsLogEntry(string Level, string Message)
{
    public bool IsError => Level.Equals("error", StringComparison.OrdinalIgnoreCase);
}

public static class DcsTelemetryParser
{
    // DCS reports the map as its internal theatre ID (mission.theatre); these are the ones whose
    // ID differs from the name DCS sells the map under. Unlisted IDs are shown as-is.
    private static readonly Dictionary<string, string> TerrainNames = new()
    {
        ["PersianGulf"] = "Persian Gulf",
        ["MarianaIslands"] = "Mariana Islands",
        ["MarianaIslandsWWII"] = "Mariana Islands WWII",
        ["SinaiMap"] = "Sinai",
        ["Falklands"] = "South Atlantic",
        ["TheChannel"] = "The Channel",
        ["GermanyCW"] = "Cold War Germany",
    };

    private static string TerrainDisplayName(string theatre) =>
        TerrainNames.TryGetValue(theatre, out string? name) ? name : theatre;

    /// <summary>
    /// Parses one line from DCS. Returns false for garbage/malformed input (caller should leave
    /// prior state untouched). Heartbeat and pause lines parse fine but aren't mission reports,
    /// so they must not touch the mission/aircraft state.
    /// </summary>
    public static bool TryParse(string line, [NotNullWhen(true)] out DcsLine? parsed)
    {
        parsed = null;

        DcsTelemetryMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(line, DcsTelemetryJsonContext.Default.DcsTelemetryMessage);
        }
        catch (JsonException)
        {
            return false;
        }

        if (message is null)
        {
            return false;
        }

        MissionInfo? mission = null;
        AircraftState? aircraft = null;
        if (message.MissionActive == true)
        {
            mission = new MissionInfo(
                message.MissionName ?? "Unknown",
                TerrainDisplayName(message.Terrain ?? "Unknown"),
                message.Aircraft ?? "Unknown");

            if (message.Ownship is { } o)
            {
                aircraft = new AircraftState(o.Lat, o.Lon, o.AltMsl, o.AltAgl, o.Ias, o.Tas, o.Mach, o.Vs, o.Hdg, o.Failures);
            }
        }

        DcsLogEntry? log = message.Log is { Message: { } text } l ? new DcsLogEntry(l.Level ?? "info", text) : null;
        // A reply without an id can't be matched to anything, so it's dropped.
        (long, DcsCommandResult)? commandResult = message.CommandResult is { Id: long id } r
            ? (id, r.Ok == true ? new DcsCommandResult(true, Data: r.Data) : DcsCommandResult.Failed(r.Error ?? "DCS rejected the command."))
            : null;
        parsed = new DcsLine(message.MissionActive.HasValue, mission, aircraft, message.Paused, log,
            AuthOk: message.AuthOk == true, AuthError: message.AuthError == true, Protocol: message.Protocol,
            CommandResult: commandResult);
        return true;
    }
}
