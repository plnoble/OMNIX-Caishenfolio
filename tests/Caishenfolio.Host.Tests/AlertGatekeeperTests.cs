using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// The gatekeeper implements plan §8's dedup semantics: same event quiet within the
/// cooldown, re-entry of a condition notifies immediately, and only real deliveries start
/// a cooldown (a failed dispatch retries next run instead of being swallowed).
/// </summary>
public class AlertGatekeeperTests
{
    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(20);

    private static PortfolioAlert Price() => new()
    {
        Kind = AlertKind.PlannedBuy,
        Severity = AlertSeverity.Info,
        Symbol = "SSE:600000",
        Title = "触及计划买入价",
        Message = "现价 9.8，已到买入价 10。",
    };

    private static PortfolioAlert Deadline() => new()
    {
        Kind = AlertKind.IpoDeadline,
        Severity = AlertSeverity.Warning,
        Symbol = "SSE:601000",
        Title = "中签待缴款",
        Message = "已中签 500 股。",
    };

    [Fact]
    public void ANewAlertIsSent()
    {
        var decision = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);

        Assert.Single(decision.ToSend);
        Assert.Empty(decision.Suppressed);
    }

    [Fact]
    public void TheSameEventStaysQuietWithinTheCooldownAfterDelivery()
    {
        var sent = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);
        var state = AlertGatekeeper.MarkDelivered(sent.NextState, sent.ToSend, Monday);

        // Next scheduled run, still firing, still within the cooldown.
        var again = AlertGatekeeper.Filter([Price()], state, Monday.AddHours(6), Cooldown);

        Assert.Empty(again.ToSend);
        Assert.Single(again.Suppressed);
    }

    [Fact]
    public void AfterTheCooldownExpiresTheEventSpeaksAgain()
    {
        var sent = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);
        var state = AlertGatekeeper.MarkDelivered(sent.NextState, sent.ToSend, Monday);

        var later = AlertGatekeeper.Filter([Price()], state, Monday.AddHours(21), Cooldown);

        Assert.Single(later.ToSend);
    }

    [Fact]
    public void ReEntryOfAConditionNotifiesImmediatelyEvenWithinTheCooldown()
    {
        var sent = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);
        var state = AlertGatekeeper.MarkDelivered(sent.NextState, sent.ToSend, Monday);

        // The condition goes away (price moved back up)…
        var gone = AlertGatekeeper.Filter([], state, Monday.AddHours(2), Cooldown);
        // …and comes back well inside the cooldown. That is a new event, not a nag.
        var back = AlertGatekeeper.Filter([Price()], gone.NextState, Monday.AddHours(4), Cooldown);

        Assert.Single(back.ToSend);
    }

    [Fact]
    public void AConditionThatNeverLeftDoesNotCountAsReEntry()
    {
        var sent = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);
        var state = AlertGatekeeper.MarkDelivered(sent.NextState, sent.ToSend, Monday);

        // Firing continuously — still suppressed until the cooldown expires.
        var still = AlertGatekeeper.Filter([Price()], state, Monday.AddHours(4), Cooldown);

        Assert.Empty(still.ToSend);
    }

    [Fact]
    public void UndeliveredAlertsDoNotStartACooldownSoTheyRetry()
    {
        var sent = AlertGatekeeper.Filter([Deadline()], AlertNotificationState.Empty, Monday, Cooldown);
        // The dispatch failed, so MarkDelivered is never called.
        var state = sent.NextState;

        var retry = AlertGatekeeper.Filter([Deadline()], state, Monday.AddHours(1), Cooldown);

        Assert.Single(retry.ToSend);
    }

    [Fact]
    public void TwoAlertsWithTheSameIdentityButDifferentPricesAreOneEvent()
    {
        // The live price lives in the message; the identity is kind/symbol/title. Otherwise
        // every run would look like a brand-new alert and dedup would never engage.
        var first = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);
        var state = AlertGatekeeper.MarkDelivered(first.NextState, first.ToSend, Monday);

        var drifted = Price() with { Message = "现价 9.6，已到买入价 10。" };
        var second = AlertGatekeeper.Filter([drifted], state, Monday.AddHours(3), Cooldown);

        Assert.Empty(second.ToSend);
    }

    [Fact]
    public void StaleRecordsArePrunedSoTheStateFileCannotGrowForever()
    {
        var delivered = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);
        var state = AlertGatekeeper.MarkDelivered(
            delivered.NextState, delivered.ToSend, Monday);

        var pruned = state.Pruned(Monday.AddDays(31));

        Assert.Empty(pruned.Records);
    }

    [Fact]
    public void TheStateStoreSurvivesAWriteAndReadRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alert-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AlertNotificationStateStore(path);
            var delivered = AlertGatekeeper.Filter([Price()], AlertNotificationState.Empty, Monday, Cooldown);
            store.Save(AlertGatekeeper.MarkDelivered(
                delivered.NextState, delivered.ToSend, Monday));

            var loaded = store.Load();

            Assert.Single(loaded.Records);
            Assert.Equal(Monday, loaded.Records[AlertGatekeeper.KeyOf(Price())].LastNotifiedAt);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void ACorruptStateFileReadsAsEmptyNotAsACrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alert-state-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json at all");
            var store = new AlertNotificationStateStore(path);

            Assert.Empty(store.Load().Records);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Layer separation, scope honesty, and dedup end-to-end in the headless runner.</summary>
