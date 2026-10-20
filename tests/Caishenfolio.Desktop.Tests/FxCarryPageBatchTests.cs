using System.IO;
using System.Windows.Controls;
using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Portfolio;
using Caishenfolio.Host.Python;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// WP-02 目标 1/2/3 on the FX page: the batch list, the "convert now" card with its named and
/// dated baseline, and the future critical rate in the pair's own unit. Display-only — the
/// numbers come from the Core/Host, this checks they reach the screen in the right shape.
/// </summary>
public class FxCarryPageBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_fx_page", Guid.NewGuid().ToString("N"));

    private static FxPathsResponse SampleResponse() => new()
    {
        Ok = true,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        BaseAmount = 100m,
        Held = new FxHeldDto { Currency = "JPY", Amount = 2_450m, CostInBase = 100m, ImpliedRate = 24.5m },
        HeldAdvantageGrossPct = 17.6m,
        HeldAdvantageNetPct = 17.6m,
        BreakevenHeldRate = 20.833333m,
        Now = new FxNowDto
        {
            TargetCurrency = "USD",
            GrossAmount = 16.333333m,
            NetAmount = 16.333333m,
            AdvantageVsBaselinePct = 17.6m,
            PnlVsCostInBase = 17.6m,
            HeldCostInBase = 100m,
            CostKnown = true,
            PnlBasis = "gross",
            Baseline = new FxBaselineDto
            {
                Name = "现在直接用 CNY 换 USD（新资金）",
                QuotePair = "USD/CNY",
                Rate = 7.2m,
                FrozenAt = "2026-08-28",
            },
        },
        FutureCritical = new FxCriticalDto
        {
            Pair = "USD/JPY",
            Unit = "1 USD = X JPY",
            Rate = 176.4m,
            CurrentRate = 150m,
            RequiredMovePct = -17.6m,
            Solvable = true,
        },
        Paths = [],
    };

    [Fact]
    public void ThePageShowsTheNowResultItsBaselineAndTheCriticalRate()
    {
        WpfTestHost.Run(async () =>
        {
            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY");

            view.RenderPathComparison(SampleResponse(), "CNY");

            Assert.Contains("16.33 USD", Text(view, "FxNowText"), StringComparison.Ordinal);
            Assert.Contains("比基准 +17.6%", Text(view, "FxNowText"), StringComparison.Ordinal);
            // Review #9: the P&L must say it is a GROSS number and in which currency, because the`n            // net arrival one line up is a different number.`n            Assert.Contains("盈亏（毛额，CNY）+17.6", Text(view, "FxNowText"), StringComparison.Ordinal);

            // The critical rate is stated in the pair's own unit, with the move still needed.
            Assert.Contains("1 USD = 176.4 JPY", Text(view, "FxCriticalText"), StringComparison.Ordinal);
            Assert.Contains("还需上升 17.6%", Text(view, "FxCriticalText"), StringComparison.Ordinal);

            // The baseline is never a bare number: name + pair + rate + freeze moment.
            var baseline = Text(view, "FxBaselineText");
            Assert.Contains("现在直接用 CNY 换 USD", baseline, StringComparison.Ordinal);
            Assert.Contains("USD/CNY", baseline, StringComparison.Ordinal);
            Assert.Contains("冻结于 2026-08-28", baseline, StringComparison.Ordinal);

            // And the old tie rate is still shown, in its own unit, as a different thing.
            Assert.Contains("20.833", Text(view, "FxBreakevenText"), StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void AnUnsolvableCriticalRateSaysWhyInsteadOfShowingANumber()
    {
        WpfTestHost.Run(async () =>
        {
            var response = SampleResponse();
            response.FutureCritical = new FxCriticalDto
            {
                Pair = "USD/JPY",
                Unit = "1 USD = X JPY",
                Solvable = false,
                CurrentRate = 150m,
                Reason = "缺历史成本：账本里没有这笔外币的买入回单，算不出临界汇率。",
            };
            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY");

            view.RenderPathComparison(response, "CNY");

            var critical = Text(view, "FxCriticalText");
            Assert.Contains("无法计算", critical, StringComparison.Ordinal);
            Assert.Contains("缺历史成本", critical, StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void TheBatchListComesFromTheLedgerAndKeepsBatchCostsApart()
    {
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var workspace = new PortfolioWorkspace(store);
            var account = workspace.AddAccount("换汇账户", AccountKind.Bank, "CNY");
            var day1 = new DateOnly(2025, 1, 10);
            var day2 = new DateOnly(2025, 3, 15);
            var day3 = new DateOnly(2025, 4, 1);
            // WP-03 R1: the account holds the money before it converts it.
            workspace.Record(LedgerTransaction.OpeningCash(account.Id, day1.AddDays(-1), 40_000m, "CNY", "建账余额"));
            workspace.Record(LedgerTransaction.FxExchange(account.Id, day1, 30_000m, "CNY", 600_000m, "JPY"));
            workspace.Record(LedgerTransaction.FxExchange(account.Id, day3, 300_000m, "JPY", 2_000m, "USD"));
            var snapshot = await workspace.RefreshAsync(new DateOnly(2026, 1, 2));

            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY", () => snapshot);
            view.RenderPathComparison(SampleResponse(), "CNY");

            var grid = (DataGrid)view.FindName("FxLotGrid")!;
            var rows = ((IEnumerable<FxLotRow>)grid.ItemsSource).ToArray();

            // Two batches: the CNY→JPY purchase and the USD bought with JPY. The JPY batch still
            // holds 300,000 because the second leg consumed the other half of it.
            Assert.Equal(2, rows.Length);
            var usd = rows.Single(r => r.Currency == "USD");
            // R4 on screen: the USD batch's own cost is JPY; the original CNY sits in another column.
            Assert.Equal("300,000 JPY", usd.Paid);
            Assert.Contains("CNY", usd.Upstream, StringComparison.Ordinal);
            Assert.DoesNotContain("JPY", usd.Upstream, StringComparison.Ordinal);

            var jpy = rows.Single(r => r.Currency == "JPY");
            Assert.Equal("300,000 JPY", jpy.Remaining);
            Assert.Equal("15,000 CNY", jpy.RemainingCost);
            Assert.Contains("先进先出", Text(view, "FxLotsHint"), StringComparison.Ordinal);
            _ = day2;
        });
    }

    [Fact]
    public void HeldCashWithNoReceiptIsCalledOutBesideTheBatches()
    {
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var workspace = new PortfolioWorkspace(store);
            var account = workspace.AddAccount("换汇账户", AccountKind.Bank, "CNY");
            // 500,000 JPY bought (with a receipt) on top of 200,000 JPY that just arrived:
            // the batch explains the first, the extra has no purchase cost (WP-02 R5).
            workspace.Record(LedgerTransaction.OpeningCash(account.Id, new DateOnly(2025, 1, 9), 30_000m, "CNY", "建账余额"));
            workspace.Record(LedgerTransaction.FxExchange(
                account.Id, new DateOnly(2025, 1, 10), 25_000m, "CNY", 500_000m, "JPY"));
            workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2025, 2, 1), 200_000m, "JPY"));
            var snapshot = await workspace.RefreshAsync(new DateOnly(2026, 1, 2));

            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY", () => snapshot);
            view.RenderPathComparison(SampleResponse(), "CNY");

            var hint = Text(view, "FxLotsHint");
            Assert.Contains("共 1 批", hint, StringComparison.Ordinal);
            // 700,000 held, 500,000 explained → 200,000 has no receipt and is called out.
            Assert.Contains("差 200,000", hint, StringComparison.Ordinal);
            Assert.Contains("没有买入成本", hint, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void WithoutAHeldPositionTheNewCardsSayNothingRatherThanGuessing()
    {
        WpfTestHost.Run(async () =>
        {
            // No held pot: the core sends now/future_critical as null. The page must show "—"
            // instead of inventing a rate, and must not throw.
            var response = new FxPathsResponse { Ok = true, BaseCurrency = "CNY", TargetCurrency = "USD" };
            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY");

            view.RenderPathComparison(response, "CNY");

            Assert.Equal("—", Text(view, "FxNowText"));
            Assert.Equal("—", Text(view, "FxCriticalText"));
            Assert.Equal("—", Text(view, "FxBaselineText"));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void WithTheHeldQuoteMissingTheCardsSayWhyInsteadOfLookingEmpty()
    {
        WpfTestHost.Run(async () =>
        {
            // A held pot but no TARGET/HELD quote: the core reports now with no amounts and a
            // critical rate it cannot solve. Empty cards look like a rendering bug, so each one
            // has to say what is missing.
            var response = SampleResponse();
            response.Now = new FxNowDto
            {
                TargetCurrency = "USD",
                CostKnown = true,
                PnlBasis = "gross",
                Baseline = new FxBaselineDto { Name = "现在直接用 CNY 换 USD（新资金）", QuotePair = "USD/CNY" },
            };
            response.FutureCritical = new FxCriticalDto
            {
                Pair = "USD/JPY",
                Unit = "1 USD = X JPY",
                Solvable = false,
                Reason = "缺关键报价 USD/JPY，临界汇率无法计算。",
            };
            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY");

            view.RenderPathComparison(response, "CNY");

            Assert.Contains("缺报价", Text(view, "FxNowText"), StringComparison.Ordinal);
            Assert.Contains("缺关键报价", Text(view, "FxCriticalText"), StringComparison.Ordinal);
            // The baseline is still named; only its rate and freeze moment are unknown, and they
            // must read as unknown rather than as 0.
            var baseline = Text(view, "FxBaselineText");
            Assert.Contains("现在直接用 CNY 换 USD", baseline, StringComparison.Ordinal);
            Assert.Contains("= —", baseline, StringComparison.Ordinal);
            Assert.DoesNotContain("= 0", baseline, StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void ThePageShowsWhichBatchEachSpendAte()
    {
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var workspace = new PortfolioWorkspace(store);
            var account = workspace.AddAccount("换汇账户", AccountKind.Bank, "CNY");
            // Two batches; the second spend crosses both, so the trail must name both.
            workspace.Record(LedgerTransaction.OpeningCash(account.Id, new DateOnly(2025, 1, 9), 500m, "CNY", "建账余额"));
            workspace.Record(LedgerTransaction.FxExchange(
                account.Id, new DateOnly(2025, 1, 10), 100m, "CNY", 2_000m, "JPY"));
            workspace.Record(LedgerTransaction.FxExchange(
                account.Id, new DateOnly(2025, 2, 10), 100m, "CNY", 2_000m, "JPY"));
            workspace.Record(LedgerTransaction.FxExchange(
                account.Id, new DateOnly(2025, 3, 10), 3_000m, "JPY", 20m, "USD", 0m, ""));
            var snapshot = await workspace.RefreshAsync(new DateOnly(2026, 1, 2));

            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY", () => snapshot);
            view.RenderPathComparison(SampleResponse(), "CNY");

            var trail = Text(view, "FxSpendList");
            // Both batches are named, with the FIFO order (January first) and the amounts.
            Assert.Contains("2025-01-10 那批 2,000 JPY", trail, StringComparison.Ordinal);
            Assert.Contains("2025-02-10 那批 1,000 JPY", trail, StringComparison.Ordinal);
            Assert.True(
                trail.IndexOf("2025-01-10", StringComparison.Ordinal)
                < trail.IndexOf("2025-02-10", StringComparison.Ordinal),
                trail);
        });
    }

    private static string Text(FxCarryView view, string name) =>
        ((TextBlock)view.FindName(name)!).Text;

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
            // Best-effort cleanup.
        }
    }
}
