using System.Globalization;
using System.Text;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Notifications;

/// <summary>What a headless run found and managed to deliver.</summary>
public sealed record HeadlessNotifyResult
{
    public required DispatchReport Dispatch { get; init; }
    public required IReadOnlyList<PortfolioAlert> Alerts { get; init; }

    /// <summary>Reasons some checks were skipped or failed. Never empty just because nothing was found.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>What this run actually checked, in one honest line (plan §8: 检查范围).</summary>
    public string CheckedScope { get; init; } = "打新时限";

    /// <summary>Data date of the extended check, when layer 2 ran and succeeded.</summary>
    public DateOnly? ExtendedDataAsOf { get; init; }

    /// <summary>How many firing alerts stayed quiet because they were delivered within the cooldown.</summary>
    public int SuppressedByCooldown { get; init; }

    public string Summarize()
    {
        var text = new StringBuilder(Dispatch.Describe());
        text.Append(" 已检查：").Append(CheckedScope);
        if (ExtendedDataAsOf is { } asOf)
        {
            text.Append("（数据时间 ").Append(asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('）');
        }

        if (SuppressedByCooldown > 0)
        {
            text.Append($"；{SuppressedByCooldown} 条在冷却期内未重复发送");
        }

        foreach (var limitation in Limitations)
        {
            text.Append(' ').Append(limitation);
        }

        return text.ToString();
    }
}

/// <summary>
/// The check that runs when the app is closed — now in two layers (plan §8):
///
/// Layer 1 (always): deadline/calendar reminders answered by the ledger alone — IPO
/// payments, listings. No market data, no analytics core, cannot fail for network reasons.
///
/// Layer 2 (optional, <see cref="NotificationSettings.BackgroundExtendedChecks"/>): the
/// full price / concentration / data-quality rule set, the same evaluator and thresholds
/// the open app uses. Needs the analytics core; the caller injects how to run it. Any
/// failure there becomes a reported limitation — the deadline layer still goes out.
///
/// Dedup/cooldown/re-entry across runs is <see cref="AlertGatekeeper"/> over a persisted
/// <see cref="AlertNotificationState"/>, so a scheduled task firing repeatedly says each
/// thing once, and re-entry of a condition notifies again.
/// </summary>
public static class HeadlessNotifier
{
    public static async Task<HeadlessNotifyResult> RunAsync(
        PortfolioStore store,
        NotificationSettings settings,
        DateOnly? asOf = null,
        HttpClient? http = null,
        Func<CancellationToken, Task<BackgroundCheckResult>>? extendedCheck = null,
        AlertNotificationStateStore? stateStore = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        var at = now ?? DateTimeOffset.Now;
        var today = asOf ?? DateOnly.FromDateTime(DateTime.Now);

        // ---- Layer 1: ledger-only, always runs, cannot be taken down by the network. ----
        var alerts = PortfolioAlertEvaluator.IpoDeadlines(store.ListIpoSubscriptions(), today).ToList();
        var limitations = new List<string>();
        var scope = "打新时限";
        DateOnly? dataAsOf = null;
        IReadOnlySet<string> unknownFxKeys = new HashSet<string>(StringComparer.Ordinal);

        // ---- Layer 2: optional, isolated. Its failure is information, never a blocker. ----
        if (settings.BackgroundExtendedChecks && extendedCheck is not null)
        {
            try
            {
                var extended = await extendedCheck(cancellationToken).ConfigureAwait(false);
                // The extended run evaluates the IPO deadlines too (same evaluator); merging
                // by key keeps each fact in the message exactly once.
                alerts = MergeBy(alerts, extended.Alerts);
                scope = "打新时限、价格与集中度（含数据质量）";
                dataAsOf = today;
                unknownFxKeys = extended.UnknownAlertKeys;

                // WP-02 R6/A10: a rule we could not judge says so, in the same report as the
                // alerts — a silent outage is indistinguishable from "nothing to report".
                foreach (var note in extended.Notes)
                {
                    limitations.Add(note);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                limitations.Add($"扩展检查失败（价格/集中度）：{ex.Message}。打新时限提醒不受影响。");
            }
        }
        else if (!settings.BackgroundExtendedChecks)
        {
            limitations.Add("（未开启扩展检查：价格与集中度提醒需要打开软件，或在设置里开启后台扩展检查。）");
        }

        // ---- Dedup / cooldown against what was already delivered. ----
        var state = stateStore?.Load().Pruned(at) ?? AlertNotificationState.Empty;
        var decision = AlertGatekeeper.Filter(alerts, state, at, CooldownOf(settings), unknownFxKeys);

        // ---- Dispatch what is due. ----
        DispatchReport report;
        var deliveredSelection = decision.ToSend;
        if (!settings.HasUsableChannel)
        {
            report = new DispatchReport { Results = [], AlertCount = decision.ToSend.Count };
        }
        else
        {
            var dispatcher = NotificationDispatcher.From(settings, http);
            report = await dispatcher
                .SendAsync(decision.ToSend, settings.IncludeRoutineAlerts, cancellationToken)
                .ConfigureAwait(false);

            // The dispatcher may drop routine alerts when muted; only what was actually in
            // the message counts as delivered, so their cooldown starts only now.
            deliveredSelection = settings.IncludeRoutineAlerts
                ? decision.ToSend
                : decision.ToSend.Where(NotificationDispatcher.IsTimeCritical).ToList();
        }

        var nextState = decision.NextState;
        if (deliveredSelection.Count > 0 && report.AnyDelivered)
        {
            nextState = AlertGatekeeper.MarkDelivered(nextState, deliveredSelection, at);
        }

        stateStore?.Save(nextState);

        return new HeadlessNotifyResult
        {
            Dispatch = report,
            Alerts = alerts,
            Limitations = limitations,
            CheckedScope = scope,
            ExtendedDataAsOf = dataAsOf,
            SuppressedByCooldown = decision.Suppressed.Count,
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

    private static TimeSpan CooldownOf(NotificationSettings settings) =>
        settings.AlertCooldownHours > 0
            ? TimeSpan.FromHours(settings.AlertCooldownHours)
            : TimeSpan.Zero;

    /// <summary>Merges two alert lists, dropping keys that appear in both (extended list wins).</summary>
    private static List<PortfolioAlert> MergeBy(
        List<PortfolioAlert> layer1, IReadOnlyList<PortfolioAlert> layer2)
    {
        var extendedKeys = layer2
            .Select(AlertGatekeeper.KeyOf)
            .ToHashSet(StringComparer.Ordinal);
        return [.. layer1.Where(a => !extendedKeys.Contains(AlertGatekeeper.KeyOf(a))), .. layer2];
    }
}
