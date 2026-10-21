using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 D5, second pass (audit): identity must survive the round trip, and the persistence
/// boundary must be the place that guarantees it — the audit's finding was precisely that the
/// field existed but nothing ever wrote it.
/// </summary>
public class FxRuleIdentityPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_rule_identity_store", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return PortfolioStore.UnderStateRoot(_root);
    }

    private static FxAlertRule NetArrival(decimal threshold, decimal fixedFee, string id = "") => new()
    {
        Kind = FxAlertRuleKind.NetArrivalAbove,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        HeldCurrency = "JPY",
        ThresholdAmount = threshold,
        SpreadPct = 0m,
        FixedFee = fixedFee,
        Id = id,
    };

    [Fact]
    public void SavingAssignsAnIdentityToARuleWrittenByAnOlderVersion()
    {
        var store = new NotificationSettingsStore(NewStore());

        // A settings file from before this field existed: the rule carries no id at all.
        var saved = store.Save(new NotificationSettings { FxAlertRules = [NetArrival(1_000m, 0m)] });

        var rule = Assert.Single(saved.FxAlertRules);
        Assert.False(string.IsNullOrWhiteSpace(rule.Id));
        Assert.Equal(0, rule.Revision);

        // …and it is on disk, not only in the returned copy.
        var reloaded = Assert.Single(store.Load().FxAlertRules);
        Assert.Equal(rule.Id, reloaded.Id);
    }

    [Fact]
    public void SavingKeepsTwoRulesWithTheSameThresholdApartForGood()
    {
        var store = new NotificationSettingsStore(NewStore());

        var saved = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 0m), NetArrival(1_000m, 2m)],
        });

        var rules = saved.FxAlertRules;
        Assert.Equal(2, rules.Count);
        // Same kind, same currencies, same threshold — only the cost model differs.
        Assert.Equal(rules[0].Level, rules[1].Level);
        Assert.NotEqual(rules[0].Id, rules[1].Id);
        Assert.NotEqual(rules[0].StableId, rules[1].StableId);

        // The identities that reached the file are the ones the next run dedupes on.
        var reloaded = store.Load().FxAlertRules;
        Assert.Equal(2, reloaded.Count);
        Assert.NotEqual(reloaded[0].StableId, reloaded[1].StableId);
        Assert.Equal(rules[0].Id, reloaded[0].Id);
        Assert.Equal(rules[1].Id, reloaded[1].Id);
    }

    [Fact]
    public void SavingAdvancesTheRevisionWhenTheWatchChangedBehindTheEditorsBack()
    {
        var store = new NotificationSettingsStore(NewStore());
        var first = Assert.Single(store
            .Save(new NotificationSettings { FxAlertRules = [NetArrival(1_000m, 0m)] })
            .FxAlertRules);

        // Same identity, different threshold: the watch changed, so the revision must move even
        // though the caller handed back the old revision (e.g. a hand-edited file).
        var changed = first with { ThresholdAmount = 1_200m };
        var second = Assert.Single(store
            .Save(new NotificationSettings { FxAlertRules = [changed] })
            .FxAlertRules);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, second.Revision);

        // An editor that already accounted for the change is not bumped twice.
        var alreadyBumped = second with { ThresholdAmount = 1_400m, Revision = 2 };
        var third = Assert.Single(store
            .Save(new NotificationSettings { FxAlertRules = [alreadyBumped] })
            .FxAlertRules);
        Assert.Equal(2, third.Revision);

        // Bookkeeping that is not the watch — enabling/disabling — does not move it either.
        var toggled = third with { Enabled = false };
        var fourth = Assert.Single(store
            .Save(new NotificationSettings { FxAlertRules = [toggled] })
            .FxAlertRules);
        Assert.Equal(2, fourth.Revision);
        Assert.False(fourth.Enabled);
    }

    [Fact]
    public void ReconcileIsIdempotentSoRepeatedSavesDoNotDrift()
    {
        // Without this, every save would look like "the parameters changed" and the revision would
        // climb on its own — which would silently change the dedupe key of an untouched rule.
        var rule = NetArrival(1_000m, 2m);
        var first = FxAlertRuleIdentity.Reconcile([rule], []);
        var second = FxAlertRuleIdentity.Reconcile(first, first);
        var third = FxAlertRuleIdentity.Reconcile(second, second);

        Assert.Equal(first[0].Id, third[0].Id);
        Assert.Equal(0, third[0].Revision);
    }

    [Fact]
    public void LoadingMigratesTheIdentityOnceAndKeepsTheKeyTheRuleAlreadyHad()
    {
        // Codex review #1/#2: identity has to exist BEFORE the rule is consumed (the background
        // check reads through Load, never through Save), and the migration must not change a key the
        // state file already holds — otherwise upgrading re-sends what the user already saw.
        var store = new NotificationSettingsStore(NewStore());
        WriteRawSettings(store, LegacyTwoRuleBlob());

        var first = store.Load().FxAlertRules;
        Assert.Equal(2, first.Count);
        foreach (var rule in first)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
        }

        // The rule that owned the shared key keeps it — its notification history stays attached, so
        // upgrading cannot re-send what the user already saw.
        Assert.Equal("NetArrivalAbove:?:CNY/USD/JPY", first[0].StableId);
        // The duplicate it used to be suppressed by now owns an identity of its own: it was being
        // denied its alert, and one extra delivery is the honest outcome of separating them.
        Assert.NotEqual("NetArrivalAbove:?:CNY/USD/JPY", first[1].StableId);
        Assert.NotEqual(first[0].StableId, first[1].StableId);
        Assert.StartsWith("rule-", first[1].Id, StringComparison.Ordinal);

        // Migrating is a one-off write: a second read sees the same ids and rewrites nothing.
        var second = store.Load().FxAlertRules;
        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[1].Id, second[1].Id);
        Assert.Equal(first[0].Revision, second[0].Revision);
    }

    [Fact]
    public void AStaleCopyCannotRollTheStoredRevisionBack()
    {
        // Codex review #4: the revision is part of the key. A caller that keeps handing back the
        // copy it built before the first save must not undo the bump — that would look like a new
        // watch and re-send.
        var store = new NotificationSettingsStore(NewStore());
        var first = Assert.Single(store
            .Save(new NotificationSettings { FxAlertRules = [NetArrival(1_000m, 0m)] })
            .FxAlertRules);

        var stale = first with { ThresholdAmount = 1_200m };   // still revision 0
        var bumped = Assert.Single(store
            .Save(new NotificationSettings { FxAlertRules = [stale] })
            .FxAlertRules);
        Assert.Equal(1, bumped.Revision);

        // The same stale input again: still revision 1 on disk, never back to 0.
        var again = Assert.Single(store
            .Save(new NotificationSettings { FxAlertRules = [stale] })
            .FxAlertRules);
        Assert.Equal(1, again.Revision);
        Assert.Equal(1, Assert.Single(store.Load().FxAlertRules).Revision);
    }

    /// <summary>Two rules as the pre-WP-03 settings file wrote them: no id, no revision.</summary>
    private static string LegacyTwoRuleBlob() =>
        """
        {"Enabled":true,"IncludeRoutineAlerts":true,"BackgroundExtendedChecks":true,
         "AlertCooldownHours":12,
         "FxAlertRules":[
           {"Kind":2,"Pair":"","BaseCurrency":"CNY","TargetCurrency":"USD","HeldCurrency":"JPY",
            "ThresholdAmount":1000,"SpreadPct":0,"FixedFee":0,"Enabled":true},
           {"Kind":2,"Pair":"","BaseCurrency":"CNY","TargetCurrency":"USD","HeldCurrency":"JPY",
            "ThresholdAmount":1000,"SpreadPct":0,"FixedFee":2,"Enabled":true}],
         "Webhooks":[]}
        """;

    private static void WriteRawSettings(NotificationSettingsStore store, string json)
    {
        // Straight into the same settings row the store reads, i.e. the real old-file shape.
        var portfolioStore = (PortfolioStore)typeof(NotificationSettingsStore)
            .GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(store)!;
        portfolioStore.SaveRawSetting(NotificationSettingsStore.SettingKey, json);
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