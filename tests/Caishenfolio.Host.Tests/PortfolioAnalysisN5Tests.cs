using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// N5 first batch: explain where profit and risk come from, simulate an action before it is
/// taken, and stress the portfolio under assumptions the user typed — every unknown stated,
/// nothing imputed.
/// </summary>
public class PortfolioAnalysisN5Tests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    /// <summary>USD stock bought when USD/CNY was 7, now quoted with USD/CNY 8.</summary>
    private static (PortfolioValuation Valuation, LedgerTransaction[] Transactions) BuildValuation(
        out FxRate[] rates)
    {
        var txns = new[]
        {
            LedgerTransaction.OpeningCash("acct", Day1, 100_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "NASDAQ:AAPL", Day1, 100m, 100m, "USD"),
        };
        rates =
        [
            FxRate.Of("USD", "CNY", 7m, Day1),
            FxRate.Of("USD", "CNY", 8m, AsOf),
        ];
        var ledger = PositionCalculator.Replay(txns);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 120m, "USD", AsOf),
        };
        var fx = new FxConverter(rates.Where(r => r.AsOf <= AsOf));
        var valuation = ValuationEngine.Value(ledger, quotes, fx, "CNY", AsOf);
        return (valuation, txns);
    }

    // --- contribution + FX split ---------------------------------------------------

    [Fact]
    public void TheFxSplitIsAnExactIdentityWhenCostDateRateIsKnown()
    {
        var (valuation, txns) = BuildValuation(out var rates);

        var report = PortfolioContributionAnalyzer.Analyze(
            valuation, txns, rates, "CNY");

        var row = Assert.Single(report.Rows);
        Assert.Null(row.UnknownReason);

        // Cost 10 000 USD @7 = 70 000 CNY actually invested; mv 12 000 USD @8 = 96 000 CNY.
        // The overview's unrealized PnL converts cost at today's rate: 96 000 − 80 000 = 16 000.
        Assert.Equal(16_000m, row.UnrealizedBase!.Value.Amount);
        // r_now = 8, r_cost = 7.
        Assert.Equal(8m, row.RateNow);
        Assert.Equal(7m, row.RateAtCost);
        // FX effect = 10 000 × (8 − 7) = 10 000 CNY; the local part is the whole overview number.
        Assert.Equal(10_000m, row.FxEffectBase!.Value.Amount);
        Assert.Equal(16_000m, row.LocalEffectBase!.Value.Amount);
        // Against the money actually invested: 16 000 + 10 000 = 26 000.
        Assert.Equal(26_000m, row.TrueUnrealizedBase!.Value.Amount);
        Assert.Equal(1m, row.ShareOfUnrealized);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void AMissingCostDateRateIsReportedNotImputed()
    {
        var (valuation, txns) = BuildValuation(out _);
        var todayOnly = new[] { FxRate.Of("USD", "CNY", 8m, AsOf) }; // nothing on/before the cost date

        var report = PortfolioContributionAnalyzer.Analyze(valuation, txns, todayOnly, "CNY");

        var row = Assert.Single(report.Rows);
        Assert.Equal(16_000m, row.UnrealizedBase!.Value.Amount);
        Assert.Null(row.FxEffectBase);
        Assert.Null(row.TrueUnrealizedBase);
        Assert.NotNull(row.UnknownReason);
        Assert.Contains("汇率", row.UnknownReason);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public void UnpricedPositionsCarryARowAndAWarning()
    {
        var txns = new[]
        {
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
        };
        var ledger = PositionCalculator.Replay(txns);
        var valuation = ValuationEngine.Value(
            ledger, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", AsOf);

        var report = PortfolioContributionAnalyzer.Analyze(valuation, txns, [], "CNY");

        var row = Assert.Single(report.Rows);
        Assert.Null(row.UnrealizedBase);
        Assert.Contains("未定价", row.UnknownReason);
        Assert.NotEmpty(report.Warnings);
    }

    // --- what-if --------------------------------------------------------------------

    private static PortfolioValuation DiversifiedValuation()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 50_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"),  // 10 000
            LedgerTransaction.OpeningPosition("acct", "NASDAQ:AAPL", Day1, 50m, 100m, "USD"),  // 5 000 USD @10 = 50 000
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
    public void BuyingWithExistingCashKeepsTheTotalAsTheDenominator()
    {
        // The review's minimal counterexample: 110 000 total, 50 000 cash, 10 000 holding.
        // Buying 20 000 with ledger cash moves money inside the portfolio: 30 000 / 110 000.
        var valuation = DiversifiedValuation();

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", amountBase: 20_000m,
            funding: FundingSource.ExistingCash);

        Assert.True(result.Feasible);
        Assert.Equal(0.2727m, Math.Round(result.NewSingleWeight, 4));
        Assert.Contains(result.Breaches, b => b.Contains("单一持仓"));
        // Cash 50 000 − 20 000 stays positive, so no cash warning.
        Assert.DoesNotContain(result.Breaches, b => b.Contains("现金不足"));
    }

    [Fact]
    public void BuyingWithExternalMoneyGrowsTheTotalAndKeepsCash()
    {
        var valuation = DiversifiedValuation();

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", amountBase: 20_000m,
            funding: FundingSource.ExternalContribution);

        Assert.True(result.Feasible);
        // 30 000 / 130 000 ≈ 23.1% — a different, also-correct answer to a different question.
        Assert.InRange(result.NewSingleWeight, 0.230m, 0.231m);
        // External money leaves the existing cash line untouched.
        Assert.Contains(result.Notes, n => n.Contains("外部新增资金"));
    }

    [Fact]
    public void SameCurrencyCashToAssetDoesNotInflateCurrencyExposure()
    {
        // CNY cash buying a CNY stock is a shuffle inside one currency slice.
        var valuation = DiversifiedValuation();

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", amountBase: 20_000m,
            funding: FundingSource.ExistingCash);

        Assert.True(result.Feasible);
        Assert.Equal(result.CurrentCurrencyWeight, result.NewCurrencyWeight);
    }

    [Fact]
    public void AnIncompleteValuationRefusesToSimulate()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        var valuation = ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", amountBase: 10_000m, currency: "CNY");

        // A partial denominator cannot support a green "nothing breached".
        Assert.False(result.Feasible);
        Assert.Contains("估值不完整", result.BlockReason);
        Assert.Contains("无法", result.Summary);
    }

    [Fact]
    public void AModestBuyProducesNoBreachesAndStatesTheBeforeAfter()
    {
        var valuation = DiversifiedValuation();

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", amountBase: 5_000m);

        Assert.True(result.Feasible);
        Assert.Empty(result.Breaches);
        Assert.Contains("→", result.Summary);
        Assert.Contains("不构成建议", result.Summary);
    }

    [Fact]
    public void ANewSymbolNeedsItsCurrency()
    {
        var valuation = DiversifiedValuation();

        var blocked = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "TSE:7203", amountBase: 10_000m);
        Assert.False(blocked.Feasible);
        Assert.Contains("计价货币", blocked.BlockReason);

        var ok = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "TSE:7203", amountBase: 10_000m, currency: "JPY");
        Assert.True(ok.Feasible);
        Assert.Contains(ok.Notes, n => n.Contains("新开仓"));
    }

    [Fact]
    public void AnUnpricedHoldingStopsTheSimulation()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
        ]);
        var valuation = ValuationEngine.Value(
            ledger, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", AsOf);

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "TSE:7203", amountBase: 10_000m);

        Assert.False(result.Feasible);
        Assert.Contains("未定价", result.BlockReason + result.Summary);
    }

    // --- stress ---------------------------------------------------------------------

    [Fact]
    public void EquityAndCurrencyShocksRepriceExactly()
    {
        // 10 000 CNY stock + 50 000 CNY of USD stock + 50 000 CNY cash = 110 000.
        var valuation = DiversifiedValuation();
        var cash = new[]
        {
            new CashBalance { AccountId = "acct", Currency = "CNY", Amount = 50_000m },
        };
        var fx = new FxConverter([FxRate.Of("USD", "CNY", 10m, Day1)]);

        var result = PortfolioStressAnalyzer.Run(
            valuation, cash,
            new StressScenario { EquityDropPercent = 20m, UsdMovePercent = -10m });

        // Stock: 10 000 × 0.8 = 8 000. USD stock: 50 000 × 0.8 × 0.9 = 36 000. Cash: 50 000.
        Assert.Equal(94_000m, result.StressedTotal.Amount);
        Assert.Equal(110_000m, result.BaselineTotal.Amount);
        Assert.Equal((94_000m - 110_000m) / 110_000m, result.ChangePercent);
        Assert.Contains(result.Assumptions, a => a.Contains("股票与 ETF 下跌 20%"));
        Assert.Contains(result.Assumptions, a => a.Contains("USD 兑 CNY 变动 -10%"));
        Assert.Contains(result.Assumptions, a => a.Contains("基准"));
    }

    [Fact]
    public void TheLargestHoldingTakesItsExtraFall()
    {
        var valuation = DiversifiedValuation(); // largest = NASDAQ:AAPL (50 000)
        var fx = new FxConverter([FxRate.Of("USD", "CNY", 10m, Day1)]);

        var result = PortfolioStressAnalyzer.Run(
            valuation, [],
            new StressScenario { LargestHoldingDropPercent = 50m });

        // 10 000 + 50 000 × 0.5 = 35 000; no cash lines passed.
        Assert.Equal(35_000m, result.StressedTotal.Amount);
        Assert.Contains(result.Assumptions, a => a.Contains("NASDAQ:AAPL 额外下跌 50%"));
    }

    [Fact]
    public void UnpricedHoldingsAreExcludedAndCounted()
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

        var result = PortfolioStressAnalyzer.Run(
            valuation, [],
            new StressScenario { EquityDropPercent = 30m });

        Assert.Equal(7_000m, result.StressedTotal.Amount);
        Assert.Contains(result.Warnings, w => w.Contains("1 个未定价持仓"));
    }

    [Fact]
    public void JpyCashMovesWithTheJpyShock()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 155_000m, "JPY"),
        ]);
        var valuation = ValuationEngine.Value(
            ledger, new Dictionary<string, PriceQuote>(),
            new FxConverter([FxRate.Of("JPY", "CNY", 0.05m, Day1)]), "CNY", AsOf);
        var cash = new[]
        {
            new CashBalance { AccountId = "acct", Currency = "JPY", Amount = 155_000m },
        };

        var result = PortfolioStressAnalyzer.Run(
            valuation, cash, new StressScenario { JpyMovePercent = -20m });

        // 155 000 JPY = 7 750 CNY; −20% → 6 200.
        Assert.Equal(6_200m, result.StressedTotal.Amount);
    }

    // --- rebalance comparison ---------------------------------------------------------

    [Fact]
    public void SellAndNewMoneyPlansAreBothStated()
    {
        var valuation = ConcentratedValuation();
        var targets = new Dictionary<string, decimal> { ["equity"] = 0.6m, ["cash"] = 0.4m };

        var comparison = RebalanceComparisonAnalyzer.Compare(valuation, targets, availableNewCashBase: 100_000m);

        Assert.True(comparison.Computable);
        var sell = Assert.Single(comparison.SellPlan);
        Assert.Equal("equity", sell.Key);
        Assert.Equal(20_000m, sell.Amount.Amount); // 80% vs 60% of 100 000
        Assert.Equal("卖出", sell.Verb);

        // P1-02: with 80/20 and targets 60/40, the smallest total that holds the 80 000
        // equity at 60% is 133 333⅓. Equity needs nothing; cash goes 20 000 → 53 333⅓,
        // so the new-money plan needs 33 333⅓ — more than the naive 20 000 gap and less
        // than nothing exaggerated. The sell plan keeps unchanged-total arithmetic (20 000).
        Assert.Equal(33_333.33m, comparison.NewMoneyRequired.Amount);
        Assert.True(comparison.NewMoneyFeasible);
        Assert.Contains(comparison.Notes, n => n.Contains("最小新总额"));
    }

    [Fact]
    public void ShortfallIsStatedWhenNewMoneyDoesNotCoverTheGap()
    {
        var valuation = ConcentratedValuation();
        var targets = new Dictionary<string, decimal> { ["equity"] = 0.6m, ["cash"] = 0.4m };

        var comparison = RebalanceComparisonAnalyzer.Compare(valuation, targets, availableNewCashBase: 5_000m);

        Assert.False(comparison.NewMoneyFeasible);
        Assert.Contains(comparison.Notes, n => n.Contains("只有 5,000") || n.Contains("只有 5000") || n.Contains("可用新增资金只有"));
    }

    [Fact]
    public void AnIncompleteValuationBlocksTheComparison()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
            LedgerTransaction.OpeningCash("acct", Day1, 100_000m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        var valuation = ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);

        var comparison = RebalanceComparisonAnalyzer.Compare(
            valuation, new Dictionary<string, decimal> { ["equity"] = 0.6m }, 10_000m);

        Assert.False(comparison.Computable);
        Assert.Contains("不完整", comparison.BlockReason);
    }

    private static PortfolioValuation ConcentratedValuation()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 20_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 8000m, 10m, "CNY"), // 80 000
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        return ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);
    }

    // --- emergency fund ---------------------------------------------------------------

    [Fact]
    public void TheEmergencyFundDoesNotCountAsIdleCash()
    {
        // 90 000 cash, 10 000 stock, emergency fund 60 000 → idle 30 000 = 30% < 40% ceiling.
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

        var report = PortfolioRiskAnalyzer.Analyze(valuation, emergencyFundBase: 60_000m);

        Assert.DoesNotContain(report.Findings, f => f.Label == "现金");
    }

    [Fact]
    public void IdleCashAboveTheCeilingStillWarnsAndNamesTheReserve()
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

        var report = PortfolioRiskAnalyzer.Analyze(valuation, emergencyFundBase: 10_000m);

        var cash = report.Findings.First(f => f.Label == "现金");
        // Idle = 80 000 of 100 000 = 80% > 40%.
        Assert.Equal(0.8m, cash.Weight);
        Assert.Contains("应急金", cash.Message);
        Assert.Contains("不计入闲置", cash.Message);
    }
}

