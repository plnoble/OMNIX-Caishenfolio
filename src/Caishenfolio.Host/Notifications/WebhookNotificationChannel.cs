using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Caishenfolio.Host.Notifications;

/// <summary>The bot flavours this supports. Each wants the same text in a different envelope.</summary>
public enum WebhookFlavor
{
    /// <summary>企业微信群机器人.</summary>
    WeCom,

    /// <summary>飞书 / Lark 群机器人.</summary>
    Feishu,

    /// <summary>钉钉群机器人.</summary>
    DingTalk,

    /// <summary>Telegram Bot API.</summary>
    Telegram,

    /// <summary>Discord webhook.</summary>
    Discord,

    /// <summary>Slack incoming webhook.</summary>
    Slack,

    /// <summary>Anything else: posts {"title": ..., "body": ..., "urgency": ...}.</summary>
    Generic,
}

/// <summary>
/// Delivers a message to a chat webhook.
///
/// One class rather than six because the differences are entirely in the JSON envelope; the
/// transport, the failure handling and the timeout are identical, and six near-copies would
/// drift apart the first time one of them needed a fix.
///
/// Telegram is the exception in one respect: its endpoint is built from the bot token and chat
/// id rather than being handed over whole, so it takes those instead of a URL.
/// </summary>
public sealed class WebhookNotificationChannel : INotificationChannel
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly WebhookFlavor _flavor;
    private readonly string _target;

    public WebhookNotificationChannel(WebhookFlavor flavor, string target, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        _flavor = flavor;
        _target = target.Trim();
        _http = http ?? new HttpClient { Timeout = Timeout };
    }

    public string Name => DisplayName(_flavor);

    public static string DisplayName(WebhookFlavor flavor) => flavor switch
    {
        WebhookFlavor.WeCom => "企业微信",
        WebhookFlavor.Feishu => "飞书",
        WebhookFlavor.DingTalk => "钉钉",
        WebhookFlavor.Telegram => "Telegram",
        WebhookFlavor.Discord => "Discord",
        WebhookFlavor.Slack => "Slack",
        _ => "自定义 Webhook",
    };

    public async Task<NotificationResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var content = new StringContent(
                BuildPayload(_flavor, message), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            using var response = await _http
                .PostAsync(_target, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadBrieflyAsync(response, cancellationToken).ConfigureAwait(false);
                return NotificationResult.Failure(
                    Name,
                    string.IsNullOrWhiteSpace(detail)
                        ? $"HTTP {(int)response.StatusCode}"
                        : $"HTTP {(int)response.StatusCode}：{detail}");
            }

            // WeCom, Feishu and DingTalk answer 200 with an error code in the body, so a
            // successful status is not by itself a delivered message.
            var body = await ReadBrieflyAsync(response, cancellationToken).ConfigureAwait(false);
            var upstream = UpstreamError(body);
            return upstream is null
                ? NotificationResult.Success(Name)
                : NotificationResult.Failure(Name, upstream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A dead channel must not take the other channels down with it.
            return NotificationResult.Failure(Name, ex.Message);
        }
    }

    /// <summary>Builds the endpoint for a Telegram bot, which is addressed by token and chat.</summary>
    public static string TelegramEndpoint(string botToken, string chatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        return $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage"
               + $"?chat_id={Uri.EscapeDataString(chatId.Trim())}";
    }

    internal static string BuildPayload(WebhookFlavor flavor, NotificationMessage message)
    {
        var text = message.ToPlainText();
        object payload = flavor switch
        {
            WebhookFlavor.WeCom => new { msgtype = "text", text = new { content = text } },
            WebhookFlavor.Feishu => new { msg_type = "text", content = new { text } },
            WebhookFlavor.DingTalk => new
            {
                msgtype = "text",
                // DingTalk bots require the keyword filter to match, and the title carries it.
                text = new { content = text },
            },
            WebhookFlavor.Telegram => new { text },
            WebhookFlavor.Discord => new { content = text },
            WebhookFlavor.Slack => new { text },
            _ => new
            {
                title = message.Title,
                body = message.Body,
                urgency = message.Urgency.ToString().ToLowerInvariant(),
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Chinese must survive the trip; escaping it makes DingTalk's keyword filter miss.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Reads the chat platforms' in-body error codes; null means nothing was reported.</summary>
    internal static string? UpstreamError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var field in new[] { "errcode", "code", "StatusCode" })
            {
                if (document.RootElement.TryGetProperty(field, out var element)
                    && element.TryGetInt32(out var code)
                    && code != 0)
                {
                    return $"上游返回 {field}={code}：{Describe(document.RootElement)}";
                }
            }

            if (document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.False)
            {
                return $"上游拒绝：{Describe(document.RootElement)}";
            }
        }
        catch (JsonException)
        {
            // A non-JSON body on a 2xx is normal for Discord and Slack.
            return null;
        }

        return null;
    }

    private static string Describe(JsonElement root)
    {
        foreach (var field in new[] { "errmsg", "msg", "description", "StatusMessage", "error" })
        {
            if (root.TryGetProperty(field, out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? "";
            }
        }

        return "未提供原因";
    }

    private static async Task<string> ReadBrieflyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > 300 ? body[..300] : body;
        }
        catch (Exception)
        {
            return "";
        }
    }
}
