using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-02 A12 / R8: the WP-01 semantics this package builds on must stay in force. The WP-01
/// acceptance suite runs alongside; this is the compact tripwire that names the rules in one
/// place, so a regression says which rule broke instead of just going red.
///
/// Note what replay does and does not know: it produces cash, positions and flows without
/// looking at account modes (those live in the store) — the mode gating happens in the valuation
/// and the write path. The assertions below therefore pin the exact seams, not a wish.
/// </summary>
public class Wp01SemanticsTripwireTests
{
    private const string Detail = "acct_detail";
    private const string Balance = "acct_balance";
    private static readonly DateOnly Day1 = new(2025, 1, 2);
    private static readonly DateOnly Day2 = new(2025, 6, 30);

    private static Dictionary<string, Account> Accounts() => new(StringComparer.Ordinal)
    {
        [Detail] = Portfolio.Account.Create("明细账户", AccountKind.Securities, "CNY", id: Detail),
        [Balance] = Portfolio.Account.Create(
            "余额账户", AccountKind.Bank, "CNY", id: Balance, mode: AccountValuationMode.Balance),
    };

    [Fact]
    public void A12_ABalanceAssertionIsAValueStatementNotCash()
    {
        var state = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit(Balance, Day1, 50_000m, "CNY"),
            LedgerTransaction.BalanceAssertion(Balance, Day2, 195_000m, "CNY"),
        ]);

        // The assertion itself moves no cash and creates no holding: the only cash is the deposit.
        Assert.Equal(50_000m, Assert.Single(state.CashBalances).Money.Amount);
        Assert.Empty(state.Positions);
        // And it is not an external flow: only the deposit is.
        var flow = Assert.Single(state.ExternalFlows);
        Assert.Equal(50_000m, flow.Amount.Amount);
        Assert.Equal(Day1, flow.Date);

        // The mode gating lives in the valuation: a balance account is worth what it reported,
        // not its tracked cash plus the report.
        var valuation = ValuationEngine.Value(
            state, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", Day2, null, Accounts());
        Assert.Equal(195_000m, valuation.TotalValue.Amount);
    }

    [Fact]
    public void A12_ATransferIsInternalToThePortfolioButExternalToEachAccount()
    {
        var state = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit(Detail, Day1, 50_000m, "CNY"),
            LedgerTransaction.Transfer(Detail, Balance, Day2, 10_000m, "CNY"),
        ]);

        // Portfolio level: only the deposit crossed the boundary; the move did not.
        var external = Assert.Single(state.ExternalFlows);
        Assert.Equal(50_000m, external.Amount.Amount);
        Assert.Equal(Day1, external.Date);

        // Account level: money did leave the detail account, with its counterparty named. The
        // amount is signed from that account's point of view — money out is negative.
        var move = Assert.Single(state.FlowsForAccount(Detail), f => f.Date == Day2);
        Assert.Equal(-10_000m, move.Amount.Amount);
        Assert.False(move.CrossesPortfolioBoundary);
        Assert.Equal(Balance, move.CounterAccountId);
    }

    [Fact]
    public void A12_AMultiCurrencyFlowUsesItsOwnDaysRate()
    {
        var history = new HistoricalFxConverter(
        [
            FxRate.Of("JPY", "CNY", 0.05m, Day1),
            FxRate.Of("JPY", "CNY", 0.06m, Day2),
        ]);
        var money = Money.Of(1_000_000m, "JPY");

        Assert.True(history.TryConvert(money, "CNY", Day1, out var onFlowDay));
        Assert.True(history.TryConvert(money, "CNY", Day2, out var onValuationDay));
        Assert.Equal(50_000m, onFlowDay.Amount);
        Assert.Equal(60_000m, onValuationDay.Amount);

        // And a day with no observation refuses instead of borrowing a neighbour's rate.
        Assert.False(history.TryConvert(money, "CNY", new DateOnly(2025, 3, 3), out _));
    }
}
