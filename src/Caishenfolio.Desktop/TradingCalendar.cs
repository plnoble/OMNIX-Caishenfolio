namespace Caishenfolio.Desktop;

/// <summary>
/// Lightweight trading-day helpers for default chart ranges (not a full exchange calendar).
/// </summary>
public static class TradingCalendar
{
    /// <summary>Last weekday on or before <paramref name="from"/> (UTC date semantics local-friendly).</summary>
    public static DateOnly LastWeekdayOnOrBefore(DateOnly from)
    {
        var d = from;
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            d = d.AddDays(-1);
        }

        return d;
    }

    public static DateOnly TodayLocal() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// Recommended visible window by bar interval (industry-like defaults).
    /// </summary>
    public static (DateOnly Start, DateOnly End) DefaultRange(string interval)
    {
        var end = LastWeekdayOnOrBefore(TodayLocal());
        var start = interval switch
        {
            "1m" or "5m" or "15m" or "30m" or "60m" => end.AddDays(-5),
            "weekly" => end.AddYears(-4),
            "monthly" => end.AddYears(-10),
            "quarterly" => end.AddYears(-12),
            "yearly" => end.AddYears(-20),
            _ => end.AddMonths(-9),
        };
        return (start, end);
    }

    public static string Format(DateOnly d) => d.ToString("yyyy-MM-dd");
}