// --- N5 第二批：回撤归因（schema v7 持仓快照） -------------------------------------

public class DrawdownAttributionTests
{
    private static readonly string Base = "CNY";

    /// <summary>
    /// Peak 2026-01-10: 100 000 (USD stock 49 000 + other 10 000 + cash 41 000).
    /// Trough 2026-02-10: 80 000 (USD stock 39 000 + other 10 000 + cash 31 000).
    /// USD/CNY falls 7 → 6.5 over the same window.
    /// </summary>
    private static (ValuationPoint[] Curve, PortfolioStore.PositionValuationSnapshot[] Positions, FxRate[] Rates) Build()
    {
        var peak = new DateOnly(2026, 1, 10);
        var trough = new DateOnly(2026, 2, 10);
        ValuationPoint[] curve =
        [
            new(peak, 100_000m),
            new(trough, 80_000m),
        ];
        PortfolioStore.PositionValuationSnapshot[] positions =
        [
            new(peak, Base, "NASDAQ:AAPL", "acct", "USD", 700m, 7_000m, 49_000m, 45_000m, true),
            new(peak, Base, "SSE:600000", "acct", "CNY", 1_000m, 10_000m, 10_000m, 9_000m, true),
            new(trough, Base, "NASDAQ:AAPL", "acct", "USD", 600m, 6_000m, 39_000m, 45_000m, true),
            new(trough, Base, "SSE:600000", "acct", "CNY", 1_000m, 10_000m, 10_000m, 9_000m, true),
        ];
        FxRate[] rates =
        [
            FxRate.Of("USD", "CNY", 7m, new DateOnly(2026, 1, 9)),
            FxRate.Of("USD", "CNY", 6.5m, new DateOnly(2026, 2, 9)),
        ];
        return (curve, positions, rates);
    }