public class HeadlessNotifierLayerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_headless_layers", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Today = new(2026, 8, 1);
    private static readonly DateTimeOffset Morning = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

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

    private static PortfolioAlert PriceAlert() => new()
    {
        Kind = AlertKind.Concentration,
        Severity = AlertSeverity.Info,
        Symbol = "SSE:600000",
        Title = "集中度：SSE:600000",
        Message = "单一持仓 25%，超过你设的 20%。",
    };

    [Fact]
    public async Task ALayerTwoFailureNeverBlocksTheDeadlineLayer()
    {
        var store = StoreWithAllottedIpo();
        var settings = NotificationSettings.Default with { BackgroundExtendedChecks = true };

        var result = await HeadlessNotifier.RunAsync(
            store,
            settings,
            Today,
            extendedCheck: _ => throw new HttpRequestException("连接超时"));

        // The deadline is still there, the failure is reported, not hidden.
        Assert.Contains(result.Alerts, a => a.Kind == AlertKind.IpoDeadline);
        Assert.Contains(result.Limitations, l => l.Contains("扩展检查失败", StringComparison.Ordinal));
        Assert.Contains("打新时限提醒不受影响", result.Summarize(), StringComparison.Ordinal);
        Assert.Equal("打新时限", result.CheckedScope);
    }

    [Fact]
    public async Task ASuccessfulExtendedRunWidensTheScopeAndMergesAlerts()
    {
        var store = StoreWithAllottedIpo();
        var settings = NotificationSettings.Default with { BackgroundExtendedChecks = true };
        // The extended run also re-derives the IPO deadline (same evaluator) — it must not
        // appear twice in the merged list.
        var extendedAlerts = new List<PortfolioAlert>
        {
            PriceAlert(),
            new PortfolioAlert
            {
                Kind = AlertKind.IpoDeadline,
                Severity = AlertSeverity.Warning,
                Symbol = "SSE:601000",
                Title = "中签待缴款",
                Message = "已中签 500 股。",
            },
        };

        var result = await HeadlessNotifier.RunAsync(
            store,
            settings,
            Today,
            extendedCheck: _ => Task.FromResult(new BackgroundCheckResult { Alerts = extendedAlerts }));

        Assert.Equal(2, result.Alerts.Count);
        Assert.Contains("价格与集中度", result.CheckedScope, StringComparison.Ordinal);
        Assert.Equal(Today, result.ExtendedDataAsOf);
        Assert.DoesNotContain(result.Limitations, l => l.Contains("扩展检查失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheSwitchTheExtendedCheckIsNotEvenAttempted()
    {
        var store = StoreWithAllottedIpo();
        var attempted = false;

        var result = await HeadlessNotifier.RunAsync(
            store,
            NotificationSettings.Default with { BackgroundExtendedChecks = false },
            Today,
            extendedCheck: _ =>
            {
                attempted = true;
                return Task.FromResult(new BackgroundCheckResult { Alerts = [] });
            });

        Assert.False(attempted);
        Assert.Equal("打新时限", result.CheckedScope);
    }

    [Fact]
    public async Task TheSameDeadlineIsOnlySentOncePerCooldown()
    {
        var store = StoreWithAllottedIpo();
        var settings = NotificationSettings.Default with { AlertCooldownHours = 20 };
        var stateStore = new AlertNotificationStateStore(
            Path.Combine(_root, "alert-state.json"));

        var first = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning);
        var second = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning.AddHours(4));

        // Without channels nothing was delivered, so no cooldown started either: the second
        // run still wants to send the deadline (it will, once a channel exists).
        Assert.Single(first.Alerts);
        Assert.Equal(0, first.SuppressedByCooldown);
        Assert.Equal(0, second.SuppressedByCooldown);
    }

    [Fact]
    public async Task DeliveredAlertsGoQuietAndUndeliveredOnesDoNot()
    {
        var store = StoreWithAllottedIpo();
        var settings = NotificationSettings.Default with { AlertCooldownHours = 20 };
        var stateStore = new AlertNotificationStateStore(
            Path.Combine(_root, "alert-state.json"));

        // No channel configured: nothing can be delivered, the alert stays due.
        var first = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning);
        Assert.False(first.Dispatch.AnyDelivered);

        var second = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning.AddHours(1));
        Assert.Equal(0, second.SuppressedByCooldown);
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
        }
    }
}

