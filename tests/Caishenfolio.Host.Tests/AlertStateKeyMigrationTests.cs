using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 D6, third round: the cooldown history is MIGRATED, not aliased. An upgrade rewrites the old
/// key's record in <c>alert-state.json</c> onto the rule's new key — once, with a backup, and only
/// when the write actually succeeds. Nothing keeps a permanent alias afterwards, which is what makes
/// the whole "whose history is this?" family of edge cases disappear instead of being patched.
/// </summary>
public class AlertStateKeyMigrationTests : IDisposable
{
    private static readonly DateTimeOffset Delivered = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_key_migration", Guid.NewGuid().ToString("N"));

    private string StateFile => Path.Combine(_root, "alert-state.json");

    private AlertNotificationStateStore StateStore() => new(StateFile);

    private NotificationSettingsStore SettingsStore()
    {
        Directory.CreateDirectory(_root);
        return new NotificationSettingsStore(PortfolioStore.UnderStateRoot(_root));
    }

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

    private static FxAlertRule LegacyBand(decimal lower) => new()
    {
        Kind = FxAlertRuleKind.RateBand,
        Pair = "USD/CNY",
        Lower = lower,
    };

    private static IReadOnlyList<PortfolioAlert> Firing(IReadOnlyList<FxAlertRule> rules) =>
        FxAlertRuleEvaluator.Evaluate(rules, _ => (7.2m, "fixture"), _ => null).Alerts;

    private static string KeyOf(IReadOnlyList<PortfolioAlert> alerts, int index) =>
        AlertGatekeeper.KeyOf(alerts[index]);

    [Fact]
    public void UpgradingMovesTheOldRecordOntoTheNewKeyInsteadOfAliasingIt()
    {
        // A rule whose level gained digits: the old build wrote "@r0#band:7.1~-", the new key is
        // "@r0#band:7.1000001~-". The record must MOVE — the state file afterwards holds the new key
        // and no longer holds the old one.
        var state = StateStore();
        const string oldKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Record(state, oldKey);

        var settled = SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] })
            .FxAlertRules;
        var rule = Assert.Single(settled);

        var alerts = Firing([rule]);
        var newKey = KeyOf(alerts, 0);
        Assert.NotEqual(oldKey, newKey);

        var records = state.Load().Records;
        Assert.True(records.ContainsKey(newKey), $"新键 {newKey} 应该在状态文件里");
        Assert.False(records.ContainsKey(oldKey), "旧键的冷却记录应该已经搬走，不再留在文件里");
        Assert.Equal(Delivered, records[newKey].LastNotifiedAt);
        Assert.True(records[newKey].WasInRange);

        // D6: what the user already saw must not speak again inside the cooldown.
        var decision = AlertGatekeeper.Filter(alerts, state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void TwoLegacyBandsThatDifferOnlyByTheirLevelEachGetTheirOwnRecord()
    {
        // The named regression: two rules with the same derived identity and different levels. Each
        // owns its own record, and each record moves onto that rule's own new key.
        var state = StateStore();
        const string lowKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-";
        const string highKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Record(state, lowKey, highKey);

        var settled = SettingsStore().Save(new NotificationSettings
        {
            FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)],
        }).FxAlertRules;
        Assert.Equal(2, settled.Count);

        // Rates 7.2 satisfies both bands, so both rules fire and we can read their keys.
        var alerts = Firing(settled);
        Assert.Equal(2, alerts.Count);
        var keys = alerts.Select(AlertGatekeeper.KeyOf).ToList();

        var records = state.Load().Records;
        Assert.All(keys, key => Assert.True(records.ContainsKey(key), $"应该搬到 {key}"));
        Assert.False(records.ContainsKey(lowKey));
        Assert.False(records.ContainsKey(highKey));

        var decision = AlertGatekeeper.Filter(alerts, state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void RunningTheUpgradeAgainDoesNotRewriteAgainOrLoseRecords()
    {
        var state = StateStore();
        const string oldKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Record(state, oldKey);

        var store = SettingsStore();
        var first = store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] }).FxAlertRules;
        var afterFirst = File.ReadAllText(StateFile);
        Assert.False(state.Load().Records.ContainsKey(oldKey), "第一次就应该把记录搬走了");

        // A second load+save (what the background check and the settings page do) must be a no-op:
        // the same records, byte for byte, and no records lost.
        var second = store.Load().FxAlertRules;
        var third = store.Save(new NotificationSettings { FxAlertRules = second }).FxAlertRules;
        Assert.Equal(afterFirst, File.ReadAllText(StateFile));
        Assert.Equal(second[0].StableId, third[0].StableId);
        Assert.Equal(first[0].StableId, second[0].StableId);
    }

    [Fact]
    public void AFailedRewriteKeepsTheOriginalFileAndPostponesTheUpgrade()
    {
        // The state file cannot be replaced. The cooldown history must survive untouched, and the
        // upgrade must be POSTPONED — the identity may not move forward while the record stays behind,
        // because that combination is exactly what re-sends inside the cooldown (D6).
        var state = StateStore();
        const string oldKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Record(state, oldKey);
        var before = File.ReadAllText(StateFile);

        File.SetAttributes(StateFile, FileAttributes.ReadOnly);
        try
        {
            var settings = SettingsStore();
            var loaded = settings.Load();
            var rule = Assert.Single(loaded.FxAlertRules);

            // The record survived, a backup was taken, and the rule kept the identity whose key the
            // record is actually filed under — so nothing re-sends.
            Assert.Equal(before, File.ReadAllText(StateFile));
            Assert.True(File.Exists(StateFile + ".pre-key-migration.bak"), "写入前必须先备份");
            Assert.True(string.IsNullOrEmpty(rule.Id), "迁移失败时不得把身份往前推");

            var decision = AlertGatekeeper.Filter(Firing([rule]), state.Load(), Delivered.AddMinutes(30), Cooldown);
            Assert.Empty(decision.ToSend);
        }
        finally
        {
            File.SetAttributes(StateFile, FileAttributes.Normal);
        }

        // With the obstacle gone, the next attempt migrates for real.
        var retried = SettingsStore().Load().FxAlertRules;
        var retry = Assert.Single(retried);
        Assert.False(string.IsNullOrEmpty(retry.Id));
        var records = state.Load().Records;
        Assert.False(records.ContainsKey(oldKey));
        Assert.Contains(KeyOf(Firing([retry]), 0), records.Keys);
    }

    [Fact]
    public void ThePersistedSettingsCarryNoPermanentAliasAfterwards()
    {
        // The whole point of the mechanism change: no alias survives in the settings blob. Not a
        // field that stays empty forever, and not the old key material either.
        var state = StateStore();
        const string oldKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Record(state, oldKey);

        var store = SettingsStore();
        _ = store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] });
        var raw = PortfolioStore.UnderStateRoot(_root).LoadRawSetting("notifications.json") ?? string.Empty;

        Assert.DoesNotContain("legacyRuleKeys", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band:7.1~-", raw, StringComparison.Ordinal);
        Assert.Contains("band:7.1000001~-", raw, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(StateFile))
            {
                File.SetAttributes(StateFile, FileAttributes.Normal);
            }

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