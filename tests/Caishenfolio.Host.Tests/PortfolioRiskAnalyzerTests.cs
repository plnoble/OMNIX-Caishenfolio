using Caishenfolio.Host;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class PortfolioRiskAnalyzerTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    /// <summary>60 000 in one A-share, 20 000 in a US stock, 20 000 cash — 100 000 total.</summary>
    private static PortfolioValuation Concentrated()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 20_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 6000m, 10m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "NASDAQ:AAPL", Day1, 20m, 100m, "USD"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
            ["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 100m, "USD", AsOf),
        };
        var fx = new FxConverter([FxRate.Of("USD", "CNY", 10m, Day1)]);
        return ValuationEngine.Value(ledger, quotes, fx, "CNY", AsOf);
    }

    [Fact]
    public void FlagsASinglePositionOverTheCeiling()
    {
        var report = PortfolioRiskAnalyzer.Analyze(Concentrated());

        var finding = report.Findings.First(f => f.Label == "SSE:600000");
        Assert.Equal(0.6m, finding.Weight);
        Assert.Equal(0.20m, finding.Threshold);
        Assert.Equal(RiskLevel.Warning, finding.Level);
        Assert.Contains("超过你设定的单一持仓上限", finding.Message);
        Assert.True(report.HasWarnings);
    }

    [Fact]
    public void RespectsCustomThresholds()
    {
        var relaxed = new RiskThresholds
        {
            SinglePosition = 0.90m,
            AssetClass = 0.95m,
            Region = 0.95m,
            Currency = 0.95m,
            Cash = 0.95m,
        };

        var report = PortfolioRiskAnalyzer.Analyze(Concentrated(), relaxed);

        Assert.Empty(report.Findings);
        Assert.False(report.HasWarnings);
        Assert.Contains("未触及你设定的任何上限", report.Summary);
    }

    [Fact]
    public void FlagsIdleCashSeparatelyFromOtherAssetClasses()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 90_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        var valuation = ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);

        var report = PortfolioRiskAnalyzer.Analyze(valuation);

        var cash = report.Findings.First(f => f.Label == "现金");
        Assert.Equal(0.9m, cash.Weight);
        Assert.Equal(0.40m, cash.Threshold);
        Assert.Contains("现金占", cash.Message);
    }

    [Fact]
    public void EveryFindingIsFactualNotAdvisory()
    {
        var report = PortfolioRiskAnalyzer.Analyze(Concentrated());

        // The tool states thresholds you set; it never tells you what to trade.
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f => Assert.DoesNotContain("建议", f.Message));
        Assert.All(report.Findings, f => Assert.DoesNotContain("应该", f.Message));
        Assert.Contains(ProductInfo.ResearchDisclaimer, report.Summary);
    }

    [Fact]
    public void ComputesMaxDrawdownFromTheEquityCurve()
    {
        ValuationPoint[] curve =
        [
            new(new DateOnly(2026, 1, 1), 100_000m),
            new(new DateOnly(2026, 2, 1), 120_000m),
            new(new DateOnly(2026, 3, 1), 90_000m),
            new(new DateOnly(2026, 4, 1), 110_000m),
        ];

        var report = PortfolioRiskAnalyzer.Analyze(Concentrated(), equityCurve: curve);

        // Peak 120 000 on Feb 1 down to 90 000 on Mar 1 is 25%.
        Assert.Equal(0.25m, report.MaxDrawdown);
        Assert.Equal(new DateOnly(2026, 2, 1), report.DrawdownPeak);
        Assert.Equal(new DateOnly(2026, 3, 1), report.DrawdownTrough);
    }

    [Fact]
    public void DrawdownIsNullWithoutEnoughHistory()
    {
        Assert.Null(PortfolioRiskAnalyzer.MaxDrawdown(null));
        Assert.Null(PortfolioRiskAnalyzer.MaxDrawdown([]));
        Assert.Null(PortfolioRiskAnalyzer.MaxDrawdown([new ValuationPoint(Day1, 100m)]));

        // A curve that only rises has no drawdown.
        Assert.Null(PortfolioRiskAnalyzer.MaxDrawdown(
        [
            new(new DateOnly(2026, 1, 1), 100m),
            new(new DateOnly(2026, 2, 1), 120m),
        ]));

        Assert.Contains("尚无足够的估值历史", PortfolioRiskAnalyzer.Analyze(Concentrated()).Summary);
    }

    [Fact]
    public void DrawdownIgnoresInputOrder()
    {
        ValuationPoint[] shuffled =
        [
            new(new DateOnly(2026, 3, 1), 90_000m),
            new(new DateOnly(2026, 1, 1), 100_000m),
            new(new DateOnly(2026, 2, 1), 120_000m),
        ];

        Assert.Equal(0.25m, PortfolioRiskAnalyzer.MaxDrawdown(shuffled)!.Value.Depth);
    }

    [Fact]
    public void ReportsArithmeticDriftFromUserSuppliedTargets()
    {
        var targets = new Dictionary<string, decimal>
        {
            ["equity"] = 0.60m,
            ["cash"] = 0.40m,
        };

        var report = PortfolioRiskAnalyzer.Analyze(Concentrated(), targetAssetAllocation: targets);

        // Equity is 80% against a 60% target on a 100 000 portfolio: 20 000 over.
        var equity = report.Drift.First(d => d.Key == "equity");
        Assert.Equal(0.8m, equity.CurrentWeight);
        Assert.Equal(0.60m, equity.TargetWeight);
        Assert.Equal(-20_000m, equity.Delta.Amount);
        Assert.True(equity.IsOverweight);

        var cash = report.Drift.First(d => d.Key == "cash");
        Assert.Equal(20_000m, cash.Delta.Amount);
        Assert.False(cash.IsOverweight);
    }

    [Fact]
    public void NoTargetsMeansNoDrift()
    {
        Assert.Empty(PortfolioRiskAnalyzer.Analyze(Concentrated()).Drift);
        Assert.Empty(PortfolioRiskAnalyzer.Analyze(Concentrated(), targetAssetAllocation: new Dictionary<string, decimal>()).Drift);
    }

    [Fact]
    public void AnIncompleteValuationIsCalledOutSoWeightsAreNotTrustedBlindly()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        var valuation = ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);

        var report = PortfolioRiskAnalyzer.Analyze(valuation);

        Assert.False(valuation.IsComplete);
        Assert.Contains("估值不完整", report.Summary);
    }

    [Fact]
    public void EmptyPortfolioProducesNoFindings()
    {
        var valuation = ValuationEngine.Value(
            LedgerState.Empty, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", AsOf);

        var report = PortfolioRiskAnalyzer.Analyze(valuation);

        Assert.Empty(report.Findings);
        Assert.Empty(report.Drift);
        Assert.Null(report.MaxDrawdown);
    }
}
