public class AtcToolsTests
{
    private const string Flights = """
        [{"group":"Enfield-1","callsign":"Enfield11","type":"F/A-18C","coalition":2,"lat":1,"lon":2,"altMsl":4572.0,"player":false},
         {"group":"Player #001","callsign":"Colt11","type":"F/A-18C","coalition":2,"lat":1,"lon":2,"altMsl":6000.0,"player":true},
         {"group":"Player","callsign":"Colt11","type":"F/A-18C","coalition":2,"lat":1,"lon":2,"altMsl":6000.0,"player":false}]
        """;

    private readonly FakeDcsConnection _connection = new();
    private readonly BridgeStatus _status = new() { Units = UnitSystem.Imperial };
    private AtcTools Tools => new(_connection, _status);

    public AtcToolsTests()
    {
        _connection.RespondWithData("listFlights", Flights);
        _connection.RespondWithData("vector", """{"altMsl":6096.0,"variation":6.2}""");
        _connection.RespondWithData("orbit", """{"altMsl":4572.0,"variation":null}""");
        _connection.RespondWithData("hold", """{"altMsl":6096.0,"variation":6.2}""");
    }

    [Fact]
    public async Task Orbit_TasksTheFlight_WithoutAHeading()
    {
        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Orbit, heading: 270);

