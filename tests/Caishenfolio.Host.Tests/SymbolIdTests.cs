using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Tests;

public class SymbolIdTests
{
    [Theory]
    [InlineData("SSE:600000", "SSE", "600000")]
    [InlineData("hkex:00700", "HKEX", "00700")]
    [InlineData("NASDAQ:AAPL", "NASDAQ", "AAPL")]
    [InlineData("NYSE:BRK.B", "NYSE", "BRK.B")]
    public void ParsesValidSymbols(string raw, string exchange, string code)
    {
        var symbol = SymbolId.Parse(raw);
        Assert.Equal(exchange, symbol.Exchange);
        Assert.Equal(code, symbol.Code);
        Assert.Equal($"{exchange}:{code}", symbol.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAPL")]
    [InlineData(":AAPL")]
    [InlineData("NASDAQ:")]
    [InlineData("BAD SYMBOL")]
    public void RejectsInvalidSymbols(string raw)
    {
        Assert.False(SymbolId.TryParse(raw, out _));
        Assert.Throws<FormatException>(() => SymbolId.Parse(raw));
    }
}
