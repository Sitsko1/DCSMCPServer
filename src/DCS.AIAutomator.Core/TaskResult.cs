using System.Text.Json.Serialization;

namespace DCS.AIAutomator.Core;

/// <summary>What DCS reports back after tasking an AI group (vector, orbit, hold).</summary>
/// <param name="AltitudeMslMeters">The altitude DCS was told to fly (the current one if none was given).</param>
/// <param name="MagneticVariationDegrees">Local variation used to turn a magnetic heading into a true
/// course; null when there was no player aircraft to measure it from (the heading was flown as true),
/// and for tasks without a heading (orbit).</param>
public sealed record TaskResult(double? AltitudeMslMeters, double? MagneticVariationDegrees);

/// <summary>A tasking command's result data.</summary>
public sealed class TaskResultTelemetry
{
    [JsonPropertyName("altMsl")] public double? AltMsl { get; set; }
    [JsonPropertyName("variation")] public double? Variation { get; set; }
}
