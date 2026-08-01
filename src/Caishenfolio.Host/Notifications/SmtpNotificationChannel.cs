using System.Net;
using System.Net.Mail;

namespace Caishenfolio.Host.Notifications;

/// <summary>Where to send mail from, and with whose credentials.</summary>
public sealed record SmtpSettings
{
    public required string Host { get; init; }
    public int Port { get; init; } = 465;
    public bool UseSsl { get; init; } = true;
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string To { get; init; }

    /// <summary>Defaults to the account being authenticated with, which is what most servers demand.</summary>
    public string From { get; init; } = "";

    public string EffectiveFrom => string.IsNullOrWhiteSpace(From) ? Username : From;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(To)
        && Port is > 0 and <= 65535;
}

/// <summary>
/// Email delivery, for the case where chat bots are not wanted or not reachable.
///
/// Kept alongside the webhook channels rather than treated as special: it is one more place a
/// message can go, and it fails the same way — reported, not thrown.
/// </summary>
public sealed class SmtpNotificationChannel : INotificationChannel
{
    private readonly SmtpSettings _settings;
    private readonly Func<SmtpSettings, MailMessage, CancellationToken, Task>? _send;

    public SmtpNotificationChannel(
        SmtpSettings settings,
        Func<SmtpSettings, MailMessage, CancellationToken, Task>? send = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _send = send;
    }

    public string Name => "邮件";

    public async Task<NotificationResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_settings.IsComplete)
        {
            return NotificationResult.Failure(Name, "邮件设置不完整（需要服务器、端口、账号、密码、收件人）。");
        }

        try
        {
            using var mail = new MailMessage(_settings.EffectiveFrom, _settings.To)
            {
                Subject = message.Title,
                Body = message.Body,
                // Plain text: the body is a list of facts, and HTML mail is more to get wrong.
                IsBodyHtml = false,
            };

            if (_send is not null)
            {
                await _send(_settings, mail, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendOverSmtpAsync(mail, cancellationToken).ConfigureAwait(false);
            }

            return NotificationResult.Success(Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NotificationResult.Failure(Name, ex.Message);
        }
    }

    private async Task SendOverSmtpAsync(MailMessage mail, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_settings.Host, _settings.Port)
        {
            EnableSsl = _settings.UseSsl,
            Credentials = new NetworkCredential(_settings.Username, _settings.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
    }
}