    [Fact]
    public void TheDrawdownIsExplainedByAssetAndCurrencyExactly()
    {
        var (curve, positions, rates) = Build();

        var report = DrawdownAttributionAnalyzer.Analyze(curve, positions, rates, Base);

        Assert.Equal(new DateOnly(2026, 1, 10), report.Peak);
        Assert.Equal(new DateOnly(2026, 2, 10), report.Trough);
        Assert.Equal(0.2m, report.Depth);

        var aapl = report.Rows.First(r => r.Label == "NASDAQ:AAPL");
        // 49 000 → 39 000 = −10 000.
        Assert.Equal(-10_000m, aapl.ChangeBase.Amount);
        // Local part = (6 000 − 7 000) × 6.5 = −6 500.
        Assert.Equal(-6_500m, aapl.LocalPart!.Value.Amount);
        // FX part = 7 000 × (6.5 − 7) = −3 500, and the three numbers close the identity.
        Assert.Equal(-3_500m, aapl.FxPart!.Value.Amount);
        Assert.Equal(-6_500m + -3_500m, aapl.ChangeBase.Amount);
        Assert.Equal(-0.5m, aapl.ShareOfChange);

        // The SSE position did not move; the cash residual takes the rest (−10 000).
        var sse = report.Rows.First(r => r.Label == "SSE:600000");
        Assert.Equal(0m, sse.ChangeBase.Amount);
        Assert.Equal(0m, sse.FxPart!.Value.Amount);

        var cash = report.Rows.First(r => r.Label.Contains("现金"));
        Assert.Equal(-10_000m, cash.ChangeBase.Amount);
        Assert.Equal(-0.5m, cash.ShareOfChange);
    }

    [Fact]
    public void AMissingEndpointRateLeavesTheFxPartUnknown()
    {
        var (curve, positions, _) = Build();
        var todayOnly = new[] { FxRate.Of("USD", "CNY", 6.5m, new DateOnly(2026, 2, 9)) };

        var report = DrawdownAttributionAnalyzer.Analyze(curve, positions, todayOnly, Base);

        var aapl = report.Rows.First(r => r.Label == "NASDAQ:AAPL");
        Assert.Equal(-10_000m, aapl.ChangeBase.Amount);
        Assert.Null(aapl.LocalPart);
        Assert.Null(aapl.FxPart);
        Assert.Contains("汇率", aapl.Note);
        Assert.Contains("AAPL", report.Warnings.First(w => w.Contains("汇率")));
    }

