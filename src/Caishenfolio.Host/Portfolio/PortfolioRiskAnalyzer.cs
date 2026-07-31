using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// Weight ceilings the user considers worth flagging. These are personal preferences, not
/// recommendations — the analyzer only reports that a threshold you set has been crossed.
/// </summary>
public sealed record RiskThresholds
{
    public decimal SinglePosition { get; init; } = 0.20m;
    public decimal AssetClass { get; init; } = 0.60m;
    public decimal Region { get; init; } = 0.70m;
    public decimal Currency { get; init; } = 0.80m;
    /// <summary>Idle cash above this share is worth noticing in a wealth ledger.</summary>
    public decimal Cash { get; init; } = 0.40m;

    public static RiskThresholds Default { get; } = new();
}

public enum RiskLevel
{
    Info,
    Warning,
}

/// <summary>A threshold that was crossed. Factual, not advisory.</summary>
public sealed record RiskFinding
{
    public required string Dimension { get; init; }
    public required string Label { get; init; }
    public required decimal Weight { get; init; }
    public required decimal Threshold { get; init; }
    public required RiskLevel Level { get; init; }
    public required string Message { get; init; }
}

/// <summary>Arithmetic drift from a target weight the user supplied.</summary>
public sealed record RebalanceDrift
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required decimal CurrentWeight { get; init; }
    public required decimal TargetWeight { get; init; }
    public required Money Delta { get; init; }
    public required string Message { get; init; }

    public bool IsOverweight => CurrentWeight > TargetWeight;
}

public sealed record PortfolioRiskReport
{
    public required IReadOnlyList<RiskFinding> Findings { get; init; }
    public required IReadOnlyList<RebalanceDrift> Drift { get; init; }
    /// <summary>Largest peak-to-trough fall in the stored equity curve; null without history.</summary>
    public decimal? MaxDrawdown { get; init; }
    public DateOnly? DrawdownPeak { get; init; }
    public DateOnly? DrawdownTrough { get; init; }
    public required string Summary { get; init; }

    public bool HasWarnings => Findings.Any(f => f.Level == RiskLevel.Warning);

    public static PortfolioRiskReport Empty { get; } = new()
    {
        Findings = [],
        Drift = [],
        Summary = ProductInfo.ResearchDisclaimer,
    };
}

/// <summary>
/// Reports concentration against user-set ceilings, the realised drawdown of the stored equity
/// curve, and arithmetic drift from user-supplied target weights.
///
/// Deliberately opinion-free: it never suggests what to buy or sell, only states that a number
/// you configured has been exceeded and by how much. Every summary carries the research
/// disclaimer.
/// </summary>
public static class PortfolioRiskAnalyzer
{
    public static PortfolioRiskReport Analyze(
        PortfolioValuation valuation,
        RiskThresholds? thresholds = null,
        IReadOnlyList<ValuationPoint>? equityCurve = null,
        IReadOnlyDictionary<string, decimal>? targetAssetAllocation = null)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        var limits = thresholds ?? RiskThresholds.Default;
        var findings = new List<RiskFinding>();

        foreach (var position in valuation.Positions.Where(p => p.Weight is not null))
        {
            var weight = position.Weight!.Value;
            if (weight <= limits.SinglePosition)
            {
                continue;
            }

            findings.Add(new RiskFinding
            {
                Dimension = "持仓",
                Label = position.Position.Symbol,
                Weight = weight,
                Threshold = limits.SinglePosition,
                Level = RiskLevel.Warning,
                Message = $"{position.Position.Symbol} 占总资产 {Percent(weight)}，超过你设定的单一持仓上限 {Percent(limits.SinglePosition)}。",
            });
        }

        AddSliceFindings(findings, "品种", valuation.ByAssetClass, limits.AssetClass, limits.Cash);
        AddSliceFindings(findings, "市场", valuation.ByRegion, limits.Region, null);
        AddSliceFindings(findings, "货币", valuation.ByCurrency, limits.Currency, null);

        var drawdown = MaxDrawdown(equityCurve);
        var drift = BuildDrift(valuation, targetAssetAllocation);

