using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCS.Scripting;

/// <summary>
/// Wire contract for DCS-side telemetry: one JSON object per line, written by the Hooks script
/// (LuaHooksScriptGenerator). Keep the two in sync by hand — nothing enforces it across the
/// language boundary.
/// </summary>
public sealed class DcsTelemetryMessage
{
    [JsonPropertyName("missionActive")]
    public bool MissionActive { get; set; }

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
internal partial class DcsTelemetryJsonContext : JsonSerializerContext
{
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
    /// Parses one telemetry line. Returns false for garbage/malformed input (caller should
    /// leave prior state untouched). Returns true with <paramref name="mission"/> null when the
    /// line is a valid "no mission active" report, or non-null when a mission is active.
    /// <paramref name="aircraft"/> is non-null only during a mission with a player aircraft.
    /// </summary>
    public static bool TryParse(string line, out MissionInfo? mission, out AircraftState? aircraft)
    {
        mission = null;
        aircraft = null;

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

        if (message.MissionActive)
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

        return true;
    }
}
