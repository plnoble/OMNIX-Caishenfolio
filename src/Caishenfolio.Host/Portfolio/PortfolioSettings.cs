using System.Globalization;
using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>How the app presents itself on startup. Task-first for novices, full tree otherwise.</summary>
public enum UiMode
{
    Novice,
    Professional,
}

/// <summary>How soon the money might be needed — one of a small, honest set (plan §6).</summary>
public static class LiquidityNeeds
{
    public const string Anytime = "anytime";
    public const string ThreeMonths = "3m";
    public const string OneYear = "1y";
    public const string LongTerm = "long";

    public static readonly IReadOnlyList<string> All = ["", Anytime, ThreeMonths, OneYear, LongTerm];

    public static bool IsKnown(string code) =>
        All.Contains((code ?? "").Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Display label; blank input reads as not stated.</summary>
    public static string Label(string code) => code.Trim().ToLowerInvariant() switch
    {
        Anytime => "随时可能要用",
        ThreeMonths => "3 个月内可能用",
        OneYear => "1 年内可能用",
        LongTerm => "3 年以上基本不动",
        _ => "",
    };
}

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

    // --- novice profile (N1) -------------------------------------------------------

    /// <summary>Which presentation the app opens with: task-first or the full professional tree.</summary>
    public UiMode UiMode { get; init; } = UiMode.Novice;

    /// <summary>False until the first-run guide has been completed or skipped — never nag twice.</summary>
    public bool OnboardingCompleted { get; init; }

    /// <summary>What the money is for, in the user's own words; shown back on the home page.</summary>
    public string Goal { get; init; } = "";

    /// <summary>How long until the money is needed, in years; null when not stated.</summary>
    public decimal? HorizonYears { get; init; }

    /// <summary>
    /// How soon the money might have to be spent: "" (not stated), anytime, 3m, 1y, long.
    /// Collected at onboarding (plan §6) and shown back on the home page — a portfolio that
    /// may be emptied next month should not be read like a ten-year one.
    /// </summary>
    public string LiquidityNeed { get; init; } = "";

    /// <summary>Emergency reserve in the base currency. Tracked separately so idle-cash warnings skip it.</summary>
    public decimal EmergencyFundAmount { get; init; }

    /// <summary>Largest fall from a peak the user says they can sit through; 0 = not stated.</summary>
    public decimal MaxDrawdownTolerance { get; init; }

    /// <summary>Region codes the user cares about, e.g. <c>["cn", "hk"]</c>.</summary>
    public IReadOnlyList<string> WatchMarkets { get; init; } = [];

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

            // An explicit zero target is a statement ("hold none of this"), not an omission
            // (P1-R08): dropping it here made the rebalance analyzer treat the class as
            // unconfigured and refuse to plan its liquidation.
            targets[asset.ToCode()] = weight;
        }

        if (HorizonYears is < 0m)
        {
            throw new LedgerException($"资金期限不能为负（收到 {HorizonYears} 年）。");
        }

        if (EmergencyFundAmount < 0m)
        {
            throw new LedgerException($"应急金不能为负（收到 {EmergencyFundAmount}）。");
        }

        if (!LiquidityNeeds.IsKnown(LiquidityNeed))
        {
            throw new LedgerException($"未知流动性需求「{LiquidityNeed}」。");
        }

        if (MaxDrawdownTolerance != 0m && (MaxDrawdownTolerance <= 0m || MaxDrawdownTolerance > 1m))
        {
            throw new LedgerException($"可承受回撤必须是 0（未填）或 1%–100%（收到 {MaxDrawdownTolerance}）。");
        }

        foreach (var market in WatchMarkets)
        {
            if (!MarketRegions.TryParse(market, out _))
            {
                throw new LedgerException($"未知市场「{market}」。");
            }
        }

        var markets = WatchMarkets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => MarketRegions.TryParse(code, out var region) ? region.ToCode() : code)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        var validated = this with
        {
            BaseCurrency = currency,
            TargetAssetAllocation = targets,
            Goal = Goal.Trim(),
            LiquidityNeed = LiquidityNeed.Trim().ToLowerInvariant(),
            WatchMarkets = markets,
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
            ["ui.mode"] = UiMode.ToString(),
            ["novice.onboarded"] = OnboardingCompleted ? "1" : "0",
            ["novice.goal"] = Goal,
            ["novice.horizon_years"] = HorizonYears is { } years ? Format(years) : "",
            ["novice.liquidity"] = LiquidityNeed,
            ["novice.emergency_fund"] = EmergencyFundAmount != 0m ? Format(EmergencyFundAmount) : "",
            ["novice.max_drawdown"] = MaxDrawdownTolerance != 0m ? Format(MaxDrawdownTolerance) : "",
            ["novice.watch_markets"] = string.Join(",", WatchMarkets),
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

            // Novice profile: unknown keys and blank values fall back to defaults, so a settings
            // row written by an older release simply reads as "not stated yet".
            UiMode = values.TryGetValue("ui.mode", out var mode)
                && Enum.TryParse<UiMode>(mode, ignoreCase: true, out var parsed) ? parsed : UiMode.Novice,
            OnboardingCompleted = values.TryGetValue("novice.onboarded", out var done) && done == "1",
            Goal = values.TryGetValue("novice.goal", out var goal) ? goal : "",
            HorizonYears = ReadOptional(values, "novice.horizon_years"),
            // Unknown/blank keys read as not-stated; an older settings row never breaks startup.
            LiquidityNeed = LiquidityNeeds.IsKnown(
                (values.TryGetValue("novice.liquidity", out var liquidity) ? liquidity : "").Trim())
                ? (liquidity ?? "").Trim().ToLowerInvariant()
                : "",
            EmergencyFundAmount = Read(values, "novice.emergency_fund", 0m),
            MaxDrawdownTolerance = Read(values, "novice.max_drawdown", 0m),
            WatchMarkets = values.TryGetValue("novice.watch_markets", out var markets) && markets.Length > 0
                ? markets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [],
        };
    }

    private static decimal? ReadOptional(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) && TryParse(raw, out var parsed)
            ? parsed
            : null;

    private static decimal Read(IReadOnlyDictionary<string, string> values, string key, decimal fallback) =>
        values.TryGetValue(key, out var raw) && TryParse(raw, out var parsed) ? parsed : fallback;

    private static bool TryParse(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    private static string Format(decimal value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
