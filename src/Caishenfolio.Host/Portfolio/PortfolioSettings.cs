using System.Globalization;
using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// User preferences that shape valuation and risk reporting.
///
/// Target weights are the user's own plan, not a recommendation — the analyzer only reports the
/// arithmetic distance from them.
/// </summary>
public sealed record PortfolioSettings
{
    public string BaseCurrency { get; init; } = Currencies.Cny;
    public RiskThresholds Thresholds { get; init; } = RiskThresholds.Default;
    /// <summary>Target weight per asset-class code, e.g. <c>{"equity": 0.6, "bond": 0.4}</c>.</summary>
    public IReadOnlyDictionary<string, decimal> TargetAssetAllocation { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    /// <summary>
    /// Ask every data source for each price and compare, instead of trusting whichever answers
    /// first. Costs one request per extra source; catches a bad price before it mis-values the
    /// portfolio.
    /// </summary>
    public bool CrossCheckPrices { get; init; } = true;

    /// <summary>Percent spread between sources beyond which a quote is reported as disputed.</summary>
    public decimal PriceTolerancePercent { get; init; } = 2m;

    public static PortfolioSettings Default { get; } = new();

    public decimal TargetTotal => TargetAssetAllocation.Values.Sum();

    /// <summary>True when targets are either unset or add up to 100%.</summary>
    public bool TargetsAreCoherent =>
        TargetAssetAllocation.Count == 0 || Math.Abs(TargetTotal - 1m) <= 0.0001m;

    /// <summary>Normalizes and rejects values that cannot be a valid preference.</summary>
    public PortfolioSettings Validated()
    {
        var currency = Currencies.Normalize(BaseCurrency);
        EnsureFraction(Thresholds.SinglePosition, "单一持仓上限");
        EnsureFraction(Thresholds.AssetClass, "品种上限");
        EnsureFraction(Thresholds.Region, "市场上限");
        EnsureFraction(Thresholds.Currency, "货币上限");
        EnsureFraction(Thresholds.Cash, "现金上限");
        if (PriceTolerancePercent <= 0m || PriceTolerancePercent > 100m)
        {
            throw new LedgerException($"价格容差必须大于 0% 且不超过 100%（收到 {PriceTolerancePercent}）。");
        }

        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (key, weight) in TargetAssetAllocation)
        {
            if (!AssetClasses.TryParse(key, out var asset))
            {
                throw new LedgerException($"未知品种「{key}」。");
            }

            if (weight < 0m || weight > 1m)
            {
                throw new LedgerException($"「{asset.ToDisplayName()}」的目标占比必须在 0% 到 100% 之间。");
            }

            if (weight > 0m)
            {
                targets[asset.ToCode()] = weight;
            }
        }

        var validated = this with
        {
            BaseCurrency = currency,
            TargetAssetAllocation = targets,
        };

        if (!validated.TargetsAreCoherent)
        {
            throw new LedgerException(
                $"目标配置合计为 {validated.TargetTotal * 100m:0.##}%，必须正好 100%（或全部留空）。");
        }

        return validated;
    }

    private static void EnsureFraction(decimal value, string label)
    {
        if (value <= 0m || value > 1m)
        {
            throw new LedgerException($"{label}必须大于 0% 且不超过 100%。");
        }
    }

    // --- key/value mapping ---------------------------------------------------------

    internal const string TargetPrefix = "target.";

    internal IReadOnlyDictionary<string, string> ToKeyValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["base_currency"] = BaseCurrency,
            ["risk.single_position"] = Format(Thresholds.SinglePosition),
            ["risk.asset_class"] = Format(Thresholds.AssetClass),
            ["risk.region"] = Format(Thresholds.Region),
            ["risk.currency"] = Format(Thresholds.Currency),
            ["risk.cash"] = Format(Thresholds.Cash),
            ["price.cross_check"] = CrossCheckPrices ? "1" : "0",
            ["price.tolerance_pct"] = Format(PriceTolerancePercent),
        };

        foreach (var (key, weight) in TargetAssetAllocation)
        {
            values[TargetPrefix + key] = Format(weight);
        }

        return values;
    }

    internal static PortfolioSettings FromKeyValues(IReadOnlyDictionary<string, string> values)
    {
        var defaults = Default;
        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (key, raw) in values)
        {
            if (key.StartsWith(TargetPrefix, StringComparison.Ordinal) && TryParse(raw, out var weight))
            {
                targets[key[TargetPrefix.Length..]] = weight;
            }
        }

        return new PortfolioSettings
        {
            BaseCurrency = values.TryGetValue("base_currency", out var currency) && Currencies.IsKnown(currency)
                ? Currencies.Normalize(currency)
                : defaults.BaseCurrency,
            Thresholds = new RiskThresholds
            {
                SinglePosition = Read(values, "risk.single_position", defaults.Thresholds.SinglePosition),
                AssetClass = Read(values, "risk.asset_class", defaults.Thresholds.AssetClass),
                Region = Read(values, "risk.region", defaults.Thresholds.Region),
                Currency = Read(values, "risk.currency", defaults.Thresholds.Currency),
                Cash = Read(values, "risk.cash", defaults.Thresholds.Cash),
            },
            TargetAssetAllocation = targets,
            CrossCheckPrices = !values.TryGetValue("price.cross_check", out var cross) || cross != "0",
            PriceTolerancePercent = Read(values, "price.tolerance_pct", defaults.PriceTolerancePercent),
        };
    }

    private static decimal Read(IReadOnlyDictionary<string, string> values, string key, decimal fallback) =>
        values.TryGetValue(key, out var raw) && TryParse(raw, out var parsed) ? parsed : fallback;

    private static bool TryParse(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    private static string Format(decimal value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