/// <summary>What leaves the machine: no user notes, no credentials, no local paths (plan §8).</summary>
public class OutgoingMessageHygieneTests
{
    [Fact]
    public void UserNotesAttachedToPlannedLevelsAreStripped()
    {
        var message = "SSE:600000 现价 9.8，已到你设定的买入价 10。（备注：老婆账户的仓位）";

        var outgoing = NotificationDispatcher.OutgoingText(message);

        Assert.DoesNotContain("老婆账户", outgoing, StringComparison.Ordinal);
        Assert.Contains("买入价", outgoing, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsAndLocalPathsAreRedacted()
    {
        var message = "取价失败：password=hunter2，缓存位于 C:\\Users\\me\\AppData\\Local\\Caishenfolio。";

        var outgoing = NotificationDispatcher.OutgoingText(message);

        Assert.DoesNotContain("hunter2", outgoing, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", outgoing, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outgoing, StringComparison.Ordinal);
        Assert.Contains("[路径已隐去]", outgoing, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuiltMessageCarriesNoNoteSuffix()
    {
        var alert = new PortfolioAlert
        {
            Kind = AlertKind.PlannedSell,
            Severity = AlertSeverity.Info,
            Symbol = "SSE:600000",
            Title = "触及计划卖出价",
            Message = "现价 12，已到你设定的卖出价 12。（备注：止盈线）",
        };

        var message = NotificationDispatcher.BuildMessage([alert]);

        Assert.DoesNotContain("止盈线", message.Body, StringComparison.Ordinal);
        Assert.Contains("卖出价", message.Body, StringComparison.Ordinal);
        // The non-negotiable footer survives.
        Assert.Contains("非投资建议", message.Body, StringComparison.Ordinal);
    }
}

/// <summary>The new settings fields persist through the encrypted JSON round-trip.</summary>
public class BackgroundCheckSettingsTests
{
    [Fact]
    public void ExtendedChecksAndCooldownRoundTrip()
    {
        var settings = NotificationSettings.Default with
        {
            BackgroundExtendedChecks = true,
            AlertCooldownHours = 6,
        };

        var restored = NotificationSettings.FromJson(settings.ToJson());

        Assert.True(restored.BackgroundExtendedChecks);
        Assert.Equal(6, restored.AlertCooldownHours);
    }

    [Fact]
    public void DefaultsAreOffAndTwentyHours()
    {
        var defaults = NotificationSettings.Default;

        Assert.False(defaults.BackgroundExtendedChecks);
        Assert.Equal(20, defaults.AlertCooldownHours);
    }
}
