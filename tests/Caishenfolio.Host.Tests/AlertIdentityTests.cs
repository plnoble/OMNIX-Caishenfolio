using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 A7–A9 (R4/R5/D5/D6): two rules that differ only by their threshold used to share one
/// dedupe key, so the first delivery silenced the second. The key now carries the rule's
/// identity, its revision and its level — and an old state file is migrated conservatively so
/// upgrading does not re-send everything the user already saw.
/// </summary>
public class AlertIdentityTests
{
    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);

    private static FxAlertRule NetArrival(decimal threshold, string id = "jpy-usd") => new()
    {
        Kind = FxAlertRuleKind.NetArrivalAbove,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        HeldCurrency = "JPY",
        ThresholdAmount = threshold,
        SpreadPct = 0m,
        FixedFee = 0m,
        Id = id,
    };

    /// <summary>Evaluates one rule with facts that satisfy it, returning its (single) alert.</summary>
    private static PortfolioAlert Firing(FxAlertRule rule)
    {
        var facts = new FxPathFacts { NetArrival = (rule.ThresholdAmount ?? 0m) + 1m };
        var evaluated = FxAlertRuleEvaluator.Evaluate([rule], _ => null, _ => facts);
        return Assert.Single(evaluated.Alerts);
    }

    [Fact]
    public void A7_TwoRulesThatDifferOnlyByTheirThresholdAreBothDelivered()
    {
        var small = NetArrival(10m);
        var large = NetArrival(1_000m);
        var alerts = new[] { Firing(small), Firing(large) };

        // The identities must differ: same kind, same pair, same wording — different level.
        Assert.NotEqual(AlertGatekeeper.KeyOf(alerts[0]), AlertGatekeeper.KeyOf(alerts[1]));

        var decision = AlertGatekeeper.Filter(alerts, AlertNotificationState.Empty, Monday, Cooldown);

        Assert.Equal(2, decision.ToSend.Count);
    }

    [Fact]
    public void A8_TheSameRuleStillAnnouncesOnceAndOnlyOnce()
    {
        var rule = NetArrival(10m);
        var alert = Firing(rule);

        var first = AlertGatekeeper.Filter([alert], AlertNotificationState.Empty, Monday, Cooldown);
        Assert.Single(first.ToSend);

        var delivered = first.NextState;
        var again = AlertGatekeeper.Filter([alert], delivered, Monday.AddMinutes(30), Cooldown);
        Assert.Empty(again.ToSend);

        // After a genuine exit and re-entry it speaks again — unchanged by the new key.
        var exited = AlertGatekeeper.Filter([], again.NextState, Monday.AddHours(1), Cooldown);
        var reentered = AlertGatekeeper.Filter([alert], exited.NextState, Monday.AddHours(2), Cooldown);
        Assert.Single(reentered.ToSend);
    }

    [Fact]
    public void A8_TwoRulesDoNotSuppressEachOthersExitReentry()
    {
        var small = NetArrival(10m);
        var large = NetArrival(1_000m);
        var smallAlert = Firing(small);
        var largeAlert = Firing(large);

        // Both delivered, then only the small one keeps firing.
        var both = AlertGatekeeper.Filter(
            [smallAlert, largeAlert], AlertNotificationState.Empty, Monday, Cooldown).NextState;
        var onlySmall = AlertGatekeeper.Filter([smallAlert], both, Monday.AddHours(1), Cooldown);
        var smallAgain = AlertGatekeeper.Filter(
            [smallAlert], onlySmall.NextState, Monday.AddHours(2), Cooldown);

        Assert.Empty(smallAgain.ToSend);
        // The large rule's record was the one marked as left; the small rule stayed in range.
        Assert.True(onlySmall.NextState.Records[AlertGatekeeper.KeyOf(largeAlert)].WasInRange == false);
        Assert.True(onlySmall.NextState.Records[AlertGatekeeper.KeyOf(smallAlert)].WasInRange);
    }

    [Fact]
    public void A8_AnUnknownRuleDoesNotAdvanceItsNeighboursCooldown()
    {
        var small = NetArrival(10m);
        var large = NetArrival(1_000m);
        var smallAlert = Firing(small);
        var largeAlert = Firing(large);
        var both = AlertGatekeeper.Filter(
            [smallAlert, largeAlert], AlertNotificationState.Empty, Monday, Cooldown).NextState;

        // The small rule cannot be judged this round; the large one is still firing.
        var decided = AlertGatekeeper.Filter(
            [largeAlert],
            both,
            Monday.AddHours(1),
            Cooldown,
            notEvaluated: new HashSet<string>([AlertGatekeeper.KeyOf(smallAlert)], StringComparer.Ordinal));

        Assert.True(decided.NextState.Records[AlertGatekeeper.KeyOf(smallAlert)].WasInRange);
        Assert.Equal(Monday, decided.NextState.Records[AlertGatekeeper.KeyOf(smallAlert)].LastNotifiedAt);
    }

    [Fact]
    public void A9_AnOldStateFileIsAdoptedInsteadOfBombingTheUserOnUpgradeDay()
    {
        var rule = NetArrival(10m);
        var alert = Firing(rule);

        // What the previous version wrote: Kind|Symbol|Title, no rule identity.
        var legacyKey = $"{alert.Kind}|{alert.Symbol}|{alert.Title}";
        Assert.NotEqual(legacyKey, AlertGatekeeper.KeyOf(alert));
        var legacyState = new AlertNotificationState
        {
            Records = new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal)
            {
                [legacyKey] = new()
                {
                    LastNotifiedAt = Monday.AddDays(-1),
                    LastSeenAt = Monday.AddDays(-1),
                    WasInRange = true,
                },
            },
        };

        var decision = AlertGatekeeper.Filter([alert], legacyState, Monday, Cooldown);

        // R5/D6: the old key counts as already delivered — quiet on the upgrade day.
        Assert.Empty(decision.ToSend);
        // …and the new key now owns the remembered verdict, so later rounds behave normally.
        Assert.True(decision.NextState.Records[AlertGatekeeper.KeyOf(alert)].WasInRange);
        Assert.Equal(Monday.AddDays(-1), decision.NextState.Records[AlertGatekeeper.KeyOf(alert)].LastNotifiedAt);
    }

    [Fact]
    public void A9_AnOldStateFileWithoutThatAlertStillSpeaks()
    {
        // Conservative must not become "never speaks again": a key the old file never saw is new.
        var alert = Firing(NetArrival(10m));
        var legacyState = new AlertNotificationState
        {
            Records = new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal)
            {
                ["FxBand|USD/CNY|汇率进入你设定的区间"] = new()
                {
                    LastNotifiedAt = Monday.AddDays(-1),
                    LastSeenAt = Monday.AddDays(-1),
                    WasInRange = true,
                },
            },
        };

        var decision = AlertGatekeeper.Filter([alert], legacyState, Monday, Cooldown);

        Assert.Single(decision.ToSend);
    }
}
