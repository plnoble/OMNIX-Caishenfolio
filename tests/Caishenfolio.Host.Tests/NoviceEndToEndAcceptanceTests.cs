using Caishenfolio.Host.Data;
using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// The novice end-to-end acceptance script (plan §12), executed against the real services —
/// no UI, no fixtures beyond synthetic prices, no mocks of the ledger.
///
/// Steps 4, 5 and 7 (path comparison, fee flip, evidence card) are computed by the Python
/// analytics core and are covered there: `test_fx_paths` (fixed acceptance sample, inverted
/// quotes, fee flip) and `test_evidence_card`/`test_evidence_card_route`. The C# side of those
/// steps is what this test verifies: the ledger must hand over exactly the held-cost facts the
/// comparison consumes, and the symbol search must never require an internal prefix.
/// </summary>
public class NoviceEndToEndAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_e2e_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    private sealed class StubPricing : IMarketPricingSource
    {
        public Dictionary<string, decimal> Prices { get; } = new(StringComparer.Ordinal);

        public Task<PriceQuote?> TryGetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(Prices.TryGetValue(symbol, out var price)
                ? PriceQuote.Of(symbol, price,
                    ExchangeRegistry.TryGetQuoteCurrency(SymbolId.Parse(symbol), out var ccy) ? ccy : "CNY",
                    new DateOnly(2026, 9, 3), "stub")
                : null);

        public Task<FxRate?> TryGetFxRateAsync(
            string baseCurrency, string quoteCurrency, CancellationToken cancellationToken = default) =>
            Task.FromResult<FxRate?>(null);
    }

    [Fact]
    public async Task TheElevenStepNoviceScriptRunsEndToEnd()
    {
        using var store = NewStore();

        // ---- 步骤 1：首次启动，选择人民币为本位币 -----------------------------------
        var workspace = new PortfolioWorkspace(store, baseCurrency: null);
        workspace.ApplySettings(new PortfolioSettings
        {
            BaseCurrency = "CNY",
            OnboardingCompleted = true,
            Goal = "三年后买车",
            EmergencyFundAmount = 0m,
        });
        Assert.Equal("CNY", workspace.BaseCurrency);
        Assert.True(workspace.Settings.OnboardingCompleted);

        // ---- 步骤 2：创建现金账户，录入 100 CNY --------------------------------------
        var account = workspace.AddAccount("零钱包", AccountKind.Cash, "CNY");
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 9, 1), 100m, "CNY"));
        var state = store.LoadState();
        Assert.Equal(100m, Assert.Single(state.CashBalances).Amount);

        // ---- 步骤 3：录入一次 CNY → JPY 的真实换汇（按回单两边金额）-------------------
        var exchange = workspace.Record(LedgerTransaction.FxExchange(
            account.Id, new DateOnly(2026, 9, 2), 100m, "CNY", 2_100m, "JPY",
            note: "银行回单：换出 100 CNY，到账 2100 JPY"));
        state = store.LoadState();
        Assert.Equal(2_100m, state.CashBalances.Single(b => b.Currency == "JPY").Amount);
        // 100 CNY all converted: zero balances are not stored at all (absent ≠ unknown here).
        Assert.DoesNotContain(state.CashBalances, b => b.Currency == "CNY" && b.Amount != 0m);
        // Offline valuation runs on snapshot rates — the user keeps the receipt, the rate is stored.
        store.SaveFxRate(FxRate.Of("JPY", "CNY", 0.047m, new DateOnly(2026, 9, 2), "回单参考"));

        // ---- 步骤 4（桥接）：比较已有 JPY→USD 与新 CNY→USD ---------------------------
        // The comparison itself runs in the analytics core (test_fx_paths covers the math and
        // the plan's 3.011% sample). What the ledger must guarantee is the held-cost fact:
        var records = FxExchangeHistory.Records(store.ListTransactions());
        var held = FxExchangeHistory.MostRecentAcquisition(records, "CNY", "JPY");
        Assert.NotNull(held);
        Assert.Equal(2_100m, held!.ToAmount);
        Assert.Equal(100m, held.FromAmount);
        // implied historical rate = 2100 JPY per 100 CNY = 21 JPY/CNY — the receipt, not a guess.
        Assert.Equal(21m, held.ImpliedRate);

        // ---- 步骤 5（桥接）：加入手续费后结论可能反转 ---------------------------------
        // Covered by FeeFlipTests in the Python suite; the C# side only carries the cost
        // inputs the user types, which the comparison validates fail-closed.

        // ---- 步骤 6：创建一个净到账提醒并验证它能触发 --------------------------------
        var rule = new FxAlertRule
        {
            Kind = FxAlertRuleKind.NetArrivalAbove,
            BaseCurrency = "CNY",
            TargetCurrency = "USD",
            HeldCurrency = "JPY",
            ThresholdAmount = 13m,
            // P1-06: the rule states the cost model it is evaluated against.
            SpreadPct = 0.5m,
            FixedFee = 1m,
        };
        FxPathFacts FactsBelow(FxAlertRule _) => new() { NetArrival = 12.5m };
        FxPathFacts FactsAbove(FxAlertRule _) => new() { NetArrival = 13.2m };
        Assert.Empty(FxAlertRuleEvaluator.Evaluate([rule], _ => null, FactsBelow).Alerts);
        var fired = FxAlertRuleEvaluator.Evaluate([rule], _ => null, FactsAbove).Alerts;
        Assert.Single(fired);
        Assert.DoesNotContain("最佳", fired[0].Message, StringComparison.Ordinal);

        // ---- 步骤 7（桥接）：搜索一只股票（不需要内部前缀）---------------------------
        var instruments = new List<Instrument>
        {
            Instrument.FromSymbol("SSE:600000", "浦发银行", AssetClass.Equity),
        };
        var search = SymbolResolver.Resolve("浦发银行", instruments);
        Assert.Equal("SSE:600000", search.Resolved);
        // Card generation itself is the Python route (test_evidence_card_route).

        // The novice tops up and buys a small A-share position for real (steps 8-9 need one).
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 9, 3), 50m, "CNY"));
        workspace.Record(LedgerTransaction.Buy(
            account.Id, "SSE:600000", new DateOnly(2026, 9, 3), 5m, 10m, "CNY"));

        // ---- 步骤 8：模拟买入，查看仓位变化（不写入账本）------------------------------
        var pricing = new StubPricing();
        pricing.Prices["SSE:600000"] = 12m;
        workspace.PricingSource = pricing;
        var pricedSnapshot = await workspace.RefreshAsync(new DateOnly(2026, 9, 3));
        Assert.Equal(CoverageState.Complete, pricedSnapshot.Risk.Coverage.State);
        Assert.Equal(60m, pricedSnapshot.Valuation.Positions
            .Single(p => p.Position.Symbol == "SSE:600000").MarketValueBase!.Value.Amount);

        var simulation = TradeWhatIfAnalyzer.Simulate(
            pricedSnapshot.Valuation, workspace.Settings.Thresholds, "SSE:600000", 30m, "CNY");
        Assert.True(simulation.Feasible);
        Assert.True(simulation.NewSingleWeight > simulation.CurrentSingleWeight);
        Assert.Equal(5m, store.LoadState().Positions.Single(p => p.Symbol == "SSE:600000").Quantity);

        // ---- 步骤 9：查看压力情景和最大风险来源 --------------------------------------
        var stress = PortfolioStressAnalyzer.Run(
            pricedSnapshot.Valuation, pricedSnapshot.CashBalances,
            new StressScenario { EquityDropPercent = 20m });
        // Stock 60 → 48; the JPY cash line does not take the equity shock.
        Assert.Equal(48m + 2_100m * 0.047m, stress.StressedTotal.Amount);
        Assert.Contains(stress.Assumptions, a => a.Contains("股票与 ETF 下跌 20%"));
        Assert.NotEmpty(pricedSnapshot.Risk.Summary);

        // ---- 步骤 10：冲正一笔错误流水 ------------------------------------------------
        var reversal = workspace.ReverseTransaction(exchange.Id, "回单金额录错，冲正重录");
        state = store.LoadState();
        // The pair stays in the log: deposit, exchange, top-up, buy, reversal.
        Assert.Equal(5, store.ListTransactions().Count);
        Assert.Equal(reversal.ReversesTransactionId, exchange.Id);
        Assert.Equal(100m, state.CashBalances.Single(b => b.Currency == "CNY").Amount);
        Assert.DoesNotContain(state.CashBalances, b => b.Currency == "JPY");
        Assert.Single(store.ReversedTransactionIds(), exchange.Id);
        // The stock bought earlier is unaffected by the correction.
        Assert.Equal(5m, state.Positions.Single(p => p.Symbol == "SSE:600000").Quantity);

        // ---- 步骤 11：制造缺价（不接行情源），软件必须明确显示未知 --------------------
        workspace.Record(LedgerTransaction.OpeningPosition(
            account.Id, "NASDAQ:AAPL", new DateOnly(2026, 9, 4), 1m, 100m, "USD"));
        var offlineSnapshot = await workspace.RefreshAsync(new DateOnly(2026, 9, 5));

        Assert.False(offlineSnapshot.Valuation.IsComplete);
        Assert.NotEqual(CoverageState.Complete, offlineSnapshot.Risk.Coverage.State);
        Assert.Contains("估值不完整", offlineSnapshot.Risk.Summary);
        Assert.Contains("基于已定价部分", offlineSnapshot.Risk.Summary);
        Assert.Contains(offlineSnapshot.Alerts, a => a.Kind == AlertKind.Unpriced);
        // And the all-clear wording must not appear in an incomplete report.
        Assert.DoesNotContain("未触及你设定的任何上限", offlineSnapshot.Risk.Summary);
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
