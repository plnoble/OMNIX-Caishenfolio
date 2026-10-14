using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// The novice must never have to type the internal <c>EXCHANGE:CODE</c> form. Every key task in
/// the plan (record cash, record a holding) has to work from what a person actually knows: a
/// bare code, a name, or a ticker.
/// </summary>
public class SymbolResolverTests
{
    private static readonly IReadOnlyList<Instrument> SampleInstruments =
    [
        Instrument.FromSymbol("SSE:600000", "浦发银行", AssetClass.Equity),
        Instrument.FromSymbol("NASDAQ:AAPL", "Apple", AssetClass.Equity),
        Instrument.FromSymbol("HKEX:00700", "腾讯控股", AssetClass.Equity),
        Instrument.FromSymbol("FUND:110022", "易方达消费行业", AssetClass.MutualFund),
    ];

    [Theory]
    [InlineData("600000", "SSE:600000")]        // 6 位、6 开头 → 上海
    [InlineData("000001", "SZSE:000001")]       // 6 位、0 开头 → 深圳
    [InlineData("300750", "SZSE:300750")]       // 6 位、3 开头 → 深圳创业板
    [InlineData("110022", "FUND:110022")]       // 6 位、非 A 股形态 → 场外基金
    [InlineData("00700", "HKEX:00700")]         // 5 位数字（保留前导零）→ 香港
    [InlineData("SSE:600000", "SSE:600000")]    // 已是内部形式 → 原样规范化
    [InlineData("sh:600000", "SSE:600000")]     // 别名前缀 → 归一
    public void BareInputResolvesWithoutTheInternalPrefix(string input, string expected)
    {
        var resolution = SymbolResolver.Resolve(input, SampleInstruments);
        Assert.Equal(expected, resolution.Resolved);
    }

    [Theory]
    [InlineData("MSFT")]    // 不在样例表格里：NASDAQ/NYSE 都可能 —— 不猜
    [InlineData("IBM")]     // NYSE 标的，绝不能静默落到 NASDAQ
    [InlineData("KO")]      // NYSE
    [InlineData("BABA")]    // NYSE
    [InlineData("7203")]    // 4 位数字：东京，也可能是港码 —— 不猜
    [InlineData("0700")]    // 4 位数字：港码，也可能是东京 —— 不猜
    public void AVenueAmbiguousBareInputStaysUnresolved(string input)
    {
        var resolution = SymbolResolver.Resolve(input, SampleInstruments);

        Assert.Null(resolution.Resolved);
        Assert.True(resolution.IsAmbiguous);
        Assert.True(resolution.Ambiguities.Count >= 2);
        Assert.Contains("完整形式", resolution.Reason);
    }

    [Fact]
    public void AnInstrumentTableHitStillResolvesAKnownTicker()
    {
        // AAPL is in the stored instrument table: the name hit resolves unambiguously.
        var resolution = SymbolResolver.Resolve("AAPL", SampleInstruments);
        Assert.Equal("NASDAQ:AAPL", resolution.Resolved);

        // IBM is NOT stored: bare ticker stays ambiguous even with the table present.
        var ibm = SymbolResolver.Resolve("IBM", SampleInstruments);
        Assert.Null(ibm.Resolved);
        Assert.Contains("NYSE:IBM", ibm.Ambiguities);
    }

    [Fact]
    public void NamesMatchFromTheInstrumentTable()
    {
        var exact = SymbolResolver.Resolve("浦发银行", SampleInstruments);
        Assert.Equal("SSE:600000", exact.Resolved);

        var partial = SymbolResolver.Resolve("腾讯", SampleInstruments);
        Assert.Equal("HKEX:00700", partial.Resolved);
    }

    [Fact]
    public void TheReasonAlwaysExplainsHowItResolved()
    {
        var resolution = SymbolResolver.Resolve("600000");
        Assert.Contains("上海", resolution.Reason);

        var unparsed = SymbolResolver.Resolve("不存在的东西");
        Assert.Contains("没找到", unparsed.Reason);
        Assert.Equal("不存在的东西", unparsed.Input);
    }

    [Fact]
    public void AmbiguityIsReportedNotSilentlyResolved()
    {
        // 00700 matches the HK code shape and also a stored HK instrument; both readings
        // collapse to one, so it is not ambiguous. Construct a real collision instead: a fund
        // whose name contains the bare code of a stock is contrived — instead ask for a name
        // fragment that matches two instruments.
        var instruments = new List<Instrument>
        {
            Instrument.FromSymbol("SSE:600000", "浦发银行", AssetClass.Equity),
            Instrument.FromSymbol("SZSE:000001", "浦发银行B", AssetClass.Equity),
        };

        var resolution = SymbolResolver.Resolve("浦发银行", instruments);

        Assert.True(resolution.IsAmbiguous);
        Assert.Equal(2, resolution.Ambiguities.Count);
        Assert.Contains("歧义", resolution.Reason);
    }

    [Fact]
    public void NothingIsEverFabricated()
    {
        var resolution = SymbolResolver.Resolve("999999999", SampleInstruments);

        // 9 digits match no known shape: refuse rather than guess a venue.
        Assert.Null(resolution.Resolved);
        Assert.False(resolution.IsAmbiguous);
    }

    [Fact]
    public void EmptyInputIsHandled()
    {
        var resolution = SymbolResolver.Resolve("");
        Assert.Null(resolution.Resolved);
        Assert.Equal("", resolution.Input);
    }
}

/// <summary>P1-R10: Beijing-exchange bare codes must not fall into the FUND bucket.</summary>
public class BseBareCodeTests
{
    [Theory]
    [InlineData("430047", "BSE:430047")]
    [InlineData("830799", "BSE:830799")]
    [InlineData("872925", "BSE:872925")]
    [InlineData("920819", "BSE:920819")]
    public void BseSeriesCodesResolveToBse(string input, string expected)
    {
        var resolution = SymbolResolver.Resolve(input);
        Assert.Equal(expected, resolution.Resolved);
    }

    [Fact]
    public void ABseCodeInTheInstrumentTableStillWinsByExactHit()
    {
        var instruments = new List<Instrument>
        {
            Instrument.FromSymbol("BSE:830799", "艾融软件", AssetClass.Equity),
        };
        var resolution = SymbolResolver.Resolve("830799", instruments);
        Assert.Equal("BSE:830799", resolution.Resolved);
    }

    [Fact]
    public void AFundCodeStillResolvesToFund()
    {
        // 110022 is the canonical mutual-fund shape (11 series) and must stay a fund.
        var resolution = SymbolResolver.Resolve("110022");
        Assert.Equal("FUND:110022", resolution.Resolved);
    }
}
