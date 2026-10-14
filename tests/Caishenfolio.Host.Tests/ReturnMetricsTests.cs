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

        // Snapshot convention (P1-03): d1's 11 000 already contains the 1 000 deposited that
        // day, so the first sub-period returned 0% and the second 11 000 → 13 200 is +20%.
        ValuationPoint[] points = [new(d0, 10_000m), new(d1, 11_000m), new(d2, 13_200m)];
        DatedAmount[] flows = [new(d1, 1_000m)];

        var twr = ReturnMetrics.TimeWeighted(points, flows);

        Assert.NotNull(twr);
        Assert.Equal(0.20, twr!.Value, 10);
    }

    [Fact]
    public void ASameDayDepositOnTheSecondPointIsNotAppreciation()
    {
        // The review's minimal counterexample (P1-03): 100 000 then a 50 000 deposit on the
        // second snapshot day → 150 000. The true return is 0%, not +50%.
        var d0 = new DateOnly(2026, 1, 1);
        var d1 = new DateOnly(2026, 6, 1);

        ValuationPoint[] points = [new(d0, 100_000m), new(d1, 150_000m)];
        DatedAmount[] flows = [new(d1, 50_000m)];

        var twr = ReturnMetrics.TimeWeighted(points, flows);

        Assert.NotNull(twr);
        Assert.Equal(0.0, twr!.Value, 10);
    }

    [Fact]
    public void ASameDayDepositOnTheFirstPointIsAlreadyInsideTheBase()
    {
        // Flows dated on the first point predate the measured window's base value.
        var d0 = new DateOnly(2026, 1, 1);
        var d1 = new DateOnly(2026, 6, 1);

        ValuationPoint[] points = [new(d0, 100_000m), new(d1, 110_000m)];
        DatedAmount[] flows = [new(d0, 50_000m)];

        var twr = ReturnMetrics.TimeWeighted(points, flows);

        Assert.NotNull(twr);
        Assert.Equal(0.10, twr!.Value, 10);
    }

    [Fact]
    public void ASameDayWithdrawalOnTheSecondPointIsNotALoss()
    {
        var d0 = new DateOnly(2026, 1, 1);
        var d1 = new DateOnly(2026, 6, 1);

        // 100 000, withdraw 50 000 on the snapshot day → 50 000 remains: 0%, not −50%.
        ValuationPoint[] points = [new(d0, 100_000m), new(d1, 50_000m)];
        DatedAmount[] flows = [new(d1, -50_000m)];

        var twr = ReturnMetrics.TimeWeighted(points, flows);

        Assert.NotNull(twr);
        Assert.Equal(0.0, twr!.Value, 10);
    }

    [Fact]
    public void InterimFlowsStillAdjustTheCostBase()
    {
        var d0 = new DateOnly(2026, 1, 1);
        var mid = new DateOnly(2026, 4, 1);
        var d1 = new DateOnly(2026, 7, 1);

        // One sub-period only: base 100 000 + 10 000 deposited mid-window, end 132 000 → +20%.
        ValuationPoint[] points = [new(d0, 100_000m), new(d1, 132_000m)];
        DatedAmount[] flows = [new(mid, 10_000m)];

        var twr = ReturnMetrics.TimeWeighted(points, flows);
        Assert.NotNull(twr);
        Assert.Equal(0.20, twr!.Value, 10);

        // With a mid-window valuation point ON the flow date, the flow sits in that point's
        // value (snapshot convention): leg 1 = 0%, leg 2 = 110 000 → 132 000 = +20%.
        // A mid-point on a flow-free date is what lets two +10% legs link to 21%.
        ValuationPoint[] threePoints = [new(d0, 100_000m), new(mid, 110_000m), new(d1, 132_000m)];
        var linked = ReturnMetrics.TimeWeighted(threePoints, flows);
        Assert.NotNull(linked);
        Assert.Equal(0.20, linked!.Value, 10);

        var flowFreeMid = new DateOnly(2026, 4, 2);
        DatedAmount[] afterMidFlows = [new(flowFreeMid, 10_000m)];
        ValuationPoint[] cleanLegs = [new(d0, 100_000m), new(mid, 110_000m), new(d1, 132_000m)];
        // 100k→110k (+10%), then 110k+10k=120k →132k (+10%): links to 21%.
        var twr21 = ReturnMetrics.TimeWeighted(cleanLegs, afterMidFlows);
        Assert.NotNull(twr21);
        Assert.Equal(0.21, twr21!.Value, 10);
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
