using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class PortfolioPricingServiceTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    private sealed class FakeSource : IMarketPricingSource
    {
        public Dictionary<string, PriceQuote> Quotes { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string, string), FxRate> Rates { get; } = new();
        public List<string> QuoteCalls { get; } = [];
        public List<(string From, string To)> RateCalls { get; } = [];
        public HashSet<string> ThrowingSymbols { get; } = new(StringComparer.Ordinal);

        public Task<PriceQuote?> TryGetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
        {
            QuoteCalls.Add(symbol);
            if (ThrowingSymbols.Contains(symbol))
            {
                throw new HttpRequestException("core unreachable");
            }

            return Task.FromResult(Quotes.TryGetValue(symbol, out var quote) ? quote : null);
        }

        public Task<FxRate?> TryGetFxRateAsync(
            string baseCurrency, string quoteCurrency, CancellationToken cancellationToken = default)
        {
            RateCalls.Add((baseCurrency, quoteCurrency));
            return Task.FromResult(Rates.TryGetValue((baseCurrency, quoteCurrency), out var rate) ? rate : null);
        }
    }

    private static LedgerState Ledger() => PositionCalculator.Replay(
    [
        LedgerTransaction.OpeningCash("acct", Day1, 5_000m, "CNY"),
        LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"),
        LedgerTransaction.OpeningPosition("acct", "NASDAQ:AAPL", Day1, 10m, 180m, "USD"),
    ]);

    [Fact]
    public async Task FetchesOneQuotePerOpenPositionAndTheRatesTheyNeed()
    {
        var source = new FakeSource();
        source.Quotes["SSE:600000"] = PriceQuote.Of("SSE:600000", 12m, "CNY", AsOf);
        source.Quotes["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 200m, "USD", AsOf);
        source.Rates[("USD", "CNY")] = FxRate.Of("USD", "CNY", 7.2m, AsOf);

        var snapshot = await new PortfolioPricingService(source).FetchAsync(Ledger(), "CNY", AsOf);

        Assert.Equal(2, snapshot.Quotes.Count);
        Assert.Empty(snapshot.Warnings);
        // CNY is the base currency, so no rate is requested for it.
        Assert.Equal([("USD", "CNY")], source.RateCalls);
        Assert.True(snapshot.Fx.TryGetRate("USD", "CNY", out var rate));
        Assert.Equal(7.2m, rate);
    }

    [Fact]
    public async Task FallsBackToThePivotLegWhenTheDirectPairIsMissing()
    {
        var source = new FakeSource();
        source.Quotes["SSE:600000"] = PriceQuote.Of("SSE:600000", 12m, "CNY", AsOf);
        source.Quotes["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 200m, "USD", AsOf);
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
        ]);
        source.Quotes["TSE:7203"] = PriceQuote.Of("TSE:7203", 3000m, "JPY", AsOf);
        // Only the USD legs exist; JPY -> CNY has to be triangulated.
        source.Rates[("JPY", "USD")] = FxRate.Of("JPY", "USD", 1m / 150m, AsOf);
        source.Rates[("USD", "CNY")] = FxRate.Of("USD", "CNY", 7.2m, AsOf);

        var snapshot = await new PortfolioPricingService(source).FetchAsync(ledger, "CNY", AsOf);

        Assert.Contains(("JPY", "CNY"), source.RateCalls);
        Assert.Contains(("JPY", "USD"), source.RateCalls);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public async Task MissingQuoteBecomesAWarningNotAnException()
    {
        var source = new FakeSource();
        source.Quotes["SSE:600000"] = PriceQuote.Of("SSE:600000", 12m, "CNY", AsOf);
        source.Rates[("USD", "CNY")] = FxRate.Of("USD", "CNY", 7.2m, AsOf);

        var snapshot = await new PortfolioPricingService(source).FetchAsync(Ledger(), "CNY", AsOf);

        Assert.Single(snapshot.Quotes);
        Assert.Contains(snapshot.Warnings, w => w.Contains("NASDAQ:AAPL"));

        // The valuation that follows is incomplete, not wrong.
        var valuation = ValuationEngine.Value(Ledger(), snapshot.Quotes, snapshot.Fx, "CNY", AsOf);
        Assert.False(valuation.IsComplete);
        Assert.Equal(12_000m, valuation.HoldingsValue.Amount);
    }

    [Fact]
    public async Task ATransportFailureOnOneSymbolDoesNotAbortTheRest()
    {
        var source = new FakeSource();
        source.ThrowingSymbols.Add("SSE:600000");
        source.Quotes["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 200m, "USD", AsOf);
        source.Rates[("USD", "CNY")] = FxRate.Of("USD", "CNY", 7.2m, AsOf);

        var snapshot = await new PortfolioPricingService(source).FetchAsync(Ledger(), "CNY", AsOf);

        Assert.Single(snapshot.Quotes);
        Assert.Contains(snapshot.Warnings, w => w.Contains("SSE:600000") && w.Contains("取价失败"));
    }

    [Fact]
    public async Task MissingRateIsReportedAndLeavesTheHoldingUnpriced()
    {
        var source = new FakeSource();
        source.Quotes["SSE:600000"] = PriceQuote.Of("SSE:600000", 12m, "CNY", AsOf);
        source.Quotes["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 200m, "USD", AsOf);

        var snapshot = await new PortfolioPricingService(source).FetchAsync(Ledger(), "CNY", AsOf);

        Assert.Contains(snapshot.Warnings, w => w.Contains("USD") && w.Contains("汇率"));
        Assert.False(snapshot.Fx.TryGetRate("USD", "CNY", out _));
    }

    [Fact]
    public async Task ClosedPositionsAreNotPriced()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Buy("acct", "SSE:600000", Day1, 100m, 10m, "CNY"),
            LedgerTransaction.Sell("acct", "SSE:600000", Day1.AddDays(30), 100m, 12m, "CNY"),
        ]);
        var source = new FakeSource();

        var snapshot = await new PortfolioPricingService(source).FetchAsync(ledger, "CNY", AsOf);

        Assert.Empty(source.QuoteCalls);
        Assert.Empty(snapshot.Warnings);
    }
}
