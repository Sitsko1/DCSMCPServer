using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>DcsLogging against a real temp directory, as the Serilog file sink sees it.</summary>
public class DcsLoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DcsLoggingTests_" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string[] LogLines() =>
        Directory.GetFiles(_dir, "*.clef").SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void WritesStructuredJsonLines_WithSourceContext()
    {
        using (var logging = new DcsLogging(_dir, LogLevel.Information, retentionDays: 7))
        {
            logging.Provider.CreateLogger("DCS.Test").LogInformation("Bridge listening at {Url}", "http://127.0.0.1:5270/mcp");
        }

        string file = Assert.Single(Directory.GetFiles(_dir));
        Assert.Matches(@"dcs-aiautomator-\d{8}\.clef$", file);

        using var doc = JsonDocument.Parse(Assert.Single(LogLines()));
        JsonElement e = doc.RootElement;
        Assert.Equal("Bridge listening at {Url}", e.GetProperty("@mt").GetString()); // template, not a rendered string
        Assert.Equal("http://127.0.0.1:5270/mcp", e.GetProperty("Url").GetString());
        Assert.Equal("DCS.Test", e.GetProperty("SourceContext").GetString());
    }

    [Fact]
    public void LevelChange_AppliesImmediately()
    {
        using (var logging = new DcsLogging(_dir, LogLevel.Information, retentionDays: 7))
        {
            ILogger log = logging.Provider.CreateLogger("Test");
            log.LogDebug("hidden at Information");
            logging.SetMinimumLevel(LogLevel.Debug);
            log.LogDebug("shown at Debug");
        }

        string[] lines = LogLines();
        Assert.DoesNotContain(lines, l => l.Contains("hidden at Information"));
        Assert.Contains(lines, l => l.Contains("shown at Debug"));
    }

    [Fact]
    public void DeletesLogFilesOlderThanTheRetentionPeriod()
    {
        Directory.CreateDirectory(_dir);
        string old = Path.Combine(_dir, $"dcs-aiautomator-{DateTime.Now.AddDays(-30):yyyyMMdd}.clef");
        string recent = Path.Combine(_dir, $"dcs-aiautomator-{DateTime.Now.AddDays(-2):yyyyMMdd}.clef");
        foreach ((string path, int ageDays) in new[] { (old, 30), (recent, 2) })
        {
            File.WriteAllText(path, "{}\n");
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-ageDays));
        }

        using (var logging = new DcsLogging(_dir, LogLevel.Information, retentionDays: 7))
        {
            logging.Provider.CreateLogger("Test").LogInformation("opens today's file, which applies retention");
        }

        Assert.False(File.Exists(old), "a 30-day-old file should be deleted with 7-day retention");
        Assert.True(File.Exists(recent), "a 2-day-old file is within retention");
    }
}
