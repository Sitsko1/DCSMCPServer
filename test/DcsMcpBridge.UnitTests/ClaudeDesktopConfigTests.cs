using System.Text.Json.Nodes;
using DCS.Scripting;

public class ClaudeDesktopConfigTests : IDisposable
{
    private const string Relay = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\dcs-aiautomator.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClaudeDesktopConfigTests_" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_dir, "claude_desktop_config.json");

    public ClaudeDesktopConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Another tool's server (with its own secret) and unrelated settings — must survive untouched.
    private const string ExistingConfig = """
        {
          "globalShortcut": "Ctrl+Space",
          "mcpServers": {
            "other-server": { "command": "npx", "args": ["other"], "env": { "OTHER_TOKEN": "s3cret" } }
          }
        }
        """;

    [Fact]
    public void AddServer_AddsOnlyOurRelayEntry_AndPreservesEverythingElse()
    {
        JsonNode result = JsonNode.Parse(ClaudeDesktopConfig.AddServer(ExistingConfig, Relay))!;

        JsonNode ours = result["mcpServers"]![ClaudeDesktopConfig.ServerName]!;
        Assert.Equal(Relay, ours["command"]!.GetValue<string>());
        Assert.Equal(["--mcp-relay"], ours["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Null(ours["env"]); // no key or URL in the file — the relay reads them itself

        JsonNode original = JsonNode.Parse(ExistingConfig)!;
        Assert.True(JsonNode.DeepEquals(original["mcpServers"]!["other-server"], result["mcpServers"]!["other-server"]));
        Assert.Equal("Ctrl+Space", result["globalShortcut"]!.GetValue<string>());
    }

    [Fact]
    public void AddServer_IsIdempotent_UpdatingRatherThanDuplicating()
    {
        string once = ClaudeDesktopConfig.AddServer(ExistingConfig, @"C:\old\path.exe");
        string twice = ClaudeDesktopConfig.AddServer(once, Relay);

        JsonObject servers = JsonNode.Parse(twice)!["mcpServers"]!.AsObject();
        Assert.Equal(2, servers.Count);
        Assert.Equal(Relay, servers[ClaudeDesktopConfig.ServerName]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void AddServer_ToAnEmptyOrMissingConfig_CreatesIt()
    {
        foreach (string? empty in new[] { null, "", "   " })
        {
            JsonNode result = JsonNode.Parse(ClaudeDesktopConfig.AddServer(empty, Relay))!;
            Assert.NotNull(result["mcpServers"]![ClaudeDesktopConfig.ServerName]);
        }
    }

    [Fact]
    public void RemoveServer_RemovesOnlyOurs()
    {
        string connected = ClaudeDesktopConfig.AddServer(ExistingConfig, Relay);

        JsonNode result = JsonNode.Parse(ClaudeDesktopConfig.RemoveServer(connected))!;

        Assert.Null(result["mcpServers"]![ClaudeDesktopConfig.ServerName]);
        Assert.NotNull(result["mcpServers"]!["other-server"]);
        Assert.Equal("Ctrl+Space", result["globalShortcut"]!.GetValue<string>());
    }

    [Fact]
    public void IsConnected_ReflectsOurEntry()
    {
        Assert.False(ClaudeDesktopConfig.IsConnected(ExistingConfig));
        Assert.True(ClaudeDesktopConfig.IsConnected(ClaudeDesktopConfig.AddServer(ExistingConfig, Relay)));
        Assert.False(ClaudeDesktopConfig.IsConnected(null));
    }

    [Fact]
    public void Connect_BacksUpTheOriginalFileFirst()
    {
        File.WriteAllText(ConfigPath, ExistingConfig);

        ClaudeDesktopConfig.Connect(ConfigPath, Relay);

        Assert.Equal(ExistingConfig, File.ReadAllText(ConfigPath + ".bak"));
        Assert.True(ClaudeDesktopConfig.IsConnected(File.ReadAllText(ConfigPath)));
    }

    [Fact]
    public void Connect_WithNoExistingFile_CreatesIt_WithoutABackup()
    {
        ClaudeDesktopConfig.Connect(ConfigPath, Relay);

        Assert.True(ClaudeDesktopConfig.IsConnected(File.ReadAllText(ConfigPath)));
        Assert.False(File.Exists(ConfigPath + ".bak"));
    }

    [Fact]
    public void Disconnect_BacksUpAndRemovesOnlyOurs()
    {
        File.WriteAllText(ConfigPath, ClaudeDesktopConfig.AddServer(ExistingConfig, Relay));

        ClaudeDesktopConfig.Disconnect(ConfigPath);

        string after = File.ReadAllText(ConfigPath);
        Assert.False(ClaudeDesktopConfig.IsConnected(after));
        Assert.NotNull(JsonNode.Parse(after)!["mcpServers"]!["other-server"]);
        Assert.True(File.Exists(ConfigPath + ".bak"));
    }

    [Fact]
    public void Connect_RefusesToRewriteAnUnparseableFile()
    {
        const string broken = "{ \"mcpServers\": { oops";
        File.WriteAllText(ConfigPath, broken);

        Assert.ThrowsAny<Exception>(() => ClaudeDesktopConfig.Connect(ConfigPath, Relay));
        Assert.Equal(broken, File.ReadAllText(ConfigPath)); // never clobber the user's config
    }
}
