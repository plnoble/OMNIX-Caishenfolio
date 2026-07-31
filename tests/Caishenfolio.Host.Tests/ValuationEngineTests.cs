using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class ValuationEngineTests
{
    private const string Account = "acct_main";
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);
    private static readonly Dictionary<string, PriceQuote> NoQuotes = [];

    /// <summary>USD/CNY 7.2 and USD/JPY 150 — JPY→CNY has to triangulate through USD.</summary>
    private static FxConverter Rates() => new(
    [
        FxRate.Of("USD", "CNY", 7.2m, Day1),
        FxRate.Of("USD", "JPY", 150m, Day1),
        FxRate.Of("USD", "HKD", 7.8m, Day1),
    ]);

    private static LedgerState MultiMarketLedger() => PositionCalculator.Replay(
    [
        LedgerTransaction.OpeningCash(Account, Day1, 5_000m, "CNY"),
        LedgerTransaction.OpeningPosition(Account, "SSE:600000", Day1, 1000m, 10.005m, "CNY"),
        LedgerTransaction.OpeningPosition(Account, "NASDAQ:AAPL", Day1, 10m, 180m, "USD"),
        LedgerTransaction.OpeningPosition(Account, "TSE:7203", Day1, 100m, 2800m, "JPY"),
    ]);

    private static Dictionary<string, PriceQuote> Quotes() => new()
    {
        ["SSE:600000"] = PriceQuote.Of("SSE:600000", 12m, "CNY", AsOf),
        ["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 200m, "USD", AsOf),
        ["TSE:7203"] = PriceQuote.Of("TSE:7203", 3000m, "JPY", AsOf),
    };

    [Fact]
    public void ValuesAMultiCurrencyPortfolioInTheBaseCurrency()
    {
        var valuation = ValuationEngine.Value(MultiMarketLedger(), Quotes(), Rates(), "CNY", AsOf);

        Assert.True(valuation.IsComplete);
        // 12 000 CNY + 2 000 USD×7.2 + 300 000 JPY×0.048 = 12 000 + 14 400 + 14 400
        Assert.Equal(40_800m, valuation.HoldingsValue.Amount);
        Assert.Equal(5_000m, valuation.CashValue.Amount);
        Assert.Equal(45_800m, valuation.TotalValue.Amount);
        // 10 005 + 1 800×7.2 + 280 000×0.048
        Assert.Equal(36_405m, valuation.CostBasis.Amount);
        Assert.Equal(4_395m, valuation.UnrealizedPnl.Amount);
        Assert.Equal("CNY", valuation.TotalValue.Currency);
    }

    [Fact]
    public void ReportsUnrealizedPnlPerPositionInBothCurrencies()
    {
        var valuation = ValuationEngine.Value(MultiMarketLedger(), Quotes(), Rates(), "CNY", AsOf);

        var apple = valuation.Positions.Single(p => p.Position.Symbol == "NASDAQ:AAPL");
        Assert.Equal(2_000m, apple.MarketValue!.Value.Amount);
        Assert.Equal("USD", apple.MarketValue!.Value.Currency);
        Assert.Equal(200m, apple.UnrealizedPnl!.Value.Amount);
        Assert.Equal(14_400m, apple.MarketValueBase!.Value.Amount);
        Assert.Equal(1_440m, apple.UnrealizedPnlBase!.Value.Amount);
    }

    [Fact]
    public void UnpricedHoldingIsFlaggedNotCountedAsZero()
    {
        var quotes = Quotes();
        quotes.Remove("TSE:7203");

        var valuation = ValuationEngine.Value(MultiMarketLedger(), quotes, Rates(), "CNY", AsOf);

        Assert.False(valuation.IsComplete);
        Assert.Contains(valuation.Warnings, w => w.Contains("TSE:7203") && w.Contains("缺少最新价格"));

        var toyota = valuation.Positions.Single(p => p.Position.Symbol == "TSE:7203");
        Assert.False(toyota.Priced);
        Assert.Null(toyota.MarketValueBase);
        Assert.Null(toyota.Weight);

        // The Japanese holding is absent from the total rather than valued at zero.
        Assert.Equal(26_400m, valuation.HoldingsValue.Amount);
    }

    [Fact]
    public void MissingRateIsReportedRatherThanGuessed()
    {
        var noJpy = new FxConverter([FxRate.Of("USD", "CNY", 7.2m, Day1)]);

        var valuation = ValuationEngine.Value(MultiMarketLedger(), Quotes(), noJpy, "CNY", AsOf);

        Assert.False(valuation.IsComplete);
        Assert.Contains(valuation.Warnings, w => w.Contains("JPY") && w.Contains("汇率"));
        Assert.False(valuation.Positions.Single(p => p.Position.Symbol == "TSE:7203").Priced);
    }

    [Fact]
    public void WeightsSumToOneAcrossPricedHoldingsAndCash()
    {
        var valuation = ValuationEngine.Value(MultiMarketLedger(), Quotes(), Rates(), "CNY", AsOf);

        var holdingWeights = valuation.Positions.Where(p => p.Weight is not null).Sum(p => p.Weight!.Value);
        var cashWeight = valuation.CashValue.Amount / valuation.TotalValue.Amount;

        Assert.Equal(1m, Math.Round(holdingWeights + cashWeight, 10));
        Assert.Equal(1m, Math.Round(valuation.ByRegion.Sum(s => s.Weight), 10));
    }

    [Fact]
    public void GroupsAllocationByRegionAssetClassAndAccount()
    {
        var valuation = ValuationEngine.Value(MultiMarketLedger(), Quotes(), Rates(), "CNY", AsOf);

        var cn = valuation.ByRegion.Single(s => s.Key == "cn");
        var us = valuation.ByRegion.Single(s => s.Key == "us");
        var jp = valuation.ByRegion.Single(s => s.Key == "jp");
        var cash = valuation.ByRegion.Single(s => s.Key == "cash");

        Assert.Equal(12_000m, cn.Amount());
        Assert.Equal(14_400m, us.Amount());
        Assert.Equal(14_400m, jp.Amount());
        Assert.Equal(5_000m, cash.Amount());
        Assert.Equal("日股", jp.Label);
        Assert.Equal("现金", cash.Label);

        Assert.Equal(45_800m, valuation.ByAccount.Single(s => s.Key == Account).Amount());
        Assert.Equal(40_800m, valuation.ByAssetClass.Single(s => s.Key == "equity").Amount());
    }

    [Fact]
    public void CurrencyExposureKeepsCashInItsOwnCurrency()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash(Account, Day1, 5_000m, "CNY"),
            LedgerTransaction.OpeningCash(Account, Day1, 1_000m, "USD"),
            LedgerTransaction.OpeningPosition(Account, "NASDAQ:AAPL", Day1, 10m, 180m, "USD"),
        ]);

        var valuation = ValuationEngine.Value(ledger, Quotes(), Rates(), "CNY", AsOf);

        // 1 000 USD cash + 2 000 USD of Apple, both converted at 7.2.
        Assert.Equal(21_600m, valuation.ByCurrency.Single(s => s.Key == "USD").Amount());
        Assert.Equal(5_000m, valuation.ByCurrency.Single(s => s.Key == "CNY").Amount());
        Assert.DoesNotContain(valuation.ByCurrency, s => s.Key == "cash");
    }

    [Fact]
    public void UsesInstrumentMetadataForAllocationWhenAvailable()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition(Account, "SSE:510300", Day1, 10_000m, 4m, "CNY"),
            LedgerTransaction.OpeningPosition(Account, "SSE:113050", Day1, 100m, 110m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:510300"] = PriceQuote.Of("SSE:510300", 4.2m, "CNY", AsOf),
            ["SSE:113050"] = PriceQuote.Of("SSE:113050", 118m, "CNY", AsOf),
        };
        var instruments = new Dictionary<string, Instrument>
        {
            ["SSE:510300"] = Instrument.FromSymbol("SSE:510300", "沪深300ETF", AssetClass.Etf),
            ["SSE:113050"] = Instrument.FromSymbol("SSE:113050", "南银转债", AssetClass.ConvertibleBond),
        };

        var valuation = ValuationEngine.Value(ledger, quotes, Rates(), "CNY", AsOf, instruments);

        Assert.Equal(42_000m, valuation.ByAssetClass.Single(s => s.Key == "etf").Amount());
        Assert.Equal(11_800m, valuation.ByAssetClass.Single(s => s.Key == "convertible_bond").Amount());
        Assert.Equal("可转债", valuation.ByAssetClass.Single(s => s.Key == "convertible_bond").Label);
    }

    [Fact]
    public void ClosedPositionsKeepRealizedResultsOutOfMarketValue()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit(Account, Day1, 20_000m, "CNY"),
            LedgerTransaction.Buy(Account, "SSE:600000", Day1, 1000m, 10m, "CNY"),
            LedgerTransaction.Dividend(Account, "SSE:600000", new DateOnly(2026, 6, 1), 300m, "CNY"),
            LedgerTransaction.Sell(Account, "SSE:600000", new DateOnly(2026, 7, 1), 1000m, 12m, "CNY"),
        ]);

        var valuation = ValuationEngine.Value(ledger, NoQuotes, FxConverter.Empty, "CNY", AsOf);

        Assert.True(valuation.IsComplete);
        Assert.Equal(0m, valuation.HoldingsValue.Amount);
        Assert.Equal(2_000m, valuation.RealizedPnl.Amount);
        Assert.Equal(300m, valuation.Dividends.Amount);
        Assert.Equal(2_300m, valuation.TotalPnl.Amount);
        Assert.Equal(22_300m, valuation.CashValue.Amount);
    }

    [Fact]
    public void EmptyLedgerValuesToZeroWithoutWarnings()
    {
        var valuation = ValuationEngine.Value(LedgerState.Empty, NoQuotes, FxConverter.Empty, "CNY", AsOf);

        Assert.True(valuation.IsComplete);
        Assert.True(valuation.TotalValue.IsZero);
        Assert.Empty(valuation.ByRegion);
    }
}

file static class SliceExtensions
{
    public static decimal Amount(this AllocationSlice slice) => slice.Value.Amount;
}
