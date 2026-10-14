using System.Globalization;

namespace Caishenfolio.Host.Portfolio;

public enum MarketHolidayZone
{
    /// <summary>A-shares and CN off-exchange funds (SSE/SZSE/FUND).</summary>
    CN,
    HK,
    US,
    JP,
    /// <summary>Unknown venue: weekends only, no holiday model.</summary>
    Other,
}

/// <summary>
/// Trading-calendar semantics for price staleness (plan §8: 市场时区和节假日语义).
///
/// Scope, stated honestly: weekends plus the *fixed-date* market holidays that recur on the
/// same date every year (New Year, CN National Day block, US Thanksgiving/Christmas, …).
/// Lunar or movable holidays (春节、清明、复活节、日本敬老节替换日等) are NOT modelled —
/// around those windows the trading-day age can over-count, and the alert message keeps
/// saying "约 N 个交易日" so the approximation is visible rather than implied precision.
/// </summary>
public static class MarketCalendar
{
    /// <summary>Fixed-date market holidays shared by every zone we model.</summary>
    private static readonly HashSet<DateOnly> CommonHolidays =
    [
        // New Year
        new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1),
    ];

    private static readonly Dictionary<MarketHolidayZone, HashSet<DateOnly>> ZoneHolidays = new()
    {
        [MarketHolidayZone.CN] =
        [
            // 劳动节 statutory block (fixed by regulation) and 国庆节 statutory block.
            // The surrounding swapped weekend workdays do not matter here: they are
            // weekends, which never counted anyway.
            new DateOnly(2025, 5, 1), new DateOnly(2025, 5, 2), new DateOnly(2025, 5, 5),
            new DateOnly(2026, 5, 1), new DateOnly(2027, 5, 3),
            new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 2), new DateOnly(2025, 10, 3),
            new DateOnly(2025, 10, 6), new DateOnly(2025, 10, 7),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7),
            new DateOnly(2027, 10, 1), new DateOnly(2027, 10, 4), new DateOnly(2027, 10, 5),
        ],
        [MarketHolidayZone.US] =
        [
            // Fixed-date US market holidays. Movable ones (Good Friday, Memorial Day, Labor
            // Day) are skipped on purpose — see the class doc.
            new DateOnly(2025, 12, 25), new DateOnly(2026, 12, 25), new DateOnly(2027, 12, 25),
            new DateOnly(2025, 11, 27), new DateOnly(2026, 11, 26), new DateOnly(2027, 11, 25),
            new DateOnly(2025, 7, 4), new DateOnly(2026, 7, 3), new DateOnly(2027, 7, 5),
        ],
        [MarketHolidayZone.JP] =
        [
            // New Year break for TSE is Jan 1–3.
            new DateOnly(2025, 1, 2), new DateOnly(2025, 1, 3),
            new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3),
            new DateOnly(2027, 1, 2), new DateOnly(2027, 1, 3),
        ],
        [MarketHolidayZone.HK] =
        [
            new DateOnly(2025, 12, 25), new DateOnly(2026, 12, 25), new DateOnly(2027, 12, 25),
            new DateOnly(2025, 12, 26), new DateOnly(2026, 12, 26),
        ],
        [MarketHolidayZone.Other] = [],
    };

    /// <summary>The holiday zone a symbol's exchange belongs to.</summary>
    public static MarketHolidayZone ZoneOf(string symbol)
    {
        var prefix = (symbol ?? "").Split(':')[0].Trim().ToUpperInvariant();
        return prefix switch
        {
            "SSE" or "SZSE" or "BSE" or "FUND" => MarketHolidayZone.CN,
            "HKEX" => MarketHolidayZone.HK,
            "NASDAQ" or "NYSE" or "AMEX" => MarketHolidayZone.US,
            "TSE" => MarketHolidayZone.JP,
            _ => MarketHolidayZone.Other,
        };
    }

    /// <summary>Weekdays strictly between the dates, minus that zone's modelled holidays.</summary>
    public static int TradingDaysBetween(DateOnly from, DateOnly to, MarketHolidayZone zone)
    {
        if (to <= from)
        {
            return 0;
        }

        var holidays = zone == MarketHolidayZone.Other
            ? CommonHolidays
            : [.. CommonHolidays.Concat(ZoneHolidays[zone])];

        var age = 0;
        for (var day = from; day < to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            if (!holidays.Contains(day))
            {
                age++;
            }
        }

        return age;
    }
}

/// <summary>
/// "Today" as each market's own clock sees it (plan §8). A US stock's freshness cannot be
/// judged by Beijing's calendar date: while it is the 4th here, it may still be the 3rd in
/// New York, and yesterday's close is the freshest quote that can exist.
/// </summary>
public static class MarketClock
{
    private static readonly Dictionary<MarketHolidayZone, string> ZoneTimezoneIds = new()
    {
        [MarketHolidayZone.CN] = "China Standard Time",
        [MarketHolidayZone.HK] = "Hong Kong Standard Time",
        [MarketHolidayZone.US] = "Eastern Standard Time",
        [MarketHolidayZone.JP] = "Tokyo Standard Time",
        // No exchange → the machine's own local date.
        [MarketHolidayZone.Other] = TimeZoneInfo.Local.Id,
    };

    /// <summary>The calendar date inside the zone, at one explicit UTC instant.</summary>
    public static DateOnly DateIn(MarketHolidayZone zone, DateTime utcNow)
    {
        var id = ZoneTimezoneIds[zone];
        var zoneInfo = TimeZoneInfo.FindSystemTimeZoneById(id);
        var local = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zoneInfo);
        return DateOnly.FromDateTime(local);
    }

    /// <summary>The calendar date inside the zone right now.</summary>
    public static DateOnly TodayOf(MarketHolidayZone zone) => DateIn(zone, DateTime.UtcNow);
}
