using System.Text.Json;

public class FlightToolsTests
{
    private readonly FakeDcsConnection _connection = new();
    private readonly BridgeStatus _status = new() { Units = UnitSystem.Imperial };

    private Task<string> List() => new FlightTools(_connection, _status).ListAiFlights();

    private void DcsReturns(string json) =>
        _connection.Result = new DcsCommandResult(true, Data: JsonDocument.Parse(json).RootElement);

    [Fact]
    public async Task ListsEachFlight_ThenEachOfItsAircraft()
    {
        DcsReturns("""
            [{"group":"Enfield-1","coalition":2,"initialSize":2,"units":[{"callsign":"Enfield11","type":"F/A-18C","lat":41.7213,"lon":41.7674,"altMsl":4572.0,"player":false},{"callsign":"Enfield12","type":"F/A-18C","lat":41.722,"lon":41.769,"altMsl":4575.0,"player":false}]},
             {"group":"Bandit","coalition":1,"initialSize":1,"units":[{"callsign":"201","type":"MiG-29S","lat":-33.5,"lon":-70.1,"altMsl":1000.0,"player":false}]}]
            """);

        string result = await List();

        Assert.Equal("listFlights", _connection.LastCmd);
        Assert.Equal(
            "Enfield 1 (group \"Enfield-1\"): 2/2, F/A-18C, blue\n" +
            "  Enfield 1-1: 41.7213° N, 41.7674° E, 15,000 ft MSL\n" +
            "  Enfield 1-2: 41.7220° N, 41.7690° E, 15,010 ft MSL\n" +
            "201 (group \"Bandit\"): 1/1, MiG-29S, red\n" +
            "  201: 33.5000° S, 70.1000° W, 3,281 ft MSL",
            result);
    }

    [Fact]
    public async Task ALostAircraft_ShowsInTheCount_AndTheNextOneLeads()
    {
        DcsReturns("""[{"group":"Player #001","coalition":2,"initialSize":2,"units":[{"callsign":"Colt12","type":"F/A-18C","lat":1,"lon":2,"altMsl":0,"player":false}]}]""");

        Assert.Equal("Colt 1 (group \"Player #001\"): 1/2, F/A-18C, blue\n  Colt 1-2: 1.0000° N, 2.0000° E, 0 ft MSL", await List());
    }

    [Fact]
    public async Task AnUnknownInitialSize_ShowsAsAQuestionMark()
    {
        DcsReturns("""[{"group":"g","coalition":2,"initialSize":null,"units":[{"callsign":"Uzi11","type":"F-16C","lat":1,"lon":2,"altMsl":0,"player":false}]}]""");

        Assert.StartsWith("Uzi 1 (group \"g\"): 1/?, F-16C, blue", await List());
    }

    [Fact]
    public async Task UsesTheUsersUnits()
    {
        _status.Units = UnitSystem.Metric;
        DcsReturns("""[{"group":"g","coalition":2,"initialSize":1,"units":[{"callsign":"Uzi12","type":"F-16C","lat":1,"lon":2,"altMsl":4572.0,"player":false}]}]""");

        Assert.EndsWith("4,572 m MSL", await List());
    }

    [Fact]
    public async Task MarksThePlayersGroup_AndThePlayersAircraft()
    {
        DcsReturns("""[{"group":"Me","coalition":2,"initialSize":2,"units":[{"callsign":"Colt11","type":"F/A-18C","lat":1,"lon":2,"altMsl":0,"player":true},{"callsign":"Colt12","type":"F/A-18C","lat":1,"lon":2,"altMsl":0,"player":false}]}]""");

        string result = await List();

        Assert.StartsWith("Colt 1 (group \"Me\") [player]: 2/2", result);
        Assert.Contains("\n  Colt 1-1 [player]: ", result);
        Assert.Contains("\n  Colt 1-2: ", result);
    }

    [Fact]
    public async Task MissingValues_ShowAsDashes_NotZeros()
    {
        DcsReturns("""[{"group":"g","coalition":1,"initialSize":1,"units":[{"callsign":"","type":"Su-27","lat":null,"lon":null,"altMsl":null,"player":false}]}]""");

        Assert.Equal("none (group \"g\"): 1/1, Su-27, red\n  none: —, — MSL", await List());
    }

