using System;
using System.Collections.Generic;
using System.Threading;
using DCS.AIAutomator.Agents;
using DCS.AIAutomator.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DCS.AIAutomator;

/// <summary>
/// The in-app chat (#16): each message runs one <c>claude -p</c> turn through
/// <see cref="ClaudeCodeChat"/>, resuming the previous turn's session so the conversation continues.
/// The transcript is built in code-behind, like <see cref="ToastHost"/>. Prompts, replies and tool
/// arguments are never logged; only that a turn ran, how it ended and what it cost.
/// </summary>
public sealed partial class ChatWindow : Window
{
    private readonly BridgeStatus _status;
    private readonly Func<string> _mcpUrl;
    private readonly Func<string> _apiKey;
    private readonly string _workDirectory;
    private readonly ILogger _log;

    private string? _sessionId;
    private CancellationTokenSource? _running;

    public ChatWindow(BridgeStatus status, Func<string> mcpUrl, Func<string> apiKey, string workDirectory, ILogger log)
    {
        InitializeComponent();
        _status = status;
        _mcpUrl = mcpUrl;
        _apiKey = apiKey;
        _workDirectory = workDirectory;
        _log = log;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 720));

        if (!ClaudeCodeRegistrar.IsInstalled())
        {
            AddNote("Claude Code isn't installed on this PC. The chat drives your installed Claude Code (no API key needed). " +
                    "Install it, then check Settings → AI agents.", isError: true);
        }
    }

    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter sends; Shift+Enter is a new line.
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Enter && !shift)
        {
            e.Handled = true;
            Send();
        }
    }

    private void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (_running is not null) _running.Cancel(); // the button reads "Stop" while a turn runs
        else Send();
    }

    private void OnNewChatClicked(object sender, RoutedEventArgs e)
    {
        _running?.Cancel();
        _sessionId = null;
        Transcript.Children.Clear();
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async void Send()
    {
        string prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0 || _running is not null) return;
        if (_status.BridgeState != BridgeState.Running)
        {
            AddNote("The MCP bridge isn't running, so Claude can't reach the DCS tools. Check the BRIDGE lamp on the main window.", isError: true);
            return;
        }

        PromptBox.Text = "";
        AddUserMessage(prompt);
        SetRunning(true);
        using var running = _running = new CancellationTokenSource();

        TextBlock? reply = null; // the current assistant text block; a tool call starts a new one after it
        var toolRows = new Dictionary<string, TextBlock>();
        string outcome = "unknown";
        try
        {
            await foreach (ChatEvent chatEvent in ClaudeCodeChat.RunAsync(
                prompt, _mcpUrl(), _apiKey(), _workDirectory, _sessionId, cancellationToken: running.Token))
            {
                switch (chatEvent)
                {
                    case ChatSessionStarted started:
                        _sessionId = started.SessionId;
                        if (!started.ServerConnected)
                            AddNote("Claude Code couldn't connect to this app's MCP server, so it has no DCS tools this turn.", isError: true);
                        break;
                    case ChatTextDelta delta:
                        reply ??= AddAssistantMessage();
                        reply.Text += delta.Text;
                        break;
                    case ChatToolCall call:
                        toolRows[call.Id] = AddToolCall(call);
                        reply = null;
                        break;
                    case ChatToolResult result:
                        if (toolRows.TryGetValue(result.Id, out TextBlock? row)) ShowToolResult(row, result);
                        break;
                    case ChatCompleted done:
                        outcome = done.Error is null ? "completed" : "failed";
                        if (done.Error is not null) AddNote(done.Error, isError: true);
                        _log.LogInformation("Chat turn {Outcome}; cost {CostUsd} USD", outcome, done.CostUsd);
                        break;
                    case ChatFailed failed:
                        outcome = "failed";
                        AddNote(failed.Message, isError: failed.Message != "Stopped.");
                        _log.LogWarning("Chat turn didn't complete: {Reason}", failed.Message);
                        break;
                }
                ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chat turn crashed");
            AddNote($"Something went wrong: {ex.Message}", isError: true);
        }
        finally
        {
            _running = null;
            SetRunning(false);
            ScrollToEnd();
        }
    }

    private void SetRunning(bool running)
    {
        SendButton.Content = running ? "Stop" : "Send";
        PromptBox.IsEnabled = !running;
        if (!running) PromptBox.Focus(FocusState.Programmatic);
    }

    private void AddUserMessage(string text) => Transcript.Children.Add(new Border
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        MaxWidth = 420,
        Padding = new Thickness(12, 8, 12, 8),
        CornerRadius = new CornerRadius(8),
        Background = Brush("SurfaceBrush"),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
    });

    private TextBlock AddAssistantMessage()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Transcript.Children.Add(text);
        return text;
    }

    private TextBlock AddToolCall(ChatToolCall call)
    {
        var row = new TextBlock
        {
            Text = $"⚙ {call.Tool}{(call.InputJson is "{}" or "" ? "" : " " + call.InputJson)}",
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondaryBrush"),
            IsTextSelectionEnabled = true,
        };
        Transcript.Children.Add(row);
        return row;
    }

    private void ShowToolResult(TextBlock row, ChatToolResult result)
    {
        string firstLine = result.Text.Split('\n', 2)[0];
        row.Text += $"\n  {(result.IsError ? "✗" : "→")} {firstLine}";
        if (result.IsError) row.Foreground = Brush("FaultBrush");
    }

    private void AddNote(string text, bool isError) => Transcript.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontStyle = Windows.UI.Text.FontStyle.Italic,
        Foreground = Brush(isError ? "FaultBrush" : "TextSecondaryBrush"),
        IsTextSelectionEnabled = true,
    });

    private void ScrollToEnd()
    {
        TranscriptScroller.UpdateLayout();
        TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null, disableAnimation: true);
    }

    // Theme brushes live in the merged ThemeResources.xaml's ThemeDictionaries, which the indexer
    // doesn't search: walk them for the window's actual theme (as ToastHost does).
    private Brush Brush(string key)
    {
        string theme = RootGrid.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(theme, out var dictionary)
                && ((ResourceDictionary)dictionary).TryGetValue(key, out var brush))
            {
                return (Brush)brush;
            }
        }
        throw new KeyNotFoundException($"Brush '{key}' not found in '{theme}' theme dictionary.");
    }
}
