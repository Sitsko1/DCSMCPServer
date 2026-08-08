public class DcsTelemetryParserTests
{
    [Fact]
    public void TryParse_ActiveMission_ReturnsPopulatedMissionInfo()
    {
        bool ok = DcsTelemetryParser.TryParse(
            """{"missionActive":true,"missionName":"Enfield Strike Package","terrain":"Syria","aircraft":"F-16C","multiplayer":true}""",
            out MissionInfo? mission);

        Assert.True(ok);
        Assert.NotNull(mission);
        Assert.Equal("Enfield Strike Package", mission!.MissionName);
        Assert.Equal("Syria", mission.Terrain);
        Assert.Equal("F-16C", mission.Aircraft);
        Assert.True(mission.IsMultiplayer);
    }

    [Fact]
    public void TryParse_InactiveMission_ReturnsTrueWithNullMission()
    {
        bool ok = DcsTelemetryParser.TryParse("""{"missionActive":false}""", out MissionInfo? mission);

        Assert.True(ok);
        Assert.Null(mission);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        bool ok = DcsTelemetryParser.TryParse("not json", out MissionInfo? mission);

        Assert.False(ok);
        Assert.Null(mission);
    }

    [Fact]
    public void TryParse_ActiveMissionMissingOptionalFields_DefaultsToUnknown()
    {
        bool ok = DcsTelemetryParser.TryParse("""{"missionActive":true}""", out MissionInfo? mission);

        Assert.True(ok);
        Assert.NotNull(mission);
        Assert.Equal("Unknown", mission!.MissionName);
        Assert.Equal("Unknown", mission.Terrain);
        Assert.Equal("Unknown", mission.Aircraft);
        Assert.False(mission.IsMultiplayer);
    }
}
