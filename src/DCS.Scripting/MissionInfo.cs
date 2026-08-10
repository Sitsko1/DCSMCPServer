namespace DCS.Scripting;

/// <summary>
/// Snapshot of the currently active DCS mission, as reported by Export.lua telemetry.
/// </summary>
public sealed record MissionInfo(string MissionName, string Terrain, string Aircraft, bool IsMultiplayer);
