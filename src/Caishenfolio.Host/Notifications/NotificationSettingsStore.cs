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

    public NotificationSettingsStore(PortfolioStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Reads the settings, settling rule identity on the way out (WP-03 D5, review #1): a rule
    /// written before identity existed is given the identity it was already deduped by, and the
    /// result is written back **once**, so ids are stable from the first read. Doing it here — not
    /// only on save — is what keeps the background check and the settings grid from ever running a
    /// rule with no identity, and it is idempotent: an already-settled store is not rewritten.
    /// </summary>
    public NotificationSettings Load()
    {
        var stored = NotificationSettings.FromJson(_store.LoadRawSetting(SettingKey));
        var settled = stored with
        {
            FxAlertRules = FxAlertRuleIdentity.Reconcile(stored.FxAlertRules, stored.FxAlertRules).ToList(),
        };

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
    /// </summary>
    public NotificationSettings Save(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var settled = settings with
        {
            FxAlertRules = FxAlertRuleIdentity
                .Reconcile(settings.FxAlertRules, Load().FxAlertRules)
                .ToList(),
        };

        _store.SaveRawSetting(SettingKey, settled.ToJson());
        return settled;
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
