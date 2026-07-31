using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// Static facts about something you can hold. Prices are never stored here — they come from
/// the market layer, so the ledger stays truthful when a provider is unavailable.
/// </summary>
public sealed record Instrument
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required AssetClass AssetClass { get; init; }
    public required MarketRegion Region { get; init; }
    public required string Currency { get; init; }
    /// <summary>Minimum tradable increment (A股 100 股一手, 场外基金 0.01 份)。0 means unconstrained.</summary>
    public decimal LotSize { get; init; }
    /// <summary>Face value for bonds — coupon and redemption are quoted against it.</summary>
    public decimal FaceValue { get; init; }
    public string Note { get; init; } = "";

    /// <summary>
    /// Builds an instrument from its symbol, filling region and currency from the exchange registry
    /// so a typo in the venue is caught here rather than surfacing as a mis-valued holding.
    /// </summary>
    public static Instrument FromSymbol(
        string symbol,
        string name,
        AssetClass assetClass,
        string? currency = null,
        decimal lotSize = 0m,
        decimal faceValue = 0m,
        string note = "")
    {
        if (!SymbolId.TryParse(symbol, out var parsed))
        {
            throw new ArgumentException($"标的代码 '{symbol}' 不是 交易所:代码 形式。", nameof(symbol));
        }

        parsed = parsed.Normalized();
        ExchangeRegistry.TryGetRegion(parsed.Exchange, out var region);

        var resolved = currency;
        if (string.IsNullOrWhiteSpace(resolved) && !ExchangeRegistry.TryGetQuoteCurrency(parsed, out resolved))
        {
            throw new ArgumentException(
                $"无法确定 '{parsed.Value}' 的计价货币，请显式指定。", nameof(currency));
        }

        return new Instrument
        {
            Symbol = parsed.Value,
            Name = string.IsNullOrWhiteSpace(name) ? parsed.Code : name.Trim(),
            AssetClass = assetClass,
            Region = region,
            Currency = Currencies.Normalize(resolved!),
            LotSize = lotSize,
            FaceValue = faceValue,
            Note = note.Trim(),
        };
    }
}
