using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DCS.AIAutomator.Agents;

/// <summary>One thing that happened during a chat turn, parsed from <c>claude -p</c>'s stream-json output.</summary>
public abstract record ChatEvent;

/// <param name="ServerConnected">Whether Claude Code reached this app's MCP server.</param>
public sealed record ChatSessionStarted(string SessionId, IReadOnlyList<string> Tools, bool ServerConnected) : ChatEvent;

/// <summary>A piece of the assistant's reply as it streams in.</summary>
public sealed record ChatTextDelta(string Text) : ChatEvent;

/// <param name="Tool">The app tool's own name (e.g. <c>get_aircraft_state</c>), without the MCP prefix.</param>
public sealed record ChatToolCall(string Id, string Tool, string InputJson) : ChatEvent;

public sealed record ChatToolResult(string Id, string Text, bool IsError) : ChatEvent;

/// <summary>Claude Code asks whether it may run a tool that isn't pre-allowed (a DCS-changing one,
/// while confirmation is on). The turn waits until the app answers.</summary>
/// <param name="Tool">The app tool's own name, without the MCP prefix.</param>
public sealed record ChatPermissionRequest(string RequestId, string ToolUseId, string Tool, string InputJson) : ChatEvent;

/// <summary>The app's answer to a <see cref="ChatPermissionRequest"/>; <paramref name="Message"/> tells Claude why, on a deny.</summary>
public sealed record ChatPermissionDecision(bool Allow, string? Message = null);

/// <summary>The turn finished; <paramref name="Error"/> is set when Claude Code reported it as failed.</summary>
public sealed record ChatCompleted(string? Error, double? CostUsd) : ChatEvent;

/// <summary>The turn couldn't run or ended abnormally (no Claude Code, crash, cancelled).</summary>
public sealed record ChatFailed(string Message) : ChatEvent;

/// <summary>
/// The in-app chat (#16): drives the installed Claude Code headlessly (<c>claude -p</c>), using the
/// user's own Claude login (no API key stored here). Each run is isolated to this app: only this
/// app's MCP server (<c>--strict-mcp-config</c>), no built-in tools (<c>--tools ""</c>), and no user
/// settings, plugins, hooks or skills (<c>--restricted</c>, <c>--disable-slash-commands</c>). Without
/// that a probe loaded every installed MCP server and plugin: ~167k context tokens for one sentence.
/// Flags verified against Claude Code 2.1.288.
/// </summary>
public static class ClaudeCodeChat
{
    public const string ToolPrefix = "mcp__" + ClaudeCodeRegistration.ServerName + "__";

    /// <summary>Tools that only read: always run without asking.</summary>
    public static readonly string[] ReadOnlyTools = ["get_aircraft_state", "list_ai_flights"];

    /// <summary>Tools that change things in DCS: need an Approve click while confirmation is on (#16).</summary>
    public static readonly string[] DcsChangingTools = ["send_atc_instruction"];

    /// <summary>The tools pre-allowed for a turn; anything else makes Claude Code ask the app first.</summary>
    public static string[] AllowedTools(bool confirmDcsChanges) =>
        confirmDcsChanges ? ReadOnlyTools : [.. ReadOnlyTools, .. DcsChangingTools];

    public const string SystemPrompt =
        "You are the ATC and flight assistant inside DCS.AIAutomator, talking to a DCS World pilot in a single-player mission. " +
        "Use the dcs-aiautomator tools for every fact about the sim; never guess positions, altitudes, callsigns or airfields. " +
        "Call list_ai_flights before addressing a flight you haven't seen in this conversation. Headings are magnetic. " +
        "Altitudes and distances are in the units the tools report. Keep replies short and plain: the pilot reads them mid-flight.";

