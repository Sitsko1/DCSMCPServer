using System;
using System.Threading.Tasks;
using DCS.Scripting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DCS.AIAutomator;

/// <summary>
/// Settings for the MCP/DCS connection, DCS file paths, and Export.lua deployment. Changes are
/// staged in the controls and only take effect (persisted + bridge restarted) on Save.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Func<Task> _restartBridgeAsync;
    private readonly NotificationService _notifications;

    public SettingsWindow(SettingsService settings, Func<Task> restartBridgeAsync, NotificationService notifications)
    {
        InitializeComponent();
        _settings = settings;
        _restartBridgeAsync = restartBridgeAsync;
        _notifications = notifications;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 560));

        McpPortBox.Value = _settings.McpPort;
        DcsHostBox.Text = _settings.DcsHost;
        DcsPortBox.Value = _settings.DcsPort;
        InstallPathBox.Text = _settings.DcsInstallPath;
        SavedGamesPathBox.Text = _settings.DcsSavedGamesPath;
        ToastDurationBox.Value = _settings.ToastDurationSeconds;
    }

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string ?? "Connection";
        ConnectionPanel.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
        PathsPanel.Visibility = tag == "Paths" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationPanel.Visibility = tag == "Integration" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPanel.Visibility = tag == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBrowseInstallPathClicked(object sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync();
        if (path is not null) InstallPathBox.Text = path;
    }

    private async void OnBrowseSavedGamesPathClicked(object sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync();
        if (path is not null) SavedGamesPathBox.Text = path;
    }

    private void OnPathTextChanged(object sender, TextChangedEventArgs e) => ValidatePaths();

    /// <summary>Shows each path box's error (if any) under it; returns true when both are valid.</summary>
    private bool ValidatePaths()
    {
        bool installOk = ShowError(InstallPathError, DcsPathValidator.ValidateInstallPath(InstallPathBox.Text));
        bool savedGamesOk = ShowError(SavedGamesPathError, DcsPathValidator.ValidateSavedGamesPath(SavedGamesPathBox.Text));
        return installOk && savedGamesOk;
    }

    private static bool ShowError(TextBlock errorText, string? error)
    {
        errorText.Text = error ?? string.Empty;
        errorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        return error is null;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private void OnDeployClicked(object sender, RoutedEventArgs e)
    {
        var result = LuaExportDeployer.Deploy(SavedGamesPathBox.Text, DcsHostBox.Text, (int)DcsPortBox.Value);
        DeployStatusText.Text = result.Success ? result.Message : $"Failed: {result.Message}";
        _notifications.Show(
            result.Success ? "Lua scripts deployed" : "Lua deploy failed",
            result.Message,
            result.Success ? NotificationSeverity.Success : NotificationSeverity.Error);
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!ValidatePaths())
        {
            // The errors render under the path boxes, which may be on a hidden tab — jump to it.
            SectionNav.SelectedItem = SectionNav.MenuItems[1];
            SaveStatusText.Text = "Not saved: fix the DCS paths.";
            return;
        }

        _settings.McpPort = (int)McpPortBox.Value;
        _settings.DcsHost = DcsHostBox.Text;
        _settings.DcsPort = (int)DcsPortBox.Value;
        _settings.DcsInstallPath = InstallPathBox.Text;
        _settings.DcsSavedGamesPath = SavedGamesPathBox.Text;
        _settings.ToastDurationSeconds = (int)ToastDurationBox.Value;

        SaveStatusText.Text = "Restarting bridge…";
        try
        {
            await _restartBridgeAsync();
            SaveStatusText.Text = "Saved.";
            _notifications.Show("Settings saved", "Bridge restarted with the new settings.", NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = $"Saved, but bridge restart failed: {ex.Message}";
            _notifications.Show("Bridge restart failed", ex.Message, NotificationSeverity.Error);
        }
    }
}
