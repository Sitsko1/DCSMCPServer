using Microsoft.UI.Xaml;

namespace DCS.AIAutomator;

/// <summary>
/// The popped-out chat (#48): a window around a <see cref="ChatView"/>. The conversation itself
/// lives in the app's <see cref="ChatSession"/>, so closing or popping this window back in loses
/// nothing; App docks the chat back into the main window in both cases.
/// </summary>
public sealed partial class ChatWindow : Window
{
    public ChatWindow(ChatView view)
    {
        InitializeComponent();
        RootGrid.Children.Add(view);
    }
}
