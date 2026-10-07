using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DCS.AIAutomator.Agents;
using DCS.AIAutomator.Core;
using Microsoft.Extensions.Logging;

namespace DCS.AIAutomator;

/// <summary>One entry in the chat transcript.</summary>
public abstract class ChatItem;

public sealed class ChatUserItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

/// <summary>An assistant reply; <see cref="Text"/> grows while it streams.</summary>
public sealed class ChatAssistantItem : ChatItem
{
    public string Text { get; set; } = "";
}

public sealed class ChatToolItem(string tool, string inputJson) : ChatItem
{
    public string Tool { get; } = tool;
    public string InputJson { get; } = inputJson;
    public string? Result { get; set; }
    public bool IsError { get; set; }
}

public enum ChatPermissionState { Pending, Approved, Denied }

/// <summary>A DCS-changing tool call waiting for (or answered with) the pilot's Approve / Deny.</summary>
public sealed class ChatPermissionItem(string tool, string inputJson) : ChatItem
{
    public string Tool { get; } = tool;
    public string InputJson { get; } = inputJson;
    public ChatPermissionState State { get; set; } = ChatPermissionState.Pending;
    internal TaskCompletionSource<ChatPermissionDecision> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ChatNoteItem(string text, bool isError) : ChatItem
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

/// <summary>
/// The chat's conversation (#16/#48), owned by the app so it outlives whichever view shows it:
/// docked in the main window or popped out into its own window. It runs the turns
/// (<see cref="ClaudeCodeChat"/>) and keeps the transcript and Claude Code session id; views only
/// render <see cref="Items"/> and its change events, so moving the chat between hosts doesn't
/// interrupt a reply that's streaming. Used on the UI thread only. Prompts, replies and tool
/// arguments are never logged.
/// </summary>
public sealed class ChatSession
{
    private readonly BridgeStatus _status;
    private readonly Func<string> _mcpUrl;
    private readonly Func<string> _apiKey;
    private readonly string _workDirectory;
    private readonly ILogger _log;
    private readonly Func<bool> _confirmDcsChanges;
    private readonly List<ChatItem> _items = new();
    private string? _sessionId;
    private CancellationTokenSource? _running;

    /// <param name="confirmDcsChanges">The setting, read at the start of each turn: DCS-changing tool calls need an Approve click.</param>
    public ChatSession(BridgeStatus status, Func<string> mcpUrl, Func<string> apiKey, string workDirectory, ILogger log,
        Func<bool> confirmDcsChanges)
    {
        _confirmDcsChanges = confirmDcsChanges;
        _status = status;
        _mcpUrl = mcpUrl;
        _apiKey = apiKey;
        _workDirectory = workDirectory;
        _log = log;
        AddNotInstalledNoteIfNeeded();
    }

    public IReadOnlyList<ChatItem> Items => _items;
    public bool IsRunning => _running is not null;

    public event Action<ChatItem>? ItemAdded;
    public event Action<ChatItem>? ItemChanged;
    public event Action? Cleared;
    public event Action? RunningChanged;

    public void Stop() => _running?.Cancel();

    /// <summary>The pilot's Approve / Deny on a pending tool call; the waiting turn continues with it.</summary>
    public void Decide(ChatPermissionItem item, bool allow)
    {
        if (item.State != ChatPermissionState.Pending) return;
        item.State = allow ? ChatPermissionState.Approved : ChatPermissionState.Denied;
        ItemChanged?.Invoke(item);
        _log.LogInformation("Chat tool call {Tool} {Decision} by the pilot", item.Tool, allow ? "approved" : "denied");
        item.Answer.TrySetResult(allow
            ? new ChatPermissionDecision(true)
            : new ChatPermissionDecision(false, "The pilot denied this action. Don't retry it unless the pilot asks again."));
    }

    // Called by the engine for each permission request: show a card, wait for the click (or Stop).
    private async Task<ChatPermissionDecision> AskAsync(ChatPermissionRequest request, CancellationToken cancellationToken)
    {
        var item = new ChatPermissionItem(request.Tool, request.InputJson);
        Add(item);
        using (cancellationToken.Register(() => item.Answer.TrySetCanceled(cancellationToken)))
        {
            return await item.Answer.Task;
        }
    }

    public void NewChat()
    {
        _running?.Cancel();
        _sessionId = null;
        _items.Clear();
        Cleared?.Invoke();
        AddNotInstalledNoteIfNeeded();
    }

    public async void Send(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0 || _running is not null) return;
        if (_status.BridgeState != BridgeState.Running)
        {
            Add(new ChatNoteItem("The MCP bridge isn't running, so Claude can't reach the DCS tools. Check the BRIDGE lamp on the main window.", isError: true));
            return;
        }

        Add(new ChatUserItem(prompt));
        using var running = _running = new CancellationTokenSource();
        RunningChanged?.Invoke();

        ChatAssistantItem? reply = null; // a tool call starts a new reply block after it
        var tools = new Dictionary<string, ChatToolItem>();
        try
        {
            await foreach (ChatEvent chatEvent in ClaudeCodeChat.RunAsync(
                prompt, _mcpUrl(), _apiKey(), _workDirectory, _sessionId, _confirmDcsChanges(), AskAsync,
                cancellationToken: running.Token))
            {
                switch (chatEvent)
                {
                    case ChatSessionStarted started:
                        _sessionId = started.SessionId;
                        if (!started.ServerConnected)
                            Add(new ChatNoteItem("Claude Code couldn't connect to this app's MCP server, so it has no DCS tools this turn.", isError: true));
                        break;
                    case ChatTextDelta delta:
                        if (reply is null) Add(reply = new ChatAssistantItem());
                        reply.Text += delta.Text;
                        ItemChanged?.Invoke(reply);
                        break;
                    case ChatToolCall call:
                        var tool = new ChatToolItem(call.Tool, call.InputJson);
                        tools[call.Id] = tool;
                        Add(tool);
                        reply = null;
                        break;
                    case ChatToolResult result when tools.TryGetValue(result.Id, out ChatToolItem? item):
                        item.Result = result.Text;
                        item.IsError = result.IsError;
                        ItemChanged?.Invoke(item);
                        break;
                    case ChatCompleted done:
                        if (done.Error is not null) Add(new ChatNoteItem(done.Error, isError: true));
                        _log.LogInformation("Chat turn {Outcome}; cost {CostUsd} USD", done.Error is null ? "completed" : "failed", done.CostUsd);
                        break;
                    case ChatFailed failed:
                        Add(new ChatNoteItem(failed.Message, isError: failed.Message != "Stopped."));
                        _log.LogWarning("Chat turn didn't complete: {Reason}", failed.Message);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chat turn crashed");
            Add(new ChatNoteItem($"Something went wrong: {ex.Message}", isError: true));
        }
        finally
        {
            _running = null;
            RunningChanged?.Invoke();
        }
    }

    private void Add(ChatItem item)
    {
        _items.Add(item);
        ItemAdded?.Invoke(item);
    }

    private void AddNotInstalledNoteIfNeeded()
    {
        if (!ClaudeCodeRegistrar.IsInstalled())
        {
            Add(new ChatNoteItem("Claude Code isn't installed on this PC. The chat drives your installed Claude Code (no API key needed). " +
                                 "Install it, then check Settings → AI agents.", isError: true));
        }
    }
}
