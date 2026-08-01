namespace Caishenfolio.Host.Notifications;

public enum NotificationUrgency
{
    /// <summary>Worth reading when convenient.</summary>
    Normal,

    /// <summary>Has a deadline attached — an unpaid IPO allotment is the case this exists for.</summary>
    Urgent,
}

/// <summary>
/// One message on its way out of the app.
///
/// Deliberately plain text: every channel this supports renders text, and the ones that render
/// markup do it differently enough that a shared rich format would look broken somewhere.
/// </summary>
public sealed record NotificationMessage
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public NotificationUrgency Urgency { get; init; } = NotificationUrgency.Normal;

    /// <summary>Title and body as one block, for channels that take a single field.</summary>
    public string ToPlainText() =>
        string.IsNullOrWhiteSpace(Body) ? Title : $"{Title}\n\n{Body}";
}

/// <summary>What happened when one channel tried to deliver one message.</summary>
public sealed record NotificationResult
{
    public required string Channel { get; init; }
    public required bool Ok { get; init; }
    public string Error { get; init; } = "";

    public static NotificationResult Success(string channel) =>
        new() { Channel = channel, Ok = true };

    public static NotificationResult Failure(string channel, string error) =>
        new() { Channel = channel, Ok = false, Error = error };
}

/// <summary>Somewhere a message can be delivered.</summary>
public interface INotificationChannel
{
    /// <summary>Shown in settings and in delivery reports.</summary>
    string Name { get; }

    /// <summary>
    /// Delivers the message. Must not throw for a delivery failure — a channel being
    /// misconfigured or offline has to be reportable alongside the ones that worked.
    /// </summary>
    Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
