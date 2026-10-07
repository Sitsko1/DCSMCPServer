using System.Text.Json;
using System.Text.Json.Nodes;

namespace DCS.AIAutomator.Core;

/// <summary>
/// The DCS-facing side of a tool: send a structured command to the Hooks script and wait for
/// its result. Exists so tool classes (e.g. AtcTools) can be unit tested against a fake, without
/// a real DCS instance or TCP socket.
/// </summary>
public interface IDcsConnection
{
    /// <summary>
    /// Sends <c>{"cmd":<paramref name="cmd"/>,"id":..., ...args}</c> and waits for the script's
    /// matching <c>commandResult</c>. Never throws for DCS-side problems: no connection, a
    /// timeout, a disconnect or a rejected command all come back as a failed result.
    /// </summary>
    Task<DcsCommandResult> SendCommandAsync(string cmd, JsonObject args, CancellationToken cancellationToken = default);
}

/// <param name="Ok">DCS ran the command.</param>
/// <param name="Error">Why it didn't (from DCS, or the app's own reason such as a timeout).</param>
/// <param name="Data">What a query command returned (e.g. listFlights' array); null for the others.</param>
public sealed record DcsCommandResult(bool Ok, string? Error = null, JsonElement? Data = null)
{
    public static DcsCommandResult Failed(string error) => new(false, error);
}

/// <summary>
/// One typed method per command the Hooks script handles (its <c>mcpBridgeCommands</c> table).
/// Adding a capability means a handler there and a method here — never Lua sent over the link.
/// </summary>
public static class DcsCommands
{
    /// <summary>Longest an on-screen message may stay up; the script enforces the same range.</summary>
    public const int MaxMessageSeconds = 60;

    /// <summary>Altitude range AI tasking accepts, meters MSL (100 ft to 66,000 ft); the script enforces the same.</summary>
    public const int MinTaskAltitudeMeters = 30, MaxTaskAltitudeMeters = 20000;

    /// <summary>Length of a hold's racetrack legs, meters (about 5.4 nm).</summary>
    public const int HoldLegMeters = 10000;

    /// <summary>Shows <paramref name="text"/> to the player on screen (mission scripting's outText).</summary>
    public static Task<DcsCommandResult> ShowMessageAsync(
        this IDcsConnection connection, string text, int seconds = 10, CancellationToken cancellationToken = default) =>
        connection.SendCommandAsync("message", new JsonObject
        {
            ["text"] = text,
            ["seconds"] = Math.Clamp(seconds, 1, MaxMessageSeconds),
        }, cancellationToken);

    /// <summary>Pauses or resumes the simulation (<c>Sim.setPause</c>; needs a running mission).</summary>
    public static Task<DcsCommandResult> SetPausedAsync(
        this IDcsConnection connection, bool paused, CancellationToken cancellationToken = default) =>
        connection.SendCommandAsync("pause", new JsonObject { ["paused"] = paused }, cancellationToken);

    /// <summary>
    /// Asks DCS to exit cleanly (<c>Sim.exitProcess</c>). The script replies first, then exits, so
    /// an ok means "exiting", not "exited": wait for the process with <see cref="DcsProcess"/>.
    /// </summary>
    public static Task<DcsCommandResult> QuitAsync(this IDcsConnection connection, CancellationToken cancellationToken = default) =>
        connection.SendCommandAsync("quit", new JsonObject(), cancellationToken);

    // AI tasking: headings are magnetic degrees (0-360), altitudes meters MSL (null = the group's
    // current altitude). DCS refuses groups with a player in them.

    /// <summary>Turns an AI group onto a heading.</summary>
    public static Task<(DcsCommandResult Result, TaskResult? Task)> VectorAsync(
        this IDcsConnection connection, string groupName, double headingDegrees, double? altitudeMeters,
        CancellationToken cancellationToken = default) =>
        connection.TaskAsync("vector", groupName, headingDegrees, altitudeMeters, cancellationToken);

    /// <summary>Has an AI group circle over its present position.</summary>
    public static Task<(DcsCommandResult Result, TaskResult? Task)> OrbitAsync(
        this IDcsConnection connection, string groupName, double? altitudeMeters,
        CancellationToken cancellationToken = default) =>
        connection.TaskAsync("orbit", groupName, null, altitudeMeters, cancellationToken);

    /// <summary>Has an AI group fly a racetrack at its present position, inbound on the heading.</summary>
    public static Task<(DcsCommandResult Result, TaskResult? Task)> HoldAsync(
        this IDcsConnection connection, string groupName, double inboundHeadingDegrees, double? altitudeMeters,
        CancellationToken cancellationToken = default) =>
        connection.TaskAsync("hold", groupName, inboundHeadingDegrees, altitudeMeters, cancellationToken);

    /// <summary>Lands an AI group at a friendly airfield: <paramref name="airbase"/> by name
    /// (case-insensitive, or a unique partial match), or the nearest friendly one when null.</summary>
    public static Task<(DcsCommandResult Result, TaskResult? Task)> LandAsync(
        this IDcsConnection connection, string groupName, string? airbase,
        CancellationToken cancellationToken = default) =>
        connection.TaskAsync("land", groupName, null, null, cancellationToken,
            string.IsNullOrWhiteSpace(airbase) ? null : airbase.Trim());

    private static async Task<(DcsCommandResult Result, TaskResult? Task)> TaskAsync(
        this IDcsConnection connection, string cmd, string groupName, double? headingDegrees, double? altitudeMeters,
        CancellationToken cancellationToken, string? airbase = null)
    {
        var args = new JsonObject { ["group"] = groupName };
        if (airbase is not null) args["airbase"] = airbase;
        if (headingDegrees is double heading) args["heading"] = heading;
        if (altitudeMeters is double alt) args["altitude"] = alt;
        DcsCommandResult result = await connection.SendCommandAsync(cmd, args, cancellationToken);
        if (!result.Ok) return (result, null);
        try
        {
            TaskResultTelemetry? t = result.Data?.Deserialize(DcsTelemetryJsonContext.Default.TaskResultTelemetry);
            return (result, new TaskResult(t?.AltMsl, t?.Variation, t?.Airbase, t?.Distance));
        }
        catch (JsonException)
        {
            return (result, new TaskResult(null, null)); // tasked; only the details are unreadable
        }
    }

    /// <summary>Every AI air group (aircraft and helicopters) in the running mission.</summary>
    public static async Task<(DcsCommandResult Result, IReadOnlyList<AiFlight> Flights)> ListFlightsAsync(
        this IDcsConnection connection, CancellationToken cancellationToken = default)
    {
        DcsCommandResult result = await connection.SendCommandAsync("listFlights", new JsonObject(), cancellationToken);
        if (!result.Ok) return (result, []);
        try
        {
            List<AiFlightTelemetry>? flights = result.Data?.Deserialize(DcsTelemetryJsonContext.Default.ListAiFlightTelemetry);
            return (result, flights?.Select(AiFlight.From).ToList() ?? []);
        }
        catch (JsonException)
        {
            return (DcsCommandResult.Failed("DCS sent a flight list the app couldn't read."), []);
        }
    }
}
