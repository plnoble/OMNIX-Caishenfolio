using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caishenfolio.Host.Notifications;

/// <summary>One configured chat webhook.</summary>
public sealed record WebhookTarget
{
    public required WebhookFlavor Flavor { get; init; }

    /// <summary>The webhook URL, or for Telegram the bot token.</summary>
    public required string Secret { get; init; }

    /// <summary>Telegram only: which chat to post into.</summary>
    public string ChatId { get; init; } = "";

    public bool Enabled { get; init; } = true;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Secret)
        && (Flavor != WebhookFlavor.Telegram || !string.IsNullOrWhiteSpace(ChatId));

    /// <summary>The URL to post to, or empty when this target is not usable.</summary>
    public string Endpoint => !IsComplete
        ? ""
        : Flavor == WebhookFlavor.Telegram
            ? WebhookNotificationChannel.TelegramEndpoint(Secret, ChatId)
            : Secret;
}

/// <summary>
/// Where notifications should go, and which ones are worth sending.
///
/// Held apart from <c>PortfolioSettings</c> because it contains credentials and so is written
/// and read through <see cref="SecretProtector"/>, which the rest of the preferences do not need.
/// </summary>
public sealed record NotificationSettings
{
    public bool Enabled { get; init; }

    public IReadOnlyList<WebhookTarget> Webhooks { get; init; } = [];

    public SmtpSettings? Smtp { get; init; }

    public bool SmtpEnabled { get; init; }

    /// <summary>Alert about an IPO payment this many days ahead. Missing a payment forfeits it.</summary>
    public int IpoLeadDays { get; init; } = 2;

    /// <summary>When false, only deadlines and warnings go out — no routine price alerts.</summary>
    public bool IncludeRoutineAlerts { get; init; } = true;

    public static NotificationSettings Default => new();

    /// <summary>True when at least one channel is switched on and fully configured.</summary>
    public bool HasUsableChannel =>
        Enabled
        && (Webhooks.Any(w => w.Enabled && w.IsComplete)
            || (SmtpEnabled && Smtp is { IsComplete: true }));

    /// <summary>Builds the live channels. Incomplete or disabled entries are simply left out.</summary>
    public IReadOnlyList<INotificationChannel> BuildChannels(HttpClient? http = null)
    {
        var channels = new List<INotificationChannel>();
        if (!Enabled)
        {
            return channels;
        }

        foreach (var target in Webhooks.Where(w => w.Enabled && w.IsComplete))
        {
            channels.Add(new WebhookNotificationChannel(target.Flavor, target.Endpoint, http));
        }

        if (SmtpEnabled && Smtp is { IsComplete: true } smtp)
        {
            channels.Add(new SmtpNotificationChannel(smtp));
        }

        return channels;
    }

    // -- persistence ------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes with every secret encrypted, so the stored form carries no plain tokens.</summary>
    public string ToJson() => JsonSerializer.Serialize(Protected(this), JsonOptions);

    /// <summary>Reads settings back, decrypting secrets. Unreadable input yields the defaults.</summary>
    public static NotificationSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<NotificationSettings>(json, JsonOptions);
            return stored is null ? Default : Unprotected(stored);
        }
        catch (JsonException)
        {
            // Corrupt settings must not stop the app from starting.
            return Default;
        }
    }

    private static NotificationSettings Protected(NotificationSettings settings) => settings with
    {
        Webhooks = settings.Webhooks
            .Select(w => w with { Secret = SecretProtector.Protect(w.Secret) })
            .ToList(),
        Smtp = settings.Smtp is null
            ? null
            : settings.Smtp with { Password = SecretProtector.Protect(settings.Smtp.Password) },
    };

    private static NotificationSettings Unprotected(NotificationSettings settings) => settings with
    {
        Webhooks = settings.Webhooks
            .Select(w => w with { Secret = SecretProtector.Unprotect(w.Secret) })
            .ToList(),
        Smtp = settings.Smtp is null
            ? null
            : settings.Smtp with { Password = SecretProtector.Unprotect(settings.Smtp.Password) },
    };
}
