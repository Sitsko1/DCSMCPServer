using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DCS.AIAutomator.Core;

/// <summary>An AI air group in the running mission, as list_ai_flights reports it.</summary>
/// <param name="GroupName">The mission editor's group name (unique in a mission).</param>
/// <param name="InitialSize">Aircraft the group started with; null if DCS didn't say.</param>
/// <param name="Members">Its live aircraft, lead first.</param>
public sealed partial record AiFlight(
    string GroupName, string Coalition, int? InitialSize, IReadOnlyList<AiFlightMember> Members)
{
    /// <summary>The lead's callsign as spoken ("Enfield 1-1").</summary>
    public string Callsign => Members.Count > 0 ? Members[0].Callsign : "none";

    /// <summary>The flight's callsign: "Enfield 1" for a lead "Enfield 1-1" (or for a wingman leading
    /// once the lead is lost). Anything else, e.g. a numeric callsign, is the lead's.</summary>
    public string FlightCallsign
    {
        get
        {
            Match m = SpokenForm().Match(Callsign);
            return m.Success ? m.Groups[1].Value : Callsign;
        }
    }

    // ponytail: the lead's type stands for the group; mission-editor air groups are one type.
    /// <summary>DCS's display name for the lead's type.</summary>
    public string Type => Members.Count > 0 ? Members[0].Type : "Unknown";

    /// <summary>A player is in this group, so it can't be tasked.</summary>
    public bool IsPlayer => Members.Any(m => m.IsPlayer);

    internal static AiFlight From(AiFlightTelemetry t) => new(
        t.Group ?? "Unknown", CoalitionName(t.Coalition), t.InitialSize,
        (t.Units ?? []).Select(u => new AiFlightMember(
            SpokenCallsign(u.Callsign), u.Type ?? "Unknown", u.Lat, u.Lon, u.AltMsl, u.Player == true)).ToList());

    /// <summary>"Enfield11" → "Enfield 1-1" (flight 1, aircraft 1). Anything else, e.g. numeric
    /// Russian callsigns, is shown as DCS sent it.</summary>
    public static string SpokenCallsign(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "none";
        Match m = WesternCallsign().Match(raw.Trim());
        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}-{m.Groups[3].Value}" : raw.Trim();
    }

    /// <summary>
    /// Finds the flight an instruction is addressed to: an exact group name first (unique in a
    /// mission), else the flight's or its lead's callsign in any form ("Enfield 1", "Enfield 1-1",
    /// "Enfield11"). Null with an error listing candidates when there's no match or the callsign is
    /// shared, and for a wingman's callsign: DCS tasks whole groups, and splitting one aircraft off
    /// is a later #46 slice.
    /// </summary>
    public static (AiFlight? Flight, string? Error) Resolve(IReadOnlyList<AiFlight> flights, string name)
    {
        string wanted = name.Trim();
        if (flights.FirstOrDefault(f => f.GroupName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } byGroup)
            return (byGroup, null);

        string spoken = SpokenCallsign(wanted);
        List<AiFlight> byCallsign = flights.Where(f =>
            f.Callsign.Equals(spoken, StringComparison.OrdinalIgnoreCase) ||
            f.FlightCallsign.Equals(spoken, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byCallsign.Count == 1) return (byCallsign[0], null);
        if (byCallsign.Count > 1)
            return (null, $"Callsign {spoken} is shared by several flights: {string.Join(", ", byCallsign.Select(f => $"group \"{f.GroupName}\""))}. Use the group name instead.");

        if (flights.FirstOrDefault(f => f.Members.Any(m => m.Callsign.Equals(spoken, StringComparison.OrdinalIgnoreCase))) is { } wingmans)
            return (null, $"{spoken} flies in {wingmans.FlightCallsign} (group \"{wingmans.GroupName}\"), and a single aircraft can't be " +
                          $"tasked on its own yet. Address the whole flight by its lead, {wingmans.Callsign}, or its group name.");

        string known = flights.Count == 0
            ? "There are no AI flights in the mission."
            : "Known flights: " + string.Join(", ", flights.Take(MaxCandidates).Select(f => $"{f.Callsign} (group \"{f.GroupName}\")"))
              + (flights.Count > MaxCandidates ? $", and {flights.Count - MaxCandidates} more (see list_ai_flights)." : ".");
        return (null, $"No flight called \"{wanted}\". {known}");
    }

    private const int MaxCandidates = 10;

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

    // A callsign already in spoken form: "Enfield 1-1" → flight "Enfield 1".
    [GeneratedRegex(@"^(.+ \d)-\d$")]
    private static partial Regex SpokenForm();
}

/// <summary>One live aircraft of a flight.</summary>
/// <param name="Callsign">As spoken ("Enfield 1-2"), or DCS's own string when it isn't in that form.</param>
/// <param name="Type">DCS's display name for its type.</param>
/// <param name="IsPlayer">A human player flies it.</param>
public sealed record AiFlightMember(
    string Callsign, string Type, double? Latitude, double? Longitude, double? AltitudeMslMeters, bool IsPlayer);

/// <summary>One element of listFlights' result data.</summary>
public sealed class AiFlightTelemetry
{
    [JsonPropertyName("group")] public string? Group { get; set; }
    [JsonPropertyName("coalition")] public int? Coalition { get; set; }
    [JsonPropertyName("initialSize")] public int? InitialSize { get; set; }
    [JsonPropertyName("units")] public List<AiUnitTelemetry>? Units { get; set; }
}

/// <summary>One live unit of a listFlights group, in DCS's SI units (m).</summary>
public sealed class AiUnitTelemetry
{
    [JsonPropertyName("callsign")] public string? Callsign { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("lat")] public double? Lat { get; set; }
    [JsonPropertyName("lon")] public double? Lon { get; set; }
    [JsonPropertyName("altMsl")] public double? AltMsl { get; set; }
    [JsonPropertyName("player")] public bool? Player { get; set; }
}
