using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// Market-calendar and market-clock semantics (plan §8: 市场时区和节假日语义): the fixed
/// holidays that ARE modelled keep a quote from looking stale through a holiday week, and
/// "today" is each market's own date, not the machine's.
/// </summary>
public class MarketCalendarTests
{
    [Fact]
    public void ZoneOfMapsExchangesToTheirHolidayZone()
    {
        Assert.Equal(MarketHolidayZone.CN, MarketCalendar.ZoneOf("SSE:600000"));
        Assert.Equal(MarketHolidayZone.CN, MarketCalendar.ZoneOf("SZSE:000001"));
        Assert.Equal(MarketHolidayZone.CN, MarketCalendar.ZoneOf("FUND:110022"));
        Assert.Equal(MarketHolidayZone.HK, MarketCalendar.ZoneOf("HKEX:00700"));
        Assert.Equal(MarketHolidayZone.US, MarketCalendar.ZoneOf("NASDAQ:AAPL"));
        Assert.Equal(MarketHolidayZone.JP, MarketCalendar.ZoneOf("TSE:7203"));
        Assert.Equal(MarketHolidayZone.Other, MarketCalendar.ZoneOf("FX:USDCNY"));
        Assert.Equal(MarketHolidayZone.Other, MarketCalendar.ZoneOf(""));
    }

    [Fact]
    public void WeekendsNeverCount()
    {
        // Fri 2026-07-31 → Mon 2026-08-03: one trading day.
        Assert.Equal(
            1,
            MarketCalendar.TradingDaysBetween(
                new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 3), MarketHolidayZone.CN));
    }

    [Fact]
    public void TheModelledNationalDayWeekIsNotStaleness()
    {
        // Wed 2026-09-30 → Fri 2026-10-09: 7 weekdays, 5 of them inside the modelled
        // 10-01..10-07 block → 2 trading days.
        Assert.Equal(
            2,
            MarketCalendar.TradingDaysBetween(
                new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 9), MarketHolidayZone.CN));
    }

    [Fact]
    public void NewYearCountsInEveryZone()
    {
        // Wed 2025-12-31 → Fri 2026-01-02: 2 weekdays, 1/1 is a holiday → 1 trading day.
        foreach (var zone in Enum.GetValues<MarketHolidayZone>())
        {
            Assert.Equal(
                1,
                MarketCalendar.TradingDaysBetween(
                    new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 2), zone));
        }
    }

    [Fact]
    public void AZoneUnawareSymbolCountsPlainWeekdays()
    {
        // Same span as the National Day test, but an unknown venue: 7 weekdays stay 7.
        Assert.Equal(
            7,
            MarketCalendar.TradingDaysBetween(
                new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 9), MarketHolidayZone.Other));
    }

    [Fact]
    public void EachMarketSeesItsOwnToday()
    {
        // 2026-08-04 00:30 in Beijing (UTC+8) is 2026-08-03 12:30 in New York (UTC-4).
        var beijingSmallHours = new DateTime(2026, 8, 3, 16, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 8, 4), MarketClock.DateIn(MarketHolidayZone.CN, beijingSmallHours));
        Assert.Equal(new DateOnly(2026, 8, 3), MarketClock.DateIn(MarketHolidayZone.US, beijingSmallHours));
        // Tokyo is UTC+9: still the 4th.
        Assert.Equal(new DateOnly(2026, 8, 4), MarketClock.DateIn(MarketHolidayZone.JP, beijingSmallHours));
    }
}

/// <summary>
/// Plan §8's explicit failure-mode list, each with a test named after it: rate limiting,
/// a run missed while the machine slept, and a task firing twice in quick succession.
/// </summary>
public class BackgroundFailureModeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_failure_modes", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task ARateLimitedCoreStillLeavesTheDeadlineLayerIntact()
    {
        var result = await HeadlessNotifier.RunAsync(
            StoreWithAllottedIpo(),
            NotificationSettings.Default with { BackgroundExtendedChecks = true },
            Today,
            extendedCheck: _ => throw new HttpRequestException("响应 429 (Too Many Requests)"));

        Assert.Contains(result.Alerts, a => a.Kind == AlertKind.IpoDeadline);
        Assert.Contains(result.Limitations, l => l.Contains("429", StringComparison.Ordinal));
        Assert.Contains("打新时限提醒不受影响", result.Summarize(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunMissedWhileTheMachineSleptSpeaksAgainOnTheNextWake()
    {
        var store = StoreWithAllottedIpo();
        var settings = NotificationSettings.Default with { AlertCooldownHours = 20 };
        var stateStore = new AlertNotificationStateStore(Path.Combine(_root, "state.json"));

        // Delivered on Monday 9:00; the machine then slept through every scheduled run
        // until Wednesday — far past the cooldown.
        var monday = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning);
        var wednesday = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning.AddDays(2));

        Assert.Equal(0, monday.SuppressedByCooldown);
        Assert.Equal(0, wednesday.SuppressedByCooldown);
    }

    [Fact]
    public async Task ATaskFiringTwiceInQuickSuccessionDoesNotRepeatItself()
    {
        var store = StoreWithAllottedIpo();
        var settings = NotificationSettings.Default with { AlertCooldownHours = 20 };
        var stateStore = new AlertNotificationStateStore(Path.Combine(_root, "state.json"));

        var first = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning);
        // Mark delivered, then a duplicate wake 4 hours later while the task scheduler
        // retried or the machine woke twice.
        var state = AlertGatekeeper.MarkDelivered(
            AlertGatekeeper.Filter(
                first.Alerts, stateStore.Load(), Morning, TimeSpan.FromHours(20)).NextState,
            first.Alerts,
            Morning);
        stateStore.Save(state);
        var second = await HeadlessNotifier.RunAsync(
            store, settings, Today, stateStore: stateStore, now: Morning.AddHours(4));

        Assert.Equal(1, second.SuppressedByCooldown);
        Assert.Equal(0, second.Dispatch.AlertCount);
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
