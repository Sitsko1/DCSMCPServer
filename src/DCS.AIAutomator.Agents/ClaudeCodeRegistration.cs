namespace DCS.AIAutomator.Agents;

/// <summary>
/// Builds the <c>claude mcp</c> commands that register this app's MCP server with Claude Code
/// (user scope, HTTP transport, bearer header). Pure: running the process is the app's job. Flags
/// checked against Claude Code 2.1.283's <c>claude mcp add --help</c>; <c>--header</c> is variadic,
/// so it must come after the name and URL.
/// </summary>
public static class ClaudeCodeRegistration
{
    public const string ServerName = "dcs-aiautomator";

    /// <summary>Arguments for <c>claude</c>, for ProcessStartInfo.ArgumentList (no shell quoting).</summary>
    public static string[] AddArguments(string mcpUrl, string apiKey) =>
        ["mcp", "add", "--transport", "http", "--scope", "user", ServerName, mcpUrl, "--header", $"Authorization: Bearer {apiKey}"];

    /// <summary>Queries our entry: exit code 0 = registered, 1 = not (Claude Code 2.1.283).</summary>
    public static string[] GetArguments() => ["mcp", "get", ServerName];

    /// <summary>Removes only our user-scope entry; run before <see cref="AddArguments"/> so re-registering updates instead of duplicating.</summary>
    public static string[] RemoveArguments() => ["mcp", "remove", "--scope", "user", ServerName];

    /// <summary>The same registration as one line to paste into a terminal, for when the CLI can't be run.</summary>
    public static string CopyableCommand(string mcpUrl, string apiKey) =>
        $"claude mcp add --transport http --scope user {ServerName} {mcpUrl} --header \"Authorization: Bearer {apiKey}\"";

    /// <summary>
    /// Whether <c>claude mcp remove/get</c> failed only because our entry doesn't exist (Claude Code
    /// 2.1.283's wording), so Disconnect can treat it as already done.
    /// </summary>
    public static bool IsNotRegisteredOutput(string output) =>
        output.Contains($"No MCP server named \"{ServerName}\"", StringComparison.Ordinal);

    /// <summary>Removes the key from CLI output before it's shown or logged.</summary>
    public static string Redact(string text, string apiKey) =>
        string.IsNullOrEmpty(apiKey) ? text : text.Replace(apiKey, "***", StringComparison.Ordinal);
}
