using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DCS.Scripting;

/// <summary>
/// Adds/removes this app's entry in Claude Desktop's <c>claude_desktop_config.json</c>. Claude
/// Desktop only launches stdio servers from this file, so the entry points at this app's own
/// executable in relay mode (<c>--mcp-relay</c>), which forwards stdio to the running app's HTTP
/// endpoint. The entry holds no URL and no key: the relay reads both at startup, so it never
/// needs updating when the port changes or the key is regenerated.
/// Only our key under <c>mcpServers</c> is touched; everything else is preserved.
/// </summary>
public static class ClaudeDesktopConfig
{
    public const string ServerName = ClaudeCodeRegistration.ServerName;
    public const string RelayArgument = "--mcp-relay";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The config with our entry added or updated. Throws on unparseable JSON rather than guess.</summary>
    public static string AddServer(string? configJson, string relayExecutablePath)
    {
        JsonObject root = Parse(configJson);
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        servers[ServerName] = new JsonObject
        {
            ["command"] = relayExecutablePath,
            ["args"] = new JsonArray(RelayArgument),
        };
        return Write(root);
    }

    public static string RemoveServer(string? configJson)
    {
        JsonObject root = Parse(configJson);
        (root["mcpServers"] as JsonObject)?.Remove(ServerName);
        return Write(root);
    }

    public static bool IsConnected(string? configJson)
    {
        try
        {
            return Parse(configJson)["mcpServers"] is JsonObject servers && servers.ContainsKey(ServerName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Adds our entry to the file, backing up an existing file first.</summary>
    /// <param name="backupPath">Where the backup goes (default: next to the config as .bak). A
    /// packaged app should pass a path in its own folder: new files it creates under AppData are
    /// redirected to a private location the user can't see.</param>
    public static void Connect(string configPath, string relayExecutablePath, string? backupPath = null) =>
        Rewrite(configPath, existing => AddServer(existing, relayExecutablePath), backupPath);

    /// <summary>Removes our entry from the file, backing it up first. No file: nothing to do.</summary>
    public static void Disconnect(string configPath, string? backupPath = null)
    {
        if (File.Exists(configPath)) Rewrite(configPath, RemoveServer, backupPath);
    }

    private static void Rewrite(string configPath, Func<string?, string> change, string? backupPath)
    {
        string? existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        string updated = change(existing); // throws before anything is written if the file is unparseable
        if (existing is not null)
        {
            string backup = backupPath ?? configPath + ".bak";
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(configPath, backup, overwrite: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, updated);
    }

    private static JsonObject Parse(string? configJson) =>
        string.IsNullOrWhiteSpace(configJson)
            ? new JsonObject()
            : JsonNode.Parse(configJson, documentOptions: ReadOptions) as JsonObject
              ?? throw new JsonException("claude_desktop_config.json is not a JSON object.");

    // Utf8JsonWriter + WriteTo keeps this reflection-free (AOT-safe), unlike JsonNode.ToJsonString(options).
    private static string Write(JsonObject root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
