namespace Caishenfolio.Host.Data;

public sealed record OhlcvBar(
    DateTimeOffset TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    string Currency,
    Adjustment Adjustment,
    string Provider,
    decimal? Amount = null,
    IReadOnlyDictionary<string, string>? Provenance = null);
