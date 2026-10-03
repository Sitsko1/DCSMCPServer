using System.Globalization;
using System.Text.RegularExpressions;

namespace DCS.Scripting;

public static class UnitConversion
{
    public static double MetersToFeet(double meters) => meters * 3.28084;
    public static double MpsToKnots(double mps) => mps * 1.943844;
    public static double MpsToFeetPerMinute(double mps) => mps * 196.850394;
    public static double MpsToKmh(double mps) => mps * 3.6;

    /// <summary>Whole degrees in 0–359 (a value that rounds to 360 is 0).</summary>
    public static int RadiansToHeadingDegrees(double radians)
    {
        int degrees = (int)Math.Round(radians * 180 / Math.PI) % 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }
}

/// <summary>
/// Turns <see cref="AircraftState"/> values into display text with unit names. The single source
/// of formatting for both the MainWindow panel and the get_aircraft_state MCP tool, so the two
/// always agree. Invariant culture: this is an instrument readout, not locale-formatted prose.
/// </summary>
public static partial class AircraftStateFormatter
{
    public const string Missing = "—";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Altitude(double? meters, UnitSystem units) => meters is not double m ? Missing
        : units == UnitSystem.Imperial
            ? $"{UnitConversion.MetersToFeet(m).ToString("N0", Inv)} ft"
            : $"{m.ToString("N0", Inv)} m";

    public static string Speed(double? mps, UnitSystem units) => mps is not double v ? Missing
        : units == UnitSystem.Imperial
            ? $"{UnitConversion.MpsToKnots(v).ToString("N0", Inv)} kt"
            : $"{UnitConversion.MpsToKmh(v).ToString("N0", Inv)} km/h";

    public static string VerticalSpeed(double? mps, UnitSystem units) => mps is not double v ? Missing
        : units == UnitSystem.Imperial
            ? $"{UnitConversion.MpsToFeetPerMinute(v).ToString("+#,0;-#,0;0", Inv)} ft/min"
            : $"{v.ToString("+0.0;-0.0;0.0", Inv)} m/s";

    public static string Heading(double? radians) =>
        radians is double r ? $"{UnitConversion.RadiansToHeadingDegrees(r):000}°" : Missing;

    public static string Mach(double? mach) => mach is double m ? m.ToString("0.00", Inv) : Missing;

    public static string Position(double? latitude, double? longitude) =>
        latitude is double lat && longitude is double lon
            ? $"{Math.Abs(lat).ToString("0.0000", Inv)}° {(lat >= 0 ? 'N' : 'S')}, " +
              $"{Math.Abs(lon).ToString("0.0000", Inv)}° {(lon >= 0 ? 'E' : 'W')}"
            : Missing;

    public static string Failures(IReadOnlyList<string>? failures) => failures switch
    {
        null => "Not reported by this aircraft", // never "None": we simply don't know
        [] => "None",
        _ => string.Join(", ", failures.Select(FailureName)),
    };

    /// <summary>"LeftEngineFailure" → "Left engine failure", keeping acronyms ("ACSFailure" → "ACS failure").</summary>
    public static string FailureName(string flag)
    {
        if (flag == "StallSignalization") return "Stall warning"; // DCS's own name reads oddly
        string[] words = WordBoundary().Split(flag);
        for (int i = 1; i < words.Length; i++)
        {
            if (words[i].Any(char.IsLower)) words[i] = words[i].ToLowerInvariant(); // keep acronyms
        }
        return string.Join(' ', words);
    }

    // Between a lower-case letter and a capital, or before the last capital of an acronym run.
    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundary();
}
