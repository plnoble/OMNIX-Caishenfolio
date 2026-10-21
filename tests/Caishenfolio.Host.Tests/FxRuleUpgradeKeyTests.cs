using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 D6, the third pass (统筹 second send-back): an upgrade may not re-send an alert the user
/// already received inside its cooldown — <b>any</b> of them. The previous round tied a migrated
/// rule's old key to its inherited identity, which holds only while the level string is unchanged
/// and nothing else in the batch claimed that identity. Both assumptions are wrong, and the state
/// file is the only place that can tell us what was actually delivered, so these tests feed a real
/// <c>alert-state.json</c> written by the old build and assert NOTHING is sent.
/// </summary>
public class FxRuleUpgradeKeyTests : IDisposable
{
    private static readonly DateTimeOffset Delivered = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_upgrade_keys", Guid.NewGuid().ToString("N"));

    private AlertNotificationStateStore StateStore() =>
        new(Path.Combine(_root, "alert-state.json"));

    private NotificationSettingsStore SettingsStore()
    {
        Directory.CreateDirectory(_root);
        return new NotificationSettingsStore(PortfolioStore.UnderStateRoot(_root));
    }

    /// <summary>What the old build wrote for one rule: delivered 30 minutes ago, still in range.</summary>
    private static void Record(AlertNotificationStateStore store, params string[] keys)
    {
        var records = new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            records[key] = new AlertNotificationRecord
            {
                LastNotifiedAt = Delivered,
                LastSeenAt = Delivered,
                WasInRange = true,
            };
        }

