using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class PositionCalculatorTests
{
    private const string Account = "acct_main";
    private const string Pufa = "SSE:600000";
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly Day2 = new(2026, 2, 9);
    private static readonly DateOnly Day3 = new(2026, 3, 10);

    [Fact]
    public void WeightsCostAcrossBuysAndRealizesOnSell()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Deposit(Account, Day1, 100_000m, "CNY"),
            LedgerTransaction.Buy(Account, Pufa, Day1, 1000m, 10.00m, "CNY", fee: 5m),
            LedgerTransaction.Buy(Account, Pufa, Day2, 1000m, 12.00m, "CNY", fee: 6m),
            LedgerTransaction.Sell(Account, Pufa, Day3, 500m, 15.00m, "CNY", fee: 7.5m, tax: 15m),
        ];

        var state = PositionCalculator.Replay(ledger);
        var position = Assert.Single(state.Positions);

        // Cost basis carries buy-side fees: 10 005 + 12 006 = 22 011 over 2 000 units.
        Assert.Equal(1500m, position.Quantity);
        Assert.Equal(11.0055m, position.AverageCost.Amount);
        Assert.Equal(16_508.25m, position.CostBasis.Amount);

        // Proceeds 7 500 - 7.5 fee - 15 tax = 7 477.5; released cost 5 502.75.
        Assert.Equal(1_974.75m, position.RealizedPnl.Amount);
        Assert.Equal(18.5m, position.Fees.Amount);
        Assert.Equal(15m, position.Taxes.Amount);

        var cash = Assert.Single(state.CashBalances);
        Assert.Equal(85_466.5m, cash.Amount);
        Assert.Equal("CNY", cash.Currency);
    }

    [Fact]
    public void ClosingAPositionLeavesNoCostResidue()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, Pufa, Day1, 300m, 10.10m, "CNY"),
            LedgerTransaction.Sell(Account, Pufa, Day2, 100m, 11m, "CNY"),
            LedgerTransaction.Sell(Account, Pufa, Day3, 200m, 12m, "CNY"),
        ];

        var position = Assert.Single(PositionCalculator.Replay(ledger).Positions);

        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.CostBasis.Amount);
        Assert.Equal(0m, position.AverageCost.Amount);
        Assert.False(position.IsOpen);
        // 100 × (11 - 10.10) + 200 × (12 - 10.10)
        Assert.Equal(470m, position.RealizedPnl.Amount);
    }

    [Fact]
    public void SellingMoreThanHeldFailsClosed()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, Pufa, Day1, 100m, 10m, "CNY"),
            LedgerTransaction.Sell(Account, Pufa, Day2, 300m, 11m, "CNY"),
        ];

        var error = Assert.Throws<LedgerException>(() => PositionCalculator.Replay(ledger));
        Assert.Contains("超过当时持仓", error.Message);
        Assert.Contains("期初持仓", error.Message);
    }

    [Fact]
    public void OpeningPositionLetsALedgerStartMidStream()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.OpeningPosition(Account, Pufa, Day1, 1000m, 8m, "CNY"),
            LedgerTransaction.Sell(Account, Pufa, Day2, 400m, 10m, "CNY"),
        ];

        var state = PositionCalculator.Replay(ledger);
        var position = Assert.Single(state.Positions);

        Assert.Equal(600m, position.Quantity);
        Assert.Equal(8m, position.AverageCost.Amount);
        Assert.Equal(800m, position.RealizedPnl.Amount);

        // The opening holding never moved cash, only the later sale did.
        var cash = Assert.Single(state.CashBalances);
        Assert.Equal(4000m, cash.Amount);
    }

    [Fact]
    public void StockDividendAddsUnitsWithoutAddingCost()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, Pufa, Day1, 1000m, 10m, "CNY", fee: 5m),
            LedgerTransaction.StockDividend(Account, Pufa, Day2, 100m, "CNY"),
        ];

        var position = Assert.Single(PositionCalculator.Replay(ledger).Positions);

        Assert.Equal(1100m, position.Quantity);
        Assert.Equal(10_005m, position.CostBasis.Amount);
        Assert.Equal(10_005m / 1100m, position.AverageCost.Amount);
    }

    [Fact]
    public void SplitScalesUnitsAndHalvesAverageCost()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, Pufa, Day1, 1000m, 10m, "CNY"),
            LedgerTransaction.Split(Account, Pufa, Day2, 2m, "CNY"),
        ];

        var position = Assert.Single(PositionCalculator.Replay(ledger).Positions);

        Assert.Equal(2000m, position.Quantity);
        Assert.Equal(10_000m, position.CostBasis.Amount);
        Assert.Equal(5m, position.AverageCost.Amount);
    }

    [Fact]
    public void ReverseSplitShrinksUnits()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, Pufa, Day1, 1000m, 10m, "CNY"),
            LedgerTransaction.Split(Account, Pufa, Day2, 0.5m, "CNY"),
        ];

        var position = Assert.Single(PositionCalculator.Replay(ledger).Positions);
        Assert.Equal(500m, position.Quantity);
        Assert.Equal(20m, position.AverageCost.Amount);
    }

    [Fact]
    public void CashDividendIsIncomeNotACostReduction()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, Pufa, Day1, 1000m, 10m, "CNY"),
            LedgerTransaction.Dividend(Account, Pufa, Day2, 320m, "CNY", tax: 20m),
        ];

        var state = PositionCalculator.Replay(ledger);
        var position = Assert.Single(state.Positions);

        Assert.Equal(10_000m, position.CostBasis.Amount);
        Assert.Equal(300m, position.Dividends.Amount);
        Assert.Equal(20m, position.Taxes.Amount);

        var cash = Assert.Single(state.CashBalances);
        Assert.Equal(-9_700m, cash.Amount);
    }

    [Fact]
    public void BondCouponLandsInCashWithoutTouchingCost()
    {
        const string bond = "SSE:113050";
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, bond, Day1, 10m, 118m, "CNY"),
            LedgerTransaction.Interest(Account, Day2, 25m, "CNY", symbol: bond),
        ];

        var state = PositionCalculator.Replay(ledger);
        var position = Assert.Single(state.Positions);

        Assert.Equal(1180m, position.CostBasis.Amount);
        Assert.Equal(25m, position.Dividends.Amount);
        Assert.Equal(-1155m, Assert.Single(state.CashBalances).Amount);
    }

    [Fact]
    public void FxExchangeBookedByReceiptAmountsIsExact()
    {
        // 1/7.2 has no exact decimal form, so booking by rate would leave residue in USD cash.
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Deposit(Account, Day1, 72_000m, "CNY"),
            LedgerTransaction.FxExchange(Account, Day2, 7_200m, "CNY", 1_000m, "USD", fee: 10m),
        ];

        var state = PositionCalculator.Replay(ledger);

        Assert.Equal(64_790m, state.CashBalances.Single(b => b.Currency == "CNY").Amount);
        Assert.Equal(1000m, state.CashBalances.Single(b => b.Currency == "USD").Amount);
    }

    [Fact]
    public void FxExchangeBookedByRateCreditsRateTimesAmount()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Deposit(Account, Day1, 10_000m, "USD"),
            LedgerTransaction.FxExchangeAtRate(Account, Day2, 1_000m, "USD", "CNY", rate: 7.2m),
        ];

        var state = PositionCalculator.Replay(ledger);

        Assert.Equal(9_000m, state.CashBalances.Single(b => b.Currency == "USD").Amount);
        Assert.Equal(7_200m, state.CashBalances.Single(b => b.Currency == "CNY").Amount);
    }

    [Fact]
    public void MultiCurrencyAccountKeepsBalancesSeparate()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Deposit(Account, Day1, 10_000m, "USD"),
            LedgerTransaction.Deposit(Account, Day1, 50_000m, "HKD"),
            LedgerTransaction.Buy(Account, "NASDAQ:AAPL", Day2, 10m, 180m, "USD", fee: 1m),
            LedgerTransaction.Buy(Account, "HKEX:00700", Day2, 100m, 320m, "HKD", fee: 30m),
            LedgerTransaction.Buy(Account, "TSE:7203", Day3, 100m, 2800m, "JPY"),
        ];

        var state = PositionCalculator.Replay(ledger);

        Assert.Equal(8_199m, state.CashBalances.Single(b => b.Currency == "USD").Amount);
        Assert.Equal(17_970m, state.CashBalances.Single(b => b.Currency == "HKD").Amount);
        Assert.Equal(-280_000m, state.CashBalances.Single(b => b.Currency == "JPY").Amount);
        Assert.Equal(3, state.OpenPositions.Count());
    }

    [Fact]
    public void RejectsTheSameSymbolPricedInTwoCurrencies()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, Pufa, Day1, 100m, 10m, "CNY"),
            LedgerTransaction.Buy(Account, Pufa, Day2, 100m, 10m, "HKD"),
        ];

        var error = Assert.Throws<LedgerException>(() => PositionCalculator.Replay(ledger));
        Assert.Contains("两种计价货币", error.Message);
    }

    [Fact]
    public void SeparatesPositionsPerAccount()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy("acct_a", Pufa, Day1, 100m, 10m, "CNY"),
            LedgerTransaction.Buy("acct_b", Pufa, Day1, 200m, 12m, "CNY"),
        ];

        var state = PositionCalculator.Replay(ledger);

        Assert.Equal(2, state.Positions.Count);
        Assert.Equal(100m, state.Positions.Single(p => p.AccountId == "acct_a").Quantity);
        Assert.Equal(200m, state.Positions.Single(p => p.AccountId == "acct_b").Quantity);
    }

    [Fact]
    public void CollectsExternalFlowsForReturnMetrics()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.OpeningCash(Account, Day1, 5_000m, "CNY"),
            LedgerTransaction.Deposit(Account, Day2, 20_000m, "CNY"),
            LedgerTransaction.Buy(Account, Pufa, Day2, 1000m, 10m, "CNY"),
            LedgerTransaction.Withdraw(Account, Day3, 3_000m, "CNY"),
        ];

        var flows = PositionCalculator.Replay(ledger).ExternalFlows;

        // A buy moves money inside the portfolio, so it is not an external flow.
        Assert.Equal(3, flows.Count);
        Assert.Equal(5_000m, flows[0].Amount.Amount);
        Assert.Equal(20_000m, flows[1].Amount.Amount);
        Assert.Equal(-3_000m, flows[2].Amount.Amount);
    }

    [Fact]
    public void ReplaysInTradeDateOrderRegardlessOfInputOrder()
    {
        var buy = LedgerTransaction.Buy(Account, Pufa, Day1, 100m, 10m, "CNY");
        var sell = LedgerTransaction.Sell(Account, Pufa, Day2, 100m, 12m, "CNY");

        var forward = PositionCalculator.Replay([buy, sell]).Positions.Single();
        var reversed = PositionCalculator.Replay([sell, buy]).Positions.Single();

        Assert.Equal(forward.RealizedPnl.Amount, reversed.RealizedPnl.Amount);
        Assert.Equal(200m, forward.RealizedPnl.Amount);
    }

    [Fact]
    public void StandaloneFeeReducesRealizedPnlForTheInstrument()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, "FUND:110022", Day1, 1000m, 3.5m, "CNY"),
            LedgerTransaction.Charge(TransactionKind.Fee, Account, Day2, 12m, "CNY", symbol: "FUND:110022", note: "管理费"),
        ];

        var state = PositionCalculator.Replay(ledger);
        var position = Assert.Single(state.Positions);

        Assert.Equal(-12m, position.RealizedPnl.Amount);
        Assert.Equal(12m, position.Fees.Amount);
        Assert.Equal(3500m, position.CostBasis.Amount);
        Assert.Equal(-3512m, Assert.Single(state.CashBalances).Amount);
    }

    [Fact]
    public void RejectsInvalidTransactionsAtConstruction()
    {
        Assert.Throws<LedgerException>(() => LedgerTransaction.Buy(Account, Pufa, Day1, 0m, 10m, "CNY"));
        Assert.Throws<LedgerException>(() => LedgerTransaction.Buy(Account, Pufa, Day1, 100m, -1m, "CNY"));
        Assert.Throws<LedgerException>(() => LedgerTransaction.Buy(Account, "600000", Day1, 100m, 10m, "CNY"));
        Assert.Throws<LedgerException>(() => LedgerTransaction.Dividend(Account, "", Day1, 100m, "CNY"));
        Assert.Throws<LedgerException>(() => LedgerTransaction.FxExchange(Account, Day1, 100m, "CNY", 100m, "CNY"));
        Assert.Throws<ArgumentException>(() => LedgerTransaction.Deposit(Account, Day1, 100m, "XYZ"));
    }

    [Fact]
    public void NormalizesVenueAliasesSoOneHoldingDoesNotSplitInTwo()
    {
        LedgerTransaction[] ledger =
        [
            LedgerTransaction.Buy(Account, "SH:600000", Day1, 100m, 10m, "CNY"),
            LedgerTransaction.Buy(Account, "SSE:600000", Day2, 100m, 12m, "CNY"),
        ];

        var position = Assert.Single(PositionCalculator.Replay(ledger).Positions);
        Assert.Equal("SSE:600000", position.Symbol);
        Assert.Equal(200m, position.Quantity);
    }
}
