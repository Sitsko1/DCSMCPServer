using Microsoft.UI.Xaml.Controls;

namespace DCS.AIAutomator;

/// <summary>
/// Read-only review of every notification raised so far, newest first, with a timestamp and
/// severity for each entry. Bound to <see cref="NotificationService.History"/>.
/// </summary>
public sealed partial class NotificationHistoryDialog : ContentDialog
{
    public NotificationHistoryDialog(NotificationService service)
    {
        InitializeComponent();
        HistoryList.ItemsSource = service.History;
    }
}
