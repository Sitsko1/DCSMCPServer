using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DCS.AIAutomator.Core;
using DCS.AIAutomator.Agents;
using DCS.AIAutomator.Mcp;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
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
        AgentsPanel.Visibility = tag == "Agents" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Agents") _ = RenderAgentsAsync(); // re-detect each visit (agents may have been installed meanwhile)
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

    private async void OnDeployClicked(object sender, RoutedEventArgs e)
    {
        string savedGamesPath = SavedGamesPathBox.Text;
        var result = LuaScriptDeployer.Deploy(savedGamesPath, DcsHostBox.Text, (int)DcsPortBox.Value,
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

        if (result.Success) await OfferMissionScriptingAsync(savedGamesPath);
    }

    /// <summary>
    /// Messages and AI tasking run in DCS's mission scripting environment, which a Hooks script
    /// can only reach once autoexec.cfg allows it. That's a DCS security setting, so it's only
    /// changed with consent; declining leaves those commands returning a clear error.
    /// </summary>
    private async Task OfferMissionScriptingAsync(string savedGamesPath)
    {
        if (AutoexecConfig.IsEnabled(savedGamesPath)) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Allow mission scripting?",
            Content = "To show ATC messages in DCS (and later task AI flights), the Hooks script needs DCS's " +
                      "net.dostring_in API, which DCS only enables through Config\\autoexec.cfg.\n\n" +
                      "This adds two settings to that file, keeping everything already in it (a .bak copy is made). " +
                      "They also let any other Hooks script you've installed run code in missions.\n\n" +
                      "Without them, DCS still connects and reports aircraft state, but messages fail with an error.",
            PrimaryButtonText = "Allow",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            AutoexecConfig.Enable(savedGamesPath);
            _log.LogInformation("Mission scripting enabled in autoexec.cfg");
            _notifications.Show("Mission scripting allowed", "Restart DCS to apply it.", NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Couldn't update autoexec.cfg");
            _notifications.Show("Couldn't update autoexec.cfg", ex.Message, NotificationSeverity.Error);
        }
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
        _notifications.Show("MCP API key regenerated", "Re-register your MCP clients with the new key.", NotificationSeverity.Warning);
        await OfferAgentUpdatesAsync("The MCP API key was regenerated");
    }

    private IReadOnlyList<IAgentIntegration> DetectAgents() =>
        AgentIntegrations.Detected(() => CurrentApp.McpUrl, () => CurrentApp.McpApiKey, Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "Backups"));

    private async Task RenderAgentsAsync()
    {
        IReadOnlyList<IAgentIntegration> agents = DetectAgents();
        AgentsList.Children.Clear();
        NoAgentsText.Visibility = agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (IAgentIntegration agent in agents)
        {
            AgentsList.Children.Add(await BuildAgentCardAsync(agent));
        }
    }

    private async Task<Border> BuildAgentCardAsync(IAgentIntegration agent)
    {
        var status = new TextBlock { Style = (Style)Application.Current.Resources["SubtleMonoStyle"], TextWrapping = TextWrapping.Wrap, Text = "Checking…" };
        var connect = new Button { Content = "Connect", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var disconnect = new Button { Content = "Disconnect" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { connect, disconnect } };
        if (agent is ClaudeCodeAgent claudeCode)
        {
            var copy = new Button { Content = "Copy command" };
            ToolTipService.SetToolTip(copy, "Copy the claude mcp add command to run it yourself");
            copy.Click += (_, _) =>
            {
                CopyToClipboard(claudeCode.CopyableCommand());
                status.Text = "Command copied — it contains your API key; paste it only into your own terminal.";
            };
            buttons.Children.Add(copy);
        }

        connect.Click += async (_, _) => await RunAgentActionAsync(agent, agent.ConnectAsync, "connected", status);
        disconnect.Click += async (_, _) => await RunAgentActionAsync(agent, agent.DisconnectAsync, "disconnected", status);

        var card = new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = agent.Name, Style = (Style)Application.Current.Resources["SettingsCardTitleStyle"] },
                            new TextBlock { Text = agent.Description, Style = (Style)Application.Current.Resources["SettingsCardDescriptionStyle"], TextWrapping = TextWrapping.Wrap },
                        },
                    },
                    buttons,
                    status,
                },
            },
        };
        status.Text = ConnectionText(await agent.IsConnectedAsync());
        return card;
    }

    private static string ConnectionText(bool? connected) => connected switch
    {
        true => "Connected",
        false => "Not connected",
        null => "Connection state unknown",
    };

    private async Task RunAgentActionAsync(IAgentIntegration agent, Func<Task<AgentResult>> action, string verb, TextBlock status)
    {
        status.Text = "Working…";
        AgentResult result = await action();
        status.Text = $"{result.Message}  ({ConnectionText(await agent.IsConnectedAsync())})";
        if (result.Success)
            _log.LogInformation("AI agent {Agent} {Verb}", agent.Name, verb);
        else
            _log.LogWarning("AI agent {Agent} not {Verb}: {Reason}", agent.Name, verb, result.Message); // messages never contain the key
        _notifications.Show(
            result.Success ? $"{agent.Name} {verb}" : $"{agent.Name}: action failed",
            result.Message,
            result.Success ? NotificationSeverity.Success : NotificationSeverity.Error);
    }

    /// <summary>After a key or port change, offers to update connected agents whose entries embed them.</summary>
    private async Task OfferAgentUpdatesAsync(string reason)
    {
        foreach (IAgentIntegration agent in DetectAgents().Where(a => a.NeedsUpdateWhenKeyOrPortChanges))
        {
            if (await agent.IsConnectedAsync() != true) continue;

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                RequestedTheme = RootGrid.ActualTheme,
                Title = $"Update {agent.Name}?",
                Content = $"{reason}, but {agent.Name} still has the old one and will be refused until it's updated.",
                PrimaryButtonText = "Update now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) continue;

            AgentResult result = await agent.ConnectAsync();
            _log.LogInformation("AI agent {Agent} update after change: {Outcome}", agent.Name, result.Success ? "succeeded" : "failed");
            _notifications.Show(
                result.Success ? $"{agent.Name} updated" : $"{agent.Name}: update failed",
                result.Message,
                result.Success ? NotificationSeverity.Success : NotificationSeverity.Error);
        }
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenLogFolder();

    private async void OnExportLogsClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
            SuggestedFileName = $"dcs-aiautomator-logs-{DateTime.Now:yyyyMMdd-HHmm}",
        };
        picker.FileTypeChoices.Add("Zip archive", [".zip"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            // All agents, detected or not: "not found" is useful in a report too.
            var agents = AgentIntegrations.All(() => CurrentApp.McpUrl, () => CurrentApp.McpApiKey,
                    Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "Backups"))
                .Select(a => (a.Name, a.IsDetected()));
            string about = CurrentApp.DiagnosticsAbout(agents);
            using (Stream zip = await file.OpenStreamForWriteAsync())
            {
                zip.SetLength(0); // the picker may have chosen an existing file
                DiagnosticsExport.WriteZip(zip, CurrentApp.LogDirectory, about);
            }
            _log.LogInformation("Logs exported for a bug report");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Log export failed");
            _notifications.Show("Log export failed", ex.Message, NotificationSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Logs exported",
            Content = $"Saved to {file.Path}. Attach it to your issue.",
            PrimaryButtonText = "Open folder",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var options = new Windows.System.FolderLauncherOptions();
            options.ItemsToSelect.Add(file);
            _ = Windows.System.Launcher.LaunchFolderAsync(await file.GetParentAsync(), options);
        }
    }

    // Nothing is persisted until Save, so discarding staged edits is just closing; App drops its
    // reference on Closed and the next open reloads from SettingsService.
    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!ValidatePaths())
        {
            // The errors render under the path boxes, which may be on a hidden tab — jump to it.
            SectionNav.SelectedItem = SectionNav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "Paths");
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
            if (changed.Contains(nameof(SettingsService.McpPort))) await OfferAgentUpdatesAsync("The MCP port changed");
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