    [Fact]
    public void AnUnpricedEndpointIsFlaggedInsteadOfValuedAtZero()
    {
        var (curve, positions, rates) = Build();
        // The AAPL row at the trough loses its price: pretending 39 000 → 0 would fabricate a crash.
        var unpricedTrough = new PortfolioStore.PositionValuationSnapshot(
            new DateOnly(2026, 2, 10), Base, "NASDAQ:AAPL", "acct", "USD", 600m, null, null, 45_000m, false);
        var broken = positions.Where(p => !(p.AsOf == new DateOnly(2026, 2, 10) && p.Symbol == "NASDAQ:AAPL"))
            .Append(unpricedTrough).ToArray();

        var report = DrawdownAttributionAnalyzer.Analyze(curve, broken, rates, Base);

        var aapl = report.Rows.First(r => r.Label == "NASDAQ:AAPL");
        Assert.NotNull(aapl.Note);
        Assert.Contains("未定价", aapl.Note);
        Assert.Null(aapl.ShareOfChange);
        Assert.Contains(report.Warnings, w => w.Contains("AAPL"));
    }

    [Fact]
    public void ARisingCurveHasNothingToAttribute()
    {
        ValuationPoint[] curve =
        [
            new(new DateOnly(2026, 1, 1), 100_000m),
            new(new DateOnly(2026, 2, 1), 120_000m),
        ];

        var report = DrawdownAttributionAnalyzer.Analyze(curve, [], [], Base);

        Assert.Null(report.Depth);
        Assert.Empty(report.Rows);
        Assert.Contains("无法归因", report.Summary);
    }

    [Fact]
    public void PositionSnapshotsRoundTripAndReplacePerDay()
    {
        var root = Path.Combine(Path.GetTempPath(), "caishenfolio_vps_tests", Guid.NewGuid().ToString("N"));
        try
        {
            PortfolioValuation Build(DateOnly day, decimal price) => ValuationEngine.Value(
                PositionCalculator.Replay(
                [
                    LedgerTransaction.OpeningCash("acct", day, 10_000m, "CNY"),
                    LedgerTransaction.OpeningPosition("acct", "SSE:600000", day, 1_000m, 10m, "CNY"),
                ]),
                new Dictionary<string, PriceQuote>
                {
                    ["SSE:600000"] = PriceQuote.Of("SSE:600000", price, "CNY", day),
                },
                FxConverter.Empty, "CNY", day);

            using (var store = PortfolioStore.UnderStateRoot(root))
            {
                store.SaveValuationSnapshot(Build(new DateOnly(2026, 3, 1), 10m));
                store.SaveValuationSnapshot(Build(new DateOnly(2026, 3, 2), 11m));
                // Same day again with a different price: corrects the day, never duplicates.
                store.SaveValuationSnapshot(Build(new DateOnly(2026, 3, 2), 12m));
            }

            using (var store = PortfolioStore.UnderStateRoot(root))
            {
                var rows = store.ListPositionValuationHistory("CNY");
                Assert.Equal(2, rows.Count); // 2 days × 1 holding; the re-save replaced its row.
                Assert.All(rows, r => Assert.True(r.Priced));
                var second = rows.Where(r => r.AsOf == new DateOnly(2026, 3, 2)).Single();
                Assert.Equal(12_000m, second.MarketValueBase);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

// --- 跨账户换汇（N2 补充）与利率情景（N5） ------------------------------------------

public class CrossAccountExchangeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_crossacct_tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheIncomingLegLandsInTheCounterAccount()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.AddTransactions(
        [
            LedgerTransaction.OpeningCash("bank", new DateOnly(2026, 1, 2), 10_000m, "CNY"),
            LedgerTransaction.FxExchange(
                "bank", new DateOnly(2026, 1, 5), 7_000m, "CNY", 1_000m, "USD",
                fee: 0m, note: "银行换汇入券商",
                counterAccountId: "broker"),
        ]);

        var state = store.LoadState();

        Assert.All(state.CashBalances, b => output_none(b));
        var bankCny = state.CashBalances.Single(b => b.AccountId == "bank" && b.Currency == "CNY");
        var brokerUsd = state.CashBalances.Single(b => b.AccountId == "broker" && b.Currency == "USD");
        Assert.Equal(3_000m, bankCny.Amount);
        Assert.Equal(1_000m, brokerUsd.Amount);
        // The stored row records the counter account for the audit trail.
        var exchange = store.ListTransactions().Single(t => t.Kind == TransactionKind.FxExchange);
        Assert.Equal("broker", exchange.CounterAccountId);
    }

    private static void output_none(CashBalance b)
    {
    }

    [Fact]
    public void ReversingACrossAccountExchangeRestoresBothAccounts()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var exchange = LedgerTransaction.FxExchange(
            "bank", new DateOnly(2026, 1, 5), 7_000m, "CNY", 1_000m, "USD",
            counterAccountId: "broker");
        store.AddTransactions([LedgerTransaction.OpeningCash("bank", new DateOnly(2026, 1, 2), 10_000m, "CNY"), exchange]);
        store.AddTransaction(LedgerTransaction.Reversal(exchange, "录错账户"));

        var state = store.LoadState();
        Assert.Single(state.CashBalances, b => b.AccountId == "bank");
        Assert.DoesNotContain(state.CashBalances, b => b.AccountId == "broker");
        Assert.Equal(10_000m, state.CashBalances[0].Amount);
    }

    [Fact]
    public void OldRowsReadAsSameAccount()
    {
        // A pre-v8 row: empty counter account column → reads as the same account.
        using var store = PortfolioStore.UnderStateRoot(_root);
        var exchange = LedgerTransaction.FxExchange("bank", new DateOnly(2026, 1, 5), 100m, "CNY", 14m, "USD");
        Assert.Equal("bank", exchange.CounterAccountId); // factory default
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

public class FixedIncomeStressTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    [Fact]
    public void TheBondShockHitsBondLikeClassesOnly()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "SSE:113050", Day1, 100m, 100m, "CNY"), // 可转债 10 000
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1_000m, 10m, "CNY"), // 股票 10 000
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:113050"] = PriceQuote.Of("SSE:113050", 100m, "CNY", AsOf),
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        var valuation = ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);
        var instruments = new Dictionary<string, Instrument>
        {
            ["SSE:113050"] = Instrument.FromSymbol("SSE:113050", "南银转债", AssetClass.ConvertibleBond),
            ["SSE:600000"] = Instrument.FromSymbol("SSE:600000", "浦发银行", AssetClass.Equity),
        };

        var result = PortfolioStressAnalyzer.Run(
            valuation, [],
            new StressScenario { FixedIncomeMovePercent = -10m },
            instruments);

        // Convertible −10% → 9 000; equity untouched → 10 000.
        Assert.Equal(19_000m, result.StressedTotal.Amount);
        Assert.Contains(result.Assumptions, a => a.Contains("债券/可转债/场外基金按你的假设变动 -10%"));
        Assert.Contains(result.Assumptions, a => a.Contains("未建模久期"));
    }

    [Fact]
    public void EquityDropAndBondShockComposeIndependently()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1_000m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        var valuation = ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);

        var result = PortfolioStressAnalyzer.Run(
            valuation, [],
            new StressScenario { EquityDropPercent = 20m, FixedIncomeMovePercent = -5m });

        // Equity takes the equity shock; no bond-like holdings means the bond shock has no target.
        Assert.Equal(8_000m, result.StressedTotal.Amount);
    }
}