    [Fact]
    public async Task NoFlights_IsAClearResult_NotAnError()
    {
        DcsReturns("[]");

        Assert.Equal("No AI flights in the mission.", await List());
    }

    [Fact]
    public async Task DcsFailure_IsReported()
    {
        _connection.Result = DcsCommandResult.Failed("no mission is running");

        Assert.Equal("Error: couldn't list AI flights: no mission is running", await List());
    }

    [Fact]
    public async Task UnreadableData_IsAnError_NotAnEmptyList()
    {
        DcsReturns("""{"not":"a list"}""");

        Assert.Equal("Error: couldn't list AI flights: DCS sent a flight list the app couldn't read.", await List());
    }

    private static AiFlight Flight(string group, params string[] callsigns) =>
        new(group, "blue", callsigns.Length, callsigns.Select(c => new AiFlightMember(c, "F/A-18C", 1, 2, 3, false)).ToList());

    private static readonly AiFlight[] Mission =
    [
        Flight("Enfield-1", "Enfield 1-1", "Enfield 1-2"),
        Flight("Player #001", "Colt 1-1"),
        Flight("Player", "Colt 1-1"),
    ];

    [Theory]
    [InlineData("Enfield 1-1", "Enfield-1")]
    [InlineData("enfield11", "Enfield-1")] // DCS's raw form, any case
    [InlineData("Enfield 1", "Enfield-1")] // the flight's callsign
    [InlineData("Player", "Player")] // an exact group name wins over a shared callsign
    [InlineData(" player #001 ", "Player #001")]
    public void Resolve_FindsByGroupNameOrCallsign(string name, string group) =>
        Assert.Equal(group, AiFlight.Resolve(Mission, name).Flight!.GroupName);

    [Fact]
    public void Resolve_ASharedCallsign_IsAnError_NamingTheGroups()
    {
        var (flight, error) = AiFlight.Resolve(Mission, "Colt 1-1");

        Assert.Null(flight);
        Assert.Equal("Callsign Colt 1-1 is shared by several flights: group \"Player #001\", group \"Player\". Use the group name instead.", error);
    }

    [Fact]
    public void Resolve_AWingmansCallsign_IsAnError_NamingItsFlightsLead()
    {
        var (flight, error) = AiFlight.Resolve(Mission, "Enfield 1-2");

        Assert.Null(flight);
        Assert.Equal("Enfield 1-2 flies in Enfield 1 (group \"Enfield-1\"), and a single aircraft can't be tasked on its own yet. " +
                     "Address the whole flight by its lead, Enfield 1-1, or its group name.", error);
    }

    [Fact]
    public void FlightCallsign_IsTheLeadsWithoutTheAircraftNumber_OrTheLeadsWhenItIsntWestern()
    {
        Assert.Equal("Enfield 1", Mission[0].FlightCallsign);
        Assert.Equal("201", Flight("Bandit", "201").FlightCallsign);
        Assert.Equal("none", new AiFlight("Empty", "red", 2, []).FlightCallsign);
    }

    [Fact]
    public void Resolve_NoMatch_ListsCandidates_CappedAtTen()
    {
        AiFlight[] many = Enumerable.Range(1, 12).Select(i => Flight($"G{i}", $"Uzi {i}-1")).ToArray();

        string error = AiFlight.Resolve(many, "Enfield 1-1").Error!;

        Assert.StartsWith("No flight called \"Enfield 1-1\". Known flights: Uzi 1-1 (group \"G1\"),", error);
        Assert.EndsWith(", and 2 more (see list_ai_flights).", error);
        Assert.Equal("No flight called \"x\". There are no AI flights in the mission.", AiFlight.Resolve([], "x").Error);
    }

    [Theory]
    [InlineData("Enfield11", "Enfield 1-1")]
    [InlineData("Springfield34", "Springfield 3-4")]
    [InlineData("Enfield 1-1", "Enfield 1-1")]
    [InlineData("201", "201")] // numeric (Russian-style) callsigns stay as they are
    [InlineData("", "none")]
    [InlineData(null, "none")]
    public void SpokenCallsign(string? raw, string spoken) =>
        Assert.Equal(spoken, AiFlight.SpokenCallsign(raw));
}
