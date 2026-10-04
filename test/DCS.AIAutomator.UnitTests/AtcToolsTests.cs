public class AtcToolsTests
{
    private readonly FakeDcsConnection _connection = new();

    [Fact]
    public async Task SendAtcInstruction_SendsAMessageCommand_WithTheInstructionAsData()
    {
        await new AtcTools(_connection).SendAtcInstruction("Enfield 1-1", AtcAction.Vectors, heading: 270);

        Assert.Equal("message", _connection.LastCmd);
        Assert.Equal("ATC to Enfield 1-1: vectors, fly heading 270", (string?)_connection.LastArgs!["text"]);
        Assert.Equal(AtcTools.MessageSeconds, (int?)_connection.LastArgs["seconds"]);
    }

    [Fact]
    public async Task SendAtcInstruction_DefaultsHeadingTo360()
    {
        await new AtcTools(_connection).SendAtcInstruction("Enfield 1-1", AtcAction.Hold);

        Assert.EndsWith("fly heading 360", (string?)_connection.LastArgs!["text"]);
    }

    [Fact]
    public async Task SendAtcInstruction_PassesHostileCallsignsThroughAsPlainText()
    {
        // Was Lua injection (#4): the callsign went into Lua source. Now it's only ever JSON data.
        const string callsign = "x\", 10) os.exit() --\n\\";

        await new AtcTools(_connection).SendAtcInstruction(callsign, AtcAction.Orbit);

        Assert.StartsWith($"ATC to {callsign}:", (string?)_connection.LastArgs!["text"]);
    }

    [Fact]
    public async Task SendAtcInstruction_ReportsSuccess_OnlyWhenDcsConfirms()
    {
        string result = await new AtcTools(_connection).SendAtcInstruction("Enfield 1-1", AtcAction.ClearToLand);

        Assert.Equal("Shown on screen in DCS: \"ATC to Enfield 1-1: cleared to land, fly heading 360\"", result);
    }

    [Fact]
    public async Task SendAtcInstruction_ReportsDcsReason_WhenItFails()
    {
        _connection.Result = DcsCommandResult.Failed("mission scripting isn't enabled");

        string result = await new AtcTools(_connection).SendAtcInstruction("Enfield 1-1", AtcAction.Orbit);

        Assert.Equal("Error: DCS didn't show the instruction: mission scripting isn't enabled", result);
    }
}