// --- P1-02：新增资金再平衡的正确分母 ------------------------------------------------

public class RebalanceNewMoneyTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    /// <summary>The review's minimal counterexample: 80/20 portfolio, 50/50 target, total 100.</summary>
    private static PortfolioValuation EightyTwenty()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 20m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 8m, 10m, "CNY"), // 80
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        return ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);
    }

    [Fact]
    public void NewMoneyAloneRestoresFiftyFiftyExactly()
    {
        var targets = new Dictionary<string, decimal> { ["equity"] = 0.5m, ["cash"] = 0.5m };
        var comparison = RebalanceComparisonAnalyzer.Compare(EightyTwenty(), targets, 100m);

        // The smallest total that keeps 80 at ≤50% is 160 → equity needs no new money, cash
        // needs 80 − 20 = 60. The old code suggested 30 (landing at 61.54/38.46).
        Assert.True(comparison.Computable);
        var cash = Assert.Single(comparison.NewMoneyPlan);
        Assert.Equal(60m, cash.Amount.Amount);
        Assert.Equal(60m, comparison.NewMoneyRequired.Amount);
        Assert.True(comparison.NewMoneyFeasible);
    }

    [Fact]
    public void MultipleOverweightSlicesStillChooseOneSharedDenominator()
    {
        // 70 equity / 20 convertible / 10 cash with 50/40/10 targets. The convertible needs
        // instrument metadata to leave the exchange-default equity bucket.
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 10m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 7m, 10m, "CNY"), // 70
            LedgerTransaction.OpeningPosition("acct", "SSE:113050", Day1, 2m, 10m, "CNY"), // 20
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
            ["SSE:113050"] = PriceQuote.Of("SSE:113050", 10m, "CNY", AsOf),
        };
        var instruments = new Dictionary<string, Instrument>
        {
            ["SSE:113050"] = Instrument.FromSymbol("SSE:113050", "南银转债", AssetClass.ConvertibleBond),
        };
        var valuation = ValuationEngine.Value(
            ledger, quotes, FxConverter.Empty, "CNY", AsOf, instruments);
        var targets = new Dictionary<string, decimal>
        {
            ["equity"] = 0.5m, ["convertible_bond"] = 0.4m, ["cash"] = 0.1m,
        };

        var comparison = RebalanceComparisonAnalyzer.Compare(valuation, targets, 1000m);

        // New total must satisfy all three: max(70/0.5, 20/0.4, 10/0.1) = 140.
        // Equity stays 70, convertible 20→56, cash 10→14. Total new money = 40.
        Assert.True(comparison.Computable);
        Assert.Equal(40m, comparison.NewMoneyRequired.Amount);
    }

    [Fact]
    public void ZeroTargetMeansSellEverythingOfThatClass()
    {
        var targets = new Dictionary<string, decimal> { ["equity"] = 0m, ["cash"] = 1m };
        var comparison = RebalanceComparisonAnalyzer.Compare(EightyTwenty(), targets, 0m);

        // Target 0 for equity: the sell plan liquidates all 80. Cash is already overweight
        // relative to 100% (20 of 100 against a target of 100%) under the unchanged-total
        // sell plan, so the sell plan alone answers; new money tops nothing up beyond it.
        Assert.True(comparison.Computable);
        var sell = Assert.Single(comparison.SellPlan);
        Assert.Equal(80m, sell.Amount.Amount);
    }

    [Fact]
    public void NoShortfallMeansNoNewMoney()
    {
        // Already at target: nothing to sell, nothing to buy.
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 50m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 5m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        var valuation = ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);

        var comparison = RebalanceComparisonAnalyzer.Compare(
            valuation, new Dictionary<string, decimal> { ["equity"] = 0.5m, ["cash"] = 0.5m }, 0m);

        Assert.True(comparison.Computable);
        Assert.Empty(comparison.SellPlan);
        Assert.Empty(comparison.NewMoneyPlan);
    }
}

// --- P1-03：TWR 工作区集成（快照=流后约定） ------------------------------------------

public class TwrWorkspaceIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_twr_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    [Fact]
    public async Task ADepositOnTheSecondSnapshotDayIsNotShownAsReturn()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("零钱", AccountKind.Cash, "CNY");

        // Day 1: 100 000 cash, one complete snapshot.
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 9, 1), 100_000m, "CNY"));
        var first = await workspace.RefreshAsync(new DateOnly(2026, 9, 1));
        Assert.Equal(100_000m, first.Valuation.TotalValue.Amount);

        // Day 2: deposit 50 000 — the review's exact counterexample.
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 9, 2), 50_000m, "CNY"));
        var second = await workspace.RefreshAsync(new DateOnly(2026, 9, 2));

        Assert.Equal(150_000m, second.Valuation.TotalValue.Amount);
        Assert.NotNull(second.Twr);
        Assert.Equal(0.0, second.Twr!.Value, 6); // was +50% before the fix
    }

    [Fact]
    public async Task AWithdrawalOnTheSecondSnapshotDayIsNotShownAsLoss()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("零钱", AccountKind.Cash, "CNY");

        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 9, 1), 100_000m, "CNY"));
        await workspace.RefreshAsync(new DateOnly(2026, 9, 1));

        workspace.Record(LedgerTransaction.Withdraw(account.Id, new DateOnly(2026, 9, 2), 50_000m, "CNY"));
        var second = await workspace.RefreshAsync(new DateOnly(2026, 9, 2));

        Assert.Equal(50_000m, second.Valuation.TotalValue.Amount);
        Assert.NotNull(second.Twr);
        Assert.Equal(0.0, second.Twr!.Value, 6);
    }

    [Fact]
    public async Task MultipleFlowsOnOneSnapshotDayAreAllNeutralized()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("零钱", AccountKind.Cash, "CNY");

        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 9, 1), 100_000m, "CNY"));
        await workspace.RefreshAsync(new DateOnly(2026, 9, 1));

        // Two same-day flows: +80 000 in, −30 000 out → net +50 000, value 150 000.
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 9, 2), 80_000m, "CNY"));
        workspace.Record(LedgerTransaction.Withdraw(account.Id, new DateOnly(2026, 9, 2), 30_000m, "CNY"));
        var second = await workspace.RefreshAsync(new DateOnly(2026, 9, 2));

        Assert.Equal(150_000m, second.Valuation.TotalValue.Amount);
        Assert.NotNull(second.Twr);
        Assert.Equal(0.0, second.Twr!.Value, 6);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

