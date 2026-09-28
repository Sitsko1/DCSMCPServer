using System;

namespace DCS.AIAutomator;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// A single app notification: a title, message, severity (drives the toast accent color) and the
/// time it was raised. Immutable; instances are produced by <see cref="NotificationService"/>.
/// </summary>
public sealed record Notification(
    string Title,
    string Message,
    NotificationSeverity Severity,
    DateTimeOffset Timestamp)
{
    /// <summary>Local wall-clock time for display in the history list.</summary>
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Uppercased severity name for display in the history list.</summary>
    public string SeverityText => Severity.ToString().ToUpperInvariant();
}
