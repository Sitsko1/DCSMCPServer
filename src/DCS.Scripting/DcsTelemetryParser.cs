using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCS.Scripting;

/// <summary>
/// Wire contract for DCS-side telemetry: one JSON object per line, written to Export.lua's
/// socket alongside (or instead of) raw export data. Adjust this shape to match whatever the
/// actual Export.lua script emits — this is the assumed default, not a fixed protocol.
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
    /// </summary>
    public static bool TryParse(string line, out MissionInfo? mission)
    {
        mission = null;

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
        }

        return true;
    }
}
