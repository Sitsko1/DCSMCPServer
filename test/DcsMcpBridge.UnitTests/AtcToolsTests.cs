public class AtcToolsTests
{
    [Fact]
    public void SendAtcInstruction_FormatsExpectedLuaCommand()
    {
        var connection = new FakeDcsConnection();
        var tools = new AtcTools(connection);

        tools.SendAtcInstruction("Enfield 1-1", AtcAction.Vectors, heading: 270);

        Assert.Equal(
            "trigger.action.outText(\"ATC to Enfield 1-1: Perform Vectors fly heading 270\", 10)\n",
            connection.LastLuaCommand);
    }

    [Fact]
    public void SendAtcInstruction_DefaultsHeadingTo360()
    {
        var connection = new FakeDcsConnection();
        var tools = new AtcTools(connection);

        tools.SendAtcInstruction("Enfield 1-1", AtcAction.Hold);

        Assert.Contains("fly heading 360", connection.LastLuaCommand);
    }

    [Fact]
    public void SendAtcInstruction_ReturnsSuccessMessage_WhenConnectionSucceeds()
    {
        var connection = new FakeDcsConnection { ShouldSucceed = true };
        var tools = new AtcTools(connection);

        string result = tools.SendAtcInstruction("Enfield 1-1", AtcAction.ClearToLand);

        Assert.Equal("ATC instruction broadcasted successfully.", result);
    }

    [Fact]
    public void SendAtcInstruction_ReturnsErrorMessage_WhenConnectionFails()
    {
        var connection = new FakeDcsConnection { ShouldSucceed = false };
        var tools = new AtcTools(connection);

        string result = tools.SendAtcInstruction("Enfield 1-1", AtcAction.Orbit);

        Assert.Equal("Error: DCS interface is down.", result);
    }
}
