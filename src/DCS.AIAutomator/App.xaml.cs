using System;
using System.IO;
using System.Threading.Tasks;
using DCS.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel;
using Windows.Storage;

namespace DCS.AIAutomator;

public partial class App : Application
{
    private readonly SettingsService _settings = new();
    private Window? _window;
    private SettingsWindow? _settingsWindow;
    private DcsMcpBridgeHost? _bridgeHost;
    private NotificationService? _notifications;
    private BridgeStatusNotifier? _statusNotifier;
    private DcsLogging? _logging;
    private ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private ElementTheme _currentTheme = ElementTheme.Dark;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>The folder holding the app's log files (packaged local cache), for "Open log folder"/"View log".</summary>
    public string LogDirectory => _logging?.LogDirectory ?? "";

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
        // Logging first, so everything after it (including a failed bridge start) is captured.
        // The path is resolved here: the libraries must never touch ApplicationData (see CLAUDE.md).
        _logging = new DcsLogging(
            Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "Logs"),
            _settings.LogLevel,
            _settings.LogRetentionDays);
        _log = _logging.Provider.CreateLogger("DCS.AIAutomator.App");
        HookUnhandledExceptions();
        PackageVersion v = Package.Current.Id.Version;
        _log.LogInformation("DCS.AIAutomator {Version} starting; logs in {LogDirectory}",
            $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", _logging.LogDirectory);

        _notifications = new NotificationService(DispatcherQueue.GetForCurrentThread())
        {
            ViewLog = OpenLogFolder,
        };
        _bridgeHost = new DcsMcpBridgeHost();
        _statusNotifier = new BridgeStatusNotifier(_bridgeHost.Status, _notifications);
        _bridgeHost.Status.Units = _settings.Units;

        var mainWindow = new MainWindow(_bridgeHost.Status, _notifications, _settings);
        mainWindow.Closed += OnWindowClosed;
        _window = mainWindow;
        ApplyTheme(_window);
        _window.Activate();

        _ = StartBridgeAsync();
    }

    private void HookUnhandledExceptions()
    {
        UnhandledException += (_, e) => _log.LogCritical(e.Exception, "Unhandled UI exception: {Message}", e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => _log.LogError(e.Exception, "Unobserved task exception");
    }

    private async Task StartBridgeAsync()
    {
        try
        {
            await _bridgeHost!.StartAsync(_settings.McpListenUrl, _settings.DcsHost, _settings.DcsPort, loggerProvider: _logging!.Provider);
        }
        catch (Exception ex)
        {
            // BridgeStatus already shows Faulted; the log records why (e.g. port already in use).
            _log.LogError(ex, "MCP bridge failed to start on {ListenUrl}", _settings.McpListenUrl);
        }
    }

    public void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettingsAsync, _notifications!,
                _logging!.Provider.CreateLogger("DCS.AIAutomator.Settings"));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            ApplyTheme(_settingsWindow);
        }
        _settingsWindow.Activate();
    }

    public void OpenLogFolder()
    {
        if (string.IsNullOrEmpty(LogDirectory)) return;
        Directory.CreateDirectory(LogDirectory);
        _ = Windows.System.Launcher.LaunchFolderPathAsync(LogDirectory);
    }

    private async Task ApplySettingsAsync(bool restartBridge)
    {
        if (_bridgeHost is null) return;
        _bridgeHost.Status.Units = _settings.Units; // takes effect immediately, no restart needed
        _logging?.SetMinimumLevel(_settings.LogLevel); // likewise
        if (!restartBridge) return;
        try
        {
            await _bridgeHost.StopAsync();
            await _bridgeHost.StartAsync(_settings.McpListenUrl, _settings.DcsHost, _settings.DcsPort, loggerProvider: _logging!.Provider);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MCP bridge failed to restart on {ListenUrl}", _settings.McpListenUrl);
            throw; // SettingsWindow shows it
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _log.LogInformation("DCS.AIAutomator shutting down");
        if (_bridgeHost is not null)
        {
            await _bridgeHost.DisposeAsync();
        }
        _logging?.Dispose(); // flush last, after the bridge has logged its shutdown
    }
}