    /// <summary>
    /// The <c>claude</c> arguments for one turn. Neither the key (it's in the config file) nor the
    /// prompt (it goes in on stdin, <see cref="UserMessageJson"/>) is among them: command lines are
    /// readable by other local processes. Permission prompts come to the app as
    /// <c>control_request</c>s on stdout (<c>--permission-prompts host --permission-prompt-tool stdio</c>,
    /// the Agent SDK's protocol), so no approval tool has to be exposed on the MCP server.
    /// </summary>
    /// <param name="resumeSessionId">The previous turn's session, so the conversation continues; null for a new chat.</param>
    /// <param name="confirmDcsChanges">DCS-changing tools ask the app first (the chat's setting, on by default).</param>
    public static string[] Arguments(string mcpConfigPath, string? resumeSessionId, bool confirmDcsChanges, string? extraSystemPrompt = null)
    {
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json",
            "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--permission-prompts", "host", "--permission-prompt-tool", "stdio",
            "--tools", "",
            "--restricted",
            "--strict-mcp-config", "--mcp-config", mcpConfigPath,
            "--disable-slash-commands",
            "--allowedTools", string.Join(',', AllowedTools(confirmDcsChanges).Select(t => ToolPrefix + t)),
            "--system-prompt", SystemPrompt,
        };
        if (!string.IsNullOrWhiteSpace(extraSystemPrompt)) args.AddRange(["--append-system-prompt", extraSystemPrompt]);
        // An empty id would make claude fail ("--resume requires a valid session ID"): treat it as a new chat.
        if (!string.IsNullOrWhiteSpace(resumeSessionId)) args.AddRange(["--resume", resumeSessionId]);
        return args.ToArray();
    }

    /// <summary>The prompt, as the stream-json user message written to claude's stdin.</summary>
    public static string UserMessageJson(string prompt) =>
        new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt },
        }.ToJsonString();

    /// <summary>The answer to a permission request (Agent SDK protocol, verified against Claude Code 2.1.288):
    /// allow carries the tool's input back unchanged; deny carries a message Claude sees as the tool's error.</summary>
    public static string PermissionResponseJson(ChatPermissionRequest request, ChatPermissionDecision decision)
    {
        JsonObject answer = decision.Allow
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(request.InputJson) }
            : new JsonObject { ["behavior"] = "deny", ["message"] = decision.Message ?? "The pilot denied this action." };
        return new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = request.RequestId, ["response"] = answer },
        }.ToJsonString();
    }

    /// <summary>The <c>--mcp-config</c> file's content: this app's server only, with the bearer key.</summary>
    public static string McpConfigJson(string mcpUrl, string apiKey) =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ClaudeCodeRegistration.ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = mcpUrl,
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {apiKey}" },
                },
            },
        }.ToJsonString();

    /// <summary>Parses one stream-json line; null for lines the chat doesn't show (hooks, status, thinking, …).</summary>
    public static ChatEvent? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject e ? ParseEvent(e) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null; // malformed, or a field of an unexpected type: skip the line rather than end the chat
        }
    }

    private static ChatEvent? ParseEvent(JsonObject e)
    {

        switch ((string?)e["type"])
        {
            case "system" when Str(e["subtype"]) == "init":
                return new ChatSessionStarted(
                    (string?)e["session_id"] ?? "",
                    e["tools"]?.AsArray().Select(t => (string?)t).OfType<string>().ToList() ?? [],
                    e["mcp_servers"]?.AsArray().Any(s =>
                        Str(s?["name"]) == ClaudeCodeRegistration.ServerName && Str(s?["status"]) == "connected") == true);

            case "control_request" when Str(e["request"]?["subtype"]) == "can_use_tool":
                return new ChatPermissionRequest(
                    Str(e["request_id"]) ?? "",
                    Str(e["request"]?["tool_use_id"]) ?? "",
                    StripPrefix(Str(e["request"]?["tool_name"]) ?? ""),
                    e["request"]?["input"]?.ToJsonString() ?? "{}");

            case "stream_event" when Str(e["event"]?["delta"]?["type"]) == "text_delta":
                return new ChatTextDelta((string?)e["event"]?["delta"]?["text"] ?? "");

            case "assistant":
                // Text arrives as deltas; from the full message only the tool calls are new.
                // (One message carries at most one tool_use per content block; take the first.)
                return e["message"]?["content"]?.AsArray()
                    .Where(c => Str(c?["type"]) == "tool_use")
                    .Select(c => new ChatToolCall(
                        (string?)c!["id"] ?? "",
                        StripPrefix((string?)c["name"] ?? ""),
                        c["input"]?.ToJsonString() ?? "{}"))
                    .FirstOrDefault();

            case "user":
                return e["message"]?["content"]?.AsArray()
                    .Where(c => Str(c?["type"]) == "tool_result")
                    .Select(c => new ChatToolResult(
                        (string?)c!["tool_use_id"] ?? "",
                        ToolResultText(c["content"]),
                        (bool?)c["is_error"] == true))
                    .FirstOrDefault();

            case "result":
                string? subtype = (string?)e["subtype"];
                bool failed = (bool?)e["is_error"] == true || subtype != "success";
                // A failure's reason is in "result", or (e.g. bad arguments) only in an "errors" array.
                string? errors = e["errors"] is JsonArray list && list.Count > 0
                    ? string.Join("; ", list.Select(x => Str(x)).OfType<string>())
                    : null;
                return new ChatCompleted(failed ? Str(e["result"]) ?? errors ?? subtype ?? "Claude Code reported an error." : null,
                    (double?)e["total_cost_usd"]);

            default:
                return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string StripPrefix(string tool) => tool.StartsWith(ToolPrefix, StringComparison.Ordinal) ? tool[ToolPrefix.Length..] : tool;

    // tool_result content is a string or an array of content blocks; show the text blocks.
    private static string ToolResultText(JsonNode? content) => content switch
    {
        JsonValue v => (string?)v ?? "",
        JsonArray blocks => string.Join("\n", blocks.Where(b => Str(b?["type"]) == "text").Select(b => Str(b!["text"]))),
        _ => "",
    };

    /// <summary>
    /// Runs one turn and streams its events. A <see cref="ChatPermissionRequest"/> is yielded, then
    /// the turn waits for <paramref name="askPermission"/> before answering Claude Code. The MCP config (with the key) is written to a file in
    /// <paramref name="workDirectory"/> (the app's private folder, which is also the working directory so
    /// no project CLAUDE.md is picked up) and deleted afterwards. Cancelling kills the process.
    /// Never logs or returns the prompt, replies or tool arguments anywhere but the event stream.
    /// </summary>
    public static async IAsyncEnumerable<ChatEvent> RunAsync(
        string prompt, string mcpUrl, string apiKey, string workDirectory, string? resumeSessionId,
        bool confirmDcsChanges, Func<ChatPermissionRequest, CancellationToken, Task<ChatPermissionDecision>> askPermission,
        string? extraSystemPrompt = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workDirectory);
        string configPath = Path.Combine(workDirectory, $"mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, McpConfigJson(mcpUrl, apiKey));
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo("claude")
            {
                WorkingDirectory = workDirectory,
                RedirectStandardInput = true, // the prompt and permission answers go in here (stream-json)
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (string arg in Arguments(configPath, resumeSessionId, confirmDcsChanges, extraSystemPrompt)) startInfo.ArgumentList.Add(arg);

            string? startError = null;
            try
            {
                process = Process.Start(startInfo) ?? throw new Win32Exception("claude did not start");
            }
            catch (Win32Exception)
            {
                startError = "Claude Code (the 'claude' command) wasn't found. Install it, then connect it in Settings → AI agents.";
            }
            if (startError is not null)
            {
                yield return new ChatFailed(startError);
                yield break;
            }

            Task<string> stderr = process!.StandardError.ReadToEndAsync(CancellationToken.None);
            using CancellationTokenRegistration kill = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            });
            await WriteLineAsync(process, UserMessageJson(prompt));

            bool completed = false;
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is string line)
            {
                if (Parse(line) is not { } chatEvent) continue;
                yield return chatEvent;

                if (chatEvent is ChatPermissionRequest request)
                {
                    // The turn waits here until the app answers (e.g. an Approve/Deny click).
                    ChatPermissionDecision decision = await AskAsync(askPermission, request, cancellationToken);
                    if (!cancellationToken.IsCancellationRequested)
                        await WriteLineAsync(process, PermissionResponseJson(request, decision));
                }
                else if (chatEvent is ChatCompleted)
                {
                    completed = true;
                    CloseInput(process); // stream-json input: claude waits for more until stdin closes
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);

            if (cancellationToken.IsCancellationRequested)
            {
                yield return new ChatFailed("Stopped.");
            }
            else if (!completed)
            {
                string detail = (await stderr).Trim();
                yield return new ChatFailed($"Claude Code exited (code {process.ExitCode}) without an answer" +
                    (detail.Length > 0 ? $": {ClaudeCodeRegistration.Redact(detail, apiKey)}" : "."));
            }
        }
        finally
        {
            if (process is not null) CloseInput(process);
            process?.Dispose();
            try { File.Delete(configPath); } catch (IOException) { /* best effort; it's in the app's private folder */ }
        }
    }

    private static async Task<ChatPermissionDecision> AskAsync(
        Func<ChatPermissionRequest, CancellationToken, Task<ChatPermissionDecision>> askPermission,
        ChatPermissionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await askPermission(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ChatPermissionDecision(false, "Stopped by the pilot.");
        }
    }

    private static async Task WriteLineAsync(Process process, string line)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
        }
        catch (IOException) { /* claude already exited; its output says why */ }
        catch (ObjectDisposedException) { }
    }

    private static void CloseInput(Process process)
    {
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        catch (InvalidOperationException) { /* not started or already closed */ }
    }
}
