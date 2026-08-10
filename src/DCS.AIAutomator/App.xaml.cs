using System.Threading.Tasks;
using DCS.Scripting;
using Microsoft.UI.Xaml;

namespace DCS.AIAutomator;

public partial class App : Application
{
    private readonly SettingsService _settings = new();
    private Window? _window;
    private SettingsWindow? _settingsWindow;
    private DcsMcpBridgeHost? _bridgeHost;
    private ElementTheme _currentTheme = ElementTheme.Dark;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets the requested theme for every open window's root element so ThemeResource lookups
    /// follow the chosen theme at runtime.
    /// </summary>
    public void SetAppTheme(ElementTheme theme)
    {
        _currentTheme = theme;
        ApplyTheme(_window);
        ApplyTheme(_settingsWindow);
    }

    private void ApplyTheme(Window? window)
    {
        if (window?.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = _currentTheme;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _bridgeHost = new DcsMcpBridgeHost();

        var mainWindow = new MainWindow(_bridgeHost.Status);
        mainWindow.Closed += OnWindowClosed;
        _window = mainWindow;
        ApplyTheme(_window);
        _window.Activate();

        _ = StartBridgeAsync();
    }

    private async Task StartBridgeAsync()
    {
        try
        {
            await _bridgeHost!.StartAsync(_settings.McpListenUrl, _settings.DcsHost, _settings.DcsPort);
        }
        catch
        {
            // BridgeStatus already reflects Faulted; the window surfaces it. Nothing else to do.
        }
    }

    public void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, RestartBridgeAsync);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            ApplyTheme(_settingsWindow);
        }
        _settingsWindow.Activate();
    }

    private async Task RestartBridgeAsync()
    {
        if (_bridgeHost is null) return;
        await _bridgeHost.StopAsync();
        await _bridgeHost.StartAsync(_settings.McpListenUrl, _settings.DcsHost, _settings.DcsPort);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_bridgeHost is not null)
        {
            await _bridgeHost.DisposeAsync();
        }
    }
}
