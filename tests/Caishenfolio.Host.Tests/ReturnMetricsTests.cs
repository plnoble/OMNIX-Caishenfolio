using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class ReturnMetricsTests
{
    [Fact]
    public void XirrOverExactlyOneYearIsThePlainReturn()
    {
        DatedAmount[] flows =
        [
            new(new DateOnly(2025, 1, 1), -1000m),
            new(new DateOnly(2026, 1, 1), 1100m),
        ];

        var xirr = ReturnMetrics.Xirr(flows);

        Assert.NotNull(xirr);
        Assert.Equal(0.10, xirr!.Value, 6);
    }

    [Fact]
    public void XirrMatchesTheDocumentedSpreadsheetExample()
    {
        // The example Excel ships with XIRR: 37.3362535% .
        DatedAmount[] flows =
        [
            new(new DateOnly(2008, 1, 1), -10_000m),
            new(new DateOnly(2008, 3, 1), 2_750m),
            new(new DateOnly(2008, 10, 30), 4_250m),
            new(new DateOnly(2009, 2, 15), 3_250m),
            new(new DateOnly(2009, 4, 1), 2_750m),
        ];

        var xirr = ReturnMetrics.Xirr(flows);

        Assert.NotNull(xirr);
        Assert.Equal(0.373362535, xirr!.Value, 6);
    }

    [Fact]
    public void XirrHandlesALosingPortfolio()
    {
        DatedAmount[] flows =
        [
            new(new DateOnly(2025, 1, 1), -10_000m),
            new(new DateOnly(2026, 1, 1), 8_000m),
        ];

        var xirr = ReturnMetrics.Xirr(flows);

        Assert.NotNull(xirr);
        Assert.Equal(-0.20, xirr!.Value, 6);
    }

    [Fact]
    public void XirrReturnsNullRatherThanAFabricatedNumber()
    {
        // All flows the same sign: no rate can make the present value zero.
        Assert.Null(ReturnMetrics.Xirr([
            new(new DateOnly(2025, 1, 1), -1000m),
            new(new DateOnly(2026, 1, 1), -1000m),
        ]));

        Assert.Null(ReturnMetrics.Xirr([new DatedAmount(new DateOnly(2025, 1, 1), -1000m)]));
        Assert.Null(ReturnMetrics.Xirr([]));
    }

    [Fact]
    public void XirrFromLedgerInvertsExternalFlowsAndClosesWithMarketValue()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit("acct", new DateOnly(2025, 1, 1), 10_000m, "CNY"),
            LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2025, 1, 2), 1000m, 10m, "CNY"),
        ]);

        var xirr = ReturnMetrics.Xirr(
            ledger.ExternalFlows, Money.Of(11_000m, "CNY"), new DateOnly(2026, 1, 1));

        Assert.NotNull(xirr);
        Assert.Equal(0.10, xirr!.Value, 6);
    }

    [Fact]
    public void XirrRefusesToMixCurrencies()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit("acct", new DateOnly(2025, 1, 1), 10_000m, "USD"),
        ]);

        Assert.Throws<LedgerException>(() =>
            ReturnMetrics.Xirr(ledger.ExternalFlows, Money.Of(11_000m, "CNY"), new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void ModifiedDietzWeightsAMidPeriodContribution()
    {
        var start = new DateOnly(2026, 1, 1);
        var end = start.AddDays(100);
        DatedAmount[] flows = [new(start.AddDays(50), 1_000m)];

        var result = ReturnMetrics.ModifiedDietz(10_000m, 12_000m, flows, start, end);

        // (12 000 - 10 000 - 1 000) / (10 000 + 0.5 × 1 000)
        Assert.NotNull(result);
        Assert.Equal(1000.0 / 10500.0, result!.Value, 10);
    }

    [Fact]
    public void ModifiedDietzIgnoresFlowsOutsideThePeriod()
    {
        var start = new DateOnly(2026, 1, 1);
        var end = start.AddDays(100);
        DatedAmount[] flows = [new(start.AddDays(-5), 5_000m), new(end.AddDays(5), 5_000m)];

        var result = ReturnMetrics.ModifiedDietz(10_000m, 11_000m, flows, start, end);

        Assert.NotNull(result);
        Assert.Equal(0.10, result!.Value, 10);
    }

    [Fact]
    public void ModifiedDietzReturnsNullWhenNothingWasInvested()
    {
        var start = new DateOnly(2026, 1, 1);
        Assert.Null(ReturnMetrics.ModifiedDietz(0m, 0m, [], start, start.AddDays(30)));
        Assert.Null(ReturnMetrics.ModifiedDietz(100m, 100m, [], start, start));
    }

    [Fact]
    public void TimeWeightedReturnIsUnaffectedByContributionTiming()
    {
        var d0 = new DateOnly(2026, 1, 1);
        var d1 = new DateOnly(2026, 4, 1);
        var d2 = new DateOnly(2026, 7, 1);

        ValuationPoint[] points = [new(d0, 10_000m), new(d1, 11_000m), new(d2, 13_200m)];
        DatedAmount[] flows = [new(d1, 1_000m)];

        var twr = ReturnMetrics.TimeWeighted(points, flows);

        // Two 10% sub-periods link to 21%, regardless of the contribution in the middle.
        Assert.NotNull(twr);
        Assert.Equal(0.21, twr!.Value, 10);
    }

    [Fact]
    public void TimeWeightedReturnNeedsAtLeastTwoValuationPoints()
    {
        Assert.Null(ReturnMetrics.TimeWeighted([new ValuationPoint(new DateOnly(2026, 1, 1), 100m)], []));
        Assert.Null(ReturnMetrics.TimeWeighted([], []));
    }

    [Fact]
    public void TimeWeightedReturnFailsClosedOnAZeroBaseSubPeriod()
    {
        var d0 = new DateOnly(2026, 1, 1);
        var d1 = new DateOnly(2026, 4, 1);

        Assert.Null(ReturnMetrics.TimeWeighted([new(d0, 0m), new(d1, 100m)], []));
    }

    [Theory]
    [InlineData(0.10, 365, 0.10)]
    [InlineData(0.21, 730, 0.10)]
    public void AnnualizesACumulativeReturn(double total, int days, double expected)
    {
        var annual = ReturnMetrics.Annualize(total, days);

        Assert.NotNull(annual);
        Assert.Equal(expected, annual!.Value, 6);
    }

    [Fact]
    public void AnnualizeRefusesImpossibleInputs()
    {
        Assert.Null(ReturnMetrics.Annualize(0.1, 0));
        Assert.Null(ReturnMetrics.Annualize(-1.0, 365));
    }
}
