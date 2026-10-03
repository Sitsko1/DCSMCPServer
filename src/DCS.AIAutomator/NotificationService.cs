using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;

namespace DCS.AIAutomator;

/// <summary>
/// App-wide notification hub. Every notification is appended to <see cref="History"/> (newest
/// first) and surfaced through <see cref="Raised"/> so any window's toast host can display it.
/// All mutations are marshaled to the UI thread captured at construction, so background threads
/// (e.g. DcsConnection) can call <see cref="Show"/> safely.
/// </summary>
public sealed class NotificationService
{
    private readonly DispatcherQueue _dispatcherQueue;

    public NotificationService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>Opens the app's log; error toasts offer it as a "View log" action when set.</summary>
    public Action? ViewLog { get; init; }

    /// <summary>All notifications raised so far, newest first. Bind this to the history view.</summary>
    public ObservableCollection<Notification> History { get; } = new();

    /// <summary>Raised on the UI thread for each new notification, after it is added to history.</summary>
    public event EventHandler<Notification>? Raised;

    public void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info)
    {
        var notification = new Notification(title, message, severity, DateTimeOffset.Now);
        if (_dispatcherQueue.HasThreadAccess)
        {
            Add(notification);
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => Add(notification));
        }
    }

    private void Add(Notification notification)
    {
        History.Insert(0, notification);
        Raised?.Invoke(this, notification);
    }
}