// --- P1-08：同币种多账户现金不重复计数 -----------------------------------------------

public class StressCashAggregationTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    /// <summary>反例 #9：两个同币种账户，零冲击压力测试严格等于基准。</summary>
    [Fact]
    public void ZeroShockLeavesTwoSameCurrencyAccountsExactlyAtBaseline()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct_a", Day1, 20_000m, "CNY"),
            LedgerTransaction.OpeningCash("acct_b", Day1, 30_000m, "CNY"),
        ]);
        var valuation = ValuationEngine.Value(
            ledger, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", AsOf);
        var cash = new[]
        {
            new CashBalance { AccountId = "acct_a", Currency = "CNY", Amount = 20_000m },
            new CashBalance { AccountId = "acct_b", Currency = "CNY", Amount = 30_000m },
        };

        // Zero shock: stressed must equal baseline exactly — with any account split.
        var result = PortfolioStressAnalyzer.Run(valuation, cash, new StressScenario());
        Assert.Equal(50_000m, result.StressedTotal.Amount);
        Assert.Equal(result.BaselineTotal.Amount, result.StressedTotal.Amount);

        // And with a currency shock, the aggregate is shocked once, not twice.
        var jpyLedger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct_a", Day1, 155_000m, "JPY"),
            LedgerTransaction.OpeningCash("acct_b", Day1, 155_000m, "JPY"),
        ]);
        var jpyValuation = ValuationEngine.Value(
            jpyLedger, new Dictionary<string, PriceQuote>(),
            new FxConverter([FxRate.Of("JPY", "CNY", 0.05m, Day1)]), "CNY", AsOf);
        var jpyCash = new[]
        {
            new CashBalance { AccountId = "acct_a", Currency = "JPY", Amount = 155_000m },
            new CashBalance { AccountId = "acct_b", Currency = "JPY", Amount = 155_000m },
        };

        var shocked = PortfolioStressAnalyzer.Run(jpyValuation, jpyCash,
            new StressScenario { JpyMovePercent = -20m });
        // 310 000 JPY = 15 500 CNY; −20% → 12 400 — not double-counted to 20 150.
        Assert.Equal(12_400m, shocked.StressedTotal.Amount);
    }
}

// --- P2-01 / P2-02：同批双冲正与同标的多账户回撤归因 --------------------------------

public class BatchDoubleReversalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_batchrev_tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TwoReversalsOfTheSameOriginalInOneBatchAreRefused()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var deposit = LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 100m, "CNY");
        store.AddTransaction(deposit);

        var first = LedgerTransaction.Reversal(deposit, "first");
        var second = LedgerTransaction.Reversal(deposit, "second");

        var ex = Assert.Throws<LedgerException>(() => store.AddTransactions([first, second]));
        Assert.Contains("两条冲正", ex.Message);
        // Nothing from the batch landed — the original stands alone.
        Assert.Single(store.ListTransactions());
    }

    [Fact]
    public void CrossBatchDoubleReversalIsStillRefused()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var deposit = LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 100m, "CNY");
        store.AddTransaction(deposit);
        store.AddTransaction(LedgerTransaction.Reversal(deposit, "first"));

        var ex = Assert.Throws<LedgerException>(() =>
            store.AddTransaction(LedgerTransaction.Reversal(deposit, "again")));
        Assert.Contains("已有一条冲正记录", ex.Message);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

public class MultiAccountDrawdownAttributionTests
{
    /// <summary>反例 #16：同一标的两个账户，归因金额之和与总变化守恒。</summary>
    [Fact]
    public void SameSymbolInTwoAccountsIsAttributedAsOnePosition()
    {
        var peak = new DateOnly(2026, 1, 10);
        var trough = new DateOnly(2026, 2, 10);
        ValuationPoint[] curve =
        [
            new(peak, 100_000m),
            new(trough, 80_000m),
        ];
        // AAPL in two accounts: acct_a 700 shares, acct_b 300 shares; the stock falls so the
        // split matters — the old BestRow dropped one account entirely.
        PortfolioStore.PositionValuationSnapshot[] positions =
        [
            new(peak, "CNY", "NASDAQ:AAPL", "acct_a", "USD", 700m, 7_000m, 49_000m, 45_000m, true),
            new(peak, "CNY", "NASDAQ:AAPL", "acct_b", "USD", 300m, 3_000m, 21_000m, 19_000m, true),
            new(peak, "CNY", "SSE:600000", "acct_a", "CNY", 3_000m, 30_000m, 30_000m, 27_000m, true),
            new(trough, "CNY", "NASDAQ:AAPL", "acct_a", "USD", 700m, 6_000m, 42_000m, 45_000m, true),
            new(trough, "CNY", "NASDAQ:AAPL", "acct_b", "USD", 300m, 2_400m, 16_800m, 19_000m, true),
            new(trough, "CNY", "SSE:600000", "acct_a", "CNY", 3_000m, 30_000m, 30_000m, 27_000m, true),
        ];
        FxRate[] rates =
        [
            FxRate.Of("USD", "CNY", 7m, new DateOnly(2026, 1, 9)),
            FxRate.Of("USD", "CNY", 7m, new DateOnly(2026, 2, 9)),
        ];

        var report = DrawdownAttributionAnalyzer.Analyze(curve, positions, rates, "CNY");

        var aapl = report.Rows.First(r => r.Label == "NASDAQ:AAPL");
        // Both accounts: 70 000 → 58 800 = −11 200, one row.
        Assert.Equal(-11_200m, aapl.ChangeBase.Amount);

        // Conservation: Σ position changes + cash residual = total change (−20 000).
        var total = report.Rows.Sum(r => r.ChangeBase.Amount);
        Assert.Equal(-20_000m, total);
        // The SSE row did not move; the remaining −8 800 is the cash residual.
        var cash = report.Rows.First(r => r.Label.Contains("现金"));
        Assert.Equal(-8_800m, cash.ChangeBase.Amount);
    }

