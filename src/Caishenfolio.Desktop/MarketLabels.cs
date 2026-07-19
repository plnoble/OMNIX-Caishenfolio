namespace Caishenfolio.Desktop;

/// <summary>
/// Human-readable market labels for UI (A股/港股/美股…), while internal codes stay EXCHANGE:CODE.
/// </summary>
public static class MarketLabels
{
    public static string FromSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "未知市场";
        }

        var parts = symbol.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 1)
        {
            return "未知市场";
        }

        return FromExchange(parts[0]);
    }

    public static string FromExchange(string? exchange) => (exchange ?? "").Trim().ToUpperInvariant() switch
    {
        "SSE" or "SZSE" or "BSE" or "SH" or "SZ" => "A股",
        "HKEX" or "HK" or "SEHK" => "港股",
        "NASDAQ" or "NYSE" or "AMEX" or "US" => "美股",
        "FUND" or "OF" => "基金",
        _ => string.IsNullOrWhiteSpace(exchange) ? "未知市场" : exchange!,
    };

    public static string FromMarketField(string? market) => (market ?? "").Trim().ToLowerInvariant() switch
    {
        "ashare" or "a_share" or "cn" => "A股",
        "hk" or "hongkong" => "港股",
        "us" or "usa" => "美股",
        "etf" => "ETF",
        "fund" => "基金",
        _ => string.IsNullOrWhiteSpace(market) ? "未知市场" : market!,
    };

    public static string FromAssetClass(string? asset) => (asset ?? "").Trim().ToLowerInvariant() switch
    {
        "equity" => "股票",
        "etf" => "ETF",
        "index" => "指数",
        "fund" => "基金",
        _ => string.IsNullOrWhiteSpace(asset) ? "" : asset!,
    };

    /// <summary>Example: [A股·股票] 浦发银行  ·  SSE:600000</summary>
    public static string FormatRow(string symbol, string? name, string? marketField = null, string? assetClass = null)
    {
        var market = !string.IsNullOrWhiteSpace(marketField)
            ? FromMarketField(marketField)
            : FromSymbol(symbol);
        var asset = FromAssetClass(assetClass);
        var title = string.IsNullOrWhiteSpace(name) ? symbol : name.Trim();
        var tag = string.IsNullOrEmpty(asset) ? market : $"{market}·{asset}";
        return $"[{tag}]  {title}  ·  {symbol}";
    }

    public static string FormatHint() =>
        "内部代码格式：交易所:代码（系统用）\n" +
        "· A股示例：SSE:600000（上交所浦发）、SZSE:000001（深交所平安）\n" +
        "· 港股示例：HKEX:00700（腾讯）\n" +
        "· 美股示例：NASDAQ:AAPL（苹果）、NYSE:SPY\n" +
        "界面会显示【A股/港股/美股】中文标签，不必死记交易所英文缩写。";
}
