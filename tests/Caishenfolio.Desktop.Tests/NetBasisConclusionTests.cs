using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// WP-03 A10 (R6/D7): the "convert now" verdict must speak in NET terms. Gross systematically
/// overstates what lands in the account, and "did I actually make money" is the question the
/// card exists to answer. When the net figure cannot be computed the card says so — it must
/// never fall back to the gross number and must never print 0 as if it were a result.
/// </summary>
public class NetBasisConclusionTests
{
    private static FxPathsResponse WithNow(decimal? gross, decimal? net, decimal? pnlNet, string? pnlBasis) => new()
    {
        Ok = true,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        BaseAmount = 100m,
        Held = new FxHeldDto { Currency = "JPY", Amount = 2_450m, CostInBase = 100m, ImpliedRate = 24.5m },
        HeldAdvantageGrossPct = 17.6m,
        HeldAdvantageNetPct = pnlNet is null ? null : 12.2m,
        Now = new FxNowDto
        {
            TargetCurrency = "USD",
            GrossAmount = gross,
            NetAmount = net,
            AdvantageVsBaselinePct = 17.6m,
            PnlVsCostInBase = 17.6m,
            PnlVsCostNetInBase = pnlNet,
            CostKnown = net is not null,
            PnlBasis = pnlBasis,
            Baseline = new FxBaselineDto
            {
                Name = "现在直接用 CNY 换 USD（新资金）",
                QuotePair = "USD/CNY",
                Rate = 7.2m,
                FrozenAt = "2026-08-28",
            },
        },
    };

    private static string Text(FxCarryView view, string name) =>
        ((System.Windows.Controls.TextBlock)view.FindName(name)).Text;

    [Fact]
    public void A10_TheVerdictIsTheNetFigureWhenItIsAvailable()
    {
        WpfTestHost.Run(async () =>
        {
            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY");
            view.RenderPathComparison(WithNow(16.333333m, 15.333333m, 10.4m, "net"), "CNY");

            var now = Text(view, "FxNowText");
            Assert.Contains("净额", now, StringComparison.Ordinal);
            Assert.Contains("10.4", now, StringComparison.Ordinal);
            // The gross number may stay visible as detail, but not as the verdict.
            Assert.DoesNotContain("毛额，CNY）+10.4", now, StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void A10_AUnknownNetSaysSoAndNeverFallsBackToGross()
    {
        WpfTestHost.Run(async () =>
        {
            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY");
            // The cost model is missing: gross is known, net is not.
            view.RenderPathComparison(WithNow(16.333333m, null, null, "gross"), "CNY");

            var now = Text(view, "FxNowText");
            Assert.Contains("净额未知（成本未填）", now, StringComparison.Ordinal);
            // D7: not the gross number dressed up, and not a zero.
            Assert.DoesNotContain("+17.6", now, StringComparison.Ordinal);
            Assert.DoesNotContain("盈亏（毛额", now, StringComparison.Ordinal);
            Assert.DoesNotContain("净额 0", now, StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void A10_TheHeadlineAdvantageAlsoSpeaksNet()
    {
        WpfTestHost.Run(async () =>
        {
            var view = new FxCarryView();
            view.Bind(() => null, () => "CNY");
            view.RenderPathComparison(WithNow(16.333333m, null, null, "gross"), "CNY");

            // With no cost model the advantage headline must admit it cannot answer in net terms.
            var net = Text(view, "FxAdvantageNetText");
            Assert.Contains("净额未知", net, StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }
}
