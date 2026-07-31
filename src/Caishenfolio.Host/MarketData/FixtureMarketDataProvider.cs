using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.MarketData;

public sealed class FixtureMarketDataProvider
{
    public const string ProviderCode = "fixture";

    private static readonly IReadOnlyList<SymbolSeed> Universe =
    [
        new("SSE:600000", MarketRegion.Cn, AssetClass.Equity, "浦发银行", "CNY", 10.0m),
        new("SZSE:000001", MarketRegion.Cn, AssetClass.Equity, "平安银行", "CNY", 12.5m),
        new("SSE:510300", MarketRegion.Cn, AssetClass.Etf, "沪深300ETF", "CNY", 4.2m),
        new("SSE:113050", MarketRegion.Cn, AssetClass.ConvertibleBond, "南银转债", "CNY", 118.0m),
        new("FUND:110022", MarketRegion.Cn, AssetClass.MutualFund, "易方达消费行业", "CNY", 3.5m),
        new("HKEX:00700", MarketRegion.Hk, AssetClass.Equity, "Tencent", "HKD", 320.0m),
        new("NASDAQ:AAPL", MarketRegion.Us, AssetClass.Equity, "Apple", "USD", 180.0m),
        new("NYSE:SPY", MarketRegion.Us, AssetClass.Etf, "SPDR S&P 500", "USD", 450.0m),
        new("TSE:7203", MarketRegion.Jp, AssetClass.Equity, "トヨタ自動車", "JPY", 2800.0m),
        new("FX:USDCNY", MarketRegion.Global, AssetClass.Fx, "美元/人民币", "CNY", 7.2m),
    ];

    public IReadOnlyList<SymbolHit> Search(string query, int limit = 10)
    {
        limit = Math.Clamp(limit, 1, 50);
        var q = (query ?? string.Empty).Trim();
        IEnumerable<SymbolSeed> matches = Universe;
        if (!string.IsNullOrEmpty(q))
        {
            matches = Universe.Where(item =>
                item.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return matches
            .Take(limit)
            .Select(item => new SymbolHit(item.Symbol, item.Market, item.AssetClass, item.Name, ProviderCode))
            .ToArray();
    }

    public ProviderResult<IReadOnlyList<OhlcvBar>> HistoricalBars(
        string symbol,
        DateOnly start,
        DateOnly end,
        Adjustment adjustment = Adjustment.Raw)
    {
        if (!SymbolId.TryParse(symbol, out var symbolId))
        {
            return ProviderResult<IReadOnlyList<OhlcvBar>>.Failure(
                ProviderCode,
                $"Invalid symbol '{symbol}'. Expected EXCHANGE:SYMBOL.");
        }

        if (end < start)
        {
            return ProviderResult<IReadOnlyList<OhlcvBar>>.Failure(
                ProviderCode,
                "end date must be on or after start date.");
        }

        var seed = Universe.FirstOrDefault(item =>
            string.Equals(item.Symbol, symbolId.Value, StringComparison.OrdinalIgnoreCase));
        if (seed is null)
        {
            return ProviderResult<IReadOnlyList<OhlcvBar>>.Failure(
                ProviderCode,
                $"Symbol '{symbolId.Value}' is not in the fixture universe.",
                warnings: new[] { "fail_closed" });
        }

        var bars = new List<OhlcvBar>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var offset = day.DayNumber - start.DayNumber;
            var close = seed.BaseClose + offset * 0.15m;
            var open = close - 0.05m;
            var high = close + 0.20m;
            var low = close - 0.25m;
            bars.Add(new OhlcvBar(
                new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                open,
                high,
                low,
                close,
                Volume: 1_000_000 + offset * 1000,
                Currency: seed.Currency,
                Adjustment: adjustment,
                Provider: ProviderCode,
                Amount: close * (1_000_000 + offset * 1000),
                Provenance: new Dictionary<string, string>
                {
                    ["source"] = ProviderCode,
                    ["symbol"] = seed.Symbol,
                    ["synthetic"] = "true",
                }));
        }

        var warnings = new List<string> { "fixture_synthetic_data", "not_for_investment_decisions" };
        if (adjustment == Adjustment.Unknown)
        {
            warnings.Add("adjustment_unknown");
        }

        return ProviderResult<IReadOnlyList<OhlcvBar>>.Success(ProviderCode, bars, warnings);
    }

    private sealed record SymbolSeed(
        string Symbol,
        MarketRegion Market,
        AssetClass AssetClass,
        string Name,
        string Currency,
        decimal BaseClose);

    public sealed record SymbolHit(
        string Symbol,
        MarketRegion Market,
        AssetClass AssetClass,
        string Name,
        string Provider);
}
