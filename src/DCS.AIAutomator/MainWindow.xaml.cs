using System;
using System.Threading.Tasks;
using DCS.AIAutomator.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DCS.AIAutomator;

/// <summary>
/// Status dashboard for the DCS MCP bridge: bridge/DCS annunciators plus a live mission readout.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string SunGlyph = "";
    private const string MoonGlyph = "";

    private static readonly Color NominalColor = Color.FromArgb(0xFF, 0x3E, 0xCF, 0x8E);
    private static readonly Color WarningColor = Color.FromArgb(0xFF, 0xF2, 0xB8, 0x4B);
    private static readonly Color FaultColor = Color.FromArgb(0xFF, 0xE8, 0x5D, 0x5D);
    private static readonly Color IdleColor = Color.FromArgb(0xFF, 0x7C, 0x94, 0x90);

    private readonly BridgeStatus _status;
    private readonly NotificationService _notifications;
    private readonly SettingsService _settings;
    private bool _dcsRunning;
    private bool _dcsBusy; // a start or quit is in progress
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly bool _animationsEnabled;

    public MainWindow(BridgeStatus status, NotificationService notifications, SettingsService settings)
    {
        InitializeComponent();

        _status = status;
        _notifications = notifications;
        _settings = settings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _animationsEnabled = new UISettings().AnimationsEnabled;

        AppWindow.Resize(new SizeInt32(420, 760)); // room for the aircraft status expander

        var toastHost = new ToastHost(notifications, settings)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 16, 16),
        };
        Grid.SetRowSpan(toastHost, 4);
        RootGrid.Children.Add(toastHost);

        _status.Changed += OnStatusChanged;
        Render();

        // ACTIVE fades to IDLE (and AUTH FAILED expires) with time alone, with no status change.
        DispatcherQueueTimer clientsTimer = _dispatcherQueue.CreateTimer();
        clientsTimer.Interval = TimeSpan.FromSeconds(1);
        clientsTimer.Tick += (_, _) =>
        {
            RenderClients();
            RenderDcsControls(); // DCS starting or exiting changes no status: poll the process
        };
        clientsTimer.Start();
        Closed += (_, _) => clientsTimer.Stop();

        // Initialize theme toggle to reflect current requested theme
        if (this.Content is FrameworkElement fe)
        {
            bool isDark = fe.RequestedTheme == ElementTheme.Dark;
            ThemeToggle.IsChecked = isDark;
            ThemeToggleIcon.Glyph = isDark ? MoonGlyph : SunGlyph;
        }
    }

    private void OnThemeToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            var theme = tb.IsChecked == true ? ElementTheme.Dark : ElementTheme.Light;
            ThemeToggleIcon.Glyph = theme == ElementTheme.Dark ? MoonGlyph : SunGlyph;
            ((App)Application.Current!).SetAppTheme(theme);
        }
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        ((App)Application.Current!).OpenSettingsWindow();
    }

    private void OnChatClicked(object? sender, RoutedEventArgs e) => ((App)Application.Current!).ToggleChat();

    /// <summary>Width the main window grows by while the chat is docked beside the readouts (#48).</summary>
    private const int ChatDockWidth = 460;

    /// <summary>Docks <paramref name="view"/> beside the readouts, widening the window; null undocks and restores the width.</summary>
    public void ShowChatDock(ChatView? view)
    {
        bool wasDocked = ChatDock.Child is not null;
        ChatDock.Child = view;
        ChatDock.Visibility = view is null ? Visibility.Collapsed : Visibility.Visible;
        ChatColumn.Width = new GridLength(view is null ? 0 : ChatDockWidth);
        if (wasDocked != (view is not null))
        {
            SizeInt32 size = AppWindow.Size;
            AppWindow.Resize(new SizeInt32(size.Width + (view is null ? -ChatDockWidth : ChatDockWidth), size.Height));
        }
        view?.FocusPrompt();
    }

    private void OnHistoryClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new NotificationHistoryDialog(_notifications)
        {
            XamlRoot = this.Content.XamlRoot,
        };
        _ = dialog.ShowAsync();
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        RenderBridge();
        RenderDcs();
        RenderClients();
        RenderMission();
        RenderAircraft();
    }

    private void RenderBridge()
    {
        var (color, label, pulsing) = _status.BridgeState switch
        {
            BridgeState.Running => (NominalColor, "RUNNING", false),
            BridgeState.Starting => (WarningColor, "STARTING…", true),
            BridgeState.Faulted => (FaultColor, "FAULTED", false),
            _ => (IdleColor, "STOPPED", false),
        };

        BridgeLamp.Background = new SolidColorBrush(color);
        BridgeStateText.Text = label;
        BridgeStateText.Foreground = new SolidColorBrush(color);
        BridgeUrlText.Text = _status.McpEndpoint;

        SetPulse((Storyboard)RootGrid.Resources["BridgePulseStoryboard"], pulsing);
    }

    private void RenderDcs()
    {
        // Not responding = connected but DCS has gone silent mid-mission (hung); paused is quiet.
        // Auth/version failures win: the retry loop keeps reconnecting, and the fix is user action.
        var (color, label) =
            _status.DcsAuthFailed ? (FaultColor, "AUTH FAILED")
            : _status.DcsScriptOutdated ? (FaultColor, "SCRIPT OUTDATED")
            : !_status.DcsConnected ? (IdleColor, "DISCONNECTED")
            : _status.DcsNotResponding ? (WarningColor, "NOT RESPONDING")
            : _status.DcsPaused ? (IdleColor, "PAUSED")
            : (NominalColor, "CONNECTED");

        DcsLamp.Background = new SolidColorBrush(color);
        DcsStateText.Text = label;
        DcsStateText.Foreground = new SolidColorBrush(color);
        DcsAddressText.Text = _status.DcsAuthFailed || _status.DcsScriptOutdated ? "Redeploy Lua scripts, restart DCS" : _status.DcsEndpoint;
        RenderDcsControls(pollProcess: false); // status changes arrive ~5 Hz; the 1 s timer polls the process
    }

    // Start/Quit follows the DCS *process* (running or not), Pause the connection's mission state.
    private void RenderDcsControls(bool pollProcess = true)
    {
        if (pollProcess) _dcsRunning = DcsProcess.IsRunning();
        DcsStartQuitButton.Content = _dcsRunning ? "Quit DCS" : "Start DCS";
        DcsStartQuitButton.IsEnabled = !_dcsBusy;
        DcsPauseButton.Content = _status.DcsPaused ? "Resume" : "Pause";
        DcsPauseButton.IsEnabled = _status.DcsConnected && _status.CurrentMission is not null && !_status.DcsNotResponding;
    }

    private async void OnDcsStartQuitClicked(object sender, RoutedEventArgs e)
    {
        _dcsBusy = true;
        RenderDcsControls();
        try
        {
            if (_dcsRunning) await QuitDcsAsync();
            else StartDcs();
        }
        finally
        {
            _dcsBusy = false;
            RenderDcsControls();
        }
    }

    private void StartDcs()
    {
        var (exe, error) = DcsProcess.Executable(_settings.DcsInstallPath, _settings.DcsMultithreaded);
        if (exe is null)
        {
            _notifications.Show("Can't start DCS", error!, NotificationSeverity.Error);
            return;
        }
        try
        {
            DcsProcess.Start(exe);
            _notifications.Show("Starting DCS", "DCS connects to this app once a mission is running.", NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            _notifications.Show("Can't start DCS", ex.Message, NotificationSeverity.Error);
        }
    }

    // Clean first (Sim.exitProcess through the Hooks script); force-kill only after a second confirmation.
    private async Task QuitDcsAsync()
    {
        if (!await ConfirmAsync("Quit DCS?", "The current mission ends, and unsaved work in DCS (e.g. the mission editor) is lost.", "Quit DCS"))
            return;

        string reason;
        if (_status.DcsConnected && ((App)Application.Current!).DcsConnection is { } connection)
        {
            DcsCommandResult result = await connection.QuitAsync();
            if (result.Ok && await DcsProcess.WaitForExitAsync(TimeSpan.FromSeconds(15)))
            {
                _notifications.Show("DCS closed", "DCS exited cleanly.", NotificationSeverity.Info);
                return;
            }
            reason = result.Ok ? "DCS hasn't exited after 15 seconds." : $"DCS didn't accept the quit: {result.Error}";
        }
        else
        {
            reason = "DCS isn't connected to this app, so it can't be asked to exit cleanly.";
        }

        if (!DcsProcess.IsRunning()) return; // it exited after all
        if (!await ConfirmAsync("Force-quit DCS?", $"{reason} Force-quitting skips DCS's own shutdown.", "Force quit"))
            return;
        DcsProcess.Kill();
        _notifications.Show("DCS force-quit", "DCS was ended.", NotificationSeverity.Warning);
    }

    private async void OnDcsPauseClicked(object sender, RoutedEventArgs e)
    {
        if (((App)Application.Current!).DcsConnection is not { } connection) return;
        DcsPauseButton.IsEnabled = false;
        DcsCommandResult result = await connection.SetPausedAsync(!_status.DcsPaused);
        if (!result.Ok) _notifications.Show(_status.DcsPaused ? "Can't resume DCS" : "Can't pause DCS", result.Error ?? "", NotificationSeverity.Error);
        RenderDcsControls(); // the label follows the paused line DCS sends back
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = ((FrameworkElement)Content).ActualTheme,
            Title = title,
            Content = message,
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void RenderClients()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        McpClientSnapshot clients = _status.McpClients.Snapshot(now);
        var (color, label) = clients.State switch
        {
            McpClientState.Active => (NominalColor, "ACTIVE"),
            McpClientState.Idle => (IdleColor, "IDLE"),
            McpClientState.AuthFailed => (FaultColor, "AUTH FAILED"),
            _ => (IdleColor, "NO CLIENTS"),
        };

        ClientsLamp.Background = new SolidColorBrush(color);
        ClientsStateText.Text = label;
        ClientsStateText.Foreground = new SolidColorBrush(color);
        ClientsDetailText.Text = clients.State switch
        {
            McpClientState.AuthFailed => "Rejected API key: reconnect it in Settings → AI agents",
            McpClientState.NoClients => "No AI agent has called yet",
            _ => $"{(clients.ClientNames.Count > 0 ? string.Join(" · ", clients.ClientNames) : "MCP client")} — last call {Ago(now - clients.LastSeenUtc!.Value)}",
        };
    }

    private static string Ago(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60 ? "just now"
        : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes} min ago"
        : $"{(int)elapsed.TotalHours} h ago";

    private void RenderMission()
    {
        MissionInfo? mission = _status.CurrentMission;
        bool hasMission = mission is not null;

        NoMissionPanel.Visibility = hasMission ? Visibility.Collapsed : Visibility.Visible;
        MissionDetailPanel.Visibility = hasMission ? Visibility.Visible : Visibility.Collapsed;

        if (mission is not null)
        {
            AircraftText.Text = mission.Aircraft;
            MissionNameText.Text = mission.MissionName;
            TerrainText.Text = mission.Terrain;
        }
    }

    private void RenderAircraft()
    {
        AircraftState? a = _status.Aircraft;
        NoAircraftText.Visibility = a is null ? Visibility.Visible : Visibility.Collapsed;
        AircraftDetailPanel.Visibility = a is null ? Visibility.Collapsed : Visibility.Visible;
        if (a is null) return;

        // Values stop updating while paused or hung; say so rather than look live.
        bool held = _status.DcsNotResponding || _status.DcsPaused;
        AircraftHeldText.Visibility = held ? Visibility.Visible : Visibility.Collapsed;
        if (held)
        {
            AircraftHeldText.Text = _status.DcsNotResponding
                ? "HELD — DCS NOT RESPONDING, LAST KNOWN VALUES"
                : "HELD — DCS PAUSED";
            AircraftHeldText.Foreground = new SolidColorBrush(_status.DcsNotResponding ? WarningColor : IdleColor);
        }

        // Same formatter as the get_aircraft_state MCP tool, so the panel and the LLM always agree.
        UnitSystem u = _status.Units;
        PositionText.Text = AircraftStateFormatter.Position(a.Latitude, a.Longitude);
        AltMslText.Text = AircraftStateFormatter.Altitude(a.AltitudeMslMeters, u);
        AltAglText.Text = AircraftStateFormatter.Altitude(a.AltitudeAglMeters, u);
        IasText.Text = AircraftStateFormatter.Speed(a.IndicatedAirspeedMps, u);
        TasText.Text = AircraftStateFormatter.Speed(a.TrueAirspeedMps, u);
        MachText.Text = AircraftStateFormatter.Mach(a.Mach);
        VsText.Text = AircraftStateFormatter.VerticalSpeed(a.VerticalSpeedMps, u);
        HeadingText.Text = AircraftStateFormatter.Heading(a.MagneticHeadingRadians);

        FaultsText.Text = AircraftStateFormatter.Failures(a.Failures);
        if (a.Failures is { Count: > 0 })
        {
            FaultsText.Foreground = new SolidColorBrush(FaultColor); // active faults: fault accent
        }
        else
        {
            FaultsText.ClearValue(TextBlock.ForegroundProperty); // back to the style's quiet colour
        }
    }

    private void SetPulse(Storyboard storyboard, bool shouldPulse)
    {
        if (shouldPulse && _animationsEnabled)
        {
            storyboard.Begin();
        }
        else
        {
            storyboard.Stop();
        }
    }
}
