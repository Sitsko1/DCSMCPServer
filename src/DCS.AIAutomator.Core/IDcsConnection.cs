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

    /// <summary>Altitude range a vector accepts, meters MSL (100 ft to 66,000 ft); the script enforces the same.</summary>
    public const int MinVectorAltitudeMeters = 30, MaxVectorAltitudeMeters = 20000;

    /// <summary>Shows <paramref name="text"/> to the player on screen (mission scripting's outText).</summary>
    public static Task<DcsCommandResult> ShowMessageAsync(
        this IDcsConnection connection, string text, int seconds = 10, CancellationToken cancellationToken = default) =>
        connection.SendCommandAsync("message", new JsonObject
        {
            ["text"] = text,
            ["seconds"] = Math.Clamp(seconds, 1, MaxMessageSeconds),
        }, cancellationToken);

    /// <summary>
    /// Turns an AI group onto a magnetic heading (degrees, 0-360) at an altitude in meters MSL
    /// (null = its current altitude). DCS refuses groups with a player in them.
    /// </summary>
    public static async Task<(DcsCommandResult Result, VectorResult? Vector)> VectorAsync(
        this IDcsConnection connection, string groupName, double headingDegrees, double? altitudeMeters,
        CancellationToken cancellationToken = default)
    {
        var args = new JsonObject { ["group"] = groupName, ["heading"] = headingDegrees };
        if (altitudeMeters is double alt) args["altitude"] = alt;
        DcsCommandResult result = await connection.SendCommandAsync("vector", args, cancellationToken);
        if (!result.Ok) return (result, null);
        try
        {
            VectorResultTelemetry? t = result.Data?.Deserialize(DcsTelemetryJsonContext.Default.VectorResultTelemetry);
            return (result, new VectorResult(t?.AltMsl, t?.Variation));
        }
        catch (JsonException)
        {
            return (result, new VectorResult(null, null)); // tasked; only the details are unreadable
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
