using DCS.Scripting;

public class LuaExportScriptGeneratorTests
{
    [Fact]
    public void Generate_InterpolatesHostAndPort()
    {
        string lua = LuaExportScriptGenerator.Generate("127.0.0.1", 1024);

        Assert.Contains("host = \"127.0.0.1\"", lua);
        Assert.Contains("port = 1024", lua);
    }

    [Fact]
    public void Generate_ChainsExistingHooksInsteadOfClobberingThem()
    {
        string lua = LuaExportScriptGenerator.Generate("127.0.0.1", 1024);

        Assert.Contains("local mcpBridgePrevStart = LuaExportStart", lua);
        Assert.Contains("if mcpBridgePrevStart then mcpBridgePrevStart() end", lua);
        Assert.Contains("local mcpBridgePrevAfterNextFrame = LuaExportAfterNextFrame", lua);
        Assert.Contains("local mcpBridgePrevStop = LuaExportStop", lua);
    }

    [Fact]
    public void Generate_DoesNotCallNonexistentLoGetMissionInfo()
    {
        // LoGetMissionInfo isn't a real Export API: pcall(nil) failed silently every frame, so the
        // app always showed "No active mission". Export hooks only run during a mission instead.
        string lua = LuaExportScriptGenerator.Generate("127.0.0.1", 1024);

        Assert.DoesNotContain("LoGetMissionInfo", lua);
        Assert.Contains("\"missionActive\":true", lua);
    }

    [Fact]
    public void Generate_ReportsNoMissionOnExportStop_BeforeClosingSockets()
    {
        string lua = LuaExportScriptGenerator.Generate("127.0.0.1", 1024);
        string stop = lua[lua.IndexOf("function LuaExportStop()")..];

        int sendNoMission = stop.IndexOf("{\"missionActive\":false}");
        Assert.True(sendNoMission >= 0, "LuaExportStop should send a missionActive:false line");
        Assert.True(sendNoMission < stop.IndexOf("client:close()"), "...before closing the client socket");
    }
}
