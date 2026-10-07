using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DCS.AIAutomator;

/// <summary>
/// Renders a <see cref="ChatSession"/> (#48): built from its items when created and updated from
/// its events, so a new view (after a pop-out or pop-in) shows the whole conversation, and a reply
/// still streaming carries on in it. The transcript is built in code-behind, like ToastHost.
/// </summary>
public sealed partial class ChatView : UserControl
{
    private readonly ChatSession _session;
    private readonly Action _togglePop;
    private readonly Action? _close;
    private readonly Dictionary<ChatItem, TextBlock> _rendered = new();

    /// <param name="docked">Docked in the main window (Pop out + Close), or in its own window (Pop in).</param>
    /// <param name="close">Closes the docked panel; null when popped out (the window's own close docks it back).</param>
    public ChatView(ChatSession session, bool docked, Action togglePop, Action? close)
    {
        InitializeComponent();
        _session = session;
        _togglePop = togglePop;
        _close = close;

        PopIcon.Glyph = docked ? "" : ""; // OpenInNewWindow : BackToWindow
        ToolTipService.SetToolTip(PopButton, docked ? "Pop out into its own window" : "Pop back into the main window");
        CloseButton.Visibility = close is null ? Visibility.Collapsed : Visibility.Visible;

        Loaded += (_, _) =>
        {
            foreach (ChatItem item in _session.Items) Render(item);
            _session.ItemAdded += OnItemAdded;
            _session.ItemChanged += OnItemChanged;
            _session.Cleared += OnCleared;
            _session.RunningChanged += OnRunningChanged;
            OnRunningChanged();
            ScrollToEnd();
        };
        Unloaded += (_, _) =>
        {
            _session.ItemAdded -= OnItemAdded;
            _session.ItemChanged -= OnItemChanged;
            _session.Cleared -= OnCleared;
            _session.RunningChanged -= OnRunningChanged;
        };
    }

    public void FocusPrompt() => PromptBox.Focus(FocusState.Programmatic);

    private void OnItemAdded(ChatItem item)
    {
        Render(item);
        ScrollToEnd();
    }

    private void OnItemChanged(ChatItem item)
    {
        if (!_rendered.TryGetValue(item, out TextBlock? block)) return;
        Update(item, block);
        ScrollToEnd();
    }

    private void OnCleared()
    {
        _rendered.Clear();
        Transcript.Children.Clear();
    }

    private void OnRunningChanged()
    {
        bool running = _session.IsRunning;
        SendButton.Content = running ? "Stop" : "Send";
        PromptBox.IsEnabled = !running;
        if (!running) FocusPrompt();
    }

    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter sends; Shift+Enter is a new line.
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Enter && !shift)
        {
            e.Handled = true;
            SendPrompt();
        }
    }

    private void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (_session.IsRunning) _session.Stop(); // the button reads "Stop" while a turn runs
        else SendPrompt();
    }

    private void SendPrompt()
    {
        string prompt = PromptBox.Text;
        if (prompt.Trim().Length == 0 || _session.IsRunning) return;
        PromptBox.Text = "";
        _session.Send(prompt);
    }

    private void OnNewChatClicked(object sender, RoutedEventArgs e)
    {
        _session.NewChat();
        FocusPrompt();
    }

    private void OnPopClicked(object sender, RoutedEventArgs e) => _togglePop();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => _close?.Invoke();

    private void Render(ChatItem item)
    {
        UIElement element;
        TextBlock block;
        switch (item)
        {
            case ChatUserItem user:
                block = new TextBlock { Text = user.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                element = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MaxWidth = 420,
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(8),
                    Background = Brush("SurfaceBrush"),
                    Child = block,
                };
                break;
            case ChatToolItem:
                block = new TextBlock
                {
                    FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };
                element = block;
                break;
            case ChatNoteItem note:
                block = new TextBlock
                {
                    Text = note.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = Brush(note.IsError ? "FaultBrush" : "TextSecondaryBrush"),
                    IsTextSelectionEnabled = true,
                };
                element = block;
                break;
            default: // ChatAssistantItem
                block = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                element = block;
                break;
        }
        _rendered[item] = block;
        Update(item, block);
        Transcript.Children.Add(element);
    }

    // Fields that change while a turn runs: a streaming reply's text, a tool call's result.
    private void Update(ChatItem item, TextBlock block)
    {
        switch (item)
        {
            case ChatAssistantItem reply:
                block.Text = reply.Text;
                break;
            case ChatToolItem tool:
                block.Text = $"⚙ {tool.Tool}{(tool.InputJson is "{}" or "" ? "" : " " + tool.InputJson)}" +
                             (tool.Result is null ? "" : $"\n  {(tool.IsError ? "✗" : "→")} {tool.Result.Split('\n', 2)[0]}");
                block.Foreground = Brush(tool.IsError ? "FaultBrush" : "TextSecondaryBrush");
                break;
        }
    }

    private void ScrollToEnd()
    {
        TranscriptScroller.UpdateLayout();
        TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null, disableAnimation: true);
    }

    // Theme brushes live in the merged ThemeResources.xaml's ThemeDictionaries, which the indexer
    // doesn't search: walk them for this view's actual theme (as ToastHost does).
    private Brush Brush(string key)
    {
        string theme = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
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
