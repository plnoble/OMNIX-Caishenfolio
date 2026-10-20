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

        // Both rules reproduce the complete keys they were already deduped by.
        var keys = alerts.Select(AlertGatekeeper.KeyOf).ToList();
        Assert.Contains(
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-",
            keys);
        Assert.Contains(
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-",
            keys);

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