using Caishenfolio.Host.Notifications;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-02 A10/A11: the three-state FX reminder. A quote that cannot be fetched is UNKNOWN, not
/// "the condition became false" — the difference decides whether a recovered quote fabricates a
/// brand-new "it came back" notification.
/// </summary>
public class FxAlertTriStateTests
{
    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);

    private static FxAlertRule Band() => new()
    {
        Kind = FxAlertRuleKind.RateBand,
        Pair = "USD/CNY",
        Lower = 7.0m,
        Upper = 7.3m,
    };

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
