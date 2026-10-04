
public class DcsTelemetryParserTests
{
    // Mission/aircraft view of a parsed line, as most tests here only care about those.
    private static bool Parse(string line, out MissionInfo? mission, out AircraftState? aircraft)
    {
        bool ok = DcsTelemetryParser.TryParse(line, out DcsLine? parsed);
        mission = parsed?.Mission;
        aircraft = parsed?.Aircraft;
        return ok;
    }

    [Fact]
    public void TryParse_Heartbeat_IsNotAMissionReport()
    {
        // A heartbeat has no "missionActive", so it must not be read as "no mission" — that would
        // clear the mission readout once a second.
        bool ok = DcsTelemetryParser.TryParse(DcsWireSamples.Heartbeat, out DcsLine? parsed);

        Assert.True(ok);
        Assert.False(parsed!.IsMissionReport);
        Assert.Null(parsed.Paused);
    }

    [Fact]
    public void TryParse_AuthOk_CarriesTheProtocolVersion()
    {
        DcsTelemetryParser.TryParse(DcsWireSamples.AuthOk, out DcsLine? parsed);

        Assert.True(parsed!.AuthOk);
        Assert.Equal(LuaHooksScriptGenerator.ProtocolVersion, parsed.Protocol);
    }

    [Fact]
    public void TryParse_AuthOkFromAScriptThatPredatesVersioning_HasNoProtocol()
    {
        DcsTelemetryParser.TryParse("""{"authOk":true}""", out DcsLine? parsed);

        Assert.True(parsed!.AuthOk);
        Assert.Null(parsed.Protocol);
    }

    [Fact]
    public void TryParse_WireSamples_FailuresNullMeansNotReported_EmptyMeansNone()
    {
        DcsTelemetryParser.TryParse(DcsWireSamples.MissionWithOwnshipFailuresNotReported, out DcsLine? notReported);
        DcsTelemetryParser.TryParse(DcsWireSamples.MissionWithOwnshipNoFailures, out DcsLine? none);
        DcsTelemetryParser.TryParse(DcsWireSamples.MissionWithoutOwnship, out DcsLine? noOwnship);

        Assert.Null(notReported!.Aircraft!.Failures);
        Assert.Empty(none!.Aircraft!.Failures!);
        Assert.NotNull(noOwnship!.Mission);
        Assert.Null(noOwnship.Aircraft);
    }

    [Theory]
    [InlineData("""{"authOk":true}""", true, false)]
    [InlineData(DcsWireSamples.AuthError, false, true)]
    public void TryParse_AuthReplies_AreRecognized_AndAreNotMissionReports(string line, bool authOk, bool authError)
    {
        DcsTelemetryParser.TryParse(line, out DcsLine? parsed);

        Assert.False(parsed!.IsMissionReport);
        Assert.Equal(authOk, parsed.AuthOk);
        Assert.Equal(authError, parsed.AuthError);
    }

    [Fact]
    public void TryParse_LogLine_CarriesTheEntry_AndIsNotAMissionReport()
    {
        bool ok = DcsTelemetryParser.TryParse(DcsWireSamples.Log, out DcsLine? parsed);

        Assert.True(ok);
        Assert.False(parsed!.IsMissionReport);
        Assert.Equal("error", parsed.Log!.Level);
        Assert.Equal("frame error: boom", parsed.Log.Message);
    }

    [Theory]
    [InlineData(DcsWireSamples.Paused, true)]
    [InlineData(DcsWireSamples.Resumed, false)]
    public void TryParse_PauseLines_CarryPausedState_AndAreNotMissionReports(string line, bool paused)
    {
        DcsTelemetryParser.TryParse(line, out DcsLine? parsed);

        Assert.False(parsed!.IsMissionReport);
        Assert.Equal(paused, parsed.Paused);
    }

    [Theory]
    [InlineData("""{"missionActive":true}""")]
    [InlineData("""{"missionActive":false}""")]
    public void TryParse_MissionActiveLines_AreMissionReports(string line)
    {
        DcsTelemetryParser.TryParse(line, out DcsLine? parsed);

        Assert.True(parsed!.IsMissionReport);
    }

    [Fact]
    public void TryParse_ActiveMission_ReturnsPopulatedMissionInfo()
    {
        bool ok = Parse(
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
        bool ok = Parse(
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
        Parse($$"""{"missionActive":true,"terrain":"{{theatre}}"}""", out MissionInfo? mission, out _);

        Assert.Equal(expected, mission!.Terrain);
    }

    [Fact]
    public void TryParse_InactiveMission_ReturnsTrueWithNullMission()
    {
        bool ok = Parse("""{"missionActive":false}""", out MissionInfo? mission, out _);

        Assert.True(ok);
        Assert.Null(mission);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        bool ok = Parse("not json", out MissionInfo? mission, out _);

        Assert.False(ok);
        Assert.Null(mission);
    }

    [Fact]
    public void TryParse_ActiveMissionMissingOptionalFields_DefaultsToUnknown()
    {
        bool ok = Parse("""{"missionActive":true}""", out MissionInfo? mission, out _);

        Assert.True(ok);
        Assert.NotNull(mission);
        Assert.Equal("Unknown", mission!.MissionName);
        Assert.Equal("Unknown", mission.Terrain);
        Assert.Equal("Unknown", mission.Aircraft);
    }

    [Fact]
    public void TryParse_Ownship_PopulatesAircraftStateInSiUnits()
    {
        bool ok = Parse(
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
        Parse(
            """{"missionActive":true,"ownship":{"lat":1,"lon":2,"altAgl":null,"failures":null}}""",
            out _, out AircraftState? aircraft);

        Assert.NotNull(aircraft);
        Assert.Null(aircraft!.Failures);
        Assert.Null(aircraft.AltitudeAglMeters);
    }

    [Fact]
    public void TryParse_ActiveMissionWithoutOwnship_HasNoAircraftState()
    {
        bool ok = Parse("""{"missionActive":true}""", out MissionInfo? mission, out AircraftState? aircraft);

        Assert.True(ok);
        Assert.NotNull(mission);
        Assert.Null(aircraft);
    }

    [Fact]
    public void TryParse_InactiveMission_IgnoresOwnship()
    {
        Parse("""{"missionActive":false,"ownship":{"lat":1}}""", out _, out AircraftState? aircraft);

        Assert.Null(aircraft);
    }
}