        return new PortfolioRiskReport
        {
            Findings = findings
                .OrderByDescending(f => f.Weight)
                .ToArray(),
            Drift = drift,
            MaxDrawdown = drawdown?.Depth,
            DrawdownPeak = drawdown?.Peak,
            DrawdownTrough = drawdown?.Trough,
            Summary = BuildSummary(findings, drawdown?.Depth, drift, valuation),
        };
    }

    /// <summary>Largest peak-to-trough decline in the curve, as a positive fraction.</summary>
    public static (decimal Depth, DateOnly Peak, DateOnly Trough)? MaxDrawdown(
        IReadOnlyList<ValuationPoint>? curve)
    {
        if (curve is null || curve.Count < 2)
        {
            return null;
        }

        var ordered = curve.OrderBy(p => p.Date).ToArray();
        decimal peakValue = 0m;
        DateOnly peakDate = ordered[0].Date;
        decimal worst = 0m;
        DateOnly worstPeak = ordered[0].Date;
        DateOnly worstTrough = ordered[0].Date;

        foreach (var point in ordered)
        {
            if (point.Value > peakValue)
            {
                peakValue = point.Value;
                peakDate = point.Date;
                continue;
            }

            if (peakValue <= 0m)
            {
                continue;
            }

            var decline = (peakValue - point.Value) / peakValue;
            if (decline > worst)
            {
                worst = decline;
                worstPeak = peakDate;
                worstTrough = point.Date;
            }
        }

        return worst <= 0m ? null : (worst, worstPeak, worstTrough);
    }

    private static void AddSliceFindings(
        List<RiskFinding> findings,
        string dimension,
        IReadOnlyList<AllocationSlice> slices,
        decimal threshold,
        decimal? cashThreshold)
    {
        foreach (var slice in slices)
        {
            var isCash = slice.Key == "cash";
            var limit = isCash && cashThreshold is not null ? cashThreshold.Value : threshold;
            if (slice.Weight <= limit)
            {
                continue;
            }

            findings.Add(new RiskFinding
            {
                Dimension = dimension,
                Label = slice.Label,
                Weight = slice.Weight,
                Threshold = limit,
                Level = RiskLevel.Warning,
                Message = isCash
                    ? $"现金占 {Percent(slice.Weight)}，超过你设定的 {Percent(limit)}。"
                    : $"{dimension}「{slice.Label}」占 {Percent(slice.Weight)}，超过你设定的 {Percent(limit)}。",
            });
        }
    }

    private static IReadOnlyList<RebalanceDrift> BuildDrift(
        PortfolioValuation valuation,
        IReadOnlyDictionary<string, decimal>? targets)
    {
        if (targets is null || targets.Count == 0 || valuation.TotalValue.IsZero)
        {
            return [];
        }

        var actual = valuation.ByAssetClass.ToDictionary(s => s.Key, s => s, StringComparer.Ordinal);
        var results = new List<RebalanceDrift>();

        foreach (var (key, target) in targets)
        {
            var current = actual.TryGetValue(key, out var slice) ? slice.Weight : 0m;
            var label = actual.TryGetValue(key, out var found)
                ? found.Label
                : AssetClasses.TryParse(key, out var asset) ? asset.ToDisplayName() : key;

            var deltaAmount = (target - current) * valuation.TotalValue.Amount;
            var delta = Money.Of(deltaAmount, valuation.BaseCurrency).Round();
            if (delta.IsZero)
            {
                continue;
            }

            results.Add(new RebalanceDrift
            {
                Key = key,
                Label = label,
                CurrentWeight = current,
                TargetWeight = target,
                Delta = delta,
                Message = deltaAmount < 0m
                    ? $"「{label}」当前 {Percent(current)}，目标 {Percent(target)}，高出 {Money.Of(-deltaAmount, valuation.BaseCurrency).Round().Amount:#,0.##} {valuation.BaseCurrency}。"
                    : $"「{label}」当前 {Percent(current)}，目标 {Percent(target)}，差 {delta.Amount:#,0.##} {valuation.BaseCurrency}。",
            });
        }

        return results
            .OrderByDescending(d => Math.Abs(d.Delta.Amount))
            .ToArray();
    }

    private static string BuildSummary(
        IReadOnlyList<RiskFinding> findings,
        decimal? drawdown,
        IReadOnlyList<RebalanceDrift> drift,
        PortfolioValuation valuation)
    {
        var parts = new List<string>();

        parts.Add(findings.Count == 0
            ? "集中度未触及你设定的任何上限。"
            : $"{findings.Count} 项超过你设定的集中度上限。");

        parts.Add(drawdown is null
            ? "尚无足够的估值历史来计算回撤（每次刷新会记一个点）。"
            : $"历史最大回撤 {Percent(drawdown.Value)}。");

        if (drift.Count > 0)
        {
            parts.Add($"{drift.Count} 个品种偏离你设定的目标配置。");
        }

        if (!valuation.IsComplete)
        {
            parts.Add("注意：本次估值不完整，未定价持仓未计入占比。");
        }

        parts.Add(ProductInfo.ResearchDisclaimer);
        return string.Join(" ", parts);
    }

    private static string Percent(decimal weight) =>
        (weight * 100m).ToString("0.#") + "%";
}
