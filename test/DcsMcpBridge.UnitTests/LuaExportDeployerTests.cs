public class LuaExportDeployerTests : IDisposable
{
    private readonly string _savedGamesDir = Path.Combine(Path.GetTempPath(), "DcsMcpBridgeTests_" + Guid.NewGuid());

    public void Dispose() => Directory.Delete(_savedGamesDir, recursive: true);

    [Fact]
    public void Deploy_WritesExportScriptAndCreatesExportLua_WhenNoneExists()
    {
        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        string scriptsDir = Path.Combine(_savedGamesDir, "Scripts");
        Assert.True(File.Exists(Path.Combine(scriptsDir, LuaExportDeployer.ExportScriptFileName)));

        string exportLua = File.ReadAllText(Path.Combine(scriptsDir, "Export.lua"));
        Assert.Contains(LuaExportDeployer.ExportScriptFileName, exportLua);
    }

    [Fact]
    public void Deploy_AppendsDofileAndBacksUpExistingExportLua_WhenNotAlreadyWired()
    {
        string scriptsDir = Path.Combine(_savedGamesDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        string exportLuaPath = Path.Combine(scriptsDir, "Export.lua");
        File.WriteAllText(exportLuaPath, "-- some other tool's dofile\ndofile(lfs.writedir()..[[Scripts\\OtherTool.lua]])\n");

        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        string exportLua = File.ReadAllText(exportLuaPath);
        Assert.Contains("OtherTool.lua", exportLua);
        Assert.Contains(LuaExportDeployer.ExportScriptFileName, exportLua);
        Assert.True(File.Exists(exportLuaPath + ".bak"));
        Assert.Contains("OtherTool.lua", File.ReadAllText(exportLuaPath + ".bak"));
    }

    [Fact]
    public void Deploy_IsIdempotent_DoesNotDuplicateDofileOrRewriteBackup()
    {
        LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);
        string exportLuaPath = Path.Combine(_savedGamesDir, "Scripts", "Export.lua");
        string afterFirstDeploy = File.ReadAllText(exportLuaPath);

        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        string afterSecondDeploy = File.ReadAllText(exportLuaPath);
        Assert.Equal(afterFirstDeploy, afterSecondDeploy);
        Assert.False(File.Exists(exportLuaPath + ".bak"));
    }
}
