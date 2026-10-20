using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 A7–A9 (R4/R5/D5/D6): two rules that differ only by their threshold used to share one
/// dedupe key, so the first delivery silenced the second. The key now carries the rule's
/// identity, its revision and its level — and an old state file is adopted conservatively so
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

        // The real flow marks what actually left the machine, which is what starts the cooldown.
        var delivered = AlertGatekeeper.MarkDelivered(first.NextState, first.ToSend, Monday);
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

        var first = AlertGatekeeper.Filter(
            [smallAlert, largeAlert], AlertNotificationState.Empty, Monday, Cooldown);
        var both = AlertGatekeeper.MarkDelivered(first.NextState, first.ToSend, Monday);

        // Only the small rule keeps firing this round; the large one has left its range.
        var onlySmall = AlertGatekeeper.Filter([smallAlert], both, Monday.AddHours(1), Cooldown);
        var smallAgain = AlertGatekeeper.Filter(
            [smallAlert], onlySmall.NextState, Monday.AddHours(2), Cooldown);

        Assert.Empty(smallAgain.ToSend);
        Assert.False(onlySmall.NextState.Records[AlertGatekeeper.KeyOf(largeAlert)].WasInRange);
        Assert.True(onlySmall.NextState.Records[AlertGatekeeper.KeyOf(smallAlert)].WasInRange);
    }

    [Fact]
    public void A8_AnUnknownRuleDoesNotAdvanceItsNeighboursCooldown()
    {
        var small = NetArrival(10m);
        var large = NetArrival(1_000m);
        var smallAlert = Firing(small);
        var largeAlert = Firing(large);
        var first = AlertGatekeeper.Filter(
            [smallAlert, largeAlert], AlertNotificationState.Empty, Monday, Cooldown);
        var both = AlertGatekeeper.MarkDelivered(first.NextState, first.ToSend, Monday);

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

        // What the previous version wrote: Kind|Symbol|Title, no rule identity. Delivered an hour
        // ago, i.e. inside the cooldown — that is the upgrade-day shape: without adoption every
        // one of these would fire again the moment the new build starts.
        var legacyKey = $"{alert.Kind}|{alert.Symbol}|{alert.Title}";
        Assert.NotEqual(legacyKey, AlertGatekeeper.KeyOf(alert));
        var legacyState = new AlertNotificationState
        {
            Records = new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal)
            {
                [legacyKey] = new()
                {
                    LastNotifiedAt = Monday.AddHours(-1),
                    LastSeenAt = Monday.AddHours(-1),
                    WasInRange = true,
                },
            },
        };

        var decision = AlertGatekeeper.Filter([alert], legacyState, Monday, Cooldown);

        // R5/D6: the old key counts as already delivered — quiet on the upgrade day.
        Assert.Empty(decision.ToSend);
        // …and the new key now owns the remembered verdict, so later rounds behave normally.
        Assert.True(decision.NextState.Records[AlertGatekeeper.KeyOf(alert)].WasInRange);
        Assert.Equal(Monday.AddHours(-1), decision.NextState.Records[AlertGatekeeper.KeyOf(alert)].LastNotifiedAt);
    }

    [Fact]
    public void A9_AnUpgradeWhoseFirstRoundCannotBeEvaluatedStillDoesNotFabricateAReEntry()
    {
        // Codex review #3 (D6/A8/A9): the upgrade-day chain is TWO rounds. The old file only knows
        // the legacy key; the first new-build round cannot be evaluated (no quote), the second can.
        // The old record must not be read as "left the range" in round one — that fabricated exit
        // would make round two look like a re-entry and re-send inside the cooldown.
        var rule = NetArrival(10m);
        var alert = Firing(rule);
        var newKey = AlertGatekeeper.KeyOf(alert);
        var legacyKey = $"{alert.Kind}|{alert.Symbol}|{alert.Title}";
        Assert.NotEqual(legacyKey, newKey);

        var legacyState = new AlertNotificationState
        {
            Records = new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal)
            {
                [legacyKey] = new()
                {
                    LastNotifiedAt = Monday,
                    LastSeenAt = Monday,
                    WasInRange = true,
                },
            },
        };

        // Round one, 10 minutes later: the quote is missing, so the rule is reported as NOT evaluated
        // under its NEW identity. Nothing fires.
        var roundOne = AlertGatekeeper.Filter(
            [], legacyState, Monday.AddMinutes(10), Cooldown, new HashSet<string> { newKey });

        // The legacy record keeps its verdict: no fabricated exit.
        Assert.True(roundOne.NextState.Records[legacyKey].WasInRange);

        // Round two, 20 minutes later: the quote is back and the condition has been true all along.
        var roundTwo = AlertGatekeeper.Filter([alert], roundOne.NextState, Monday.AddMinutes(20), Cooldown);

        Assert.Empty(roundTwo.ToSend);
        Assert.True(roundTwo.NextState.Records[newKey].WasInRange);
        Assert.Equal(Monday, roundTwo.NextState.Records[newKey].LastNotifiedAt);
    }

    [Fact]
    public void A7_ALevelKeepsTheFullPrecisionItsThresholdWasParsedWith()
    {
        // The audit's follow-up: Level rounded to six decimals while the threshold was parsed with
        // no such limit, so 7.1000001 and 7.1000002 collapsed into one "band:7.1~…" key — two
        // watches the user created separately would have shared one cooldown.
        var fine = new FxAlertRule { Kind = FxAlertRuleKind.RateBand, Pair = "USD/CNY", Lower = 7.1000001m };
        var finer = new FxAlertRule { Kind = FxAlertRuleKind.RateBand, Pair = "USD/CNY", Lower = 7.1000002m };

        Assert.NotEqual(fine.Level, finer.Level);
        Assert.Contains("7.1000001", fine.Level, StringComparison.Ordinal);
        Assert.Contains("7.1000002", finer.Level, StringComparison.Ordinal);
        Assert.NotEqual(AlertGatekeeper.KeyOf(Probe(fine)), AlertGatekeeper.KeyOf(Probe(finer)));

        // A key is not a display string: never scientific notation, at either end of the scale.
        // ("G29" would have printed 0.00001 as 1E-05 — a key change for an ordinary band rule.)
        var tiny = new FxAlertRule { Kind = FxAlertRuleKind.RateBand, Pair = "USD/CNY", Lower = 0.00001m };
        Assert.Equal("band:0.00001~-", tiny.Level);
        Assert.Equal(
            "band:-0.00001~-",
            new FxAlertRule { Kind = FxAlertRuleKind.RateBand, Pair = "USD/CNY", Lower = -0.00001m }.Level);
        Assert.Equal(
            "band:0.0000000000000000000000000001~-",
            new FxAlertRule
            {
                Kind = FxAlertRuleKind.RateBand,
                Pair = "USD/CNY",
                Lower = 0.0000000000000000000000000001m,
            }.Level);
        var huge = new FxAlertRule
        {
            Kind = FxAlertRuleKind.NetArrivalAbove,
            ThresholdAmount = 100_000_000_000_000_000_000_000_000m,
        };
        Assert.DoesNotContain("E", huge.Level, StringComparison.OrdinalIgnoreCase);

        // And no key churn for the values users already have: six decimals or fewer keep exactly the
        // string the old 0.###### format produced, so an existing record keeps matching its rule.
        Assert.Equal(
            "amt:1000",
            new FxAlertRule { Kind = FxAlertRuleKind.NetArrivalAbove, ThresholdAmount = 1_000m }.Level);
        Assert.Equal(
            "amt:0",
            new FxAlertRule { Kind = FxAlertRuleKind.NetArrivalAbove, ThresholdAmount = 0.00m }.Level);
        Assert.Equal(
            "pct:3",
            new FxAlertRule { Kind = FxAlertRuleKind.HeldAdvantageAbove, ThresholdPct = 3.0m }.Level);
        Assert.Equal(
            "band:7.1~8",
            new FxAlertRule { Kind = FxAlertRuleKind.RateBand, Pair = "USD/CNY", Lower = 7.10m, Upper = 8m }.Level);
        Assert.Equal(
            "band:7.1~-",
            new FxAlertRule { Kind = FxAlertRuleKind.RateBand, Pair = "USD/CNY", Lower = 7.100000m }.Level);
    }

    /// <summary>The alert a rule would produce when it fires — identity only.</summary>
    private static PortfolioAlert Probe(FxAlertRule rule) => new()
    {
        Kind = AlertKind.FxBand,
        Severity = AlertSeverity.Info,
        Symbol = "USD/CNY",
        Title = "汇率进入你设定的区间",
        Message = "（身份探针，不发送）",
        RuleId = rule.StableId,
        RuleRevision = rule.Revision,
        RuleLevel = rule.Level,
    };

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
                    LastNotifiedAt = Monday.AddHours(-1),
                    LastSeenAt = Monday.AddHours(-1),
                    WasInRange = true,
                },
            },
        };

        var decision = AlertGatekeeper.Filter([alert], legacyState, Monday, Cooldown);

        Assert.Single(decision.ToSend);
    }
}
