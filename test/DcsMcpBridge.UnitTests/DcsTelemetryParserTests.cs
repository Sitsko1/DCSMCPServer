using DCS.Scripting;

public class DcsTelemetryParserTests
{
    [Fact]
    public void TryParse_ActiveMission_ReturnsPopulatedMissionInfo()
    {
        bool ok = DcsTelemetryParser.TryParse(
            """{"missionActive":true,"missionName":"Enfield Strike Package","terrain":"Syria","aircraft":"F-16C"}""",
            out MissionInfo? mission, out _);

        Assert.True(ok);
        Assert.NotNull(mission);
        Assert.Equal("Enfield Strike Package", mission!.MissionName);
        Assert.Equal("Syria", mission.Terrain);
        Assert.Equal("F-16C", mission.Aircraft);
    }

    [Fact]
    public void TryParse_IgnoresMultiplayerFieldFromPreviouslyDeployedScripts()
    {
        // Scripts deployed before multiplayer was deferred to v2 still send this field until redeployed.
        bool ok = DcsTelemetryParser.TryParse(
            """{"missionActive":true,"missionName":"M","terrain":"T","aircraft":"A","multiplayer":true}""",
            out MissionInfo? mission, out _);

        Assert.True(ok);
        Assert.Equal("A", mission!.Aircraft);
    }

    [Theory]
    [InlineData("PersianGulf", "Persian Gulf")]
    [InlineData("Falklands", "South Atlantic")]
    [InlineData("SinaiMap", "Sinai")]
    [InlineData("Caucasus", "Caucasus")]
    [InlineData("SomeFutureMap", "SomeFutureMap")] // unknown theatre IDs pass through unchanged
    public void TryParse_MapsTheatreIdToReadableTerrainName(string theatre, string expected)
    {
        DcsTelemetryParser.TryParse($$"""{"missionActive":true,"terrain":"{{theatre}}"}""", out MissionInfo? mission, out _);

        Assert.Equal(expected, mission!.Terrain);
    }

    [Fact]
    public void TryParse_InactiveMission_ReturnsTrueWithNullMission()
    {
        bool ok = DcsTelemetryParser.TryParse("""{"missionActive":false}""", out MissionInfo? mission, out _);

        Assert.True(ok);
        Assert.Null(mission);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        bool ok = DcsTelemetryParser.TryParse("not json", out MissionInfo? mission, out _);

        Assert.False(ok);
        Assert.Null(mission);
    }

    [Fact]
    public void TryParse_ActiveMissionMissingOptionalFields_DefaultsToUnknown()
    {
        bool ok = DcsTelemetryParser.TryParse("""{"missionActive":true}""", out MissionInfo? mission, out _);

        Assert.True(ok);
        Assert.NotNull(mission);
        Assert.Equal("Unknown", mission!.MissionName);
        Assert.Equal("Unknown", mission.Terrain);
        Assert.Equal("Unknown", mission.Aircraft);
    }

    [Fact]
    public void TryParse_Ownship_PopulatesAircraftStateInSiUnits()
    {
        bool ok = DcsTelemetryParser.TryParse(
            """{"missionActive":true,"aircraft":"F/A-18C","ownship":{"lat":41.5,"lon":-70.25,"altMsl":3000,"altAgl":2950.5,"ias":150,"tas":160,"mach":0.48,"vs":-5.5,"hdg":1.5708,"failures":["LeftEngineFailure","GearFailure"]}}""",
            out _, out AircraftState? aircraft);

        Assert.True(ok);
        Assert.NotNull(aircraft);
        Assert.Equal(41.5, aircraft!.Latitude);
        Assert.Equal(-70.25, aircraft.Longitude);
        Assert.Equal(3000, aircraft.AltitudeMslMeters);
        Assert.Equal(2950.5, aircraft.AltitudeAglMeters);
        Assert.Equal(150, aircraft.IndicatedAirspeedMps);
        Assert.Equal(160, aircraft.TrueAirspeedMps);
        Assert.Equal(0.48, aircraft.Mach);
        Assert.Equal(-5.5, aircraft.VerticalSpeedMps);
        Assert.Equal(1.5708, aircraft.MagneticHeadingRadians);
        Assert.Equal(["LeftEngineFailure", "GearFailure"], aircraft.Failures);
    }

    [Fact]
    public void TryParse_OwnshipWithNullFailures_MeansUnavailable_AndNullFieldsStayNull()
    {
        DcsTelemetryParser.TryParse(
            """{"missionActive":true,"ownship":{"lat":1,"lon":2,"altAgl":null,"failures":null}}""",
            out _, out AircraftState? aircraft);

        Assert.NotNull(aircraft);
        Assert.Null(aircraft!.Failures);
        Assert.Null(aircraft.AltitudeAglMeters);
    }

    [Fact]
    public void TryParse_ActiveMissionWithoutOwnship_HasNoAircraftState()
    {
        bool ok = DcsTelemetryParser.TryParse("""{"missionActive":true}""", out MissionInfo? mission, out AircraftState? aircraft);

        Assert.True(ok);
        Assert.NotNull(mission);
        Assert.Null(aircraft);
    }

    [Fact]
    public void TryParse_InactiveMission_IgnoresOwnship()
    {
        DcsTelemetryParser.TryParse("""{"missionActive":false,"ownship":{"lat":1}}""", out _, out AircraftState? aircraft);

        Assert.Null(aircraft);
    }
}
