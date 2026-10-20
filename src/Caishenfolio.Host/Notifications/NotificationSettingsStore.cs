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

    public NotificationSettings Load() =>
        NotificationSettings.FromJson(_store.LoadRawSetting(SettingKey));

    /// <summary>
    /// Writes the settings, settling every FX rule's identity first (WP-03 D5, audit): rules that
    /// came from a settings file written before identity existed get an id, and a rule whose
    /// parameters changed outside the editor gets its revision advanced. Doing it here rather than
    /// only in the settings page means no caller can persist an identity-less rule.
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
}
