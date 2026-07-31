namespace Caishenfolio.Host.Data;

/// <summary>
/// Where an instrument trades. Deliberately separate from <see cref="AssetClass"/> —
/// the old <c>Market</c> enum mixed geography with instrument type (it had an <c>Etf</c> member),
/// which made "A股 ETF" and "美股 ETF" unrepresentable.
/// </summary>
public enum MarketRegion
{
    Cn,
    Hk,
    Us,
    Jp,
    Global,
}

public static class MarketRegions
{
    private static readonly IReadOnlyDictionary<string, MarketRegion> Aliases =
        new Dictionary<string, MarketRegion>(StringComparer.OrdinalIgnoreCase)
        {
            ["cn"] = MarketRegion.Cn,
            ["ashare"] = MarketRegion.Cn,
            ["a_share"] = MarketRegion.Cn,
            ["a-share"] = MarketRegion.Cn,
            ["china"] = MarketRegion.Cn,
            ["hk"] = MarketRegion.Hk,
            ["hongkong"] = MarketRegion.Hk,
            ["hong_kong"] = MarketRegion.Hk,
            ["us"] = MarketRegion.Us,
            ["usa"] = MarketRegion.Us,
            ["jp"] = MarketRegion.Jp,
            ["japan"] = MarketRegion.Jp,
            ["global"] = MarketRegion.Global,
            ["world"] = MarketRegion.Global,
        };

    public static string ToCode(this MarketRegion region) => region switch
    {
        MarketRegion.Cn => "cn",
        MarketRegion.Hk => "hk",
        MarketRegion.Us => "us",
        MarketRegion.Jp => "jp",
        _ => "global",
    };

    public static string ToDisplayName(this MarketRegion region) => region switch
    {
        MarketRegion.Cn => "A股",
        MarketRegion.Hk => "港股",
        MarketRegion.Us => "美股",
        MarketRegion.Jp => "日股",
        _ => "全球",
    };

    /// <summary>Accepts legacy market strings persisted before the split (e.g. "ashare", "etf").</summary>
    public static bool TryParse(string? value, out MarketRegion region)
    {
        region = MarketRegion.Global;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var key = value.Trim();

        // Legacy "etf" / "fund" were market values that actually described the asset class.
        if (key.Equals("etf", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("fund", StringComparison.OrdinalIgnoreCase))
        {
            region = MarketRegion.Cn;
            return true;
        }

        return Aliases.TryGetValue(key, out region);
    }
}
