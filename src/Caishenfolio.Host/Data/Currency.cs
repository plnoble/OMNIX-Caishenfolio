namespace Caishenfolio.Host.Data;

/// <summary>
/// Currency metadata. <see cref="MinorUnits"/> drives money rounding — JPY/KRW have none.
/// </summary>
public sealed record CurrencyInfo(string Code, string Symbol, int MinorUnits, string DisplayName);

/// <summary>
/// Registry of currencies the workbench can hold. Unknown codes are rejected so that
/// a typo can never silently create a parallel "currency" in the ledger.
/// </summary>
public static class Currencies
{
    public const string Cny = "CNY";
    public const string Hkd = "HKD";
    public const string Usd = "USD";
    public const string Jpy = "JPY";

    private static readonly IReadOnlyDictionary<string, CurrencyInfo> Registry =
        new Dictionary<string, CurrencyInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["CNY"] = new("CNY", "¥", 2, "人民币"),
            ["HKD"] = new("HKD", "HK$", 2, "港元"),
            ["USD"] = new("USD", "$", 2, "美元"),
            ["JPY"] = new("JPY", "¥", 0, "日元"),
            ["EUR"] = new("EUR", "€", 2, "欧元"),
            ["GBP"] = new("GBP", "£", 2, "英镑"),
            ["TWD"] = new("TWD", "NT$", 2, "新台币"),
            ["SGD"] = new("SGD", "S$", 2, "新加坡元"),
            ["AUD"] = new("AUD", "A$", 2, "澳元"),
            ["CAD"] = new("CAD", "C$", 2, "加元"),
            ["CHF"] = new("CHF", "CHF", 2, "瑞士法郎"),
            ["KRW"] = new("KRW", "₩", 0, "韩元"),
        };

    public static IReadOnlyCollection<CurrencyInfo> All => (IReadOnlyCollection<CurrencyInfo>)Registry.Values;

    public static bool IsKnown(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Registry.ContainsKey(code.Trim());

    public static bool TryGet(string? code, out CurrencyInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return Registry.TryGetValue(code.Trim(), out info!);
    }

    public static CurrencyInfo Get(string code)
    {
        if (!TryGet(code, out var info))
        {
            throw new ArgumentException($"未知货币 '{code}'。已支持: {string.Join(", ", Registry.Keys)}。", nameof(code));
        }

        return info;
    }

    /// <summary>Canonical upper-case code; throws for unknown currencies.</summary>
    public static string Normalize(string code) => Get(code).Code;

    public static int MinorUnitsOf(string code) => Get(code).MinorUnits;
}
