using System.Text.Json;
using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>Records what it was asked to send instead of reaching the network.</summary>
internal sealed class RecordingChannel : INotificationChannel
{
    private readonly bool _succeeds;

    public RecordingChannel(string name, bool succeeds = true)
    {
        Name = name;
        _succeeds = succeeds;
    }

    public string Name { get; }

    public List<NotificationMessage> Sent { get; } = [];

    public Task<NotificationResult> SendAsync(
        NotificationMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.FromResult(_succeeds
            ? NotificationResult.Success(Name)
            : NotificationResult.Failure(Name, "模拟失败"));
    }
}

public class WebhookPayloadTests
{
    private static readonly NotificationMessage Message = new()
    {
        Title = "【待办】中签待缴款",
        Body = "某某股份（SSE:601000）已中签 1,000 股。",
    };

    [Theory]
    [InlineData(WebhookFlavor.WeCom, "msgtype")]
    [InlineData(WebhookFlavor.Feishu, "msg_type")]
    [InlineData(WebhookFlavor.DingTalk, "msgtype")]
    [InlineData(WebhookFlavor.Telegram, "text")]
    [InlineData(WebhookFlavor.Discord, "content")]
    [InlineData(WebhookFlavor.Slack, "text")]
    public void EachFlavourUsesItsOwnEnvelope(WebhookFlavor flavor, string expectedField)
    {
        var payload = WebhookNotificationChannel.BuildPayload(flavor, Message);

        using var document = JsonDocument.Parse(payload);
        Assert.True(document.RootElement.TryGetProperty(expectedField, out _));
    }

