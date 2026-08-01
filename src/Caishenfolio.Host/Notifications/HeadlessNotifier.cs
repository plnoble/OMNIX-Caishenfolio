using System.Globalization;
using System.Text;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Notifications;

/// <summary>What a headless run found and managed to deliver.</summary>
public sealed record HeadlessNotifyResult
{
    public required DispatchReport Dispatch { get; init; }
    public required IReadOnlyList<PortfolioAlert> Alerts { get; init; }

    /// <summary>Reasons some checks were skipped. Never empty just because nothing was found.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    public string Summarize()
    {
        var text = new StringBuilder(Dispatch.Describe());
        foreach (var limitation in Limitations)
        {
            text.Append(' ').Append(limitation);
        }

        return text.ToString();
    }
}

/// <summary>
/// The check that runs when the app is closed.
///
/// Its whole reason for existing is that an IPO allotment has to be paid for within a couple of
/// days or it is forfeited, and the user does not open a portfolio app every morning. A Windows
/// scheduled task starts the app with <c>--notify</c>, this runs, the message goes out over the
/// configured channels, and the process exits without ever drawing a window.
///
/// It deliberately checks only what the ledger alone can answer. Price and concentration alerts
/// need the analytics core, a Python runtime and live quotes; starting all of that on a timer
/// turns a two-second job into a fragile one, and a failure there would be a silent no-alert
/// result — the worst possible outcome for a deadline reminder. So this reports plainly that
/// price checks were skipped rather than implying it looked and found nothing.
/// </summary>
public static class HeadlessNotifier
{
    public static async Task<HeadlessNotifyResult> RunAsync(
        PortfolioStore store,
        NotificationSettings settings,
        DateOnly? asOf = null,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        var today = asOf ?? DateOnly.FromDateTime(DateTime.Now);
        var alerts = PortfolioAlertEvaluator.IpoDeadlines(store.ListIpoSubscriptions(), today);

        var limitations = new List<string>
        {
            "（后台检查只覆盖打新时限，价格与集中度提醒需要打开软件。）",
        };

        if (!settings.HasUsableChannel)
        {
            return new HeadlessNotifyResult
            {
                Dispatch = new DispatchReport { Results = [], AlertCount = alerts.Count },
                Alerts = alerts,
                Limitations = limitations,
            };
        }

        var dispatcher = NotificationDispatcher.From(settings, http);
        var report = await dispatcher
            .SendAsync(alerts, settings.IncludeRoutineAlerts, cancellationToken)
            .ConfigureAwait(false);

        return new HeadlessNotifyResult
        {
            Dispatch = report,
            Alerts = alerts,
            Limitations = limitations,
        };
    }

    /// <summary>
    /// Appends one line to the log. A scheduled task has no console, so without this a run that
    /// silently failed would look exactly like a run that found nothing.
    /// </summary>
    public static void AppendLog(string logPath, HeadlessNotifyResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentNullException.ThrowIfNull(result);

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss}  {1}{2}",
            DateTime.Now,
            result.Summarize(),
            Environment.NewLine);

        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(logPath, line, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to log must not fail the notification that already went out.
        }
    }

    /// <summary>Default log location, next to the rest of the app's state.</summary>
    public static string DefaultLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Caishenfolio",
        "logs",
        "notify.log");
}
