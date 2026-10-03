using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DCS.Scripting;

namespace DCS.AIAutomator;

public sealed record AgentResult(bool Success, string Message);

/// <summary>
/// One AI agent that can be connected to this app's MCP server (Settings → AI agents). Add an
/// implementation per agent; the page lists only the ones detected on this machine (#15: Claude
/// first, OpenAI/Gemini later).
/// </summary>
public interface IAgentIntegration
{
    string Name { get; }

    /// <summary>One line under the name explaining what Connect does.</summary>
    string Description { get; }

    /// <summary>Cheap, synchronous: is the agent installed on this machine?</summary>
    bool IsDetected();

    /// <summary>Null when it can't be determined.</summary>
    Task<bool?> IsConnectedAsync();

    Task<AgentResult> ConnectAsync();

    Task<AgentResult> DisconnectAsync();

    /// <summary>Whether its entry embeds the URL/key, so a port change or key regeneration needs a reconnect.</summary>
    bool NeedsUpdateWhenKeyOrPortChanges { get; }
}

/// <summary>Claude Code: HTTP + bearer header, registered through its own CLI (see #7).</summary>
public sealed class ClaudeCodeAgent(Func<string> mcpUrl, Func<string> apiKey) : IAgentIntegration
{
    public string Name => "Claude Code";
    public string Description => "Registers this app's MCP server (URL and API key) with Claude Code for your user, via the claude CLI.";
    public bool NeedsUpdateWhenKeyOrPortChanges => true;

    public bool IsDetected() => ClaudeCodeRegistrar.IsInstalled();

    public Task<bool?> IsConnectedAsync() => ClaudeCodeRegistrar.IsRegisteredAsync();

    public async Task<AgentResult> ConnectAsync()
    {
        ClaudeCodeRegistrar.Result result = await ClaudeCodeRegistrar.RegisterAsync(mcpUrl(), apiKey());
        return new AgentResult(result.Success, result.Message);
    }

    public async Task<AgentResult> DisconnectAsync()
    {
        ClaudeCodeRegistrar.Result result = await ClaudeCodeRegistrar.UnregisterAsync();
        return new AgentResult(result.Success, result.Message);
    }

    /// <summary>For the "Copy command" fallback (contains the key).</summary>
    public string CopyableCommand() => ClaudeCodeRegistration.CopyableCommand(mcpUrl(), apiKey());
}

/// <summary>
/// Claude Desktop: its config only takes stdio servers, so the entry launches this app's own exe
/// as a relay (<c>dcs-aiautomator.exe --mcp-relay</c>, via the package's app execution alias).
/// The entry holds no URL or key, so it never needs updating when either changes.
/// </summary>
public sealed class ClaudeDesktopAgent(string backupDirectory) : IAgentIntegration
{
    private static readonly string ConfigDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "claude_desktop_config.json");

    /// <summary>The app execution alias (Package.appxmanifest), as Claude Desktop must launch it.</summary>
    public static string RelayExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "dcs-aiautomator.exe");

    public string Name => "Claude Desktop";
    public string Description => "Adds a relay entry to Claude Desktop's config (no key stored there). Restart Claude Desktop afterwards.";
    public bool NeedsUpdateWhenKeyOrPortChanges => false;

    public bool IsDetected() => Directory.Exists(ConfigDirectory);

    public Task<bool?> IsConnectedAsync()
    {
        try
        {
            return Task.FromResult<bool?>(File.Exists(ConfigPath) && ClaudeDesktopConfig.IsConnected(File.ReadAllText(ConfigPath)));
        }
        catch (IOException)
        {
            return Task.FromResult<bool?>(null);
        }
    }

    public Task<AgentResult> ConnectAsync()
    {
        // A packaged app's *new* files under AppData are redirected to a private copy Claude
        // Desktop can't see; only edits to an existing file reach the real one. So Claude Desktop
        // must create the file first.
        if (!File.Exists(ConfigPath))
        {
            return Task.FromResult(new AgentResult(false,
                "Claude Desktop has no config file yet. In Claude Desktop open Settings → Developer → Edit Config (that creates it), then Connect again."));
        }
        return Rewrite(() => ClaudeDesktopConfig.Connect(ConfigPath, RelayExecutablePath, BackupPath()),
            "Connected. Restart Claude Desktop to load it.");
    }

    public Task<AgentResult> DisconnectAsync() =>
        Rewrite(() => ClaudeDesktopConfig.Disconnect(ConfigPath, BackupPath()),
            "Removed from Claude Desktop. Restart Claude Desktop to apply.");

    private string BackupPath() =>
        Path.Combine(backupDirectory, $"claude_desktop_config.{DateTime.Now:yyyyMMdd-HHmmss}.json.bak");

    private static Task<AgentResult> Rewrite(Action change, string successMessage)
    {
        try
        {
            change();
            return Task.FromResult(new AgentResult(true, successMessage));
        }
        catch (JsonException)
        {
            return Task.FromResult(new AgentResult(false, "Claude Desktop's config file isn't valid JSON, so it was left untouched. Fix it, then retry."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new AgentResult(false, $"Couldn't update Claude Desktop's config: {ex.Message}"));
        }
    }
}

public static class AgentIntegrations
{
    /// <summary>Every supported agent; the UI shows only those whose IsDetected() is true.</summary>
    public static IReadOnlyList<IAgentIntegration> All(App app, string backupDirectory) =>
    [
        new ClaudeCodeAgent(() => app.McpUrl, () => app.McpApiKey),
        new ClaudeDesktopAgent(backupDirectory),
    ];

    public static IReadOnlyList<IAgentIntegration> Detected(App app, string backupDirectory) =>
        All(app, backupDirectory).Where(a => a.IsDetected()).ToList();
}