        Assert.Equal(["listFlights", "orbit", "message"], _connection.Sent.Select(s => s.Cmd));
        var orbit = _connection.ArgsOf("orbit")!;
        Assert.Equal("Enfield-1", (string?)orbit["group"]);
        Assert.False(orbit.ContainsKey("heading"));
        Assert.False(orbit.ContainsKey("altitude"));
        // No "flown as true" note: an orbit has no heading.
        Assert.Equal(
            "Enfield 1-1 (group \"Enfield-1\") is orbiting its present position at 15,000 ft MSL. " +
            "Shown on screen in DCS: \"ATC to Enfield 1-1: orbit present position\"",
            result);
    }

    [Fact]
    public async Task Hold_TasksTheFlight_WithTheInboundHeading_AndAltitude()
    {
        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Hold, heading: 90, altitude: 20000);

        Assert.Equal(["listFlights", "hold", "message"], _connection.Sent.Select(s => s.Cmd));
        var hold = _connection.ArgsOf("hold")!;
        Assert.Equal(90, (double?)hold["heading"]);
        Assert.Equal(6096.0, (double)hold["altitude"]!, precision: 1);
        Assert.Equal(
            "Enfield 1-1 (group \"Enfield-1\") is holding at its present position, inbound heading 090, at 20,000 ft MSL. " +
            "Shown on screen in DCS: \"ATC to Enfield 1-1: hold at present position, inbound heading 090, altitude 20,000 ft\"",
            result);
    }

    [Fact]
    public async Task Hold_SaysSo_WhenNoMagneticReferenceWasAvailable()
    {
        _connection.RespondWithData("hold", """{"altMsl":4572.0,"variation":null}""");

        Assert.Contains("flown as true", await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Hold, heading: 90));
    }

    [Theory]
    [InlineData(AtcAction.Orbit)]
    [InlineData(AtcAction.Hold)]
    public async Task OrbitAndHold_UseTheSameAddressingAndPlayerRules(AtcAction action)
    {
        Assert.StartsWith("Error: Callsign Colt 1-1 is shared", await Tools.SendAtcInstruction("Colt 1-1", action));
        Assert.StartsWith("Colt 1-1 (group \"Player #001\") is the player's flight, so it wasn't tasked.",
            await Tools.SendAtcInstruction("Player #001", action));
        Assert.DoesNotContain(_connection.Sent, s => s.Cmd is "orbit" or "hold");
    }

    [Fact]
    public async Task Vectors_TasksTheFlight_InMetersMsl_ThenShowsTheMessage()
    {
        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Vectors, heading: 270, altitude: 20000);

        Assert.Equal(["listFlights", "vector", "message"], _connection.Sent.Select(s => s.Cmd));
        var vector = _connection.ArgsOf("vector")!;
        Assert.Equal("Enfield-1", (string?)vector["group"]);
        Assert.Equal(270, (double?)vector["heading"]);
        Assert.Equal(6096.0, (double)vector["altitude"]!, precision: 1); // 20,000 ft
        Assert.Equal(
            "Enfield 1-1 (group \"Enfield-1\") is turning to heading 270 at 20,000 ft MSL. " +
            "Shown on screen in DCS: \"ATC to Enfield 1-1: vectors, fly heading 270, altitude 20,000 ft\"",
            result);
    }

    [Fact]
    public async Task Vectors_WithoutAltitude_KeepsTheCurrentOne_AndSaysWhichItWas()
    {
        _connection.RespondWithData("vector", """{"altMsl":4572.0,"variation":6.2}""");

        string result = await Tools.SendAtcInstruction("Enfield-1", AtcAction.Vectors, heading: 90); // by group name

        Assert.False(_connection.ArgsOf("vector")!.ContainsKey("altitude"));
        Assert.StartsWith("Enfield 1-1 (group \"Enfield-1\") is turning to heading 090 at 15,000 ft MSL.", result);
    }

    [Fact]
    public async Task Vectors_AltitudeInMeters_WhenTheUserChoseMetric()
    {
        _status.Units = UnitSystem.Metric;

        await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Vectors, heading: 270, altitude: 5000);

        Assert.Equal(5000.0, (double?)_connection.ArgsOf("vector")!["altitude"]);
    }

    [Fact]
    public async Task Vectors_SaysSo_WhenNoMagneticReferenceWasAvailable()
    {
        _connection.RespondWithData("vector", """{"altMsl":4572.0,"variation":null}""");

        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Vectors, heading: 270);

        Assert.Contains("flown as true", result);
    }

    [Fact]
    public async Task Vectors_ToASharedCallsign_ListsTheGroups_AndTasksNothing()
    {
        string result = await Tools.SendAtcInstruction("Colt 1-1", AtcAction.Vectors, heading: 270);

        Assert.Equal("Error: Callsign Colt 1-1 is shared by several flights: group \"Player #001\", group \"Player\". Use the group name instead.", result);
        Assert.Equal(["listFlights"], _connection.Sent.Select(s => s.Cmd));
    }

    [Fact]
    public async Task Vectors_ToAnUnknownFlight_ListsTheKnownOnes()
    {
        string result = await Tools.SendAtcInstruction("Uzi 1-1", AtcAction.Vectors, heading: 270);

        Assert.StartsWith("Error: No flight called \"Uzi 1-1\". Known flights: Enfield 1-1 (group \"Enfield-1\"), Colt 1-1", result);
    }

    [Fact]
    public async Task Vectors_ToThePlayersFlight_OnlyShowsTheMessage()
    {
        string result = await Tools.SendAtcInstruction("Player #001", AtcAction.Vectors, heading: 270);

        Assert.Equal(["listFlights", "message"], _connection.Sent.Select(s => s.Cmd));
        Assert.StartsWith("Colt 1-1 (group \"Player #001\") is the player's flight, so it wasn't tasked.", result);
    }

    [Fact]
    public async Task Vectors_ReportsDcsRefusal_AndShowsNoMessage()
    {
        _connection.Responses["vector"] = DcsCommandResult.Failed("no group named Enfield-1");

        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Vectors, heading: 270);

        Assert.Equal("Error: DCS didn't task Enfield 1-1: no group named Enfield-1", result);
        Assert.DoesNotContain(_connection.Sent, s => s.Cmd == "message");
    }

    [Theory]
    [InlineData(-1.0, null)]
    [InlineData(361.0, null)]
    [InlineData(270, 50.0)] // feet
    [InlineData(270, 70000.0)]
    public async Task OutOfRangeHeadingOrAltitude_IsRejected_BeforeAnythingIsSent(double heading, double? altitude)
    {
        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.Vectors, heading, altitude);

        Assert.StartsWith("Error:", result);
        Assert.Empty(_connection.Sent);
    }

    [Fact]
    public async Task OtherActions_OnlyShowTheMessage_AndSaySo()
    {
        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.ClearToLand);

        Assert.Equal(["message"], _connection.Sent.Select(s => s.Cmd));
        Assert.Equal("ClearToLand doesn't task aircraft yet, so no flight was moved. " +
                     "Shown on screen in DCS: \"ATC to Enfield 1-1: cleared to land\"", result);
    }

    [Fact]
    public async Task HostileCallsigns_TravelOnlyAsPlainText()
    {
        // Was Lua injection (#4): the callsign went into Lua source. Now it's only ever JSON data.
        const string callsign = "x\", 10) os.exit() --\n\\";

        await Tools.SendAtcInstruction(callsign, AtcAction.ClearToLand);

        Assert.StartsWith($"ATC to {callsign}:", (string?)_connection.ArgsOf("message")!["text"]);
    }

    [Fact]
    public async Task AFailedMessage_IsReported()
    {
        _connection.Responses["message"] = DcsCommandResult.Failed("mission scripting isn't enabled");

        string result = await Tools.SendAtcInstruction("Enfield 1-1", AtcAction.ClearToLand);

        Assert.EndsWith("The on-screen message failed: mission scripting isn't enabled", result);
    }
}
