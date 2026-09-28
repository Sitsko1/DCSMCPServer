using DCS.Scripting;

public class AircraftStateFormatterTests
{
    [Theory]
    [InlineData(1000, 3280.84)]
    [InlineData(0, 0)]
    public void MetersToFeet(double meters, double feet) =>
        Assert.Equal(feet, UnitConversion.MetersToFeet(meters), precision: 2);

    [Fact]
    public void MpsToKnots() => Assert.Equal(194.38, UnitConversion.MpsToKnots(100), precision: 2);

    [Fact]
    public void MpsToFeetPerMinute() => Assert.Equal(1968.5, UnitConversion.MpsToFeetPerMinute(10), precision: 1);

    [Fact]
    public void MpsToKmh() => Assert.Equal(360, UnitConversion.MpsToKmh(100), precision: 6);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(Math.PI / 2, 90)]
    [InlineData(Math.PI, 180)]
    [InlineData(-Math.PI / 2, 270)]         // negative yaw wraps into 0–359
    [InlineData(2 * Math.PI, 0)]            // a full turn is 0, never 360
    [InlineData(2 * Math.PI - 0.001, 0)]    // 359.94° rounds to 360 → shown as 0
    public void RadiansToHeadingDegrees_NormalizesTo0Through359(double radians, int degrees) =>
        Assert.Equal(degrees, UnitConversion.RadiansToHeadingDegrees(radians));

    [Fact]
    public void Altitude_Imperial_And_Metric()
    {
        Assert.Equal("9,843 ft", AircraftStateFormatter.Altitude(3000, UnitSystem.Imperial));
        Assert.Equal("3,000 m", AircraftStateFormatter.Altitude(3000, UnitSystem.Metric));
    }

    [Fact]
    public void Speed_Imperial_And_Metric()
    {
        Assert.Equal("194 kt", AircraftStateFormatter.Speed(100, UnitSystem.Imperial));
        Assert.Equal("360 km/h", AircraftStateFormatter.Speed(100, UnitSystem.Metric));
    }

    [Fact]
    public void VerticalSpeed_IsSigned()
    {
        Assert.Equal("+1,969 ft/min", AircraftStateFormatter.VerticalSpeed(10, UnitSystem.Imperial));
        Assert.Equal("-1,969 ft/min", AircraftStateFormatter.VerticalSpeed(-10, UnitSystem.Imperial));
        Assert.Equal("+10.0 m/s", AircraftStateFormatter.VerticalSpeed(10, UnitSystem.Metric));
    }

    [Fact]
    public void Heading_Position_Mach()
    {
        Assert.Equal("270°", AircraftStateFormatter.Heading(-Math.PI / 2));
        Assert.Equal("41.5000° N, 70.2500° W", AircraftStateFormatter.Position(41.5, -70.25));
        Assert.Equal("0.48", AircraftStateFormatter.Mach(0.48));
    }

    [Fact]
    public void MissingValues_ShowDash()
    {
        Assert.Equal("—", AircraftStateFormatter.Altitude(null, UnitSystem.Imperial));
        Assert.Equal("—", AircraftStateFormatter.Speed(null, UnitSystem.Metric));
        Assert.Equal("—", AircraftStateFormatter.Heading(null));
        Assert.Equal("—", AircraftStateFormatter.Position(null, 1));
        Assert.Equal("—", AircraftStateFormatter.Mach(null));
    }

    [Fact]
    public void Failures_NoneUnavailableOrReadableNames()
    {
        Assert.Equal("None", AircraftStateFormatter.Failures([]));
        Assert.Equal("Unavailable", AircraftStateFormatter.Failures(null));
        Assert.Equal("Left engine failure, ACS failure, Stall warning",
            AircraftStateFormatter.Failures(["LeftEngineFailure", "ACSFailure", "StallSignalization"]));
    }
}
