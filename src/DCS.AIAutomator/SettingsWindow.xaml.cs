using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using DCS.Scripting;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DCS.AIAutomator;

/// <summary>
/// Settings for the MCP/DCS connection, DCS file paths, and Hooks script deployment. Changes are
/// staged in the controls and only take effect (persisted + bridge restarted) on Save.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Func<bool, Task> _applySettingsAsync;
    private readonly NotificationService _notifications;
    private readonly ILogger _log;
    private bool _apiKeyVisible;
    private static readonly LogLevel[] LogLevels = [LogLevel.Warning, LogLevel.Information, LogLevel.Debug];

    /// <param name="applySettingsAsync">Applies saved settings; the argument says whether the
    /// bridge must restart (only when a connection setting changed).</param>
    public SettingsWindow(SettingsService settings, Func<bool, Task> applySettingsAsync, NotificationService notifications, ILogger log)
    {
        InitializeComponent();
        _settings = settings;
        _applySettingsAsync = applySettingsAsync;
        _log = log;
        _notifications = notifications;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 560));

        McpPortBox.Value = _settings.McpPort;
        DcsHostBox.Text = _settings.DcsHost;
        DcsPortBox.Value = _settings.DcsPort;
        InstallPathBox.Text = _settings.DcsInstallPath;
        SavedGamesPathBox.Text = _settings.DcsSavedGamesPath;
        ToastDurationBox.Value = _settings.ToastDurationSeconds;
        UnitsBox.SelectedIndex = _settings.Units == UnitSystem.Metric ? 1 : 0;
        LogLevelBox.SelectedIndex = Math.Max(0, Array.IndexOf(LogLevels, _settings.LogLevel));
        LogRetentionBox.Value = _settings.LogRetentionDays;
        LogFolderText.Text = ((App)Application.Current).LogDirectory;
        RenderApiKey();
    }

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string ?? "Connection";
        ConnectionPanel.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
        PathsPanel.Visibility = tag == "Paths" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationPanel.Visibility = tag == "Integration" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPanel.Visibility = tag == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        DisplayPanel.Visibility = tag == "Display" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = tag == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
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

    private async void OnResetConnectionClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"], // code-created dialogs don't get it implicitly
            RequestedTheme = RootGrid.ActualTheme, // ContentDialog doesn't inherit the window root's theme
            Title = "Reset connection settings?",
            Content = $"MCP server port → {SettingsService.DefaultMcpPort}\n" +
                      $"DCS host → {SettingsService.DefaultDcsHost}\n" +
                      $"DCS port → {SettingsService.DefaultDcsPort}\n\n" +
                      "Nothing is saved until you click Save.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Staged like any other edit: Save persists and restarts the bridge, Cancel discards.
        McpPortBox.Value = SettingsService.DefaultMcpPort;
        DcsHostBox.Text = SettingsService.DefaultDcsHost;
        DcsPortBox.Value = SettingsService.DefaultDcsPort;
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
        var result = LuaScriptDeployer.Deploy(SavedGamesPathBox.Text, DcsHostBox.Text, (int)DcsPortBox.Value,
            ((App)Application.Current).DcsLinkSecret);
        if (result.Success)
            _log.LogInformation("Lua scripts deployed: {DeployResult}", result.Message);
        else
            _log.LogError("Lua deploy failed: {DeployResult}", result.Message);
        DeployStatusText.Text = result.Success ? result.Message : $"Failed: {result.Message}";
        _notifications.Show(
            result.Success ? "Lua scripts deployed" : "Lua deploy failed",
            result.Message,
            result.Success ? NotificationSeverity.Success : NotificationSeverity.Error);
    }

    private static App CurrentApp => (App)Application.Current;

    // Masked by default; the key is a credential, so it's only revealed on request.
    private void RenderApiKey()
    {
        ApiKeyText.Text = _apiKeyVisible ? CurrentApp.McpApiKey : new string('•', 24);
        ShowApiKeyButton.Content = _apiKeyVisible ? "Hide" : "Show";
    }

    private void OnShowApiKeyClicked(object sender, RoutedEventArgs e)
    {
        _apiKeyVisible = !_apiKeyVisible;
        RenderApiKey();
    }

    private void OnCopyApiKeyClicked(object sender, RoutedEventArgs e) => CopyToClipboard(CurrentApp.McpApiKey);

    private async void OnRegenerateApiKeyClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Regenerate the MCP API key?",
            Content = "The current key stops working immediately. Every MCP client using it (e.g. Claude Code) " +
                      "must be registered again with the new key.",
            PrimaryButtonText = "Regenerate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        CurrentApp.RegenerateApiKey();
        RenderApiKey();
        ClaudeCodeStatusText.Text = "Key regenerated — register with Claude Code again to update it.";
        _notifications.Show("MCP API key regenerated", "Re-register your MCP clients with the new key.", NotificationSeverity.Warning);
    }

    private async void OnRegisterClaudeCodeClicked(object sender, RoutedEventArgs e)
    {
        ClaudeCodeStatusText.Text = "Registering…";
        ClaudeCodeRegistrar.Result result = await ClaudeCodeRegistrar.RegisterAsync(CurrentApp.McpUrl, CurrentApp.McpApiKey);
        ClaudeCodeStatusText.Text = result.ClaudeNotFound
            ? result.Message + " Use Copy command."
            : result.Message;
        if (result.Success)
            _log.LogInformation("Registered the MCP server with Claude Code at {McpUrl}", CurrentApp.McpUrl);
        else
            _log.LogWarning("Claude Code registration failed: {Reason}", result.Message); // already redacted
        _notifications.Show(
            result.Success ? "Registered with Claude Code" : "Claude Code registration failed",
            result.Message,
            result.Success ? NotificationSeverity.Success : NotificationSeverity.Error);
    }

    private void OnCopyClaudeCommandClicked(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(ClaudeCodeRegistration.CopyableCommand(CurrentApp.McpUrl, CurrentApp.McpApiKey));
        ClaudeCodeStatusText.Text = "Command copied — it contains your API key; paste it only into your own terminal.";
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenLogFolder();

    // Nothing is persisted until Save, so discarding staged edits is just closing; App drops its
    // reference on Closed and the next open reloads from SettingsService.
    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!ValidatePaths())
        {
            // The errors render under the path boxes, which may be on a hidden tab — jump to it.
            SectionNav.SelectedItem = SectionNav.MenuItems[1];
            SaveStatusText.Text = "Not saved: fix the DCS paths.";
            return;
        }

        // Only connection settings need a bridge restart (which also drops the DCS connection);
        // units, paths and toast duration apply without one.
        bool connectionChanged = _settings.McpPort != (int)McpPortBox.Value
            || _settings.DcsHost != DcsHostBox.Text
            || _settings.DcsPort != (int)DcsPortBox.Value;

        LogLevel newLogLevel = LogLevels[Math.Max(0, LogLevelBox.SelectedIndex)];
        UnitSystem newUnits = UnitsBox.SelectedIndex == 1 ? UnitSystem.Metric : UnitSystem.Imperial;

        // Names only, never values: keeps the log free of anything a setting might hold.
        var changed = new List<string>();
        if (_settings.McpPort != (int)McpPortBox.Value) changed.Add(nameof(SettingsService.McpPort));
        if (_settings.DcsHost != DcsHostBox.Text) changed.Add(nameof(SettingsService.DcsHost));
        if (_settings.DcsPort != (int)DcsPortBox.Value) changed.Add(nameof(SettingsService.DcsPort));
        if (_settings.DcsInstallPath != InstallPathBox.Text) changed.Add(nameof(SettingsService.DcsInstallPath));
        if (_settings.DcsSavedGamesPath != SavedGamesPathBox.Text) changed.Add(nameof(SettingsService.DcsSavedGamesPath));
        if (_settings.ToastDurationSeconds != (int)ToastDurationBox.Value) changed.Add(nameof(SettingsService.ToastDurationSeconds));
        if (_settings.Units != newUnits) changed.Add(nameof(SettingsService.Units));
        if (_settings.LogLevel != newLogLevel) changed.Add(nameof(SettingsService.LogLevel));
        if (_settings.LogRetentionDays != (int)LogRetentionBox.Value) changed.Add(nameof(SettingsService.LogRetentionDays));

        _settings.McpPort = (int)McpPortBox.Value;
        _settings.DcsHost = DcsHostBox.Text;
        _settings.DcsPort = (int)DcsPortBox.Value;
        _settings.DcsInstallPath = InstallPathBox.Text;
        _settings.DcsSavedGamesPath = SavedGamesPathBox.Text;
        _settings.ToastDurationSeconds = (int)ToastDurationBox.Value;
        _settings.Units = newUnits;
        _settings.LogLevel = newLogLevel;
        _settings.LogRetentionDays = (int)LogRetentionBox.Value;
        _log.LogInformation("Settings saved; changed: {ChangedSettings}", changed.Count == 0 ? "none" : string.Join(", ", changed));

        if (connectionChanged) SaveStatusText.Text = "Restarting bridge…";
        try
        {
            await _applySettingsAsync(connectionChanged);
            _notifications.Show("Settings saved",
                connectionChanged ? "Bridge restarted with the new settings." : "Applied without restarting the bridge.",
                NotificationSeverity.Success);
            Close(); // the toast in the main window confirms the save
        }
        catch (Exception ex)
        {
            // Stay open so the error is visible and the offending value (e.g. a busy port) can be fixed.
            SaveStatusText.Text = $"Saved, but bridge restart failed: {ex.Message}";
            _notifications.Show("Bridge restart failed", ex.Message, NotificationSeverity.Error);
        }
    }
}
