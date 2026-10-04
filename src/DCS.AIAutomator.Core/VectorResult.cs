using System.Text.Json.Serialization;

namespace DCS.AIAutomator.Core;

/// <param name="AltitudeMslMeters">The altitude DCS was told to fly (the current one if none was given).</param>
/// <param name="MagneticVariationDegrees">Local variation used to turn the magnetic heading into a
/// true course; null when there was no player aircraft to measure it from (heading flown as true).</param>
public sealed record VectorResult(double? AltitudeMslMeters, double? MagneticVariationDegrees);

/// <summary>The vector command's result data.</summary>
public sealed class VectorResultTelemetry
{
    [JsonPropertyName("altMsl")] public double? AltMsl { get; set; }
    [JsonPropertyName("variation")] public double? Variation { get; set; }
}
