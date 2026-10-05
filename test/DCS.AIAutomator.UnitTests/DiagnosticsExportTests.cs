using System.IO.Compression;
using Microsoft.Extensions.Logging;

public class DiagnosticsExportTests : IDisposable
{
    private readonly string _logs = Directory.CreateTempSubdirectory("dcs-export-").FullName;

    public void Dispose() => Directory.Delete(_logs, recursive: true);

    private static Dictionary<string, string> ReadZip(MemoryStream zip)
    {
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
    }

    [Fact]
    public void Zip_HasTheAppAndRelayLogs_AndAbout_ButNothingElseFromTheFolder()
    {
        File.WriteAllText(Path.Combine(_logs, "dcs-aiautomator-20261003.clef"), "{\"@m\":\"app\"}");
        File.WriteAllText(Path.Combine(_logs, "dcs-aiautomator-relay-20261004.clef"), "{\"@m\":\"relay\"}");
        File.WriteAllText(Path.Combine(_logs, "claude_desktop_config.json"), "{\"mcpServers\":{\"other\":{}}}");
        File.WriteAllText(Path.Combine(_logs, "notes.txt"), "unrelated");

        var zip = new MemoryStream();
        DiagnosticsExport.WriteZip(zip, _logs, "about text");

        var entries = ReadZip(zip);
        Assert.Equal(["about.txt", "dcs-aiautomator-20261003.clef", "dcs-aiautomator-relay-20261004.clef"], entries.Keys.Order());
        Assert.Equal("{\"@m\":\"relay\"}", entries["dcs-aiautomator-relay-20261004.clef"]);
        Assert.Equal("about text", entries["about.txt"]);
    }

    [Fact]
    public void Zip_ReadsTheLogTheRunningLoggerHoldsOpen()
    {
        using (var logging = new DcsLogging(_logs, LogLevel.Information, retentionDays: 7))
        {
            logging.Provider.CreateLogger("Test").LogInformation("still being written");

            var zip = new MemoryStream();
            DiagnosticsExport.WriteZip(zip, _logs, "");

            string log = Assert.Single(ReadZip(zip), e => e.Key.EndsWith(".clef")).Value;
            Assert.Contains("still being written", log);
        }
    }

    [Fact]
    public void Zip_WithNoLogFolder_StillHasAbout()
    {
        var zip = new MemoryStream();
        DiagnosticsExport.WriteZip(zip, Path.Combine(_logs, "missing"), "about text");

        Assert.Equal(["about.txt"], ReadZip(zip).Keys);
    }

    [Fact]
    public void About_DescribesTheState_WithoutAnySecret()
    {
        const string apiKey = "ApiKeySentinel_0123456789";
        const string linkSecret = "LinkSecretSentinel_0123456789";
        var status = new BridgeStatus
        {
            BridgeState = BridgeState.Running,
            McpEndpoint = "http://127.0.0.1:5270/mcp",
            DcsEndpoint = "127.0.0.1:1024",
            DcsConnected = true,
            DcsScriptOutdated = true,
            CurrentMission = new MissionInfo("Quick Start", "Caucasus", "F/A-18C"),
            Units = UnitSystem.Metric,
        };
        status.McpClients.RecordRequest("claude-code", DateTimeOffset.UtcNow);

        string about = DiagnosticsExport.About("1.2.3.0", "Windows 11 (10.0.26200)", status,
            [("Claude Code", true), ("Claude Desktop", false)], DateTimeOffset.UtcNow);

        Assert.Contains("App version:    1.2.3.0", about);
        Assert.Contains("Windows:        Windows 11 (10.0.26200)", about);
        Assert.Contains($"Wire protocol:  {LuaHooksScriptGenerator.ProtocolVersion}", about);
        Assert.Contains("Bridge:         Running at http://127.0.0.1:5270/mcp", about);
        Assert.Contains("DCS:            SCRIPT OUTDATED (127.0.0.1:1024)", about);
        Assert.Contains("Mission:        Quick Start | Caucasus | F/A-18C", about);
        Assert.Contains("AI clients:     Active (claude-code)", about);
        Assert.Contains("Claude Code: detected", about);
        Assert.Contains("Claude Desktop: not found", about);
        // about.txt takes no keys at all; the secrets in scope here must never show up in it.
        Assert.DoesNotContain(apiKey, about);
        Assert.DoesNotContain(linkSecret, about);
    }
}
