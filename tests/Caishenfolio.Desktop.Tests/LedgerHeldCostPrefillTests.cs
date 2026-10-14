using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// Recheck P1-04: the FX page's "按账本填入" must price the pot at the moving-weighted
/// cost — the review's counterexample had two buys at 2 000/2 500 JPY and the page showing
/// 180 CNY (last rate stretched over 4 500) instead of the actual 200 invested.
/// </summary>
public class LedgerHeldCostPrefillTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 2);
    private static readonly DateOnly Day2 = new(2026, 2, 2);

    [Fact]
    public void TwoBatchesShowTheActualInvestedCostNotTheLastRate()
    {
        var transactions = new[]
        {
            LedgerTransaction.FxExchange("acct", Day1, 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.FxExchange("acct", Day2, 100m, "CNY", 2_500m, "JPY"),
        };

        var prefill = LedgerHeldCostPrefill.Compute(transactions, 4_500m, "CNY", "JPY");

        Assert.True(prefill.HasCost);
        Assert.Equal("4500", prefill.AmountText);
        Assert.Equal("200", prefill.CostText); // actual invested, NOT 4500/25 = 180
    }

    [Fact]
    public void PartialWithdrawalScalesTheCost()
    {
        var transactions = new[]
        {
            LedgerTransaction.FxExchange("acct", Day1, 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.Withdraw("acct", Day2, 1_000m, "JPY"),
        };

        var prefill = LedgerHeldCostPrefill.Compute(transactions, 1_000m, "CNY", "JPY");

        Assert.True(prefill.HasCost);
        Assert.Equal("50", prefill.CostText);
    }

    [Fact]
    public void UnknownOriginMoneyBlanksTheWholeCostWithAnExplanation()
    {
        var transactions = new LedgerTransaction[]
        {
            LedgerTransaction.FxExchange("acct", Day1, 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.Deposit("acct", Day2, 1_000m, "JPY"),
        };

        var prefill = LedgerHeldCostPrefill.Compute(transactions, 3_000m, "CNY", "JPY");

        // Never a partial cost wearing the whole pot: blank + the unknown share named.
        Assert.False(prefill.HasCost);
        Assert.Equal("", prefill.CostText);
        Assert.Contains("来源不明", prefill.StatusText);
        Assert.Contains("1000", prefill.StatusText);
    }

    [Fact]
    public void PureExternalArrivalHasNoCostBasis()
    {
        var transactions = new LedgerTransaction[]
        {
            LedgerTransaction.Deposit("acct", Day1, 5_000m, "JPY"),
        };

        var prefill = LedgerHeldCostPrefill.Compute(transactions, 5_000m, "CNY", "JPY");

        // No exchange ever touched the currency: the entire pot is unpriced and the
        // message names it (which is also more precise than "no exchange record" —
        // there IS money, it just has no cost basis).
        Assert.False(prefill.HasCost);
        Assert.Contains("来源不明", prefill.StatusText);
        Assert.Contains("5000", prefill.StatusText);
    }

    [Fact]
    public void AnEmptyLedgerAsksForTheReceipt()
    {
        var prefill = LedgerHeldCostPrefill.Compute([], 0m, "CNY", "JPY");

        // Zero balance and no flows: nothing to prefill at all.
        Assert.False(prefill.HasCost);
    }
}