    [Fact]
    public void EveryFlavourCarriesTheTitleAndBody()
    {
        foreach (var flavor in Enum.GetValues<WebhookFlavor>())
        {
            var payload = WebhookNotificationChannel.BuildPayload(flavor, Message);

            Assert.Contains("中签待缴款", payload, StringComparison.Ordinal);
            Assert.Contains("1,000 股", payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChineseIsNotEscapedSoKeywordFiltersStillMatch()
    {
        // DingTalk and WeCom bots drop messages whose keyword is not present; \uXXXX escapes
        // make the keyword invisible to that filter.
        var payload = WebhookNotificationChannel.BuildPayload(WebhookFlavor.DingTalk, Message);

        Assert.DoesNotContain("\\u", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGenericFlavourKeepsTheFieldsSeparate()
    {
        var payload = WebhookNotificationChannel.BuildPayload(WebhookFlavor.Generic, Message with
        {
            Urgency = NotificationUrgency.Urgent,
        });

        using var document = JsonDocument.Parse(payload);
        Assert.Equal("【待办】中签待缴款", document.RootElement.GetProperty("title").GetString());
        Assert.Equal("urgent", document.RootElement.GetProperty("urgency").GetString());
    }

    [Theory]
    [InlineData("""{"errcode":93000,"errmsg":"invalid webhook url"}""")]
    [InlineData("""{"code":19001,"msg":"param invalid"}""")]
    [InlineData("""{"ok":false,"description":"chat not found"}""")]
    public void AnErrorInsideATwoHundredResponseIsStillAFailure(string body)
    {
        // WeCom, Feishu and Telegram answer 200 with the refusal in the body.
        Assert.NotNull(WebhookNotificationChannel.UpstreamError(body));
    }

    [Theory]
    [InlineData("""{"errcode":0,"errmsg":"ok"}""")]
    [InlineData("")]
    [InlineData("not json at all")]
    public void ASuccessfulOrEmptyBodyIsNotTreatedAsAnError(string body)
    {
        Assert.Null(WebhookNotificationChannel.UpstreamError(body));
    }

    [Fact]
    public void TheTelegramEndpointCarriesTheTokenAndChat()
    {
        var url = WebhookNotificationChannel.TelegramEndpoint("123:ABC", "-100987");

        Assert.Contains("/bot123:ABC/sendMessage", url, StringComparison.Ordinal);
        Assert.Contains("chat_id=-100987", url, StringComparison.Ordinal);
    }
}

public class NotificationDispatcherTests
{
    private static PortfolioAlert Ipo(string title = "中签待缴款") => new()
    {
        Kind = AlertKind.IpoDeadline,
        Severity = AlertSeverity.Warning,
        Symbol = "SSE:601000",
        Title = title,
        Message = "逾期未缴会作废。",
    };

    private static PortfolioAlert Routine() => new()
    {
        Kind = AlertKind.PlannedBuy,
        Severity = AlertSeverity.Info,
        Symbol = "SSE:600000",
        Title = "触及计划买入价",
        Message = "现价 10.00。",
    };

    [Fact]
    public async Task EveryChannelGetsTheMessage()
    {
        var a = new RecordingChannel("A");
        var b = new RecordingChannel("B");

        var report = await new NotificationDispatcher([a, b]).SendAsync([Ipo()]);

        Assert.Single(a.Sent);
        Assert.Single(b.Sent);
        Assert.True(report.AllDelivered);
    }

    [Fact]
    public async Task OneBrokenChannelDoesNotStopTheOthers()
    {
        var broken = new RecordingChannel("坏的", succeeds: false);
        var working = new RecordingChannel("好的");

        var report = await new NotificationDispatcher([broken, working]).SendAsync([Ipo()]);

        Assert.True(report.AnyDelivered);
        Assert.False(report.AllDelivered);
        Assert.Single(working.Sent);
        Assert.Contains("坏的", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutingRoutineAlertsStillSendsDeadlines()
    {
        var channel = new RecordingChannel("A");

        await new NotificationDispatcher([channel])
            .SendAsync([Routine(), Ipo()], includeRoutineAlerts: false);

        var body = Assert.Single(channel.Sent).Body;
        Assert.Contains("中签待缴款", body, StringComparison.Ordinal);
        Assert.DoesNotContain("触及计划买入价", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsSentWhenThereIsNothingToSay()
    {
        var channel = new RecordingChannel("A");

        var report = await new NotificationDispatcher([channel]).SendAsync([]);

        Assert.Empty(channel.Sent);
        Assert.Equal("没有需要提醒的事项。", report.Describe());
    }

    [Fact]
    public async Task AlertsWithNoChannelAreReportedRatherThanSilentlyDropped()
    {
        var report = await new NotificationDispatcher([]).SendAsync([Ipo()]);

        Assert.Contains("没有配置任何通知渠道", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeadlinesComeFirstInTheBody()
    {
        var message = NotificationDispatcher.BuildMessage([Routine(), Ipo()]);

        var deadlineAt = message.Body.IndexOf("中签待缴款", StringComparison.Ordinal);
        var routineAt = message.Body.IndexOf("触及计划买入价", StringComparison.Ordinal);
        Assert.True(deadlineAt >= 0 && deadlineAt < routineAt);
    }

    [Fact]
    public void ADeadlineMakesTheMessageUrgentAndMarksTheTitle()
    {
        var message = NotificationDispatcher.BuildMessage([Ipo()]);

        Assert.Equal(NotificationUrgency.Urgent, message.Urgency);
        Assert.Contains("【待办】", message.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoutineOnlyMessageIsNotMarkedUrgent()
    {
        var message = NotificationDispatcher.BuildMessage([Routine()]);

        Assert.Equal(NotificationUrgency.Normal, message.Urgency);
        Assert.DoesNotContain("【待办】", message.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageNeverReadsAsAdvice()
    {
        var message = NotificationDispatcher.BuildMessage([Ipo(), Routine()]);
        var text = message.ToPlainText();

        foreach (var forbidden in new[] { "建议买", "建议卖", "应该买", "应该卖", "推荐" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        Assert.Contains("非投资建议", text, StringComparison.Ordinal);
    }
}

public class SecretProtectorTests
{
    [Fact]
    public void ASecretSurvivesARoundTrip()
    {
        const string secret = "https://open.feishu.cn/open-apis/bot/v2/hook/abc-123";

        Assert.Equal(secret, SecretProtector.Unprotect(SecretProtector.Protect(secret)));
    }

    [Fact]
    public void TheStoredFormDoesNotContainThePlainSecret()
    {
        const string secret = "super-secret-token-value";

        var stored = SecretProtector.Protect(secret);

        if (SecretProtector.IsSupported)
        {
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmptyStaysEmpty()
    {
        Assert.Equal("", SecretProtector.Protect(""));
        Assert.Equal("", SecretProtector.Protect(null));
        Assert.Equal("", SecretProtector.Unprotect(null));
    }

    [Fact]
    public void AValueStoredBeforeProtectionExistedIsStillReadable()
    {
        Assert.Equal("plain-old-url", SecretProtector.Unprotect("plain-old-url"));
    }

    [Fact]
    public void UnreadableCiphertextYieldsNoSecretRatherThanGibberish()
    {
        // Copied from another machine: sending the raw bytes as a token would be worse.
        Assert.Equal("", SecretProtector.Unprotect("dpapi:bm90LXJlYWwtY2lwaGVydGV4dA=="));
    }

    [Fact]
    public void MaskingKeepsEnoughToTellSecretsApartWithoutRevealingThem()
    {
        var masked = SecretProtector.Mask("abcd-1234-efgh-5678");

        Assert.StartsWith("abcd", masked, StringComparison.Ordinal);
        Assert.EndsWith("5678", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("1234", masked, StringComparison.Ordinal);
    }
}

public class NotificationSettingsTests
{
    private static NotificationSettings Configured() => new()
    {
        Enabled = true,
        Webhooks =
        [
            new WebhookTarget { Flavor = WebhookFlavor.Feishu, Secret = "https://hook/abc" },
            new WebhookTarget { Flavor = WebhookFlavor.Telegram, Secret = "123:ABC", ChatId = "-100" },
        ],
        SmtpEnabled = true,
        Smtp = new SmtpSettings
        {
            Host = "smtp.example.com",
            Username = "me@example.com",
            Password = "hunter2",
            To = "me@example.com",
        },
    };

    [Fact]
    public void SettingsSurviveARoundTripThroughJson()
    {
        var restored = NotificationSettings.FromJson(Configured().ToJson());

        Assert.True(restored.Enabled);
        Assert.Equal(2, restored.Webhooks.Count);
        Assert.Equal("https://hook/abc", restored.Webhooks[0].Secret);
        Assert.Equal("hunter2", restored.Smtp!.Password);
    }

    [Fact]
    public void SecretsAreNotStoredInThePlain()
    {
        var json = Configured().ToJson();

        if (SecretProtector.IsSupported)
        {
            Assert.DoesNotContain("https://hook/abc", json, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaultsRatherThanThrowing()
    {
        Assert.False(NotificationSettings.FromJson("{ not json").Enabled);
        Assert.False(NotificationSettings.FromJson(null).Enabled);
    }

    [Fact]
    public void ChannelsAreBuiltOnlyForCompleteEnabledTargets()
    {
        var settings = Configured() with
        {
            Webhooks =
            [
                new WebhookTarget { Flavor = WebhookFlavor.Feishu, Secret = "https://hook/ok" },
                new WebhookTarget { Flavor = WebhookFlavor.WeCom, Secret = "", Enabled = true },
                new WebhookTarget { Flavor = WebhookFlavor.Slack, Secret = "https://x", Enabled = false },
                // Telegram without a chat id has nowhere to post.
                new WebhookTarget { Flavor = WebhookFlavor.Telegram, Secret = "123:ABC" },
            ],
            SmtpEnabled = false,
        };

        var channels = settings.BuildChannels();

        Assert.Single(channels);
        Assert.Equal("飞书", channels[0].Name);
    }

    [Fact]
    public void DisablingNotificationsBuildsNoChannelsAtAll()
    {
        var settings = Configured() with { Enabled = false };

        Assert.Empty(settings.BuildChannels());
        Assert.False(settings.HasUsableChannel);
    }

    [Fact]
    public void AnIncompleteSmtpBlockIsNotUsable()
    {
        var settings = new NotificationSettings
        {
            Enabled = true,
            SmtpEnabled = true,
            Smtp = new SmtpSettings { Host = "smtp.example.com", Username = "u", Password = "", To = "x@y" },
        };

        Assert.False(settings.HasUsableChannel);
    }

    [Fact]
    public async Task AnIncompleteSmtpChannelReportsWhyRatherThanThrowing()
    {
        var channel = new SmtpNotificationChannel(
            new SmtpSettings { Host = "", Username = "", Password = "", To = "" });

        var result = await channel.SendAsync(new NotificationMessage { Title = "t", Body = "b" });

        Assert.False(result.Ok);
        Assert.Contains("设置不完整", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SmtpFallsBackToTheAuthenticatedAccountAsSender()
    {
        var smtp = new SmtpSettings
        {
            Host = "smtp.example.com", Username = "me@example.com", Password = "p", To = "me@example.com",
        };

        Assert.Equal("me@example.com", smtp.EffectiveFrom);
        Assert.Equal("other@example.com", (smtp with { From = "other@example.com" }).EffectiveFrom);
    }
}

public class NotificationSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_notify", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    [Fact]
    public void SettingsPersistAcrossStoreInstances()
    {
        var settings = new NotificationSettings
        {
            Enabled = true,
            IpoLeadDays = 3,
            Webhooks = [new WebhookTarget { Flavor = WebhookFlavor.WeCom, Secret = "https://hook/x" }],
        };

        new NotificationSettingsStore(NewStore()).Save(settings);
        var restored = new NotificationSettingsStore(NewStore()).Load();

        Assert.True(restored.Enabled);
        Assert.Equal(3, restored.IpoLeadDays);
        Assert.Equal("https://hook/x", Assert.Single(restored.Webhooks).Secret);
    }

    [Fact]
    public void AnUnconfiguredLedgerLoadsTheDefaults()
    {
        var loaded = new NotificationSettingsStore(NewStore()).Load();

        Assert.False(loaded.Enabled);
        Assert.Empty(loaded.Webhooks);
    }

    [Fact]
    public void RawSettingsRoundTripAndClearOnEmpty()
    {
        var store = NewStore();

        store.SaveRawSetting("probe", "value");
        Assert.Equal("value", store.LoadRawSetting("probe"));

        store.SaveRawSetting("probe", "");
        Assert.Null(store.LoadRawSetting("probe"));
    }

    [Fact]
    public void RawSettingsDoNotDisturbThePreferenceRecord()
    {
        var store = NewStore();
        store.SaveSettings(PortfolioSettings.Default with { BaseCurrency = "USD" });

        new NotificationSettingsStore(store).Save(new NotificationSettings { Enabled = true });

        Assert.Equal("USD", store.LoadSettings().BaseCurrency);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
            // A locked temp file is not a test failure.
        }
    }
}

public class HeadlessNotifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_headless", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Today = new(2026, 8, 1);

    private PortfolioStore StoreWithAllottedIpo()
    {
        var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveAccount(Account.Create("打新账户", AccountKind.Securities, "CNY"));
        var accounts = store.ListAccounts();

        var ipo = IpoSubscription.Create(
            accounts[0].Id, "SSE:601000", new DateOnly(2026, 7, 29), 1000m, 20m, "CNY", "某某股份");
        store.SaveIpoSubscription(ipo with { Status = IpoStatus.Allotted, AllottedQuantity = 500m });
        return store;
    }

    [Fact]
    public async Task AnUnpaidAllotmentIsFoundWithoutAnyMarketData()
    {
        var result = await HeadlessNotifier.RunAsync(
            StoreWithAllottedIpo(), NotificationSettings.Default, Today);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal(AlertKind.IpoDeadline, alert.Kind);
        Assert.Equal("中签待缴款", alert.Title);
    }

    [Fact]
    public async Task ItSaysWhatItDidNotCheck()
    {
        var result = await HeadlessNotifier.RunAsync(
            StoreWithAllottedIpo(), NotificationSettings.Default, Today);

        // Reporting "no alerts" would imply prices were checked and were fine.
        Assert.Contains(result.Limitations, note => note.Contains("价格", StringComparison.Ordinal));
        Assert.Contains("只覆盖打新时限", result.Summarize(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoChannelsItStillReportsWhatItFound()
    {
        var result = await HeadlessNotifier.RunAsync(
            StoreWithAllottedIpo(), NotificationSettings.Default, Today);

        Assert.False(result.Dispatch.AnyDelivered);
        Assert.Contains("没有配置任何通知渠道", result.Summarize(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyLedgerFindsNothingAndSaysSo()
    {
        var store = PortfolioStore.UnderStateRoot(_root);

        var result = await HeadlessNotifier.RunAsync(store, NotificationSettings.Default, Today);

        Assert.Empty(result.Alerts);
        Assert.Contains("没有需要提醒的事项", result.Summarize(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogRecordsEveryRunSoASilentFailureIsVisible()
    {
        var path = Path.Combine(_root, "logs", "notify.log");
        var result = new HeadlessNotifyResult
        {
            Dispatch = new DispatchReport { Results = [], AlertCount = 0 },
            Alerts = [],
        };

        HeadlessNotifier.AppendLog(path, result);
        HeadlessNotifier.AppendLog(path, result);

        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
            // A locked temp file is not a test failure.
        }
    }
}

public class ScheduledCheckInstallerTests
{
    [Fact]
    public void TheCommandIsShownBeforeAnythingIsChanged()
    {
        var command = ScheduledCheckInstaller.DescribeInstall(
            @"C:\Program Files\OMNIX\Caishenfolio.Desktop.exe", new TimeOnly(9, 30));

        Assert.Contains("--notify", command, StringComparison.Ordinal);
        Assert.Contains("09:30", command, StringComparison.Ordinal);
        Assert.Contains("/SC DAILY", command, StringComparison.Ordinal);
        // An install path with a space must stay one argument.
        Assert.Contains(@"""C:\Program Files\OMNIX\Caishenfolio.Desktop.exe"" --notify",
            command, StringComparison.Ordinal);
    }
}
