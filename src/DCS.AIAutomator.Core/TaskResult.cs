using System.Text.Json.Serialization;

namespace DCS.AIAutomator.Core;

/// <summary>What DCS reports back after tasking an AI group (vector, orbit, hold).</summary>
/// <param name="AltitudeMslMeters">The altitude DCS was told to fly (the current one if none was given).</param>
/// <param name="MagneticVariationDegrees">Local variation used to turn a magnetic heading into a true
/// course; null when there was no player aircraft to measure it from (the heading was flown as true),
/// and for tasks without a heading (orbit, land).</param>
/// <param name="Airbase">For land: the airfield DCS chose (the named one, or the nearest friendly).</param>
/// <param name="DistanceMeters">For land: how far that airfield was from the flight.</param>
public sealed record TaskResult(double? AltitudeMslMeters, double? MagneticVariationDegrees,
    string? Airbase = null, double? DistanceMeters = null);

/// <summary>A tasking command's result data.</summary>
public sealed class TaskResultTelemetry
{
    [JsonPropertyName("altMsl")] public double? AltMsl { get; set; }
    [JsonPropertyName("variation")] public double? Variation { get; set; }
    [JsonPropertyName("airbase")] public string? Airbase { get; set; }
    [JsonPropertyName("distance")] public double? Distance { get; set; }
}
