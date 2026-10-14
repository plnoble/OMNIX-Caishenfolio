using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// The path comparison's "historical cost" input comes from the user's actual booked
/// exchanges. These tests pin the reading rules: receipt amounts as stored, reversal pairs
/// excluded, display-only implied rate, and honest null when no acquisition exists.
/// </summary>
public class FxExchangeHistoryTests
{
    private static readonly DateOnly Day1 = new(2026, 8, 1);
    private static readonly DateOnly Day2 = new(2026, 8, 20);

    [Fact]
    public void RecordsCarryTheReceiptAmountsAndADisplayOnlyRate()
    {
        // The plan's fixed sample: 100 CNY bought 2,450 JPY → 24.5 JPY per CNY.
        var txn = LedgerTransaction.FxExchange("acct", Day1, 100m, "CNY", 2450m, "JPY");

        var records = FxExchangeHistory.Records([txn]);

        var record = Assert.Single(records);
        Assert.Equal("CNY", record.FromCurrency);
        Assert.Equal(100m, record.FromAmount);
        Assert.Equal("JPY", record.ToCurrency);
        Assert.Equal(2450m, record.ToAmount);
        Assert.Equal(24.5m, record.ImpliedRate);
    }

    [Fact]
    public void ReversedExchangesNeverHappened()
    {
        var original = LedgerTransaction.FxExchange("acct", Day1, 100m, "CNY", 2450m, "JPY");
        var reversal = LedgerTransaction.Reversal(original, "记错了");

        var records = FxExchangeHistory.Records([original, reversal]);

        Assert.Empty(records);
    }

    [Fact]
    public void NonExchangeTransactionsAreIgnored()
    {
        var records = FxExchangeHistory.Records(
        [
            LedgerTransaction.Deposit("acct", Day1, 1000m, "CNY"),
            LedgerTransaction.Buy("acct", "SSE:600000", Day1, 100m, 7.5m, "CNY"),
        ]);

        Assert.Empty(records);
    }

    [Fact]
    public void MostRecentAcquisitionPicksTheLatestOfMany()
    {
        var older = LedgerTransaction.FxExchange("acct", Day1, 100m, "CNY", 2000m, "JPY");
        var newer = LedgerTransaction.FxExchange("acct", Day2, 100m, "CNY", 2450m, "JPY");

        var records = FxExchangeHistory.Records([older, newer]);
        var latest = FxExchangeHistory.MostRecentAcquisition(records, "CNY", "JPY");

        Assert.NotNull(latest);
        Assert.Equal(Day2, latest!.Date);
        Assert.Equal(24.5m, latest.ImpliedRate);
    }

    [Fact]
    public void DirectionMattersWhenLookingForTheCostBasis()
    {
        // Selling JPY back to CNY is not how the JPY was acquired.
        var txn = LedgerTransaction.FxExchange("acct", Day1, 2450m, "JPY", 100m, "CNY");

        var records = FxExchangeHistory.Records([txn]);

        Assert.Null(FxExchangeHistory.MostRecentAcquisition(records, "CNY", "JPY"));
        Assert.NotNull(FxExchangeHistory.MostRecentAcquisition(records, "JPY", "CNY"));
    }

    [Fact]
    public void NoHistoryMeansUnknownNotZero()
    {
        Assert.Null(FxExchangeHistory.MostRecentAcquisition([], "CNY", "JPY"));
    }

    [Fact]
    public void CurrencyCodesAreMatchedCaseInsensitively()
    {
        var txn = LedgerTransaction.FxExchange("acct", Day1, 100m, "cny", 2450m, "jpy");

        var records = FxExchangeHistory.Records([txn]);
        var latest = FxExchangeHistory.MostRecentAcquisition(records, "CNY", "JPY");

        // The store normalizes codes; if raw input slips through, matching still works.
        Assert.NotNull(latest);
    }
}
