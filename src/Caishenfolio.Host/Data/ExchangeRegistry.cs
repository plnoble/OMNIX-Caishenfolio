namespace Caishenfolio.Host.Data;

/// <summary>
/// A venue an instrument is quoted on. <c>FUND</c> and <c>FX</c> are pseudo-venues:
/// off-exchange open-end funds and currency pairs still need a stable <c>EXCHANGE:CODE</c> identity.
/// </summary>
/// <param name="Currency">Quote currency, or empty when it depends on the code (FX pairs).</param>
/// <param name="YahooSuffix">Suffix used by Yahoo-style vendors, e.g. <c>TSE:7203</c> → <c>7203.T</c>.</param>
public sealed record ExchangeInfo(
    string Code,
    string DisplayName,
    MarketRegion Region,
    string Currency,
    string TimeZoneId,
    AssetClass DefaultAssetClass,
    string YahooSuffix = "");

/// <summary>
/// Single source of truth mapping <c>EXCHANGE</c> to region, currency, and time zone.
/// Unknown venues are rejected — deny-by-default also applies to market identity.
/// </summary>
public static class ExchangeRegistry
{
    public const string Fx = "FX";
    public const string CnFund = "FUND";

    private static readonly ExchangeInfo[] Known =
    [
        new("SSE", "上海证券交易所", MarketRegion.Cn, Currencies.Cny, "Asia/Shanghai", AssetClass.Equity, ".SS"),
        new("SZSE", "深圳证券交易所", MarketRegion.Cn, Currencies.Cny, "Asia/Shanghai", AssetClass.Equity, ".SZ"),
        new("BSE", "北京证券交易所", MarketRegion.Cn, Currencies.Cny, "Asia/Shanghai", AssetClass.Equity, ".BJ"),
        new("CNIB", "银行间债券市场", MarketRegion.Cn, Currencies.Cny, "Asia/Shanghai", AssetClass.Bond),
        new(CnFund, "场外公募基金", MarketRegion.Cn, Currencies.Cny, "Asia/Shanghai", AssetClass.MutualFund),
        new("HKEX", "香港交易所", MarketRegion.Hk, Currencies.Hkd, "Asia/Hong_Kong", AssetClass.Equity, ".HK"),
        new("NASDAQ", "纳斯达克", MarketRegion.Us, Currencies.Usd, "America/New_York", AssetClass.Equity),
        new("NYSE", "纽约证券交易所", MarketRegion.Us, Currencies.Usd, "America/New_York", AssetClass.Equity),
        new("AMEX", "美国证券交易所", MarketRegion.Us, Currencies.Usd, "America/New_York", AssetClass.Equity),
        new("TSE", "东京证券交易所", MarketRegion.Jp, Currencies.Jpy, "Asia/Tokyo", AssetClass.Equity, ".T"),
        new(Fx, "外汇", MarketRegion.Global, "", "UTC", AssetClass.Fx),
    ];

    private static readonly IReadOnlyDictionary<string, ExchangeInfo> ByCode =
        Known.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SH"] = "SSE",
            ["SHSE"] = "SSE",
            ["SZ"] = "SZSE",
            ["BJ"] = "BSE",
            ["HK"] = "HKEX",
            ["SEHK"] = "HKEX",
            ["OF"] = CnFund,
            ["CNFUND"] = CnFund,
            ["TYO"] = "TSE",
            ["JPX"] = "TSE",
            ["FOREX"] = Fx,
        };

    public static IReadOnlyCollection<ExchangeInfo> All => Known;

    public static bool TryGet(string? exchange, out ExchangeInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return false;
        }

        var code = exchange.Trim();
        if (Aliases.TryGetValue(code, out var canonical))
        {
            code = canonical;
        }

        return ByCode.TryGetValue(code, out info!);
    }

    public static ExchangeInfo Get(string exchange)
    {
        if (!TryGet(exchange, out var info))
        {
            throw new ArgumentException(
                $"未知交易所 '{exchange}'。已支持: {string.Join(", ", Known.Select(e => e.Code))}。", nameof(exchange));
        }

        return info;
    }

    /// <summary>Canonical exchange code, resolving aliases (<c>SH</c> → <c>SSE</c>).</summary>
    public static string Canonicalize(string exchange) => Get(exchange).Code;

    public static bool TryGetRegion(string? exchange, out MarketRegion region)
    {
        if (TryGet(exchange, out var info))
        {
            region = info.Region;
            return true;
        }

        region = MarketRegion.Global;
        return false;
    }

    /// <summary>
    /// Quote currency for a symbol. FX pairs carry it in the code (<c>FX:USDCNY</c> is quoted in CNY);
    /// every other venue takes it from the registry.
    /// </summary>
    public static bool TryGetQuoteCurrency(SymbolId symbol, out string currency)
    {
        currency = "";
        if (!TryGet(symbol.Exchange, out var info))
        {
            return false;
        }

        if (info.Code == Fx)
        {
            return symbol.TryGetFxPair(out _, out currency);
        }

        currency = info.Currency;
        return !string.IsNullOrEmpty(currency);
    }
}
