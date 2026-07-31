using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// Cross-checked quotes: sources that disagree must be visible, not averaged into silence.
/// </summary>
public class PriceCrossCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_crosscheck", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    private static PortfolioValuation Valuation(PriceQuote quote)
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"),
        ]);
        var quotes = new Dictionary<string, PriceQuote> { ["SSE:600000"] = quote };
        return ValuationEngine.Value(ledger, quotes, FxConverter.Empty, "CNY", AsOf);
    }

    [Fact]
    public void DisagreeingSourcesRaiseAWarning()
    {
        var quote = PriceQuote.Of("SSE:600000", 11m, "CNY", AsOf, "auto",
            sourceCount: 2, spreadPercent: 18.18m, sources: "akshare=10;yfinance=12");

        var alert = Assert.Single(
            PortfolioAlertEvaluator.Evaluate(Valuation(quote), priceTolerancePercent: 2m),
            a => a.Kind == AlertKind.PriceDisagreement);

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal("SSE:600000", alert.Symbol);
        Assert.Contains("18.18%", alert.Message);
        Assert.Contains("akshare=10;yfinance=12", alert.Message);
        Assert.Contains("已取中位数", alert.Message);
    }

    [Fact]
    public void AgreementWithinToleranceIsSilent()
    {
        var quote = PriceQuote.Of("SSE:600000", 10.05m, "CNY", AsOf, "auto",
            sourceCount: 2, spreadPercent: 1m, sources: "akshare=10;yfinance=10.1");

        Assert.DoesNotContain(
            PortfolioAlertEvaluator.Evaluate(Valuation(quote), priceTolerancePercent: 2m),
            a => a.Kind == AlertKind.PriceDisagreement);
    }

    [Fact]
    public void ASingleSourceIsNeverReportedAsDisagreeing()
    {
        // Without a second opinion there is nothing to disagree with, whatever the spread field says.
        var quote = PriceQuote.Of("SSE:600000", 10m, "CNY", AsOf, "akshare",
            sourceCount: 1, spreadPercent: 99m);

        Assert.DoesNotContain(
            PortfolioAlertEvaluator.Evaluate(Valuation(quote), priceTolerancePercent: 2m),
            a => a.Kind == AlertKind.PriceDisagreement);
    }

    [Fact]
    public void TheToleranceIsTheUsersSetting()
    {
        var quote = PriceQuote.Of("SSE:600000", 11m, "CNY", AsOf, "auto",
            sourceCount: 2, spreadPercent: 5m, sources: "a=10;b=12");

        Assert.Contains(
            PortfolioAlertEvaluator.Evaluate(Valuation(quote), priceTolerancePercent: 2m),
            a => a.Kind == AlertKind.PriceDisagreement);
        Assert.DoesNotContain(
            PortfolioAlertEvaluator.Evaluate(Valuation(quote), priceTolerancePercent: 10m),
            a => a.Kind == AlertKind.PriceDisagreement);
    }

    [Fact]
    public void CrossCheckPreferencesRoundTripAndDefaultToOn()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);

        Assert.True(store.LoadSettings().CrossCheckPrices);
        Assert.Equal(2m, store.LoadSettings().PriceTolerancePercent);

        store.SaveSettings(new PortfolioSettings { CrossCheckPrices = false, PriceTolerancePercent = 5m });

        var loaded = store.LoadSettings();
        Assert.False(loaded.CrossCheckPrices);
        Assert.Equal(5m, loaded.PriceTolerancePercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void AnImpossibleToleranceIsRefused(decimal tolerance)
    {
        using var store = PortfolioStore.UnderStateRoot(_root);

        Assert.Throws<LedgerException>(() =>
            store.SaveSettings(new PortfolioSettings { PriceTolerancePercent = tolerance }));
    }

    [Fact]
    public async Task TheWorkspaceAppliesThePreferenceToThePricingSource()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveSettings(new PortfolioSettings { CrossCheckPrices = true, PriceTolerancePercent = 3m });

        var workspace = new PortfolioWorkspace(store);
        Assert.True(workspace.Settings.CrossCheckPrices);
        Assert.Equal(3m, workspace.Settings.PriceTolerancePercent);

        // A disputed quote must reach the alert list through a full refresh, not just in isolation.
        workspace.Record(LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 100m, 10m, "CNY"));
        workspace.PricingSource = new DisputedSource();

        var snapshot = await workspace.RefreshAsync(AsOf);

        Assert.Contains(snapshot.Alerts, a => a.Kind == AlertKind.PriceDisagreement);
    }

    private sealed class DisputedSource : IMarketPricingSource
    {
        public Task<PriceQuote?> TryGetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<PriceQuote?>(PriceQuote.Of(symbol, 11m, "CNY", AsOf, "auto",
                sourceCount: 2, spreadPercent: 18m, sources: "a=10;b=12"));

        public Task<FxRate?> TryGetFxRateAsync(
            string baseCurrency, string quoteCurrency, CancellationToken cancellationToken = default) =>
            Task.FromResult<FxRate?>(null);
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
            // Best-effort cleanup.
        }
    }
}
