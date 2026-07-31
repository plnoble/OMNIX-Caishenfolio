using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class IpoAlertTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);
    private static readonly DateOnly Applied = new(2026, 7, 20);

    private static readonly PortfolioValuation Empty = ValuationEngine.Value(
        LedgerState.Empty, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", Today);

    private static IpoSubscription New() =>
        IpoSubscription.Create("acct", "SSE:601000", Applied, 1000m, 20m, "CNY", "某某股份");

    private static IReadOnlyList<PortfolioAlert> Alerts(params IpoSubscription[] subscriptions) =>
        PortfolioAlertEvaluator.Evaluate(Empty, asOf: Today, ipoSubscriptions: subscriptions);

    [Fact]
    public void AnUnpaidAllotmentIsTheLoudestReminder()
    {
        var alert = Assert.Single(Alerts(New().WithAllotment(500m)));

        Assert.Equal(AlertKind.IpoDeadline, alert.Kind);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("已中签", alert.Message);
        // Missing the payment forfeits the shares and blocks later applications.
        Assert.Contains("逾期未缴会作废", alert.Message);
        Assert.Contains("影响后续申购资格", alert.Message);
    }

    [Fact]
    public void AnUpcomingListingIsAnnouncedWithTheCountdown()
    {
        var ipo = New().WithAllotment(500m).WithPayment(Today, Today.AddDays(3));

        var alert = Assert.Single(Alerts(ipo));

        Assert.Equal("即将上市", alert.Title);
        Assert.Contains("3 天后", alert.Message);
        Assert.Equal(AlertSeverity.Info, alert.Severity);
    }

    [Fact]
    public void ListingDayItselfSaysItCanBeSold()
    {
        var ipo = New().WithAllotment(500m).WithPayment(Today.AddDays(-5), Today);

        var alert = Assert.Single(Alerts(ipo));

        Assert.Equal("今日上市", alert.Title);
        Assert.Contains("可以卖出", alert.Message);
    }

    [Fact]
    public void AListedButUnsoldAllotmentKeepsReminding()
    {
        var ipo = New().WithAllotment(500m).WithPayment(Today.AddDays(-10), Today.AddDays(-2));

        var alert = Assert.Single(Alerts(ipo));

        Assert.Equal("已上市待卖出", alert.Title);
        Assert.Contains("还没登记卖出", alert.Message);
    }

    [Fact]
    public void AListingFurtherOutStaysQuiet()
    {
        var ipo = New().WithAllotment(500m).WithPayment(Today, Today.AddDays(30));

        Assert.Empty(Alerts(ipo));
    }

    [Fact]
    public void ClosedAndPendingRecordsRaiseNothing()
    {
        var subscribed = New();
        var missed = New().WithAllotment(0m);
        var abandoned = New().WithAllotment(500m).WithAbandonment();
        var sold = New().WithAllotment(500m)
            .WithPayment(Today.AddDays(-10), Today.AddDays(-3))
            .WithSale(Today.AddDays(-1), 30m);

        Assert.Empty(Alerts(subscribed, missed, abandoned, sold));
    }

    [Fact]
    public void AnAllotmentWithNoListingDateStillRemindsToPayButNotToSell()
    {
        var paidNoDate = New().WithAllotment(500m).WithPayment(Today);

        // Paid, but nothing is known about when it lists, so there is nothing to count down to.
        Assert.Empty(Alerts(paidNoDate));
    }

    [Fact]
    public void NoRecordsMeansNoIpoAlerts()
    {
        Assert.Empty(Alerts());
        Assert.DoesNotContain(
            PortfolioAlertEvaluator.Evaluate(Empty, asOf: Today),
            a => a.Kind == AlertKind.IpoDeadline);
    }

    [Fact]
    public void RemindersReportStateAndNeverInstructATrade()
    {
        var ipo = New().WithAllotment(500m).WithPayment(Today, Today.AddDays(2));

        foreach (var alert in Alerts(ipo, New().WithAllotment(500m)))
        {
            foreach (var word in new[] { "建议买", "建议卖", "应该买", "推荐" })
            {
                Assert.DoesNotContain(word, alert.Message);
            }
        }
    }
}
