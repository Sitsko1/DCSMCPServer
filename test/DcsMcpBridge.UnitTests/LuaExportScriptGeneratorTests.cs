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
}
