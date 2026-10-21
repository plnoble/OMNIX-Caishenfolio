using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 D6, third round: the upgrade RENAMES the cooldown records in <c>alert-state.json</c> instead
/// of keeping a permanent alias on each rule. Everything the earlier rounds argued about — whose
/// history is this, when does an alias expire, which record is newest — is decided once, here, by
/// moving the record onto the key the rule actually uses. These tests drive the real settings store,
/// the real state file and the real gatekeeper.
/// </summary>
public class FxRuleUpgradeKeyTests : IDisposable
{
    private static readonly DateTimeOffset Delivered = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_upgrade_key", Guid.NewGuid().ToString("N"));

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

    /// <summary>A rule the settings page just created: it owns an identity from birth.</summary>
    private static FxAlertRule Created(FxAlertRule shape) => FxAlertRuleIdentity.AssignNewIdentity(shape);

    /// <summary>The key a rule uses right now, whether or not it currently fires.</summary>
    private static string KeyNow(FxAlertRule rule) => AlertGatekeeper.KeyOf(FxAlertRuleEvaluator.Probe(rule));

    private static IReadOnlyList<PortfolioAlert> Firing(IReadOnlyList<FxAlertRule> rules) =>
        FxAlertRuleEvaluator.Evaluate(rules, _ => (7.2m, "fixture"), _ => null).Alerts;

