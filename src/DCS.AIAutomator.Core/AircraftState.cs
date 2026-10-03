namespace DCS.AIAutomator.Core;

/// <summary>
/// The player aircraft's state as DCS reports it, in DCS's native SI units (m, m/s, radians).
/// Conversion to display units happens only in <see cref="AircraftStateFormatter"/>. Any field
/// is null when DCS returned nothing for it.
/// </summary>
/// <param name="Failures">Active failure/warning flags from LoGetMCPState. Null means this aircraft
/// doesn't report failures (only FC3 aircraft do — full-fidelity modules never set these flags),
/// which must not be shown as "none"; empty means none are active.</param>
public sealed record AircraftState(
    double? Latitude,
    double? Longitude,
    double? AltitudeMslMeters,
    double? AltitudeAglMeters,
    double? IndicatedAirspeedMps,
    double? TrueAirspeedMps,
    double? Mach,
    double? VerticalSpeedMps,
    double? MagneticHeadingRadians,
    IReadOnlyList<string>? Failures);

/// <summary>Display units for aircraft state, chosen in the app's settings.</summary>
public enum UnitSystem
{
    /// <summary>Aviation units: ft, kt, ft/min.</summary>
    Imperial,
    /// <summary>m, km/h, m/s.</summary>
    Metric,
}
