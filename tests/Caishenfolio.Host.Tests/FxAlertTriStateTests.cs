using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-02 A10/A11: the three-state FX reminder. A quote that cannot be fetched is UNKNOWN, not
/// "the condition became false" — the difference decides whether a recovered quote fabricates a
/// brand-new "it came back" notification.
/// </summary>
public class FxAlertTriStateTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);
    private static readonly DateOnly Today = new(2026, 8, 3);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_fx_tristate", Guid.NewGuid().ToString("N"));

    private static FxAlertRule Band() => new()
    {
        Kind = FxAlertRuleKind.RateBand,
        Pair = "USD/CNY",
        Lower = 7.0m,
        Upper = 7.3m,
    };

    /// <summary>The alert the evaluator actually produces for a firing band rule.</summary>
    private static PortfolioAlert MetBand(FxAlertRule rule) =>
        Assert.Single(FxAlertRuleEvaluator.Evaluate([rule], _ => (7.1m, "fixture"), _ => null).Alerts);

    /// <summary>Evaluates once and returns the alert identity this rule uses when it fires.</summary>
    private static string KeyOf(FxAlertRule rule)
    {
        var met = FxAlertRuleEvaluator.Evaluate([rule], _ => (7.1m, "fixture"), _ => null);
        return AlertGatekeeper.KeyOf(Assert.Single(met.Alerts));
    }

    private static AlertNotificationState DeliveredOnMonday(string key) => new()
    {
        Records = new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal)
        {
            [key] = new AlertNotificationRecord
            {
                LastNotifiedAt = Monday,
                LastSeenAt = Monday,
                WasInRange = true,
            },
        },
    };

    [Fact]
    public void A10_AnUnreachableQuoteIsUnknownAndKeepsTheLastVerdictAndCooldown()
    {
        var rule = Band();
        var key = KeyOf(rule);
        var state = DeliveredOnMonday(key);

        var unknown = FxAlertRuleEvaluator.Evaluate([rule], _ => null, _ => null);

        // The evaluation says UNKNOWN, names the failing input, and keeps producing no alert.
        Assert.Empty(unknown.Alerts);
        var outcome = Assert.Single(unknown.Outcomes);
        Assert.Equal(FxRuleState.Unknown, outcome.State);
        Assert.Equal(key, outcome.AlertKey);
        Assert.Contains("报价", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains(unknown.Notes, note => note.Contains("报价", StringComparison.Ordinal));
        Assert.Contains(key, unknown.UnknownAlertKeys);

        var decision = AlertGatekeeper.Filter(
            [], state, Monday.AddMinutes(10), Cooldown, unknown.UnknownAlertKeys);

        // Nothing is sent, and the remembered verdict is untouched: the condition did not go
        // false, we simply could not look. The cooldown must not advance either.
        Assert.Empty(decision.ToSend);
        var record = decision.NextState.Records[key];
        Assert.True(record.WasInRange);
        Assert.Equal(Monday, record.LastNotifiedAt);
        Assert.Equal(Monday, record.LastSeenAt);
    }

    [Fact]
    public void A11_RecoveringFromUnknownDoesNotFabricateAReentry()
    {
        var rule = Band();
        var key = KeyOf(rule);
        var delivered = DeliveredOnMonday(key);

        // Run 1: the quote is unreachable for a few minutes.
        var unknown = FxAlertRuleEvaluator.Evaluate([rule], _ => null, _ => null);
        var afterUnknown = AlertGatekeeper.Filter(
            [], delivered, Monday.AddMinutes(5), Cooldown, unknown.UnknownAlertKeys);
        Assert.Empty(afterUnknown.ToSend);

        // Run 2: the quote is back and still inside the band, well within the cooldown.
        var recovered = FxAlertRuleEvaluator.Evaluate([rule], _ => (7.1m, "fixture"), _ => null);
        var decision = AlertGatekeeper.Filter(
            recovered.Alerts, afterUnknown.NextState, Monday.AddMinutes(10), Cooldown, null);

        // The user was already told; the outage must not turn into a second "it is back" message.
        Assert.Empty(decision.ToSend);
        Assert.Single(decision.Suppressed);
        Assert.Equal(key, AlertGatekeeper.KeyOf(decision.Suppressed[0]));
    }

    [Fact]
    public void ATrueExitAndReentryIsStillAnnounced()
    {
        // The guard must not swallow the real thing: a judged NOT-met run does leave the band,
        // so coming back later is a genuine re-entry and must notify immediately.
        var rule = Band();
        var key = KeyOf(rule);
        var delivered = DeliveredOnMonday(key);

        var outside = FxAlertRuleEvaluator.Evaluate([rule], _ => (7.9m, "fixture"), _ => null);
        var outcome = Assert.Single(outside.Outcomes);
        Assert.Equal(FxRuleState.NotMet, outcome.State);
        Assert.Equal(key, outcome.AlertKey);
        var gone = AlertGatekeeper.Filter(
            outside.Alerts, delivered, Monday.AddMinutes(5), Cooldown, outside.UnknownAlertKeys);
        Assert.False(gone.NextState.Records[key].WasInRange);

        var back = FxAlertRuleEvaluator.Evaluate([rule], _ => (7.1m, "fixture"), _ => null);
        var decision = AlertGatekeeper.Filter(back.Alerts, gone.NextState, Monday.AddMinutes(10), Cooldown, null);

        Assert.Single(decision.ToSend);
    }

    [Fact]
    public void ProbeKeyMatchesTheKeyOfTheAlertItRepresents()
    {
        var rule = Band();
        Assert.Equal(KeyOf(rule), Assert.Single(FxAlertRuleEvaluator.KnownKeys([rule])));
    }
    [Fact]
    public async Task AWholeRoundThatCouldNotRunIsUnknownForEveryRule()
    {
        // Review #6: not just one unreachable quote — the whole extended check throwing. Before
        // this, the catch left unknownFxKeys empty, the gatekeeper recorded an exit, and the next
        // good round announced a re-entry that never happened.
        var rule = Band();
        var key = KeyOf(rule);
        Directory.CreateDirectory(_root);
        using var store = PortfolioStore.UnderStateRoot(_root);
        var stateStore = new AlertNotificationStateStore(Path.Combine(_root, "alert-state.json"));
        stateStore.Save(DeliveredOnMonday(key));
        var settings = NotificationSettings.Default with
        {
            BackgroundExtendedChecks = true,
            FxAlertRules = [rule],
        };

        var failed = await HeadlessNotifier.RunAsync(
            store,
            settings,
            Today,
            extendedCheck: _ => throw new HttpRequestException("核心没起来"),
            stateStore: stateStore,
            now: Monday.AddMinutes(10));

        Assert.Contains(failed.Limitations, note => note.Contains("扩展检查失败", StringComparison.Ordinal));
        var afterFailure = stateStore.Load();
        Assert.True(afterFailure.Records[key].WasInRange);
        Assert.Equal(Monday, afterFailure.Records[key].LastNotifiedAt);

        // Recovery, condition still met: suppressed. A fake re-entry would send here.
        var recovered = await HeadlessNotifier.RunAsync(
            store,
            settings,
            Today,
            extendedCheck: _ => Task.FromResult(new BackgroundCheckResult
            {
                // WP-03 R4: the alert the evaluator really produces carries the rule identity;
                // a hand-built one would key differently and the dedupe check would miss it.
                Alerts = [MetBand(rule)],
                FxOutcomes = [new FxRuleOutcome { AlertKey = key, State = FxRuleState.Met }],
            }),
            stateStore: stateStore,
            now: Monday.AddMinutes(20));

        // Nothing left the machine: same verdict, no re-entry announcement.
        Assert.Equal(0, recovered.Dispatch.AlertCount);
        Assert.Equal(1, recovered.SuppressedByCooldown);
        Assert.True(stateStore.Load().Records[key].WasInRange);
    }

    [Fact]
    public async Task A10_TheNotifierReportsTheReasonAndKeepsItsCooldownWhileTheQuoteIsMissing()
    {
        // The whole chain, not just the two pure pieces: evaluator → notifier → gatekeeper.
        var rule = Band();
        var key = KeyOf(rule);
        Directory.CreateDirectory(_root);
        using var store = PortfolioStore.UnderStateRoot(_root);
        var stateStore = new AlertNotificationStateStore(Path.Combine(_root, "alert-state.json"));
        stateStore.Save(DeliveredOnMonday(key));
        var settings = NotificationSettings.Default with { BackgroundExtendedChecks = true };

        var unknown = await HeadlessNotifier.RunAsync(
            store,
            settings,
            Today,
            extendedCheck: _ => Task.FromResult(new BackgroundCheckResult
            {
                Alerts = [],
                Notes = ["规则 USD/CNY：本次未取到该货币对的报价，未评估。"],
                FxOutcomes =
                [
                    new FxRuleOutcome
                    {
                        AlertKey = key,
                        State = FxRuleState.Unknown,
                        Reason = "本次未取到 USD/CNY 的报价：条件是否成立未知，不是条件不成立。",
                    },
                ],
            }),
            stateStore: stateStore,
            now: Monday.AddMinutes(10));

        // The reason is in the report the user actually receives.
        Assert.Contains(unknown.Limitations, note => note.Contains("报价", StringComparison.Ordinal));
        Assert.Contains("报价", unknown.Summarize(), StringComparison.Ordinal);

        // And the outage left the remembered verdict alone: still in range, cooldown untouched.
        var afterOutage = stateStore.Load();
        Assert.True(afterOutage.Records[key].WasInRange);
        Assert.Equal(Monday, afterOutage.Records[key].LastNotifiedAt);

        // The quote comes back, still inside the band: suppressed, not re-announced.
        var recovered = await HeadlessNotifier.RunAsync(
            store,
            settings,
            Today,
            extendedCheck: _ => Task.FromResult(new BackgroundCheckResult
            {
                // WP-03 R4: the alert the evaluator really produces carries the rule identity;
                // a hand-built one would key differently and the dedupe check would miss it.
                Alerts = [MetBand(rule)],
                FxOutcomes = [new FxRuleOutcome { AlertKey = key, State = FxRuleState.Met }],
            }),
            stateStore: stateStore,
            now: Monday.AddMinutes(20));

        Assert.Equal(0, recovered.Dispatch.AlertCount);
        Assert.Equal(1, recovered.SuppressedByCooldown);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void EveryEnabledRuleGetsExactlyOneOutcome()
    {
        var rules = new[]
        {
            Band(),
            Band() with { Pair = "USD/JPY", Lower = 140m, Upper = 160m },
            new FxAlertRule
            {
                Kind = FxAlertRuleKind.HeldAdvantageAbove,
                BaseCurrency = "CNY",
                TargetCurrency = "USD",
                HeldCurrency = "JPY",
                ThresholdPct = 3m,
            },
        };

        var result = FxAlertRuleEvaluator.Evaluate(
            rules,
            _ => null,
            _ => null);

        Assert.Equal(rules.Length, result.Outcomes.Count);
        Assert.All(result.Outcomes, outcome => Assert.Equal(FxRuleState.Unknown, outcome.State));
        Assert.Equal(rules.Length, result.UnknownAlertKeys.Count);
    }
}
