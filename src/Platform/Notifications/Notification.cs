namespace GridCore.Platform.Notifications;

/// <summary>Delivery channels a notification can be sent over.</summary>
public static class NotificationChannels
{
    /// <summary>Email.</summary>
    public const string Email = "email";

    /// <summary>SMS.</summary>
    public const string Sms = "sms";

    /// <summary>An in-app message shown in the SPA.</summary>
    public const string InApp = "in-app";
}

/// <summary>Something GridCore wants to tell someone.</summary>
/// <param name="Channel">How to deliver it — see <see cref="NotificationChannels"/>.</param>
/// <param name="Recipient">Who to tell: a user id, an email address or a role name.</param>
/// <param name="Subject">One-line summary.</param>
/// <param name="Body">The message.</param>
public sealed record Notification(string Channel, string Recipient, string Subject, string Body);

/// <summary>
/// Where notifications go. A stub for now: the MVP has no mail server, and the point of the seam is
/// that swapping in a real transport is a DI change and nothing else — the same rule as the provider
/// interfaces in ARCHITECTURE.md.
/// </summary>
public interface INotificationSender
{
    /// <summary>Delivers <paramref name="notification"/>. Never throws for a delivery failure — a
    /// business action must not fail because nobody could be told about it.</summary>
    Task SendAsync(Notification notification, CancellationToken cancellationToken = default);
}
