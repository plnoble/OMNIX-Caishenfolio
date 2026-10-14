using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// Exercises the production result renderer, real item template and WPF automation peer.
/// The resource-only STA host never creates a window, ledger, core or network client.
/// These are semantic/Tab-property contracts, not a claim that Narrator was listened to.
/// </summary>
public class FxResultAccessibilityTests
{
    [Fact]
    public void ZeroCostCardExposesNamedGroupWithAmountsAndSeparateDetails()
    {
        WpfTestHost.Run(() =>
        {
            var view = new FxCarryView();
            view.RenderPathComparison(Response("USD", Path("已持有 JPY 直接换", "ok", 15m, 15m)), "CNY");
            var card = Assert.Single(RealizeCards(view));
            var peer = Peer(card);

            Assert.Equal(AutomationControlType.Group, peer.GetAutomationControlType());
            Assert.Contains("已持有 JPY 直接换", peer.GetName());
            Assert.Contains("目标币种 USD", peer.GetName());
            Assert.Contains("毛到账 15 USD", peer.GetName());
            Assert.Contains("净到账 15 USD", peer.GetName());
            Assert.Contains("净额已计入成本", peer.GetName());
            Assert.DoesNotContain("成本输入缺失", peer.GetName());
            Assert.DoesNotContain("公式标记", peer.GetName());
            Assert.Contains("起始：2100 JPY", peer.GetHelpText());
            Assert.Contains("公式标记", peer.GetHelpText());
            Assert.Contains("说明标记", peer.GetHelpText());
            Assert.Contains("研究模拟，不写入账本", peer.GetHelpText());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void UnknownCostCardDoesNotAnnounceAConfirmedNetAmount()
    {
        WpfTestHost.Run(() =>
        {
            var view = new FxCarryView();
            view.RenderPathComparison(Response("USD", Path("新 CNY 直接换", "gross_only", 14m, null)), "CNY");
            var name = Peer(Assert.Single(RealizeCards(view))).GetName();

            Assert.Contains("新 CNY 直接换", name);
            Assert.Contains("目标币种 USD", name);
            Assert.Contains("毛到账 14 USD", name);
            Assert.Contains("净到账 未知（成本未填）", name);
            Assert.Contains("成本输入缺失：仅毛额比较", name);
            Assert.DoesNotContain("净到账 14", name);
            Assert.DoesNotContain("净到账 0", name);
            Assert.DoesNotContain("净额已计入成本", name);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void MissingQuoteCardNamesTargetAndBothUnknownAmounts()
    {
        WpfTestHost.Run(() =>
        {
            var view = new FxCarryView();
            view.RenderPathComparison(Response("EUR", Path("先换 JPY 再换", "missing_quote", null, null)), "CNY");
            var name = Peer(Assert.Single(RealizeCards(view))).GetName();

            Assert.Contains("目标币种 EUR", name);
            Assert.Contains("毛到账 未知（缺报价）", name);
            Assert.Contains("净到账 未知（缺报价）", name);
            Assert.Contains("缺少关键报价", name);
            Assert.DoesNotContain("净额已计入成本", name);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void EveryRenderedPathIsOneTabStopWithNonTrappingNavigation()
    {
        WpfTestHost.Run(() =>
        {
            var view = new FxCarryView();
            string[] labels = ["已持有 JPY 直接换", "新 CNY 直接换", "先换 JPY 再换", "继续持有", "分批兑换"];
            view.RenderPathComparison(Response("USD", labels.Select(label => Path(label, "gross_only", 14m, null)).ToArray()), "CNY");
            var cards = RealizeCards(view);
            var list = (ItemsControl)view.FindName("FxPathList");

            Assert.Equal(labels.Length, cards.Length);
            Assert.Equal(KeyboardNavigationMode.Continue, KeyboardNavigation.GetTabNavigation(list));
            for (var index = 0; index < cards.Length; index++)
            {
                var card = cards[index];
                Assert.StartsWith(labels[index], Peer(card).GetName());
                Assert.True(card.Focusable);
                Assert.True(KeyboardNavigation.GetIsTabStop(card));
                Assert.Equal(KeyboardNavigationMode.Continue, KeyboardNavigation.GetTabNavigation(card));
                // The static child text is still available to browsing/smoke checks, but
                // must not turn one card into several unexpected keyboard stops.
                Assert.NotEmpty(Descendants<TextBlock>(card));
                Assert.Empty(Descendants<UIElement>(card).Where(child => child.Focusable));
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void NewResultReplacesOldSemanticsAndFailedResponseRemovesCards()
    {
        WpfTestHost.Run(() =>
        {
            var view = new FxCarryView();
            view.RenderPathComparison(Response("USD", Path("旧持有路径", "ok", 15m, 15m)), "CNY");
            var oldCard = Assert.Single(RealizeCards(view));
            Assert.Contains("净到账 15 USD", Peer(oldCard).GetName());

            view.RenderPathComparison(Response("EUR", Path("新直接路径", "gross_only", 20m, null)), "CNY");
            var currentCard = Assert.Single(RealizeCards(view));
            var currentName = Peer(currentCard).GetName();
            Assert.Contains("新直接路径", currentName);
            Assert.Contains("目标币种 EUR", currentName);
            Assert.Contains("毛到账 20 EUR", currentName);
            Assert.Contains("成本输入缺失", currentName);
            Assert.DoesNotContain("旧持有路径", currentName);
            Assert.DoesNotContain("USD", currentName);
            Assert.DoesNotContain("净额已计入成本", currentName);

            view.RenderPathComparison(new FxPathsResponse { Ok = false, Error = "本轮报价失败" }, "CNY");
            Assert.Empty(RealizeCards(view));
            Assert.Empty(((ItemsControl)view.FindName("FxPathList")).Items);
            Assert.Equal("本轮报价失败", ((TextBlock)view.FindName("StatusText")).Text);
            return Task.CompletedTask;
        });
    }

    private static FxPathsResponse Response(string target, params FxPathDto[] paths) => new()
    {
        Ok = true, BaseCurrency = "CNY", TargetCurrency = target, Paths = [.. paths],
        Disclaimer = "仅供研究模拟。",
    };

    private static FxPathDto Path(string label, string status, decimal? gross, decimal? net) => new()
    {
        Label = label, Status = status, GrossAmount = gross, NetAmount = net,
        StartingAsset = "2100 JPY", OpportunityCost = "放弃继续持有", Formula = "公式标记", Note = "说明标记",
    };

    private static GroupBox[] RealizeCards(FxCarryView view)
    {
        // Measure/arrange realizes the actual ItemsControl template without presenting a
        // window. Looking only at the DTO projection would miss broken XAML bindings.
        view.Measure(new Size(1200, 3000));
        view.Arrange(new Rect(0, 0, 1200, 3000));
        view.UpdateLayout();
        return Descendants<GroupBox>((ItemsControl)view.FindName("FxPathList")).ToArray();
    }

    private static GroupBoxAutomationPeer Peer(GroupBox card) =>
        Assert.IsType<GroupBoxAutomationPeer>(FrameworkElementAutomationPeer.CreatePeerForElement(card));

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
