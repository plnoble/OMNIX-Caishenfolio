using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 D6, fourth pass: the alias lifecycle. An upgrade may not re-send what the user already saw
/// (that is the acceptance bar), but "never re-send" must not become "attach every rule to some
/// history": an alias may only belong to a rule that really was migrated, only while it still watches
/// the same thing, and never when the key belongs to a sibling that is still using it. These tests
/// feed the REAL production path — settings store, alert state file, and the background result record
/// the notifier reads — because the previous round's fix stopped one layer short of that.
/// </summary>
public class FxRuleAliasLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset Delivered = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_alias_lifecycle", Guid.NewGuid().ToString("N"));

    private AlertNotificationStateStore StateStore() => new(Path.Combine(_root, "alert-state.json"));

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

    private static IReadOnlyList<PortfolioAlert> Firing(IReadOnlyList<FxAlertRule> rules) =>
        FxAlertRuleEvaluator.Evaluate(rules, _ => (7.2m, "fixture"), _ => null).Alerts;

    /// <summary>A rule the settings page just created: it already owns an identity.</summary>
    private static FxAlertRule Created(FxAlertRule shape) =>
        FxAlertRuleIdentity.AssignNewIdentity(shape);

    [Fact]
    public void C5_1_ANewlyCreatedRuleDoesNotInheritASiblingRulesCooldown()
    {
        // The audited case: a migrated rule's record is still in the state file, but that rule is no
        // longer in the settings (the user removed it). A rule created NOW with the same watch has its
        // own identity from birth and must speak: attaching the derived-form alias to it would make it
        // inherit the removed rule's cooldown and swallow its first alert.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7~-");

        var store = SettingsStore();

        // The page creates it with an identity already; nothing about it is a migration.
        var created = Created(LegacyBand(7m));
        Assert.Empty(created.LegacyRuleKeys);
        Assert.NotEqual(created.DerivedId, created.Id);

        var saved = Assert.Single(
            store.Save(new NotificationSettings { FxAlertRules = [created] }).FxAlertRules);
        Assert.Empty(saved.LegacyRuleKeys);

        var decision = AlertGatekeeper.Filter(Firing([saved]), state.Load(), Delivered.AddMinutes(30), Cooldown);
        // It has never been delivered: the leftover record belongs to a rule that no longer exists.
        Assert.Equal(saved.StableId, Assert.Single(decision.ToSend).RuleId);
    }

    [Fact]
    public void C5_2_ARealEditDoesNotInheritTheOldRevisionsCooldown()
    {
        // The aliases describe the watch the user has just changed away from. After a real edit, the
        // new condition must be able to speak — inheriting the old key's delivery would suppress it.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var store = SettingsStore();
        var migrated = Assert.Single(
            store.Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] }).FxAlertRules);
        Assert.NotEmpty(migrated.LegacyRuleKeys);

        var edited = FxAlertRuleIdentity.WithEditedParameters(migrated, LegacyBand(7.15m));
        Assert.Equal(1, edited.Revision);
        Assert.Empty(edited.LegacyRuleKeys);

        var saved = Assert.Single(
            store.Save(new NotificationSettings { FxAlertRules = [edited] }).FxAlertRules);
        Assert.Empty(saved.LegacyRuleKeys);

        var decision = AlertGatekeeper.Filter(Firing([saved]), state.Load(), Delivered.AddMinutes(30), Cooldown);
        Assert.Single(decision.ToSend);
    }

    [Fact]
    public void C5_3_ASiblingsAliasDoesNotSwallowARealExitAndReEntry()
    {
        // Two legacy net-arrival rules that the old build could not tell apart (same identity, same
        // level, different fee) shared ONE delivery record. One of them keeps that identity and its
        // record; the other is new and speaks for itself. A sibling's old key must not be kept alive
        // for the rule that owns it either, or the owner can never record that it left its range.
        var state = StateStore();
        Record(state, "FxNetArrival|JPY/USD|净到账达到你设定的目标|rule:NetArrivalAbove:?:CNY/USD/JPY@r0#amt:1000");

        var store = SettingsStore();
        var migrated = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 2m), NetArrival(1_000m, 0m)],
        }).FxAlertRules;
        Assert.Equal(2, migrated.Count);

        FxPathFacts FactsFor(FxAlertRule rule, decimal gross) =>
            new() { NetArrival = gross - (rule.FixedFee ?? 0m) };
        IReadOnlyList<PortfolioAlert> Evaluate(decimal gross) =>
            FxAlertRuleEvaluator.Evaluate(migrated, _ => (7.2m, "fixture"), rule => FactsFor(rule, gross)).Alerts;

        // T: gross 1,001 — the fee-paying rule is below its threshold, the other is over it.
        var start = AlertGatekeeper.Filter(Evaluate(1_001m), state.Load(), Delivered, Cooldown);
        var firstSent = Assert.Single(start.ToSend);
        var owner = migrated[0].StableId;   // keeps the shared record
        var newcomer = migrated[1].StableId;
        Assert.Equal(newcomer, firstSent.RuleId);
        var delivered = AlertGatekeeper.MarkDelivered(start.NextState, start.ToSend, Delivered);

        // T+10: the same gross — the owner is still below, so it has really left its range.
        var middle = AlertGatekeeper.Filter(Evaluate(1_001m), delivered, Delivered.AddMinutes(10), Cooldown);
        Assert.Empty(middle.ToSend);
        Assert.False(middle.NextState.Records[
            "FxNetArrival|JPY/USD|净到账达到你设定的目标|rule:NetArrivalAbove:?:CNY/USD/JPY@r0#amt:1000"].WasInRange);

        // T+20: both qualify again; the owner re-enters and must be announced, the newcomer is in cooldown.
        var again = AlertGatekeeper.Filter(Evaluate(1_003m), middle.NextState, Delivered.AddMinutes(20), Cooldown);
        Assert.Equal(owner, Assert.Single(again.ToSend).RuleId);
    }

    [Fact]
    public void C4_1_AddingASecondRuleAfterAFirstMigrationStillKeepsIdentitiesUnique()
    {
        // The audited fallback hole: A is migrated alone first (taking the derived identity), then the
        // same id-less A and a new id-less B are submitted. The fallback must see that A already owns
        // the derived identity — one shared "taken" set, not two that drift apart.
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
    public void C4_2_AHalfMigratedBatchWithADuplicateCannotMintTheSameIdentity()
    {
        // One explicit identity plus two indistinguishable id-less rules: every output identity must
        // appear exactly once, whichever pool it came from.
        _ = StateStore();
        var store = SettingsStore();

        var first = store.Save(new NotificationSettings
        {
            FxAlertRules = [NetArrival(1_000m, 0m), NetArrival(1_000m, 2m)],
        }).FxAlertRules;

        var mixed = store.Save(new NotificationSettings
        {
            FxAlertRules = [first[1], NetArrival(1_000m, 0m), NetArrival(1_000m, 0m)],
        }).FxAlertRules;

        Assert.Equal(3, mixed.Count);
        Assert.Equal(3, mixed.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first[1].Id, mixed[0].Id);
    }

    [Fact]
    public void C4_4_TheBackgroundResultKeepsTheOldCompleteKeyAliveWhileUnknown()
    {
        // The production path re-projects the evaluator's outcomes into BackgroundCheckResult, and the
        // notifier reads THAT property. Dropping the legacy keys there recreates the fabricated exit:
        // round one cannot be judged, round two can, and the recovery is delivered again.
        var state = StateStore();
        Record(state, "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-");

        var settled = SettingsStore()
            .Save(new NotificationSettings { FxAlertRules = [LegacyBand(7.1000001m)] })
            .FxAlertRules;
        var rule = Assert.Single(settled);

        // Round one, through the record the notifier actually reads.
        var evaluated = FxAlertRuleEvaluator.Evaluate([rule], _ => null, _ => null);
        var background = new BackgroundCheckResult
        {
            Alerts = evaluated.Alerts,
            Notes = evaluated.Notes,
            FxOutcomes = evaluated.Outcomes,
        };

        const string oldKey = "FxBand|USD/CNY|汇率进入你设定的区间|rule:RateBand:USD/CNY:?/?/?@r0#band:7.1~-";
        Assert.Contains(oldKey, background.UnknownAlertKeys);

        var roundOne = AlertGatekeeper.Filter(
            background.Alerts, state.Load(), Delivered.AddMinutes(10), Cooldown, background.UnknownAlertKeys);
        Assert.True(roundOne.NextState.Records[oldKey].WasInRange);

        // Round two: the quote is back and the condition has been true all along.
        var roundTwo = AlertGatekeeper.Filter(
            Firing([rule]), roundOne.NextState, Delivered.AddMinutes(20), Cooldown);
        Assert.Empty(roundTwo.ToSend);
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