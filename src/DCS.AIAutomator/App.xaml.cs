using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DCS.AIAutomator.Core;
using DCS.AIAutomator.Mcp;
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
    private ChatWindow? _chatWindow;
    private MainWindow? _mainWindow;
    private ChatSession? _chat;
    private bool _chatDocked;
    private bool _shuttingDown;
    private DcsMcpBridgeHost? _bridgeHost;
    private NotificationService? _notifications;
    private BridgeStatusNotifier? _statusNotifier;
    private DcsLogging? _logging;
    private ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly SecretStore _secrets = new();
    private string _apiKey = "";
    private string _dcsLinkSecret = "";
    private ElementTheme _currentTheme = ElementTheme.Dark;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>The folder holding the app's log files (packaged local cache), for "Open log folder"/"View log".</summary>
    public string LogDirectory => _logging?.LogDirectory ?? "";

    /// <summary>The installed package's version, e.g. "1.0.0.0".</summary>
    public static string AppVersion
    {
        get
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
    }

    /// <summary>Non-secret facts for a bug report's about.txt (see DiagnosticsExport).</summary>
    public string DiagnosticsAbout(IEnumerable<(string Name, bool Detected)> agents) =>
        DiagnosticsExport.About(AppVersion,
            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})",
            _bridgeHost?.Status ?? new BridgeStatus(), agents, DateTimeOffset.UtcNow);

    /// <summary>
    /// Sets the requested theme for every open window's root element so ThemeResource lookups
    /// follow the chosen theme at runtime.
    /// </summary>
    public void SetAppTheme(ElementTheme theme)
    {
        _currentTheme = theme;
        ApplyTheme(_window);
        ApplyTheme(_settingsWindow);
        ApplyTheme(_chatWindow);
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
        _log.LogInformation("DCS.AIAutomator {Version} starting; logs in {LogDirectory}", AppVersion, _logging.LogDirectory);

        _notifications = new NotificationService(DispatcherQueue.GetForCurrentThread())
        {
            ViewLog = OpenLogFolder,
        };
        // Both generated on first run and kept in the Credential Locker (never logged).
        _apiKey = _secrets.GetOrCreate(SecretStore.McpApiKey);
        _dcsLinkSecret = _secrets.GetOrCreate(SecretStore.DcsLinkSecret);
        _bridgeHost = new DcsMcpBridgeHost();
        _statusNotifier = new BridgeStatusNotifier(_bridgeHost.Status, _notifications);
        _bridgeHost.Status.Units = _settings.Units;

        var mainWindow = new MainWindow(_bridgeHost.Status, _notifications, _settings);
        mainWindow.Closed += OnWindowClosed;
        _window = mainWindow;
        _mainWindow = mainWindow;
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
            await _bridgeHost!.StartAsync(_settings.McpListenUrl, _settings.DcsHost, _settings.DcsPort, loggerProvider: _logging!.Provider,
                apiKey: _apiKey, dcsLinkSecret: _dcsLinkSecret);
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

    /// <summary>The DCS connection for the main window's DCS controls (#43); null while the bridge is stopped.</summary>
    public IDcsConnection? DcsConnection => _bridgeHost?.Connection;

    // --- Chat (#16, #48): one conversation (ChatSession), shown docked in the main window or popped
    // out into ChatWindow. Moving it creates a new ChatView over the same session, so the transcript
    // and a reply still streaming carry over.

    private ChatSession Chat => _chat ??= new ChatSession(_bridgeHost!.Status, () => McpUrl, () => McpApiKey,
        // App-private and empty: holds the per-turn MCP config (with the key) and is claude's
        // working directory, so no project CLAUDE.md is picked up.
        Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "Chat"),
        _logging!.Provider.CreateLogger("DCS.AIAutomator.Chat"),
        () => _settings.ChatConfirmDcsChanges);

    /// <summary>The main window's chat button: opens the chat where it was last (docked the first
    /// time), closes the docked panel, or brings the popped-out window to the front.</summary>
    public void ToggleChat()
    {
        if (_bridgeHost is null || _mainWindow is null) return;
        if (_chatWindow is not null) _chatWindow.Activate();
        else if (_chatDocked) CloseChatDock();
        else if (_settings.ChatPoppedOut) PopOutChat();
        else DockChat();
    }

    private void DockChat()
    {
        _mainWindow!.ShowChatDock(new ChatView(Chat, docked: true, togglePop: PopOutChat, close: CloseChatDock));
        _chatDocked = true;
        _settings.ChatPoppedOut = false;
    }

    private void CloseChatDock()
    {
        _mainWindow?.ShowChatDock(null);
        _chatDocked = false;
    }

    private void PopOutChat()
    {
        if (_chatDocked) CloseChatDock();
        var window = new ChatWindow(new ChatView(Chat, docked: false, togglePop: PopInChat, close: null));
        RestoreChatWindowBounds(window);
        window.AppWindow.Closing += (_, _) => SaveChatWindowBounds(window);
        window.Closed += (_, _) =>
        {
            _chatWindow = null;
            if (!_shuttingDown) DockChat(); // closing (or popping in) the window docks the chat back
        };
        _chatWindow = window;
        _settings.ChatPoppedOut = true;
        ApplyTheme(window);
        window.Activate();
    }

    private void PopInChat() => _chatWindow?.Close(); // its Closed handler docks the chat

    private void SaveChatWindowBounds(Window window)
    {
        var (position, size) = (window.AppWindow.Position, window.AppWindow.Size);
        _settings.ChatWindowBounds = $"{position.X},{position.Y},{size.Width},{size.Height}";
    }

    private void RestoreChatWindowBounds(Window window)
    {
        int[] b = Array.ConvertAll(_settings.ChatWindowBounds.Split(',', StringSplitOptions.RemoveEmptyEntries),
            s => int.TryParse(s, out int v) ? v : -1);
        var rect = b.Length == 4 && b[2] > 200 && b[3] > 200 ? new Windows.Graphics.RectInt32(b[0], b[1], b[2], b[3]) : (Windows.Graphics.RectInt32?)null;
        // Only where a monitor still shows it (a monitor may have been unplugged since).
        if (rect is { } r && Microsoft.UI.Windowing.DisplayArea.GetFromRect(r, Microsoft.UI.Windowing.DisplayAreaFallback.None) is not null)
            window.AppWindow.MoveAndResize(r);
        else
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 720));
    }

    /// <summary>The MCP API key MCP clients must send as a bearer token. Shown in Settings; never logged.</summary>
    public string McpApiKey => _apiKey;

    /// <summary>The secret baked into the deployed Hooks script; DCS only talks to an app that sends it.</summary>
    public string DcsLinkSecret => _dcsLinkSecret;

    /// <summary>The MCP endpoint clients register against.</summary>
    public string McpUrl => $"{_settings.McpListenUrl}/mcp";

    /// <summary>
    /// Replaces the MCP API key; requests with the old one are rejected immediately, without
    /// restarting the bridge (so DCS stays connected). Registered clients must be re-registered.
    /// </summary>
    public void RegenerateApiKey()
    {
        _apiKey = _secrets.Regenerate(SecretStore.McpApiKey);
        _bridgeHost?.SetApiKey(_apiKey);
        _log.LogWarning("MCP API key regenerated; clients using the old key will get 401 until re-registered");
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
            await _bridgeHost.StartAsync(_settings.McpListenUrl, _settings.DcsHost, _settings.DcsPort, loggerProvider: _logging!.Provider,
                apiKey: _apiKey, dcsLinkSecret: _dcsLinkSecret);
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
        _shuttingDown = true;
        _chat?.Stop();
        _chatWindow?.Close(); // without docking back into the closing main window
        if (_bridgeHost is not null)
        {
            await _bridgeHost.DisposeAsync();
        }
        _logging?.Dispose(); // flush last, after the bridge has logged its shutdown
    }
}
