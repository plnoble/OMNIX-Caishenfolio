using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Tests;

public class MoneyTests
{
    [Fact]
    public void AddsAndSubtractsWithinOneCurrency()
    {
        var a = Money.Of(10.25m, "CNY");
        var b = Money.Of(4.75m, "CNY");

        Assert.Equal(15.00m, (a + b).Amount);
        Assert.Equal(5.50m, (a - b).Amount);
        Assert.Equal("CNY", (a + b).Currency);
    }

    [Fact]
    public void RefusesMixedCurrencyArithmetic()
    {
        var cny = Money.Of(100m, "CNY");
        var usd = Money.Of(100m, "USD");

        Assert.Throws<InvalidOperationException>(() => cny + usd);
        Assert.Throws<InvalidOperationException>(() => cny - usd);
        Assert.Throws<InvalidOperationException>(() => cny > usd);
    }

    [Fact]
    public void KeepsExactDecimalWhereDoubleWouldDrift()
    {
        // 0.1 + 0.2 == 0.30000000000000004 in binary floating point.
        var sum = Money.Of(0.1m, "CNY") + Money.Of(0.2m, "CNY");
        Assert.Equal(0.3m, sum.Amount);
    }

    [Theory]
    [InlineData("CNY", 2, 12.345, 12.35)]
    [InlineData("USD", 2, 2.005, 2.01)]
    [InlineData("JPY", 0, 2800.4, 2800)]
    [InlineData("JPY", 0, 2800.5, 2801)]
    public void RoundsToCurrencyMinorUnits(string currency, int minorUnits, decimal raw, decimal expected)
    {
        Assert.Equal(minorUnits, Currencies.MinorUnitsOf(currency));
        Assert.Equal(expected, Money.Of(raw, currency).Round().Amount);
    }

    [Fact]
    public void ConvertsWithPositiveRateOnly()
    {
        var usd = Money.Of(100m, "USD");
        var cny = usd.ConvertTo("CNY", 7.2m);

        Assert.Equal(720m, cny.Amount);
        Assert.Equal("CNY", cny.Currency);
        Assert.Throws<ArgumentOutOfRangeException>(() => usd.ConvertTo("CNY", 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => usd.ConvertTo("CNY", -1m));
    }

    [Fact]
    public void ConvertingToSameCurrencyIsIdentity()
    {
        var cny = Money.Of(88.88m, "CNY");
        Assert.Equal(cny, cny.ConvertTo("CNY", 7.2m));
    }

    [Fact]
    public void RejectsUnknownCurrency()
    {
        Assert.Throws<ArgumentException>(() => Money.Of(1m, "XYZ"));
        Assert.False(Currencies.IsKnown("XYZ"));
    }

    [Fact]
    public void SumsEmptySequenceIntoExplicitCurrencyZero()
    {
        var total = Money.Sum([], "HKD");
        Assert.True(total.IsZero);
        Assert.Equal("HKD", total.Currency);
    }

    [Fact]
    public void SumsSequence()
    {
        Money[] values = [Money.Of(1.11m, "USD"), Money.Of(2.22m, "USD"), Money.Of(3.33m, "USD")];
        Assert.Equal(6.66m, Money.Sum(values, "USD").Amount);
    }
}
