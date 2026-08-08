using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private static readonly Color NominalColor = Color.FromArgb(0xFF, 0x3E, 0xCF, 0x8E);
    private static readonly Color WarningColor = Color.FromArgb(0xFF, 0xF2, 0xB8, 0x4B);
    private static readonly Color FaultColor = Color.FromArgb(0xFF, 0xE8, 0x5D, 0x5D);
    private static readonly Color IdleColor = Color.FromArgb(0xFF, 0x7C, 0x94, 0x90);

    private readonly BridgeStatus _status;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly bool _animationsEnabled;

    public MainWindow(BridgeStatus status)
    {
        InitializeComponent();

        _status = status;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _animationsEnabled = new UISettings().AnimationsEnabled;

        AppWindow.Resize(new SizeInt32(420, 520));

        _status.Changed += OnStatusChanged;
        Render();

        // Initialize theme toggle to reflect current requested theme
        try
        {
            var app = (App)Application.Current!;
            // If root content is available, determine its RequestedTheme
            if (app is not null && app is App)
            {
                if (this.Content is FrameworkElement fe)
                {
                    ThemeToggle.IsChecked = fe.RequestedTheme == ElementTheme.Dark;
                    ThemeToggle.Content = fe.RequestedTheme == ElementTheme.Dark ? "Dark" : "Light";
                }
            }
        }
        catch
        {
            // best-effort only
        }
    }

    private void OnThemeToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            var theme = tb.IsChecked == true ? ElementTheme.Dark : ElementTheme.Light;
            ((App)Application.Current!).SetAppTheme(theme);
            tb.Content = theme == ElementTheme.Dark ? "Dark" : "Light";
        }
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        RenderBridge();
        RenderDcs();
        RenderMission();
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
        BridgeUrlText.Text = _status.BridgeState == BridgeState.Running ? "http://127.0.0.1:5270/mcp" : "—";

        SetPulse((Storyboard)RootGrid.Resources["BridgePulseStoryboard"], pulsing);
    }

    private void RenderDcs()
    {
        bool connected = _status.DcsConnected;
        Color color = connected ? NominalColor : IdleColor;

        DcsLamp.Background = new SolidColorBrush(color);
        DcsStateText.Text = connected ? "CONNECTED" : "DISCONNECTED";
        DcsStateText.Foreground = new SolidColorBrush(color);
        DcsAddressText.Text = "127.0.0.1:1024";
    }

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
            ModeText.Text = mission.IsMultiplayer ? "MULTIPLAYER" : "SINGLEPLAYER";
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