        store.Save(new AlertNotificationState { Records = records });
    }

    /// <summary>A rate-band rule as the pre-identity settings file held it: no id, revision 0.</summary>
    private static FxAlertRule LegacyBand(decimal lower) => new()
    {
        Kind = FxAlertRuleKind.RateBand,
        Pair = "USD/CNY",
        Lower = lower,
    };

    /// <summary>Evaluates the (already migrated) rules against a rate that satisfies every band.</summary>
    private static IReadOnlyList<PortfolioAlert> Firing(IReadOnlyList<FxAlertRule> rules) =>
        FxAlertRuleEvaluator.Evaluate(rules, _ => (7.2m, "fixture"), _ => null).Alerts;

    [Fact]
    public void A9_TwoLegacyBandsThatDifferOnlyByTheirLevelKeepBothOfTheirKeys()
    {
        // The audited regression: two legacy USD/CNY band rules (7.0 and 7.1) share one DerivedId,
        // but their complete keys differ by level and BOTH were being delivered independently. A
        // migration that reconciles by id alone hands the second one a fresh identity and loses the
        // key its cooldown lives under — so the next run re-sends an alert it already sent.
        var state = StateStore();
        Record(
            state,
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-",
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        // The upgrade: both rules still carry no id, exactly as the old settings file wrote them.
        var settled = SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)] })
            .FxAlertRules;

        var alerts = Firing(settled);
        Assert.Equal(2, alerts.Count);

        // Both rules are separate identities (an id is unique — sharing one would break every other
        // consumer), and each maps its COMPLETE old key: the one that owns the derived identity keeps
        // it as its key, the other carries it as a legacy key it may have been recorded under.
        Assert.NotEqual(settled[0].StableId, settled[1].StableId);
        var allKeys = alerts.SelectMany(AlertGatekeeper.KeysOf).ToList();
        Assert.Contains(
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-",
            allKeys);
        Assert.Contains(
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-",
            allKeys);

        // D6: nothing may be sent again inside the cooldown.
        var decision = AlertGatekeeper.Filter(alerts, state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void A9_ALegacyRuleWhoseLevelGainedDigitsStillFindsItsOldKey()
    {
        // The precision fix changed the level STRING for a threshold with more than six decimals:
        // 7.1000001 is recorded as band:7.1~- by the old build and band:7.1000001~- now. The format
        // is right; what is missing is a migration of the old key. One rule, no identity conflict.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var settled = SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] })
            .FxAlertRules;
        var rule = Assert.Single(settled);

        // The fix must not be undone: the level keeps every digit.
        Assert.Equal("band:7.1000001~-", rule.Level);

        var alerts = Firing(settled);
        Assert.Single(alerts);
        var decision = AlertGatekeeper.Filter(alerts, state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void SavingTheSameIdentityLessBatchTwiceKeepsTheIdentities()
    {
        // Retrying a save of the SAME id-less batch may not rebuild identities: the second rule used
        // to get a fresh random id on every attempt, so a retry would have looked like a new watch.
        var store = SettingsStore();
        NotificationSettings Batch() => new()
        {
            FxAlertRules =
            [
                new FxAlertRule
                {
                    Kind = FxAlertRuleKind.NetArrivalAbove,
                    BaseCurrency = "CNY",
                    TargetCurrency = "USD",
                    HeldCurrency = "JPY",
                    ThresholdAmount = 1_000m,
                    SpreadPct = 0m,
                    FixedFee = 0m,
                },
                new FxAlertRule
                {
                    Kind = FxAlertRuleKind.NetArrivalAbove,
                    BaseCurrency = "CNY",
                    TargetCurrency = "USD",
                    HeldCurrency = "JPY",
                    ThresholdAmount = 1_000m,
                    SpreadPct = 0m,
                    FixedFee = 2m,
                },
            ],
        };

        var first = store.Save(Batch()).FxAlertRules;
        var second = store.Save(Batch()).FxAlertRules;   // the original input, retried

        Assert.Equal(2, first.Count);
        Assert.NotEqual(first[0].Id, first[1].Id);
        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[1].Id, second[1].Id);

        // And a third attempt still lands on the same two identities.
        var third = store.Save(Batch()).FxAlertRules;
        Assert.Equal(first[0].Id, third[0].Id);
        Assert.Equal(first[1].Id, third[1].Id);
    }

    [Fact]
    public void A9_TheSecondLoadAfterMigrationKeepsTheHistoryAndTheRevisions()
    {
        // Review #C4-1: two rules could no longer both inherit the same id (an id has to be unique —
        // the settings page and every id-keyed lookup assume it), so the second one gets its own id
        // and maps the old key as a legacy key. A LOAD right after the migration must not then treat
        // the two as one rule and bump a revision — that would change a key and re-send.
        var state = StateStore();
        Record(
            state,
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-",
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var store = SettingsStore();
        store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)] });

        // The read-back path, which is what the background check and the settings page use.
        var reloaded = store.Load().FxAlertRules;
        Assert.Equal(2, reloaded.Count);
        Assert.All(reloaded, rule => Assert.Equal(0, rule.Revision));
        Assert.Equal(2, reloaded.Select(rule => rule.StableId).Distinct().Count());

        var decision = AlertGatekeeper.Filter(Firing(reloaded), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void A9_AHalfMigratedBatchDoesNotHandOutAnIdentityTwice()
    {
        // Review #C4-2: a normal partially backfilled input — one rule already carries an identity, its
        // neighbour is still the old id-less shape. The explicit identity must be reserved, or both
        // end up on one id and one of them loses its history.
        var state = StateStore();
        Record(
            state,
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-",
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var store = SettingsStore();
        var first = store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)] })
            .FxAlertRules;

        // Re-submit with the first rule's identity stated and the second one still id-less.
        var mixed = store.Save(new NotificationSettings
        {
            FxAlertRules = [first[0], LegacyBand(7.1m)],
        }).FxAlertRules;

        Assert.Equal(2, mixed.Count);
        Assert.Equal(2, mixed.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first[0].Id, mixed[0].Id);

        var decision = AlertGatekeeper.Filter(Firing(mixed), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void A9_ReorderingOrDroppingARuleDoesNotRebindIdentities()
    {
        // Review #C4-3: the settings list has no order contract, and a user may remove one rule. A
        // retry must not hand rule A the identity (and therefore the history) of rule B.
        _ = StateStore();

        var store = SettingsStore();
        var first = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 0m), NetArrival(1_000m, 2m)],
        }).FxAlertRules;

        // Reordered, still id-less: each rule keeps its own identity.
        var reordered = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 2m), NetArrival(1_000m, 0m)],
        }).FxAlertRules;
        Assert.Equal(first[1].Id, reordered[0].Id);
        Assert.Equal(first[0].Id, reordered[1].Id);

        // Drop the first rule (by keeping the second one's identity), then re-submit it id-less.
        var kept = store.Save(new NotificationSettings { FxAlertRules = [first[1]] }).FxAlertRules;
        Assert.Equal(first[1].Id, Assert.Single(kept).Id);

        var resubmitted = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 2m)],
        }).FxAlertRules;
        Assert.Equal(first[1].Id, Assert.Single(resubmitted).Id);
    }

    [Fact]
    public void A9_AnUnknownRoundAfterTheKeyMigrationStillDoesNotFabricateAReEntry()
    {
        // Review #C4-4: the old complete key has to stay live while the rule cannot be judged, or the
        // recovery looks like a re-entry and re-sends inside the cooldown.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var settled = SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] })
            .FxAlertRules;
        var rule = Assert.Single(settled);

        // Round one, ten minutes later: the quote is missing, so the rule is reported un-evaluated.
        var unknown = FxAlertRuleEvaluator.Evaluate([rule], _ => null, _ => null);
        var roundOne = AlertGatekeeper.Filter(
            unknown.Alerts, state.Load(), Delivered.AddMinutes(10), Cooldown, unknown.UnknownAlertKeys);

        // The old complete key keeps its verdict: no fabricated exit.
        Assert.True(
            roundOne.NextState.Records[
                "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-"]
                .WasInRange);

        // Round two: the quote is back, the condition has been true all along.
        var roundTwo = AlertGatekeeper.Filter(
            Firing([rule]), roundOne.NextState, Delivered.AddMinutes(20), Cooldown);
        Assert.Empty(roundTwo.ToSend);
    }

    [Fact]
    public void A9_TheMostRecentHistoryWinsWhenBareAndCompleteKeysCoexist()
    {
        // Review #C4-5: a mixed state file (bare pre-WP-03 key + a newer complete key) must not let the
        // older bare record override the newer complete one.
        var state = StateStore();
        var path = Path.Combine(_root, "alert-state.json");
        state.Save(new AlertNotificationState
        {
            Records = new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal)
            {
                ["FxBand|USD/CNY|汇率进入你设定的区间"] = new()
                {
                    LastNotifiedAt = Delivered.AddHours(-13),
                    LastSeenAt = Delivered.AddHours(-13),
                    WasInRange = true,
                },
                ["FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-"] = new()
                {
                    LastNotifiedAt = Delivered,
                    LastSeenAt = Delivered,
                    WasInRange = true,
                },
            },
        });
        Assert.True(File.Exists(path));

        var settled = SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] })
            .FxAlertRules;

        var decision = AlertGatekeeper.Filter(Firing(settled), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void A9_AnUnusualExplicitIdentityCannotAdoptAnotherRulesHistory()
    {
        // Review #C4-6: alias lookup composes keys from structured fields, so an identity that itself
        // contains the key's separators cannot truncate the lookup and steal a neighbour's record.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:rule-a@r0#band:7.1~-");

        var stranger = new FxAlertRule
        {
            Kind = FxAlertRuleKind.RateBand,
            Pair = "USD/CNY",
            Lower = 7.1000001m,
            Id = "rule-a@r0#branch",
            Revision = 7,
        };

        var alerts = Firing([stranger]);
        var decision = AlertGatekeeper.Filter(alerts, state.Load(), Delivered.AddMinutes(30), Cooldown);

        // It never delivered anything, so it must speak — not inherit rule-a's cooldown.
        Assert.Single(decision.ToSend);
    }

    /// <summary>A net-arrival rule as the old settings file held it: no id, revision 0.</summary>
    private static FxAlertRule NetArrival(decimal threshold, decimal fixedFee) => new()
    {
        Kind = FxAlertRuleKind.NetArrivalAbove,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        HeldCurrency = "JPY",
        ThresholdAmount = threshold,
        SpreadPct = 0m,
        FixedFee = fixedFee,
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}