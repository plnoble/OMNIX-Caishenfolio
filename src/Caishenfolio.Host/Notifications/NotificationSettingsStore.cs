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

    public NotificationSettings Save(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.SaveRawSetting(SettingKey, settings.ToJson());
        return settings;
    }
}
