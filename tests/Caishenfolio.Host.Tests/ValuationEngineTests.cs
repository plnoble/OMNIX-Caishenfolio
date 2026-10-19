using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class ValuationEngineTests
{
    private const string Account = "acct_main";
    private const string BalanceAccount = "acct_balance";
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);
    private static readonly Dictionary<string, PriceQuote> NoQuotes = [];

    /// <summary>One detail account and one balance-mode account, as the store would hand them over.</summary>
    private static Dictionary<string, Account> Accounts()
    {
        // The type is spelled out because this class has a member called Account (the account id).
        static Portfolio.Account New(string name, AccountKind kind, string id, AccountValuationMode mode) =>
            Portfolio.Account.Create(name, kind, "CNY", id: id, mode: mode);

        return new(StringComparer.Ordinal)
        {
            [Account] = New("主账户", AccountKind.Securities, Account, AccountValuationMode.Detail),
            [BalanceAccount] = New("余额账户", AccountKind.Bank, BalanceAccount, AccountValuationMode.Balance),
        };
    }

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

    /// <summary>
    /// A reported total speaks for one day only. Money recorded after that day has certainly moved
    /// the account — an internal move even lands in the receiving account while staying inside the
    /// sender's stale total, which is the silent double count R2 names — so the aggregate may not
    /// be presented as complete. The reported figure stays counted: it is still the last fact
    /// known, and dropping a whole account would replace a small error with a large one.
    /// </summary>
    [Fact]
    public void MoneyRecordedAfterABalanceReportMakesTheAggregateIncomplete()
    {
        var reportDate = new DateOnly(2026, 2, 28);
        var transferDate = new DateOnly(2026, 4, 10);
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit(Account, Day1, 50_000m, "CNY"),
            LedgerTransaction.Deposit(BalanceAccount, Day1, 100_000m, "CNY"),
            LedgerTransaction.BalanceAssertion(BalanceAccount, reportDate, 100_000m, "CNY"),
            LedgerTransaction.Transfer(BalanceAccount, Account, transferDate, 10_000m, "CNY"),
        ]);

        var valuation = ValuationEngine.Value(
            ledger, NoQuotes, FxConverter.Empty, "CNY", AsOf, instruments: null, accounts: Accounts());

        Assert.False(valuation.IsComplete);
        Assert.Contains(
            valuation.Warnings,
            w => w.Contains("余额账户", StringComparison.Ordinal)
                && w.Contains("2026-02-28", StringComparison.Ordinal)
                && w.Contains("2026-04-10", StringComparison.Ordinal));
        // Still counted at the reported figure, deliberately: see the summary on this test. The
        // 10 000 is therefore in the aggregate twice — A holds it as cash (60 000) and it is also
        // still inside B's report (100 000) — which is exactly why the warning must exist. The
        // truth is 150 000; a silent 160 000 would be the R2 failure mode.
        Assert.Equal(160_000m, valuation.TotalValue.Amount);
        var balanceAccount = valuation.AccountValues.Single(v => v.AccountId == BalanceAccount);
        Assert.True(balanceAccount.ValueKnown);
        Assert.Equal(100_000m, balanceAccount.TotalValue.Amount);
    }

    [Fact]
    public void ABalanceReportDoesNotPriceADayBeforeItWasMade()
    {
        var reportDate = new DateOnly(2026, 6, 30);
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit(BalanceAccount, Day1, 100_000m, "CNY"),
            LedgerTransaction.BalanceAssertion(BalanceAccount, reportDate, 195_000m, "CNY"),
        ]);

        // Valuing 2026-03-31 with a report made on 2026-06-30 would be hindsight, not information.
        var valuation = ValuationEngine.Value(
            ledger, NoQuotes, FxConverter.Empty, "CNY", new DateOnly(2026, 3, 31),
            instruments: null, accounts: Accounts());

        Assert.False(valuation.IsComplete);
        Assert.Contains(
            valuation.Warnings,
            w => w.Contains("2026-06-30", StringComparison.Ordinal) && w.Contains("未知"));
        Assert.False(valuation.AccountValues.Single(v => v.AccountId == BalanceAccount).ValueKnown);
        Assert.True(valuation.TotalValue.IsZero);
    }

    [Fact]
    public void ABalanceReportMadeAfterItsFlowsIsStillComplete()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit(BalanceAccount, Day1, 100_000m, "CNY"),
            LedgerTransaction.BalanceAssertion(BalanceAccount, new DateOnly(2026, 6, 30), 195_000m, "CNY"),
        ]);

        var valuation = ValuationEngine.Value(
            ledger, NoQuotes, FxConverter.Empty, "CNY", AsOf, instruments: null, accounts: Accounts());

        Assert.True(valuation.IsComplete);
        Assert.Equal(195_000m, valuation.TotalValue.Amount);
    }
}

file static class SliceExtensions
{
    public static decimal Amount(this AllocationSlice slice) => slice.Value.Amount;
}
