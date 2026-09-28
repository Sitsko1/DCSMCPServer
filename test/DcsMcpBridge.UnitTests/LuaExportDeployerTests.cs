using DCS.Scripting;

public class LuaExportDeployerTests : IDisposable
{
    private readonly string _savedGamesDir = Path.Combine(Path.GetTempPath(), "DcsMcpBridgeTests_" + Guid.NewGuid());

    // Config marks it as a real DCS Saved Games folder (see DcsPathValidator).
    public LuaExportDeployerTests() => Directory.CreateDirectory(Path.Combine(_savedGamesDir, "Config"));

    public void Dispose() => Directory.Delete(_savedGamesDir, recursive: true);

    private string ScriptsDir => Path.Combine(_savedGamesDir, "Scripts");
    private string AppDir => Path.Combine(ScriptsDir, LuaExportDeployer.AppSubdirectoryName);
    private string ExportScriptPath => Path.Combine(AppDir, LuaExportDeployer.ExportScriptFileName);
    private string ExportLuaPath => Path.Combine(ScriptsDir, "Export.lua");

    [Fact]
    public void Deploy_WritesExportScriptInAppSubdirectoryAndCreatesExportLua_WhenNoneExists()
    {
        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        Assert.True(File.Exists(ExportScriptPath));

        string exportLua = File.ReadAllText(ExportLuaPath);
        Assert.Contains(LuaExportDeployer.ExportScriptFileName, exportLua);
        Assert.Contains(LuaExportDeployer.AppSubdirectoryName, exportLua);
    }

    [Fact]
    public void Deploy_AppendsDofileAndBacksUpExistingExportLua_WhenNotAlreadyWired()
    {
        Directory.CreateDirectory(ScriptsDir);
        File.WriteAllText(ExportLuaPath, "-- some other tool's dofile\ndofile(lfs.writedir()..[[Scripts\\OtherTool.lua]])\n");

        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        string exportLua = File.ReadAllText(ExportLuaPath);
        Assert.Contains("OtherTool.lua", exportLua);
        Assert.Contains(LuaExportDeployer.ExportScriptFileName, exportLua);
        Assert.True(File.Exists(ExportLuaPath + ".bak"));
        Assert.Contains("OtherTool.lua", File.ReadAllText(ExportLuaPath + ".bak"));
    }

    [Fact]
    public void Deploy_IsIdempotent_DoesNotDuplicateDofileOrRewriteBackup()
    {
        LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);
        string afterFirstDeploy = File.ReadAllText(ExportLuaPath);

        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        string afterSecondDeploy = File.ReadAllText(ExportLuaPath);
        Assert.Equal(afterFirstDeploy, afterSecondDeploy);
        Assert.False(File.Exists(ExportLuaPath + ".bak"));
    }

    [Fact]
    public void Deploy_ReportsAlreadyDeployed_WhenScriptExistsAndIsIdentical()
    {
        LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        Assert.Contains("Already deployed", result.Message);
    }

    [Fact]
    public void Deploy_RewiresLegacyDofileToAppSubdirectory_WhenPreviousVersionWasDeployed()
    {
        // Pre-subdirectory versions deployed the script directly under Scripts\.
        const string legacyLine = "dofile(lfs.writedir()..[[Scripts\\DCSMcpBridgeExport.lua]])";
        Directory.CreateDirectory(ScriptsDir);
        File.WriteAllText(ExportLuaPath, "dofile(lfs.writedir()..[[Scripts\\OtherTool.lua]])\n" + legacyLine + "\n");

        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        string exportLua = File.ReadAllText(ExportLuaPath);
        Assert.DoesNotContain(legacyLine, exportLua);
        Assert.Contains("Scripts\\DCS.AIAutomator\\DCSMcpBridgeExport.lua", exportLua);
        Assert.Contains("OtherTool.lua", exportLua);
        Assert.Contains(legacyLine, File.ReadAllText(ExportLuaPath + ".bak"));
    }

    [Fact]
    public void Deploy_RewritesScript_WhenWiredButScriptIsStale()
    {
        LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);
        File.WriteAllText(ExportScriptPath, "-- stale content");

        var result = LuaExportDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024);

        Assert.True(result.Success);
        Assert.Contains("Deployed", result.Message);
        Assert.Contains("DCSMcpBridgeExport.lua", File.ReadAllText(ExportScriptPath));
    }
}
