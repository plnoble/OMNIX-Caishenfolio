using Caishenfolio.Host.Data;
using Caishenfolio.Host.MarketData;

namespace Caishenfolio.Host.Tests;

public class FixtureMarketDataProviderTests
{
    private readonly FixtureMarketDataProvider _provider = new();

    [Fact]
    public void Search_FindsAshareAndUsSymbols()
    {
        var hits = _provider.Search("AAPL");
        Assert.Contains(hits, hit => hit.Symbol == "NASDAQ:AAPL");
    }

    [Fact]
    public void HistoricalBars_ReturnsSyntheticWeekdays()
    {
        var result = _provider.HistoricalBars(
            "SSE:600000",
            new DateOnly(2024, 1, 2),
            new DateOnly(2024, 1, 5),
            Adjustment.Raw);

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);
        Assert.NotEmpty(result.Data!);
        Assert.All(result.Data!, bar => Assert.Equal("fixture", bar.Provider));
        Assert.Contains("fixture_synthetic_data", result.Warnings);
    }

    [Fact]
    public void HistoricalBars_UnknownSymbol_FailClosed()
    {
        var result = _provider.HistoricalBars(
            "SSE:999999",
            new DateOnly(2024, 1, 2),
            new DateOnly(2024, 1, 3));

        Assert.False(result.Ok);
        Assert.Null(result.Data);
        Assert.Contains("fail_closed", result.Warnings);
    }
}