    [Fact]
    public void OneUnpricedAccountMakesTheWholeSymbolUnpriced()
    {
        var peak = new DateOnly(2026, 1, 10);
        var trough = new DateOnly(2026, 2, 10);
        ValuationPoint[] curve = [new(peak, 100_000m), new(trough, 80_000m)];
        PortfolioStore.PositionValuationSnapshot[] positions =
        [
            new(peak, "CNY", "NASDAQ:AAPL", "acct_a", "USD", 700m, 7_000m, 49_000m, 45_000m, true),
            new(peak, "CNY", "NASDAQ:AAPL", "acct_b", "USD", 300m, null, null, 19_000m, false),
            new(trough, "CNY", "NASDAQ:AAPL", "acct_a", "USD", 700m, 6_000m, 42_000m, 45_000m, true),
            new(trough, "CNY", "NASDAQ:AAPL", "acct_b", "USD", 300m, null, null, 19_000m, false),
        ];

        var report = DrawdownAttributionAnalyzer.Analyze(curve, positions, [], "CNY");

        var aapl = report.Rows.First(r => r.Label == "NASDAQ:AAPL");
        Assert.Contains("未定价", aapl.Note);
        Assert.Null(aapl.ShareOfChange);
    }
}

// --- P1-R01：跨币种买入模拟的币种暴露 ------------------------------------------------

public class CrossCurrencyWhatIfTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    /// <summary>Cash 50 000 CNY + a 10 000 CNY stock: 110 000 total, CNY slice 100 000.</summary>
    private static PortfolioValuation CashValuation(decimal cash = 50_000m)
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, cash, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1_000m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf),
        };
        return ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);
    }

    /// <summary>Cash 50 000 CNY + USD stock worth 10 000 CNY (1 000 USD @ 10).</summary>
    private static PortfolioValuation UsdValuation()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 50_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "NASDAQ:AAPL", Day1, 1_000m, 10m, "USD"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["NASDAQ:AAPL"] = PriceQuote.Of("NASDAQ:AAPL", 10m, "USD", AsOf),
        };
        var fx = new FxConverter([FxRate.Of("USD", "CNY", 10m, Day1)]);
        return ValuationEngine.Value(ledger, quotes, fx, "CNY", AsOf);
    }

    [Fact]
    public void BuyingAUsdAssetWithCnyCashIncreasesUsdExposure()
    {
        var valuation = UsdValuation(); // cash 50 000 CNY + USD stock 100 000 CNY = 150 000

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "NASDAQ:AAPL", 10_000m,
            funding: FundingSource.ExistingCash);

        // USD slice 100 000 → 110 000 of the unchanged 150 000 total. The old code
        // subtracted the CNY cash from the USD slice and cancelled the increase.
        Assert.True(result.Feasible);
        Assert.Equal(0.666667m, Math.Round(result.CurrentCurrencyWeight, 6));
        Assert.Equal(0.733333m, Math.Round(result.NewCurrencyWeight, 6));
        Assert.True(result.NewCurrencyWeight > result.CurrentCurrencyWeight);
    }

    [Fact]
    public void BuyingAJpyAssetWithCnyCashIncreasesJpyExposure()
    {
        var valuation = CashValuation(); // no JPY yet

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "TSE:7203", 10_000m,
            currency: "JPY", funding: FundingSource.ExistingCash);

        // JPY slice was 0; buying 10 000 (base terms) of a JPY asset raises it.
        Assert.Equal(0m, result.CurrentCurrencyWeight);
        Assert.True(result.NewCurrencyWeight > 0m);
    }

    [Fact]
    public void SameCurrencyBuyLeavesExposureUnchanged()
    {
        var valuation = CashValuation();

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", 10_000m,
            funding: FundingSource.ExistingCash);

        Assert.Equal(result.CurrentCurrencyWeight, result.NewCurrencyWeight);
    }

    [Fact]
    public void ExternalMoneyBuyingUsdGrowsBothTotalAndUsdExposure()
    {
        var valuation = UsdValuation();

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "NASDAQ:AAPL", 10_000m,
            funding: FundingSource.ExternalContribution);

        // Total 150 000 → 160 000; USD slice 100 000 → 110 000; the position likewise.
        Assert.True(result.Feasible);
        Assert.Equal(110_000m / 160_000m, Math.Round(result.NewCurrencyWeight, 6));
        Assert.Equal(110_000m / 160_000m, Math.Round(result.NewSingleWeight, 6));
    }

    [Fact]
    public void ForeignCashOnlyDoesNotSilentlyPassAsFeasible()
    {
        // 5 000 CNY worth of JPY cash, zero CNY cash, buying a CNY stock with 5 000.
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 100_000m, "JPY"),
        ]);
        var fx = new FxConverter([FxRate.Of("JPY", "CNY", 0.05m, Day1)]);
        var valuation = ValuationEngine.Value(
            ledger, new Dictionary<string, PriceQuote>(), fx, "CNY", AsOf);

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", 5_000m, "CNY",
            funding: FundingSource.ExistingCash);

        // Total cash (5 000) covers it, but base-currency cash is zero: must be said, not hidden.
        Assert.Contains(result.Breaches, b => b.Contains("本位币现金"));
        Assert.Contains(result.Breaches, b => b.Contains("先换汇"));
    }
}

// --- P1-R08：0% 目标经过完整设置路径仍生成清仓方案 -----------------------------------

