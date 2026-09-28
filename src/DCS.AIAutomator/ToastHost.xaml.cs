using System;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DCS.AIAutomator;

/// <summary>
/// Displays transient in-app toasts for <see cref="NotificationService"/>. Subscribes to
/// <see cref="NotificationService.Raised"/> and shows each notification as a card that
/// auto-dismisses after <see cref="SettingsService.ToastDurationSeconds"/> (or on close).
/// Cards are styled by the theme-aware ToastCardStyle/ToastTitleStyle/ToastMessageStyle in
/// ThemeResources.xaml; only the severity accent bar is resolved at runtime, from the active
/// theme dictionary.
/// </summary>
public sealed partial class ToastHost : UserControl
{
    private readonly NotificationService _service;
    private readonly SettingsService _settings;
    private readonly DispatcherQueue _dispatcherQueue;

    private static readonly TimeSpan StaggerGap = TimeSpan.FromSeconds(1);
    private DateTimeOffset _lastExpiry = DateTimeOffset.MinValue;

    public ToastHost(NotificationService service, SettingsService settings)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _service.Raised += OnNotificationRaised;
    }

    private void OnNotificationRaised(object? sender, Notification notification)
    {
        _dispatcherQueue.TryEnqueue(() => ShowToast(notification));
    }

    private void ShowToast(Notification notification)
    {
        var accentBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = ThemeBrush(SeverityBrushKey(notification.Severity)),
        };

        var title = new TextBlock
        {
            Text = notification.Title,
            Style = (Style)Application.Current.Resources["ToastTitleStyle"],
        };

        var message = new TextBlock
        {
            Text = notification.Message,
            Style = (Style)Application.Current.Resources["ToastMessageStyle"],
        };

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Style = (Style)Application.Current.Resources["GlassIconGhostButtonStyle"],
            Width = 24,
            Height = 24,
            Padding = new Thickness(0, 0, 0, 0),
        };

        var textStack = new StackPanel { Spacing = 2 };
        textStack.Children.Add(title);
        textStack.Children.Add(message);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(accentBar);
        grid.Children.Add(textStack);
        grid.Children.Add(close);

        var toast = new Border
        {
            Style = (Style)Application.Current.Resources["ToastCardStyle"],
            Child = grid,
        };
        close.Click += (_, _) => Dismiss(toast);

        ToastStack.Children.Add(toast);

        // Notifications arrive in bursts (bridge start → running → DCS connected → mission), so
        // equal TTLs would expire together. Give each its full TTL, but never less than a gap
        // after the previous toast's expiry, so a burst leaves one at a time, oldest first.
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset expiry = now + TimeSpan.FromSeconds(Math.Max(1, _settings.ToastDurationSeconds));
        if (expiry < _lastExpiry + StaggerGap) expiry = _lastExpiry + StaggerGap;
        _lastExpiry = expiry;

        var timer = new DispatcherTimer { Interval = expiry - now };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss(toast);
        };
        timer.Start();
    }

    private void Dismiss(Border toast)
    {
        if (ToastStack.Children.Contains(toast))
        {
            ToastStack.Children.Remove(toast);
        }
    }

    /// <summary>
    /// Resolves a brush from the active theme dictionary (Light/Dark) so the severity accent bar
    /// follows the window's effective theme, matching how XAML {ThemeResource} lookups behave.
    /// </summary>
    private Brush ThemeBrush(string key)
    {
        string theme = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        // ThemeDictionaries live in the merged ThemeResources.xaml, not on the app root, and the
        // ThemeDictionaries indexer doesn't search merged dictionaries — walk them explicitly.
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(theme, out var dictionary)
                && ((ResourceDictionary)dictionary).TryGetValue(key, out var brush))
            {
                return (Brush)brush;
            }
        }
        throw new System.Collections.Generic.KeyNotFoundException($"Brush '{key}' not found in '{theme}' theme dictionary.");
    }

    private static string SeverityBrushKey(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => "NominalBrush",
        NotificationSeverity.Warning => "WarningBrush",
        NotificationSeverity.Error => "FaultBrush",
        _ => "TextSecondaryBrush",
    };
}
