using Microsoft.Extensions.Logging;

namespace GridCore.Platform.Notifications;

/// <summary>
/// The stub implementation: notifications are written to the log, which is enough for the MVP and
/// visible in the Aspire dashboard. Replaced by a real transport through DI, with no caller change.
/// </summary>
public sealed partial class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    /// <inheritdoc />
    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        Notified(logger, notification.Channel, notification.Recipient, notification.Subject, notification.Body);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Notification [{Channel}] to {Recipient}: {Subject} — {Body}")]
    private static partial void Notified(ILogger logger, string channel, string recipient, string subject, string body);
}