    [Fact]
    public void A9_TwoLegacyBandsThatDifferOnlyByTheirLevelEachKeepTheirOwnHistory()
    {
        // The named regression: two legacy rules with the same derived identity and different levels.
        // Each record must end up on its OWN rule's key — not merely "found somewhere".
        var state = StateStore();
        const string lowKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-";
        const string highKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Record(state, lowKey, highKey);

        var settled = SettingsStore().Save(new NotificationSettings
        {
            FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)],
        }).FxAlertRules;

        Assert.Equal(2, settled.Count);
        Assert.Equal(2, settled.Select(rule => rule.StableId).Distinct(StringComparer.Ordinal).Count());

        var keys = settled.Select(KeyNow).ToList();
        var records = state.Load().Records;
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.True(records.ContainsKey(key), $"记录应该搬到 {key}"));

        // The band-7 rule's key is unchanged (no precision was lost), so only the other one had to
        // move: nothing may be left under a key that no rule uses any more.
        Assert.Contains(lowKey, keys);
        Assert.False(records.ContainsKey(highKey) && !keys.Contains(highKey));

        var decision = AlertGatekeeper.Filter(Firing(settled), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void A9_ALegacyRuleWhoseLevelGainedDigitsDoesNotSpeakAgain()
    {
        var state = StateStore();
        const string oldKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Record(state, oldKey);

        var rule = Assert.Single(SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] })
            .FxAlertRules);

        // The level keeps its full precision, and the record moved onto the key that precision produces.
        Assert.Equal("band:7.1000001~-", rule.Level);
        var records = state.Load().Records;
        Assert.True(records.ContainsKey(KeyNow(rule)));
        Assert.False(records.ContainsKey(oldKey));

        var decision = AlertGatekeeper.Filter(Firing([rule]), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void SavingTheSameIdentityLessBatchTwiceKeepsTheIdentities()
    {
        // Idempotency (the coordinator's third named case): retrying the same id-less input must not
        // rebuild anyone's identity.
        _ = StateStore();
        var store = SettingsStore();

        var first = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 0m), NetArrival(1_000m, 2m)],
        }).FxAlertRules;
        var second = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 0m), NetArrival(1_000m, 2m)],
        }).FxAlertRules;
        var third = store.Load().FxAlertRules;

        Assert.Equal(2, first.Count);
        Assert.NotEqual(first[0].Id, first[1].Id);
        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[1].Id, second[1].Id);
        Assert.Equal(first[0].Id, third[0].Id);
        Assert.Equal(first[1].Id, third[1].Id);
    }

    [Fact]
    public void A9_TheSecondLoadAfterMigrationKeepsTheHistoryAndTheRevisions()
    {
        var state = StateStore();
        Record(
            state,
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-",
            "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var store = SettingsStore();
        _ = store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)] });

        var reloaded = store.Load().FxAlertRules;
        Assert.Equal(2, reloaded.Count);
        Assert.All(reloaded, rule => Assert.Equal(0, rule.Revision));
        Assert.Equal(2, reloaded.Select(rule => rule.StableId).Distinct(StringComparer.Ordinal).Count());

        var decision = AlertGatekeeper.Filter(Firing(reloaded), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void A9_AHalfMigratedBatchDoesNotHandOutAnIdentityTwice()
    {
        // One explicit identity plus one id-less rule, and the harder permutations of the same idea:
        // every output identity appears exactly once.
        _ = StateStore();
        var store = SettingsStore();

        var first = store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)] })
            .FxAlertRules;

        var mixed = store.Save(new NotificationSettings { FxAlertRules = [first[0], LegacyBand(7.1m)] })
            .FxAlertRules;
        Assert.Equal(2, mixed.Count);
        Assert.Equal(2, mixed.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first[0].Id, mixed[0].Id);

        // An explicit identity followed by two indistinguishable id-less rules.
        var duplicate = store.Save(new NotificationSettings
        {
            FxAlertRules = [first[1], NetArrival(1_000m, 0m), NetArrival(1_000m, 0m)],
        }).FxAlertRules;
        Assert.Equal(3, duplicate.Count);
        Assert.Equal(3, duplicate.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first[1].Id, duplicate[0].Id);
    }

    [Fact]
    public void C4_1_AddingASecondRuleAfterAFirstMigrationStillKeepsIdentitiesUnique()
    {
        // The audited fallback hole: A is migrated alone (taking the derived identity), then the same
        // id-less A and a new id-less B are submitted. The allocator must see A already owns it.
        _ = StateStore();
        var store = SettingsStore();

        var first = Assert.Single(
            store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7m)] }).FxAlertRules);

        var second = store.Save(new NotificationSettings
        {
            FxAlertRules = [LegacyBand(7m), LegacyBand(7.1m)],
        }).FxAlertRules;

        Assert.Equal(2, second.Count);
        Assert.Equal(first.Id, second[0].Id);
        Assert.Equal(2, second.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(second[0].Id, second[0].StableId);
    }

    [Fact]
    public void A9_ReorderingOrDroppingARuleDoesNotRebindIdentities()
    {
        _ = StateStore();
        var store = SettingsStore();

        var first = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 0m), NetArrival(1_000m, 2m)],
        }).FxAlertRules;

        var reordered = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 2m), NetArrival(1_000m, 0m)],
        }).FxAlertRules;
        Assert.Equal(first[1].Id, reordered[0].Id);
        Assert.Equal(first[0].Id, reordered[1].Id);

        var kept = store.Save(new NotificationSettings { FxAlertRules = [first[1]] }).FxAlertRules;
        Assert.Equal(first[1].Id, Assert.Single(kept).Id);

        var resubmitted = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 2m)],
        }).FxAlertRules;
        Assert.Equal(first[1].Id, Assert.Single(resubmitted).Id);
    }

    [Fact]
    public void A9_AnUnknownRoundAfterTheUpgradeStillDoesNotFabricateAReEntry()
    {
        // After the rename, the record lives under the NEW key. A round that cannot be judged must
        // keep that record's verdict, or the recovery looks like a re-entry.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var rule = Assert.Single(SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] })
            .FxAlertRules);
        var newKey = KeyNow(rule);

        var unknown = FxAlertRuleEvaluator.Evaluate([rule], _ => null, _ => null);
        Assert.Contains(newKey, unknown.UnknownAlertKeys);

        var roundOne = AlertGatekeeper.Filter(
            unknown.Alerts, state.Load(), Delivered.AddMinutes(10), Cooldown, unknown.UnknownAlertKeys);
        Assert.True(roundOne.NextState.Records[newKey].WasInRange);

        var roundTwo = AlertGatekeeper.Filter(Firing([rule]), roundOne.NextState, Delivered.AddMinutes(20), Cooldown);
        Assert.Empty(roundTwo.ToSend);
    }

    [Fact]
    public void A9_APreRuleBareRecordStillSuppressesTheFirstUpgradeDayAlert()
    {
        // The other generation of old keys: a state file from before keys carried a rule identity.
        // Nothing renames those, so the bare-prefix adoption still has to hold (D6).
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间");

        var rule = Assert.Single(SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1m)] })
            .FxAlertRules);

        var decision = AlertGatekeeper.Filter(Firing([rule]), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void A9_ANewlyCreatedRuleDoesNotInheritALeftoverRulesCooldown()
    {
        // Review #C5-1, under the new mechanism: a created rule owns its identity, its record is
        // absent, and the leftover record of a rule that is no longer in the settings belongs to
        // nobody. It must speak.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-");

        var saved = Assert.Single(SettingsStore().Save(new NotificationSettings
        {
            FxAlertRules = [Created(LegacyBand(7m))],
        }).FxAlertRules);

        var decision = AlertGatekeeper.Filter(Firing([saved]), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Equal(saved.StableId, Assert.Single(decision.ToSend).RuleId);
    }

    [Fact]
    public void C5_2_ARealEditDoesNotInheritTheOldRevisionsCooldown()
    {
        // The user changes what the rule watches. Its key changes with the revision, and the old
        // record is not carried over — the new condition is allowed to speak.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var store = SettingsStore();
        var migrated = Assert.Single(
            store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] }).FxAlertRules);

        var edited = FxAlertRuleIdentity.WithEditedParameters(migrated, LegacyBand(7.15m));
        Assert.Equal(1, edited.Revision);

        var saved = Assert.Single(store.Save(new NotificationSettings { FxAlertRules = [edited] }).FxAlertRules);
        var records = state.Load().Records;
        Assert.False(records.ContainsKey(KeyNow(saved)));

        var decision = AlertGatekeeper.Filter(Firing([saved]), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Single(decision.ToSend);
    }

    [Fact]
    public void C5_3_ARuleCanStillRecordItsExitWhenASiblingSharedItsOldKey()
    {
        // Two legacy rules the old build could not tell apart shared ONE record. The rule that keeps
        // that history may still record that it left its range — no sibling alias keeps it alive.
        var state = StateStore();
        const string sharedKey = "FxNetArrival|JPY/USD|净到账达到你设定的目标|rule:NetArrivalAbove:?:CNY/USD/JPY@r0#amt:1000";
        Record(state, sharedKey);

        var migrated = SettingsStore().Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 2m), NetArrival(1_000m, 0m)],
        }).FxAlertRules;
        Assert.Equal(2, migrated.Count);

        FxPathFacts FactsFor(FxAlertRule rule, decimal gross) =>
            new() { NetArrival = gross - (rule.FixedFee ?? 0m) };
        IReadOnlyList<PortfolioAlert> Evaluate(decimal gross) =>
            FxAlertRuleEvaluator.Evaluate(migrated, _ => (7.2m, "fixture"), rule => FactsFor(rule, gross)).Alerts;

        // T: the fee-paying rule is below its threshold, the other is above it.
        var start = AlertGatekeeper.Filter(Evaluate(1_001m), state.Load(), Delivered, Cooldown);
        var newcomer = migrated[1].StableId;
        Assert.Equal(newcomer, Assert.Single(start.ToSend).RuleId);
        var delivered = AlertGatekeeper.MarkDelivered(start.NextState, start.ToSend, Delivered);

        // T+10: still below, so the rule that owns the shared record has really left its range.
        var middle = AlertGatekeeper.Filter(Evaluate(1_001m), delivered, Delivered.AddMinutes(10), Cooldown);
        Assert.Empty(middle.ToSend);
        var ownerKey = KeyNow(migrated[0]);
        Assert.True(middle.NextState.Records.ContainsKey(ownerKey) || middle.NextState.Records.ContainsKey(sharedKey));
        Assert.False(
            middle.NextState.Records.TryGetValue(ownerKey, out var ownerRecord)
                ? ownerRecord.WasInRange
                : middle.NextState.Records[sharedKey].WasInRange);

        // T+20: both qualify again; the owner re-enters and must be announced.
        var again = AlertGatekeeper.Filter(Evaluate(1_003m), middle.NextState, Delivered.AddMinutes(20), Cooldown);
        Assert.Equal(migrated[0].StableId, Assert.Single(again.ToSend).RuleId);
    }

    [Fact]
    public void A9_AnUnusualExplicitIdentityCannotAdoptAnotherRulesHistory()
    {
        // Review #C4-6: candidates are composed from structured fields, never parsed out of the
        // current key, so an identity containing the key's own separators cannot truncate a lookup.
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

        var decision = AlertGatekeeper.Filter(Firing([stranger]), state.Load(), Delivered.AddMinutes(30), Cooldown);

        // It never delivered anything, so it must speak — not inherit rule-a's cooldown.
        Assert.Single(decision.ToSend);
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