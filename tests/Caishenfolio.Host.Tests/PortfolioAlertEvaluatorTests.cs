using Caishenfolio.Host.Data;
using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class PortfolioAlertEvaluatorTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    private static PortfolioValuation Valuation(decimal price = 9m, DateOnly? quoteDate = null)
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", price, "CNY", quoteDate ?? AsOf),
        };
        return ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);
    }

    private static PlannedPriceLevel Level(string side, double price, bool active = true, string symbol = "SSE:600000") =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Symbol = symbol,
            Side = side,
            Price = price,
            Active = active,
            Note = "",
        };

    [Fact]
    public void RaisesAnAlertWhenPriceReachesAPlannedBuyLevel()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(Valuation(price: 9m), [Level("buy", 9.5)]);

        var alert = Assert.Single(alerts, a => a.Kind == AlertKind.PlannedBuy);
        Assert.Equal("SSE:600000", alert.Symbol);
        Assert.Contains("已到你设定的买入价", alert.Message);
    }

    [Fact]
    public void RaisesAnAlertWhenPriceReachesAPlannedSellLevel()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(Valuation(price: 13m), [Level("sell", 12.0)]);

        Assert.Single(alerts, a => a.Kind == AlertKind.PlannedSell);
    }

    [Fact]
    public void StaysQuietWhenNoLevelIsReached()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(
            Valuation(price: 10m), [Level("buy", 8.0), Level("sell", 12.0)]);

        Assert.Empty(alerts);
    }

    [Fact]
    public void IgnoresDeactivatedLevels()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(
            Valuation(price: 9m), [Level("buy", 9.5, active: false)]);

        Assert.Empty(alerts);
    }

    [Fact]
    public void IgnoresLevelsForInstrumentsYouDoNotHold()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(
            Valuation(price: 9m), [Level("buy", 9.5, symbol: "NASDAQ:AAPL")]);

        Assert.Empty(alerts);
    }

    [Fact]
    public void MatchesLevelsRecordedUnderAVenueAlias()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(
            Valuation(price: 9m), [Level("buy", 9.5, symbol: "SH:600000")]);

        Assert.Single(alerts, a => a.Kind == AlertKind.PlannedBuy);
    }

    [Fact]
    public void FlagsAStalePrice()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(
            Valuation(price: 10m, quoteDate: AsOf.AddDays(-30)), stalePriceDays: 5);

        var alert = Assert.Single(alerts, a => a.Kind == AlertKind.StalePrice);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("30 天前", alert.Message);
    }

    [Fact]
    public void FreshPriceRaisesNoStaleAlert()
    {
        var alerts = PortfolioAlertEvaluator.Evaluate(
            Valuation(price: 10m, quoteDate: AsOf.AddDays(-2)), stalePriceDays: 5);

        Assert.DoesNotContain(alerts, a => a.Kind == AlertKind.StalePrice);
    }

    [Fact]
    public void FlagsAnUnpricedHoldingSoItIsNotMistakenForZero()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
        ]);
        var valuation = ValuationEngine.Value(
            ledger, new Dictionary<string, PriceQuote>(), FxConverter.Empty, "CNY", AsOf);

        var alert = Assert.Single(PortfolioAlertEvaluator.Evaluate(valuation), a => a.Kind == AlertKind.Unpriced);

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("不会按 0 计算", alert.Message);
    }

    [Fact]
    public void CarriesConcentrationFindingsThrough()
    {
        var valuation = Valuation(price: 10m);
        var risk = PortfolioRiskAnalyzer.Analyze(valuation);

        var alerts = PortfolioAlertEvaluator.Evaluate(valuation, risk: risk);

        // A lone holding is 100% of the portfolio, so it breaches every ceiling at once:
        // the position limit plus asset class, region and currency.
        var concentration = alerts.Where(a => a.Kind == AlertKind.Concentration).ToArray();
        Assert.Equal(4, concentration.Length);
        Assert.All(concentration, a => Assert.Equal(AlertSeverity.Warning, a.Severity));
        Assert.Contains(concentration, a => a.Symbol == "SSE:600000");
        Assert.Contains(concentration, a => a.Title.Contains("股票"));
    }

    [Fact]
    public void WarningsSortAheadOfInformationalAlerts()
    {
        var valuation = Valuation(price: 9m, quoteDate: AsOf.AddDays(-30));

        var alerts = PortfolioAlertEvaluator.Evaluate(valuation, [Level("buy", 9.5)], stalePriceDays: 5);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(AlertKind.StalePrice, alerts[0].Kind);
        Assert.Equal(AlertSeverity.Warning, alerts[0].Severity);
        Assert.Equal(AlertKind.PlannedBuy, alerts[1].Kind);
        Assert.Equal(AlertSeverity.Info, alerts[1].Severity);
    }

    [Fact]
    public void NoLevelsAndNoRiskMeansNoAlerts()
    {
        Assert.Empty(PortfolioAlertEvaluator.Evaluate(Valuation(price: 10m)));
    }
}
