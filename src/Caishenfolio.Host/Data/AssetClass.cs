namespace Caishenfolio.Host.Data;

/// <summary>
/// What an instrument is. Drives pricing channel (OHLCV vs NAV vs FX quote),
/// cost accounting, and allocation grouping.
/// </summary>
public enum AssetClass
{
    Equity,
    Etf,
    Index,
    /// <summary>Off-exchange open-end fund priced by daily NAV (场外公募基金).</summary>
    MutualFund,
    Bond,
    /// <summary>可转债 — bond with equity conversion, tracked separately for allocation.</summary>
    ConvertibleBond,
    /// <summary>Currency pair, held as an FX position or used only as a conversion rate.</summary>
    Fx,
    /// <summary>Cash / deposit / money-market balance.</summary>
    Cash,
    Commodity,
    Reit,
}

public static class AssetClasses
{
    private static readonly IReadOnlyDictionary<string, AssetClass> Aliases =
        new Dictionary<string, AssetClass>(StringComparer.OrdinalIgnoreCase)
        {
            ["equity"] = AssetClass.Equity,
            ["stock"] = AssetClass.Equity,
            ["etf"] = AssetClass.Etf,
            ["index"] = AssetClass.Index,
            ["mutual_fund"] = AssetClass.MutualFund,
            ["mutualfund"] = AssetClass.MutualFund,
            // Legacy: "fund" meant off-exchange open-end fund before ETFs got their own class.
            ["fund"] = AssetClass.MutualFund,
            ["bond"] = AssetClass.Bond,
            ["convertible_bond"] = AssetClass.ConvertibleBond,
            ["convertiblebond"] = AssetClass.ConvertibleBond,
            ["cb"] = AssetClass.ConvertibleBond,
            ["fx"] = AssetClass.Fx,
            ["forex"] = AssetClass.Fx,
            ["currency"] = AssetClass.Fx,
            ["cash"] = AssetClass.Cash,
            ["deposit"] = AssetClass.Cash,
            ["commodity"] = AssetClass.Commodity,
            ["reit"] = AssetClass.Reit,
        };

    public static string ToCode(this AssetClass asset) => asset switch
    {
        AssetClass.Equity => "equity",
        AssetClass.Etf => "etf",
        AssetClass.Index => "index",
        AssetClass.MutualFund => "mutual_fund",
        AssetClass.Bond => "bond",
        AssetClass.ConvertibleBond => "convertible_bond",
        AssetClass.Fx => "fx",
        AssetClass.Cash => "cash",
        AssetClass.Commodity => "commodity",
        _ => "reit",
    };

    public static string ToDisplayName(this AssetClass asset) => asset switch
    {
        AssetClass.Equity => "股票",
        AssetClass.Etf => "ETF",
        AssetClass.Index => "指数",
        AssetClass.MutualFund => "场外基金",
        AssetClass.Bond => "债券",
        AssetClass.ConvertibleBond => "可转债",
        AssetClass.Fx => "外汇",
        AssetClass.Cash => "现金",
        AssetClass.Commodity => "商品",
        _ => "REITs",
    };

    /// <summary>True when the instrument is priced by daily NAV instead of OHLCV bars.</summary>
    public static bool IsNavPriced(this AssetClass asset) => asset == AssetClass.MutualFund;

    public static bool TryParse(string? value, out AssetClass asset)
    {
        asset = AssetClass.Equity;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Aliases.TryGetValue(value.Trim(), out asset);
    }
}
