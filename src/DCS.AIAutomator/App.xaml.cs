using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace DCS.AIAutomator;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private DcsMcpBridgeHost? _bridgeHost;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets the requested theme for the main window's root element so ThemeResource lookups
    /// follow the chosen theme at runtime.
    /// </summary>
    /// <param name="theme">ElementTheme.Default/Light/Dark</param>
    public void SetAppTheme(Microsoft.UI.Xaml.ElementTheme theme)
    {
        if (_window is not null && _window.Content is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            fe.RequestedTheme = theme;
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _bridgeHost = new DcsMcpBridgeHost();

        var mainWindow = new MainWindow(_bridgeHost.Status);
        mainWindow.Closed += OnWindowClosed;
        _window = mainWindow;
        _window.Activate();

        _ = StartBridgeAsync();
    }

    private async Task StartBridgeAsync()
    {
        try
        {
            await _bridgeHost!.StartAsync();
        }
        catch
        {
            // BridgeStatus already reflects Faulted; the window surfaces it. Nothing else to do.
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_bridgeHost is not null)
        {
            await _bridgeHost.DisposeAsync();
        }
    }
}
