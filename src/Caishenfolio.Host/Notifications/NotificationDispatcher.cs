using System.Globalization;
using System.Text;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Notifications;

/// <summary>What one dispatch attempt achieved.</summary>
public sealed record DispatchReport
{
    public required IReadOnlyList<NotificationResult> Results { get; init; }
    public required int AlertCount { get; init; }

    public bool AnyDelivered => Results.Any(r => r.Ok);
    public bool AllDelivered => Results.Count > 0 && Results.All(r => r.Ok);

    public string Describe()
    {
        if (AlertCount == 0)
        {
            return "没有需要提醒的事项。";
        }

        if (Results.Count == 0)
        {
            return $"有 {AlertCount} 条提醒，但没有配置任何通知渠道。";
        }

        var ok = Results.Count(r => r.Ok);
        var failures = Results.Where(r => !r.Ok).Select(r => $"{r.Channel}：{r.Error}");
        var summary = $"{AlertCount} 条提醒，{ok}/{Results.Count} 个渠道送达。";
        return failures.Any() ? summary + " 失败：" + string.Join("；", failures) : summary;
    }
}

/// <summary>
/// Turns alerts into a message and fans it out to every configured channel.
///
/// The reason this exists is the IPO payment window: winning an allotment and not paying by the
/// deadline forfeits it, and three forfeits inside a year suspend the account from subscribing
/// for six months. That deadline does not wait for the app to be opened, so the notification has
/// to leave the machine.
///
/// Channels are tried independently and in parallel. One broken webhook must not stop the email.
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly IReadOnlyList<INotificationChannel> _channels;

    public NotificationDispatcher(IReadOnlyList<INotificationChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = channels;
    }

    public static NotificationDispatcher From(NotificationSettings settings, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new NotificationDispatcher(settings.BuildChannels(http));
    }

    public async Task<DispatchReport> SendAsync(
        IReadOnlyList<PortfolioAlert> alerts,
        bool includeRoutineAlerts = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        var selected = includeRoutineAlerts
            ? alerts
            : alerts.Where(IsTimeCritical).ToList();

        if (selected.Count == 0 || _channels.Count == 0)
        {
            return new DispatchReport { Results = [], AlertCount = selected.Count };
        }

        var message = BuildMessage(selected);
        var results = await Task
            .WhenAll(_channels.Select(c => c.SendAsync(message, cancellationToken)))
            .ConfigureAwait(false);

        return new DispatchReport { Results = results, AlertCount = selected.Count };
    }

    /// <summary>Sends one message directly, so the user can prove a channel works before relying on it.</summary>
    public async Task<DispatchReport> SendTestAsync(CancellationToken cancellationToken = default)
    {
        var message = new NotificationMessage
        {
            Title = "OMNIX-Caishenfolio 测试通知",
            Body = "这是一条测试消息。收到它说明该渠道可用，打新缴款等有时限的提醒能送达。",
        };

        if (_channels.Count == 0)
        {
            return new DispatchReport { Results = [], AlertCount = 1 };
        }

        var results = await Task
            .WhenAll(_channels.Select(c => c.SendAsync(message, cancellationToken)))
            .ConfigureAwait(false);

        return new DispatchReport { Results = results, AlertCount = 1 };
    }

    /// <summary>An alert with a deadline behind it, which is sent even when routine ones are muted.</summary>
    private static bool IsTimeCritical(PortfolioAlert alert) =>
        alert.Kind is AlertKind.IpoDeadline || alert.Severity == AlertSeverity.Warning;

    internal static NotificationMessage BuildMessage(IReadOnlyList<PortfolioAlert> alerts)
    {
        // Deadlines first: they are the ones that stop mattering if read late.
        var ordered = alerts
            .OrderByDescending(a => a.Kind == AlertKind.IpoDeadline)
            .ThenByDescending(a => a.Severity == AlertSeverity.Warning)
            .ToList();

        var urgent = ordered.Any(a => a.Kind == AlertKind.IpoDeadline);
        var body = new StringBuilder();
        foreach (var alert in ordered)
        {
            var marker = alert.Severity == AlertSeverity.Warning ? "[注意] " : "";
            body.Append(marker).Append(alert.Title);
            if (!string.IsNullOrWhiteSpace(alert.Message))
            {
                body.Append('\n').Append("  ").Append(alert.Message);
            }

            body.Append('\n');
        }

        body.Append("\n(研究/记录用途，非投资建议。)");

        var stamp = DateTime.Now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        var title = urgent
            ? $"【待办】OMNIX 提醒 {ordered.Count} 条 · {stamp}"
            : $"OMNIX 提醒 {ordered.Count} 条 · {stamp}";

        return new NotificationMessage
        {
            Title = title,
            Body = body.ToString().TrimEnd(),
            Urgency = urgent ? NotificationUrgency.Urgent : NotificationUrgency.Normal,
        };
    }
}
