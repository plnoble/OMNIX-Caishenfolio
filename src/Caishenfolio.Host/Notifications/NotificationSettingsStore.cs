using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Notifications;

/// <summary>
/// Persists notification settings in the ledger's settings table.
///
/// Stored as one JSON blob under a single key rather than exploded into rows: the shape is
/// nested (a list of webhooks, each with several fields), and flattening it into key/value pairs
/// would invent an encoding that the next channel type immediately outgrows.
/// </summary>
public sealed class NotificationSettingsStore
{
    internal const string SettingKey = "notifications.json";

    private readonly PortfolioStore _store;
    private readonly AlertNotificationStateStore _alertState;

    public NotificationSettingsStore(PortfolioStore store, AlertNotificationStateStore? alertState = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        // The alert state file sits next to the ledger (both live under the same state root). Deriving
        // it means every existing caller gets the upgrade for free, and a caller that keeps its state
        // elsewhere can still pass its own store.
        _alertState = alertState ?? new AlertNotificationStateStore(
            Path.Combine(Path.GetDirectoryName(store.DatabasePath) ?? ".", "alert-state.json"));
    }

    /// <summary>
    /// Why the last <see cref="Load"/> or <see cref="Save"/> could not finish the upgrade: the alert
    /// state file could not be rewritten, so the identity change was postponed. Empty when fine.
    /// </summary>
    public string? LastMigrationFailure { get; private set; }

    /// <summary>
    /// Reads the settings, settling rule identity on the way out (WP-03 D5, review #1): a rule
    /// written before identity existed is given the identity it was already deduped by, and the
    /// result is written back **once**, so ids are stable from the first read. Doing it here — not
    /// only on save — is what keeps the background check and the settings grid from ever running a
    /// rule with no identity, and it is idempotent: an already-settled store is not rewritten.
    ///
    /// Before that identity is persisted, the cooldown records filed under the rules' OLD keys are
    /// renamed onto the new ones (WP-03 D6, third round). If that rename cannot be written, the
    /// upgrade is postponed: the caller gets the unsettled rules back, whose keys are still the ones
    /// the state file actually uses — a moved-forward identity next to a left-behind record is
    /// exactly the combination that re-sends an alert the user already saw.
    /// </summary>
    public NotificationSettings Load()
    {
        var stored = NotificationSettings.FromJson(_store.LoadRawSetting(SettingKey));
        var settled = stored with
        {
            FxAlertRules = FxAlertRuleIdentity.Reconcile(stored.FxAlertRules, stored.FxAlertRules).ToList(),
        };

        LastMigrationFailure = null;
        var migrated = AlertStateKeyMigration.Apply(_alertState, stored.FxAlertRules, settled.FxAlertRules);
        if (!migrated.Succeeded)
        {
            LastMigrationFailure = migrated.FailureReason;
            return stored;
        }

        if (NeedsRewrite(stored.FxAlertRules, settled.FxAlertRules))
        {
            _store.SaveRawSetting(SettingKey, settled.ToJson());
        }

        return settled;
    }

    /// <summary>
    /// Writes the settings, settling every FX rule's identity first (WP-03 D5, audit): rules that
    /// came from a settings file written before identity existed get an identity, and a rule whose
    /// parameters changed outside the editor gets its revision advanced — never twice, never
    /// backwards. Doing it here rather than only in the settings page means no caller can persist an
    /// identity-less rule.
    ///
    /// The cooldown records of the renamed keys move in the same operation (WP-03 D6, third round),
    /// and if they cannot be written the whole save is refused: silently persisting a new identity
    /// while the history stays under the old key would re-send what the user already received.
    /// </summary>
    public NotificationSettings Save(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var prepared = settings with
        {
            FxAlertRules = FxAlertRuleIdentity
                .Reconcile(settings.FxAlertRules, Load().FxAlertRules)
                .ToList(),
        };

        LastMigrationFailure = null;
        var migrated = AlertStateKeyMigration.Apply(_alertState, settings.FxAlertRules, prepared.FxAlertRules);
        if (!migrated.Succeeded)
        {
            LastMigrationFailure = migrated.FailureReason;
            throw new AlertStateMigrationException(
                "提醒冷却记录无法更新，这次保存已取消（原设置与冷却历史都保持不变）。"
                + $"原因：{migrated.FailureReason}");
        }

        _store.SaveRawSetting(SettingKey, prepared.ToJson());
        return prepared;
    }

    private static bool NeedsRewrite(
        IReadOnlyList<FxAlertRule> before,
        IReadOnlyList<FxAlertRule> after)
    {
        if (before.Count != after.Count)
        {
            return true;
        }

        for (var i = 0; i < before.Count; i++)
        {
            if (!string.Equals(before[i].Id, after[i].Id, StringComparison.Ordinal)
                || before[i].Revision != after[i].Revision)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The alert state file could not be rewritten, so an identity upgrade was refused rather than
/// risking a re-send of an alert the user already received (WP-03 D6).
/// </summary>
public sealed class AlertStateMigrationException(string message) : Exception(message);
