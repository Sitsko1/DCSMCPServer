using DCS.Scripting;

public class LuaScriptDeployerTests : IDisposable
{
    private readonly string _savedGamesDir = Path.Combine(Path.GetTempPath(), "DcsMcpBridgeTests_" + Guid.NewGuid());

    // Config marks it as a real DCS Saved Games folder (see DcsPathValidator).
    public LuaScriptDeployerTests() => Directory.CreateDirectory(Path.Combine(_savedGamesDir, "Config"));

    public void Dispose() => Directory.Delete(_savedGamesDir, recursive: true);

    private string ScriptsDir => Path.Combine(_savedGamesDir, "Scripts");
    private string HooksScriptPath => Path.Combine(ScriptsDir, "Hooks", LuaScriptDeployer.HooksScriptFileName);
    private string ExportLuaPath => Path.Combine(ScriptsDir, "Export.lua");

    // What earlier versions of this app deployed via Export.lua.
    private const string ExportDofileLine = "dofile(lfs.writedir()..[[Scripts\\DCS.AIAutomator\\DCSMcpBridgeExport.lua]])";
    private const string LegacyExportDofileLine = "dofile(lfs.writedir()..[[Scripts\\DCSMcpBridgeExport.lua]])";
    private const string OtherToolLine = "dofile(lfs.writedir()..[[Scripts\\DCS-BIOS\\BIOS.lua]])";
    private string OldExportScriptPath => Path.Combine(ScriptsDir, "DCS.AIAutomator", "DCSMcpBridgeExport.lua");
    private string LegacyExportScriptPath => Path.Combine(ScriptsDir, "DCSMcpBridgeExport.lua");

    [Fact]
    public void Deploy_WritesHooksScript_AndDoesNotCreateExportLua()
    {
        var result = LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        Assert.True(result.Success);
        Assert.Equal(LuaHooksScriptGenerator.Generate("127.0.0.1", 1024, "TestLinkSecret_0123456789"), File.ReadAllText(HooksScriptPath));
        Assert.False(File.Exists(ExportLuaPath));
    }

    [Fact]
    public void Deploy_ReportsAlreadyDeployed_WhenHooksScriptIsIdentical()
    {
        LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        var result = LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        Assert.True(result.Success);
        Assert.Contains("Already deployed", result.Message);
    }

    [Fact]
    public void Deploy_RewritesHooksScript_WhenStale()
    {
        LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");
        File.WriteAllText(HooksScriptPath, "-- stale");

        var result = LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        Assert.True(result.Success);
        Assert.DoesNotContain("Already deployed", result.Message);
        Assert.Equal(LuaHooksScriptGenerator.Generate("127.0.0.1", 1024, "TestLinkSecret_0123456789"), File.ReadAllText(HooksScriptPath));
    }

    [Fact]
    public void Deploy_MigratesFromExportLua_RemovingOnlyOurLinesAndOldScripts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OldExportScriptPath)!);
        File.WriteAllText(OldExportScriptPath, "-- old export script");
        File.WriteAllText(LegacyExportScriptPath, "-- legacy export script");
        string original = OtherToolLine + "\r\n" + ExportDofileLine + "\r\n" + LegacyExportDofileLine + "\r\n";
        File.WriteAllText(ExportLuaPath, original);

        var result = LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        Assert.True(result.Success);
        string exportLua = File.ReadAllText(ExportLuaPath);
        Assert.Contains(OtherToolLine, exportLua);
        Assert.DoesNotContain("DCSMcpBridgeExport.lua", exportLua);
        Assert.Equal(original, File.ReadAllText(ExportLuaPath + ".bak"));
        Assert.False(File.Exists(OldExportScriptPath));
        Assert.False(File.Exists(LegacyExportScriptPath));
        Assert.True(File.Exists(HooksScriptPath));
    }

    [Fact]
    public void Deploy_LeavesUnrelatedExportLuaUntouched_AndMakesNoBackup()
    {
        Directory.CreateDirectory(ScriptsDir);
        File.WriteAllText(ExportLuaPath, OtherToolLine + "\n");

        LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        Assert.Equal(OtherToolLine + "\n", File.ReadAllText(ExportLuaPath));
        Assert.False(File.Exists(ExportLuaPath + ".bak"));
    }

    [Fact]
    public void Deploy_StillMigrates_WhenHooksScriptIsAlreadyCurrent()
    {
        // e.g. the Hooks script was copied in by hand but Export.lua still loads the old one,
        // which would fight the Hooks script for the port.
        LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");
        File.WriteAllText(ExportLuaPath, ExportDofileLine + "\n");

        var result = LuaScriptDeployer.Deploy(_savedGamesDir, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        Assert.True(result.Success);
        Assert.DoesNotContain("DCSMcpBridgeExport.lua", File.ReadAllText(ExportLuaPath));
    }
}
