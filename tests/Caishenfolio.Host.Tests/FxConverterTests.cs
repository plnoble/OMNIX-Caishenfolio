using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class FxConverterTests
{
    private static readonly DateOnly Day = new(2026, 7, 31);

    [Fact]
    public void ConvertsDirectlyAndByInverse()
    {
        var fx = new FxConverter([FxRate.Of("USD", "CNY", 7.2m, Day)]);

        Assert.True(fx.TryConvert(Money.Of(100m, "USD"), "CNY", out var cny));
        Assert.Equal(720m, cny.Amount);

        Assert.True(fx.TryGetRate("CNY", "USD", out var inverse));
        Assert.Equal(1m / 7.2m, inverse);
    }

    [Fact]
    public void TriangulatesThroughThePivotCurrency()
    {
        var fx = new FxConverter([
            FxRate.Of("USD", "CNY", 7.2m, Day),
            FxRate.Of("USD", "HKD", 7.8m, Day),
        ]);

        // No HKDCNY rate was published, so HKD -> USD -> CNY.
        Assert.True(fx.TryGetRate("HKD", "CNY", out var rate));
        Assert.Equal(7.2m / 7.8m, rate, 12);

        // The triangulated product is not exact, so the converted amount lands on CNY's minor units.
        Assert.True(fx.TryConvert(Money.Of(780m, "HKD"), "CNY", out var cny));
        Assert.Equal(720m, cny.Amount);
    }

    [Fact]
    public void ConvertedAmountsCarryTheTargetCurrencyPrecision()
    {
        var fx = new FxConverter([FxRate.Of("USD", "JPY", 150m, Day), FxRate.Of("USD", "CNY", 7.2m, Day)]);

        Assert.True(fx.TryConvert(Money.Of(300_000m, "JPY"), "CNY", out var cny));
        Assert.Equal(14_400.00m, cny.Amount);

        // JPY has no minor units, so a conversion into it lands on whole yen.
        Assert.True(fx.TryConvert(Money.Of(100m, "CNY"), "JPY", out var jpy));
        Assert.Equal(2083m, jpy.Amount);
    }

    [Fact]
    public void SameCurrencyIsIdentity()
    {
        Assert.True(FxConverter.Empty.TryConvert(Money.Of(100m, "CNY"), "CNY", out var same));
        Assert.Equal(100m, same.Amount);
    }

    [Fact]
    public void MissingPairFailsClosed()
    {
        var fx = new FxConverter([FxRate.Of("USD", "CNY", 7.2m, Day)]);

        Assert.False(fx.TryGetRate("JPY", "CNY", out _));
        Assert.False(fx.TryConvert(Money.Of(1000m, "JPY"), "CNY", out _));
        var error = Assert.Throws<LedgerException>(() => fx.Convert(Money.Of(1000m, "JPY"), "CNY"));
        Assert.Contains("缺少", error.Message);
    }

    [Fact]
    public void FreshestObservationWins()
    {
        var fx = new FxConverter([
            FxRate.Of("USD", "CNY", 7.0m, Day.AddDays(-30)),
            FxRate.Of("USD", "CNY", 7.2m, Day),
        ]);

        Assert.True(fx.TryGetRate("USD", "CNY", out var rate));
        Assert.Equal(7.2m, rate);
    }

    [Fact]
    public void RateCarriesItsFxSymbol()
    {
        Assert.Equal("FX:USDCNY", FxRate.Of("usd", "cny", 7.2m, Day).Symbol);
    }

    [Fact]
    public void RejectsImpossibleRates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FxRate.Of("USD", "CNY", 0m, Day));
        Assert.Throws<ArgumentOutOfRangeException>(() => FxRate.Of("USD", "CNY", -1m, Day));
        Assert.Throws<ArgumentException>(() => FxRate.Of("CNY", "CNY", 1m, Day));
        Assert.Throws<ArgumentException>(() => FxRate.Of("USD", "XYZ", 1m, Day));
    }
}
