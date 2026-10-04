using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DCS.AIAutomator.Core;

/// <summary>An AI air group in the running mission, as list_ai_flights reports it.</summary>
/// <param name="GroupName">The mission editor's group name (unique in a mission).</param>
/// <param name="Callsign">The lead unit's callsign as spoken ("Enfield 1-1"), or DCS's own string when it isn't in that form.</param>
/// <param name="Type">DCS's display name for the lead unit's type.</param>
/// <param name="IsPlayer">A player is in this group, so it can't be tasked.</param>
public sealed partial record AiFlight(
    string GroupName, string Callsign, string Type, string Coalition,
    double? Latitude, double? Longitude, double? AltitudeMslMeters, bool IsPlayer)
{
    internal static AiFlight From(AiFlightTelemetry t) => new(
        t.Group ?? "Unknown", SpokenCallsign(t.Callsign), t.Type ?? "Unknown", CoalitionName(t.Coalition),
        t.Lat, t.Lon, t.AltMsl, t.Player == true);

    /// <summary>"Enfield11" → "Enfield 1-1" (flight 1, aircraft 1). Anything else, e.g. numeric
    /// Russian callsigns, is shown as DCS sent it.</summary>
    public static string SpokenCallsign(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "none";
        Match m = WesternCallsign().Match(raw.Trim());
        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}-{m.Groups[3].Value}" : raw.Trim();
    }

    // DCS's coalition.side values.
    private static string CoalitionName(int? side) => side switch
    {
        0 => "neutral",
        1 => "red",
        2 => "blue",
        _ => "unknown",
    };

    [GeneratedRegex(@"^([A-Za-z]+)\s*(\d)\s*-?\s*(\d)$")]
    private static partial Regex WesternCallsign();
}

/// <summary>One element of listFlights' result data, in DCS's SI units (m).</summary>
public sealed class AiFlightTelemetry
{
    [JsonPropertyName("group")] public string? Group { get; set; }
    [JsonPropertyName("callsign")] public string? Callsign { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("coalition")] public int? Coalition { get; set; }
    [JsonPropertyName("lat")] public double? Lat { get; set; }
    [JsonPropertyName("lon")] public double? Lon { get; set; }
    [JsonPropertyName("altMsl")] public double? AltMsl { get; set; }
    [JsonPropertyName("player")] public bool? Player { get; set; }
}
