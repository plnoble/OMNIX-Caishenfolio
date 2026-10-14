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

    /// <summary>How much of the portfolio the totals actually cover.</summary>
    public enum CoverageState
    {
        /// <summary>Every open holding priced and every currency converted.</summary>
        Complete,
        /// <summary>Part of the market value is missing — ratios stand on a partial denominator.</summary>
        Partial,
        /// <summary>No priced value at all — every ratio is unknown.</summary>
        InsufficientData,
    }

    /// <summary>
    /// What an incomplete valuation means for the report. Risk rules must surface this instead of
    /// a green "all clear": a portfolio with a large unpriced holding can look perfectly diversified
    /// on the part that happened to price.
    /// </summary>
    public sealed record RiskCoverage
    {
        public required CoverageState State { get; init; }
        /// <summary>Open holdings without a usable price or rate.</summary>
        public required IReadOnlyList<string> UnpricedPositions { get; init; }
        /// <summary>Cash lines a missing rate excluded from the totals.</summary>
        public int MissingRateCashLines { get; init; }
        /// <summary>
        /// Priced market value over the cost of everything held — by cost, because the missing
        /// market value is exactly what is unknown. A rough gauge, never a priced-share exact figure.
        /// </summary>
        public decimal PricedFractionByCost { get; init; }

        public bool IsUnknown => State == CoverageState.InsufficientData;
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
    /// <summary>What part of the portfolio the weights above actually cover.</summary>
    public required RiskCoverage Coverage { get; init; }
    public required string Summary { get; init; }

    public bool HasWarnings => Findings.Any(f => f.Level == RiskLevel.Warning);

    /// <summary>Report for an empty ledger: nothing to value, so nothing claimed.</summary>
    public static PortfolioRiskReport Empty() => new()
    {
        Findings = [],
        Drift = [],
        Coverage = new RiskCoverage
        {
            State = CoverageState.InsufficientData,
            UnpricedPositions = [],
        },
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
        IReadOnlyDictionary<string, decimal>? targetAssetAllocation = null,
        decimal emergencyFundBase = 0m)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        var limits = thresholds ?? RiskThresholds.Default;
        var findings = new List<RiskFinding>();

        var coverage = CoverageOf(valuation);

        // With nothing priced there is no denominator at all: a green "no findings" here would be
        // a fabricated all-clear. The only honest answer is that every ratio is unknown.
        if (coverage.IsUnknown)
        {
            return new PortfolioRiskReport
            {
                Findings =
                [
                    new RiskFinding
                    {
                        Dimension = "数据",
                        Label = "估值",
                        Weight = 0m,
                        Threshold = 0m,
                        Level = RiskLevel.Warning,
                        Message = "没有可用的市值数据（全部持仓未定价或缺汇率），集中度、现金占比与目标配置偏离均为未知，" +
                                  "本次不输出任何“正常”结论。",
                    },
                ],
                Drift = [],
                Coverage = coverage,
                Summary = BuildUnknownSummary(coverage, valuation),
            };
        }

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

        // Cash is evaluated once, under 品种. It also appears in the region slices, and reporting
        // the same idle balance under two headings is noise rather than information.
        AddSliceFindings(findings, "品种", valuation.ByAssetClass, limits.AssetClass, limits.Cash, skipCash: true);
        AddCashFinding(findings, valuation, limits.Cash, emergencyFundBase);
        AddSliceFindings(findings, "市场", valuation.ByRegion, limits.Region, null, skipCash: true);
        AddSliceFindings(findings, "货币", valuation.ByCurrency, limits.Currency, null);

        var drawdown = MaxDrawdown(equityCurve);
        var drift = coverage.State == CoverageState.Partial
            ? [] // Drift deltas are in money against a partial total; reporting them would invent precision.
            : BuildDrift(valuation, targetAssetAllocation);

        return new PortfolioRiskReport
        {
            Findings = findings
                .OrderByDescending(f => f.Weight)
                .ToArray(),
            Drift = drift,
            Coverage = coverage,
            MaxDrawdown = drawdown?.Depth,
            DrawdownPeak = drawdown?.Peak,
            DrawdownTrough = drawdown?.Trough,
            Summary = BuildSummary(findings, drawdown?.Depth, drift, coverage, valuation, targetAssetAllocation),
        };
    }

    /// <summary>What the ratios can and cannot see, straight from the valuation itself.</summary>
    private static RiskCoverage CoverageOf(PortfolioValuation valuation)
    {
        var open = valuation.Positions.Where(p => p.Position.IsOpen).ToArray();
        var unpriced = open
            .Where(p => !p.Priced)
            .Select(p => p.Position.Symbol)
            .ToArray();

        // Cash lines a missing rate dropped are named in the valuation warnings, not modelled
        // separately; count them so the report can say how many, not just that "something" is off.
        var missingRateCash = valuation.Warnings.Count(w => w.Contains("现金缺少汇率"));

        var pricedValue = valuation.TotalValue.Amount;
        // Cost for positions whose own currency would not convert (e.g. a JPY cost basis with no
        // JPY→CNY rate) is itself unknown — count zero rather than guessing, which understates the
        // unknown share instead of overstating it.
        var unpricedCost = unpriced.Length > 0
            ? open.Where(p => !p.Priced).Sum(p => p.CostKnownByCost(valuation) ? p.CostBasisBase.Amount : 0m)
            : 0m;
        var costBase = pricedValue > 0m || unpricedCost > 0m ? pricedValue + unpricedCost : 0m;

        var state = unpriced.Length > 0 || missingRateCash > 0
            ? pricedValue > 0m ? CoverageState.Partial : CoverageState.InsufficientData
            : CoverageState.Complete;

        return new RiskCoverage
        {
            State = state,
            UnpricedPositions = unpriced,
            MissingRateCashLines = missingRateCash,
            PricedFractionByCost = costBase > 0m ? pricedValue / costBase : 0m,
        };
    }

    /// <summary>
    /// Whether this row carries a usable cost figure in base currency. PositionValuation signals
    /// pricing by market value; cost presence has to be inferred from the converter warnings.
    /// </summary>
    private static bool CostKnownByCost(
        this PositionValuation position, PortfolioValuation valuation) =>
        !valuation.Warnings.Any(w =>
            w.Contains(position.Position.Symbol) && w.Contains("缺少") && w.Contains("汇率"));

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

    /// <summary>
    /// The idle-cash check, with the emergency fund carved out first. Money the user has already
    /// declared as reserved is not idle, and flagging it as such would train them to ignore the
    /// warning — so it is excluded and named instead.
    /// </summary>
    private static void AddCashFinding(
        List<RiskFinding> findings,
        PortfolioValuation valuation,
        decimal limit,
        decimal emergencyFundBase)
    {
        var cash = valuation.CashValue.Amount;
        var total = valuation.TotalValue.Amount;
        if (cash <= 0m || total <= 0m)
        {
            return;
        }

        var reserved = Math.Min(emergencyFundBase, cash);
        var idle = cash - reserved;
        var idleWeight = idle / total;
        if (idleWeight <= limit)
        {
            return;
        }

        var reserveNote = reserved > 0m
            ? $"（其中 {reserved:#,0.##} 已标记为应急金，不计入闲置）"
            : "";
        findings.Add(new RiskFinding
        {
            Dimension = "品种",
            Label = "现金",
            Weight = idleWeight,
            Threshold = limit,
            Level = RiskLevel.Warning,
            Message = $"闲置现金占 {Percent(idleWeight)}，超过你设定的 {Percent(limit)}{reserveNote}。",
        });
    }

    private static void AddSliceFindings(
        List<RiskFinding> findings,
        string dimension,
        IReadOnlyList<AllocationSlice> slices,
        decimal threshold,
        decimal? cashThreshold,
        bool skipCash = false)
    {
        foreach (var slice in slices)
        {
            var isCash = slice.Key == "cash";
            if (isCash && skipCash)
            {
                continue;
            }

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
        RiskCoverage coverage,
        PortfolioValuation valuation,
        IReadOnlyDictionary<string, decimal>? targets)
    {
        var parts = new List<string>();

        if (coverage.State == CoverageState.Partial)
        {
            // This leads on purpose: every number below it divides by a partial total. The share
            // is by cost — the missing market value is exactly what is unknown.
            parts.Add($"估值不完整：以下集中度结论仅基于已定价部分（按成本口径约 {Percent(coverage.PricedFractionByCost)}），" +
                      $"{coverage.UnpricedPositions.Count} 个持仓未定价" +
                      (coverage.MissingRateCashLines > 0
                          ? $"、{coverage.MissingRateCashLines} 笔现金缺汇率"
                          : "") +
                      "，未计入合计（不会按 0 计算）。");
        }

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
        else if (coverage.State == CoverageState.Partial && targets is { Count: > 0 })
        {
            // Deltas in money against a partial total would invent precision; say so instead.
            parts.Add("目标配置偏离基于的总额不完整，本次未计算。");
        }

        parts.Add(ProductInfo.ResearchDisclaimer);
        return string.Join(" ", parts);
    }

    private static string BuildUnknownSummary(RiskCoverage coverage, PortfolioValuation valuation)
    {
        var unpriced = coverage.UnpricedPositions.Count > 0
            ? $"未定价持仓：{string.Join("、", coverage.UnpricedPositions)}。"
            : "";
        var cash = coverage.MissingRateCashLines > 0
            ? $"{coverage.MissingRateCashLines} 笔现金缺少汇率，未计入总额。"
            : "";
        return string.Join(" ",
        [
            "估值不完整：缺少可用的市值或汇率，集中度、现金占比与目标配置偏离均为未知，不输出“正常”结论。",
            unpriced,
            cash,
            ProductInfo.ResearchDisclaimer,
        ]).Trim();
    }

    private static string Percent(decimal weight) =>
        (weight * 100m).ToString("0.#") + "%";
}
