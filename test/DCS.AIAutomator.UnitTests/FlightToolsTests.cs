using System.Text.Json;

public class FlightToolsTests
{
    private readonly FakeDcsConnection _connection = new();
    private readonly BridgeStatus _status = new() { Units = UnitSystem.Imperial };

    private Task<string> List() => new FlightTools(_connection, _status).ListAiFlights();

    private void DcsReturns(string json) =>
        _connection.Result = new DcsCommandResult(true, Data: JsonDocument.Parse(json).RootElement);

    [Fact]
    public async Task ListsEachFlight_WithSpokenCallsign_GroupName_Type_Coalition_Position_AndAltitude()
    {
        DcsReturns("""
            [{"group":"Enfield-1","callsign":"Enfield11","type":"F/A-18C","coalition":2,"lat":41.7213,"lon":41.7674,"altMsl":4572.0,"player":false},
             {"group":"Bandit","callsign":"201","type":"MiG-29S","coalition":1,"lat":-33.5,"lon":-70.1,"altMsl":1000.0,"player":false}]
            """);

        string result = await List();

        Assert.Equal("listFlights", _connection.LastCmd);
        Assert.Equal(
            "Enfield 1-1 (group \"Enfield-1\"): F/A-18C, blue, 41.7213° N, 41.7674° E, 15,000 ft MSL\n" +
            "201 (group \"Bandit\"): MiG-29S, red, 33.5000° S, 70.1000° W, 3,281 ft MSL",
            result);
    }

    [Fact]
    public async Task UsesTheUsersUnits()
    {
        _status.Units = UnitSystem.Metric;
        DcsReturns("""[{"group":"g","callsign":"Uzi12","type":"F-16C","coalition":2,"lat":1,"lon":2,"altMsl":4572.0,"player":false}]""");

        Assert.EndsWith("4,572 m MSL", await List());
    }

    [Fact]
    public async Task MarksThePlayersGroup()
    {
        DcsReturns("""[{"group":"Me","callsign":"Colt11","type":"F/A-18C","coalition":2,"lat":1,"lon":2,"altMsl":0,"player":true}]""");

        Assert.StartsWith("Colt 1-1 (group \"Me\") [player]:", await List());
    }

    [Fact]
    public async Task MissingValues_ShowAsDashes_NotZeros()
    {
        DcsReturns("""[{"group":"g","callsign":"","type":"Su-27","coalition":1,"lat":null,"lon":null,"altMsl":null,"player":false}]""");

        Assert.Equal("none (group \"g\"): Su-27, red, —, — MSL", await List());
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