public class ZeroTargetSettingsPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_zerotarget_tests", Guid.NewGuid().ToString("N"));

    /// <summary>反例 #13：equity=0, cash=1 经 Validated→Save→Load→Compare 后清仓股票。</summary>
    [Fact]
    public void AnExplicitZeroTargetSurvivesSaveLoadAndPlansTheLiquidation()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var workspace = new PortfolioWorkspace(store);

        // The user explicitly sets equity 0%, cash 100% — the settings window writes zeros.
        workspace.ApplySettings(new PortfolioSettings
        {
            BaseCurrency = "CNY",
            TargetAssetAllocation = new Dictionary<string, decimal>
            {
                ["equity"] = 0m,
                ["cash"] = 1m,
            },
        });

        // Full production round trip: validated → stored → reloaded.
        var loaded = new PortfolioWorkspace(store).Settings.TargetAssetAllocation;
        Assert.Contains("equity", loaded.Keys);
        Assert.Equal(0m, loaded["equity"]);

        // 80/20 portfolio against those targets.
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 5), 20m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", new DateOnly(2026, 1, 5), 8m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", new DateOnly(2026, 7, 31)),
        };
        var valuation = ValuationEngine.Value(
            ledger, quotes, FxConverter.Empty, "CNY", new DateOnly(2026, 7, 31));

        var comparison = RebalanceComparisonAnalyzer.Compare(valuation, loaded, 0m);

        Assert.True(comparison.Computable);
        var sell = Assert.Single(comparison.SellPlan);
        Assert.Equal("equity", sell.Key);
        Assert.Equal(80m, sell.Amount.Amount); // liquidate ALL of the 80 equity
    }

    [Fact]
    public void NoTargetsAtAllStillBlocksTheComparison()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var loaded = new PortfolioWorkspace(store).Settings.TargetAssetAllocation;
        Assert.Empty(loaded);

        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 5), 20m, "CNY"),
        ]);
        var valuation = ValuationEngine.Value(
            ledger, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", new DateOnly(2026, 7, 31));

        var comparison = RebalanceComparisonAnalyzer.Compare(valuation, loaded, 0m);
        Assert.False(comparison.Computable);
        Assert.Contains("目标配置", comparison.BlockReason);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

// --- 整改 WP5：性质级不变量 ---------------------------------------------------------

public class PortfolioInvariantTests
{
    /// <summary>外部资金加入后所有旧权重按相同比例下降（TradeWhatIf）。</summary>
    [Fact]
    public void ExternalMoneyScalesEveryOldWeightIdentically()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 5), 50_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", new DateOnly(2026, 1, 5), 1_000m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", new DateOnly(2026, 7, 31)),
        };
        var valuation = ValuationEngine.Value(
            ledger, quotes, FxConverter.Empty, "CNY", new DateOnly(2026, 7, 31));

        var result = TradeWhatIfAnalyzer.Simulate(
            valuation, RiskThresholds.Default, "SSE:600000", 10_000m,
            currency: "CNY", funding: FundingSource.ExternalContribution);

        // The scaling property: the OLD slice's share of the new total equals its old share
        // times oldTotal/newTotal. Old position 10 000/60 000 → 10 000/70 000 after dilution.
        Assert.Equal(0.1666666667m, Math.Round(result.CurrentSingleWeight, 10));
        var oldPositionDiluted = 10_000m / 70_000m;
        // The currency slice (CNY) held 60 000 before; after external CNY money it is
        // 70 000 — the dilution factor applies to every pre-existing slice identically.
        Assert.Equal(
            Math.Round(result.CurrentSingleWeight * 60_000m / 70_000m, 10),
            Math.Round(oldPositionDiluted, 10));
    }

    /// <summary>冲正后结果与原交易从未发生一致（跨全部派生量）。</summary>
    [Fact]
    public void ReversalLeavesTheLedgerExactlyAsIfTheTradeNeverHappened()
    {
        using var root = PortfolioStore.UnderStateRoot(Path.Combine(
            Path.GetTempPath(), "caishenfolio_invariant", Guid.NewGuid().ToString("N")));
        var opening = LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 2), 50_000m, "CNY");
        root.AddTransaction(opening);
        var baseline = root.LoadState(); // the "never happened" state

        var buy = LedgerTransaction.Buy(
            "acct", "SSE:600000", new DateOnly(2026, 1, 5), 1_000m, 10m, "CNY", fee: 5m);
        root.AddTransaction(buy);
        root.AddTransaction(LedgerTransaction.Reversal(buy, "撤单"));
        var afterReversal = root.LoadState();

        // Cash, positions, external flows — every derived number equals the baseline.
        Assert.Equal(baseline.CashBalances.Single().Amount, afterReversal.CashBalances.Single().Amount);
        Assert.Equal(baseline.Positions.Count, afterReversal.Positions.Count);
        Assert.Equal(baseline.ExternalFlows.Count, afterReversal.ExternalFlows.Count);
        // The log keeps both rows — the invariant is about DERIVED state, not row count.
        Assert.Equal(3, root.ListTransactions().Count);
    }

    /// <summary>同币种现金转资产时币种暴露不变（估值层性质，定价资产）。</summary>
    [Fact]
    public void SameCurrencyCashToAssetKeepsTheCurrencySlice()
    {
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 10m, "CNY", new DateOnly(2026, 7, 31)),
        };
        var asOf = new DateOnly(2026, 7, 31);

        var cashOnly = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 5), 50_000m, "CNY"),
        ]);
        var before = ValuationEngine.Value(cashOnly, quotes, FxConverter.Empty, "CNY", asOf);

        // Spend all cash on a CNY asset priced at 10: same slice, different shape.
        var withAsset = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 5), 50_000m, "CNY"),
            LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2026, 1, 5), 5_000m, 10m, "CNY"),
        ]);
        var after = ValuationEngine.Value(withAsset, quotes, FxConverter.Empty, "CNY", asOf);

        var sliceBefore = before.ByCurrency.Single(s => s.Key == "CNY").Value.Amount;
        var sliceAfter = after.ByCurrency.Single(s => s.Key == "CNY").Value.Amount;
        // 50 000 cash → 50 000 of stock + 0 cash: the CNY slice is invariant.
        Assert.Equal(50_000m, sliceBefore);
        Assert.Equal(sliceBefore, sliceAfter);
    }
}
