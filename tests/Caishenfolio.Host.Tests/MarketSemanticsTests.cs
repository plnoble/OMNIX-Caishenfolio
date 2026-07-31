using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Tests;

public class MarketSemanticsTests
{
    [Theory]
    [InlineData("SSE:600000", MarketRegion.Cn, "CNY")]
    [InlineData("SZSE:000001", MarketRegion.Cn, "CNY")]
    [InlineData("HKEX:00700", MarketRegion.Hk, "HKD")]
    [InlineData("NASDAQ:AAPL", MarketRegion.Us, "USD")]
    [InlineData("TSE:7203", MarketRegion.Jp, "JPY")]
    [InlineData("FUND:110022", MarketRegion.Cn, "CNY")]
    public void ResolvesRegionAndQuoteCurrencyFromExchange(string raw, MarketRegion region, string currency)
    {
        var symbol = SymbolId.Parse(raw);

        Assert.True(ExchangeRegistry.TryGetRegion(symbol.Exchange, out var resolved));
        Assert.Equal(region, resolved);
        Assert.True(ExchangeRegistry.TryGetQuoteCurrency(symbol, out var quote));
        Assert.Equal(currency, quote);
    }

    [Fact]
    public void FxPairCarriesItsQuoteCurrencyInTheCode()
    {
        var symbol = SymbolId.Parse("FX:USDCNY");

        Assert.True(symbol.IsFx);
        Assert.True(symbol.TryGetFxPair(out var baseCcy, out var quoteCcy));
        Assert.Equal("USD", baseCcy);
        Assert.Equal("CNY", quoteCcy);
        Assert.True(ExchangeRegistry.TryGetQuoteCurrency(symbol, out var currency));
        Assert.Equal("CNY", currency);
        Assert.Equal("FX:USDCNY", SymbolId.FxPair("usd", "cny").Value);
    }

    [Theory]
    [InlineData("FX:XXXYYY")]
    [InlineData("FX:USD")]
    [InlineData("NASDAQ:AAPL")]
    public void RejectsMalformedFxPairs(string raw)
    {
        Assert.False(SymbolId.Parse(raw).TryGetFxPair(out _, out _));
    }

    [Theory]
    [InlineData("SH:600000", "SSE:600000")]
    [InlineData("SZ:000001", "SZSE:000001")]
    [InlineData("HK:00700", "HKEX:00700")]
    [InlineData("OF:110022", "FUND:110022")]
    [InlineData("TYO:7203", "TSE:7203")]
    public void NormalizesVenueAliasesToOneIdentity(string raw, string expected)
    {
        Assert.Equal(expected, SymbolId.Parse(raw).Normalized().Value);
    }

    [Fact]
    public void KeepsUnknownVenuesInsteadOfDroppingProviderOutput()
    {
        Assert.Equal("LSE:VOD", SymbolId.Parse("LSE:VOD").Normalized().Value);
        Assert.False(ExchangeRegistry.TryGet("LSE", out _));
        Assert.Throws<ArgumentException>(() => ExchangeRegistry.Get("LSE"));
    }

    [Fact]
    public void SeparatesRegionFromAssetClass()
    {
        // The old Market enum could not express "an ETF listed in the US" — it had one Etf member.
        Assert.True(ExchangeRegistry.TryGetRegion("NYSE", out var usRegion));
        Assert.True(ExchangeRegistry.TryGetRegion("SSE", out var cnRegion));
        Assert.Equal(MarketRegion.Us, usRegion);
        Assert.Equal(MarketRegion.Cn, cnRegion);
        Assert.NotEqual(usRegion, cnRegion);
        Assert.Equal("etf", AssetClass.Etf.ToCode());
    }

    [Theory]
    [InlineData("fund", AssetClass.MutualFund)]
    [InlineData("mutual_fund", AssetClass.MutualFund)]
    [InlineData("convertible_bond", AssetClass.ConvertibleBond)]
    [InlineData("fx", AssetClass.Fx)]
    [InlineData("cash", AssetClass.Cash)]
    public void ParsesAssetClassIncludingLegacyNames(string raw, AssetClass expected)
    {
        Assert.True(AssetClasses.TryParse(raw, out var asset));
        Assert.Equal(expected, asset);
    }

    [Theory]
    [InlineData("ashare", MarketRegion.Cn)]
    [InlineData("etf", MarketRegion.Cn)]
    [InlineData("hk", MarketRegion.Hk)]
    [InlineData("us", MarketRegion.Us)]
    [InlineData("jp", MarketRegion.Jp)]
    public void ParsesLegacyMarketStringsFromPersistedState(string raw, MarketRegion expected)
    {
        Assert.True(MarketRegions.TryParse(raw, out var region));
        Assert.Equal(expected, region);
    }

    [Fact]
    public void OnlyMutualFundsArePricedByNav()
    {
        Assert.True(AssetClass.MutualFund.IsNavPriced());
        Assert.False(AssetClass.Etf.IsNavPriced());
        Assert.False(AssetClass.Equity.IsNavPriced());
    }

    [Fact]
    public void YahooSuffixCoversTheNewJapanChannel()
    {
        Assert.Equal(".T", ExchangeRegistry.Get("TSE").YahooSuffix);
        Assert.Equal(".HK", ExchangeRegistry.Get("HKEX").YahooSuffix);
        Assert.Equal(".SS", ExchangeRegistry.Get("SSE").YahooSuffix);
        Assert.Equal("", ExchangeRegistry.Get("NASDAQ").YahooSuffix);
    }
}
