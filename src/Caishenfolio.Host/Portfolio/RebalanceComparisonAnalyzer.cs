using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>One slice's move in a rebalancing plan. Sell to cut overweights, buy to fill underweights.</summary>
public sealed record RebalanceAction
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>"卖出" or "买入" — the direction this plan applies to this slice.</summary>
    public required string Verb { get; init; }
    public required Money Amount { get; init; }
    public required decimal CurrentWeight { get; init; }
    public required decimal TargetWeight { get; init; }
}

public sealed record RebalanceComparison
{
    public required bool Computable { get; init; }
    /// <summary>Why the comparison cannot run (incomplete valuation, no targets).</summary>
    public string? BlockReason { get; init; }
    /// <summary>Sell the overweight back to target. Empty when nothing is overweight.</summary>
    public required IReadOnlyList<RebalanceAction> SellPlan { get; init; }
    /// <summary>Buy underweights back to target with fresh money. Empty when nothing is underweight.</summary>
    public required IReadOnlyList<RebalanceAction> NewMoneyPlan { get; init; }
    /// <summary>Cash the new-money plan needs to fully restore the underweights.</summary>
    public required Money NewMoneyRequired { get; init; }
    public required bool NewMoneyFeasible { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required string Summary { get; init; }
}

/// <summary>
/// Compares the two classic ways to move back to the target mix the user defined: selling the
/// overweights, or directing new cash into the underweights. Arithmetic only — the app does not
/// know prices you would actually get, so every amount is a target-sized estimate, and the
/// summary says so.
///
/// A new-money plan generally cannot fully restore the mix when something is overweight: buying
/// underweights dilutes the overweight toward target but rarely onto it. The notes say that
/// plainly instead of presenting a partial plan as a full one.
/// </summary>
public static class RebalanceComparisonAnalyzer
{
    public static RebalanceComparison Compare(
        PortfolioValuation valuation,
        IReadOnlyDictionary<string, decimal>? targets,
        decimal availableNewCashBase)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        if (targets is null || targets.Count == 0)
        {
            return Blocked(valuation, "没有填写目标配置，先在设置里给出各品种的目标比例。");
        }

        if (!valuation.IsComplete)
        {
            return Blocked(valuation, "当前估值不完整：金额偏离基于不完整总额会伪造精度，请先让估值完整再比较。");
        }

        var total = valuation.TotalValue.Amount;
        if (total <= 0m)
        {
            return Blocked(valuation, "组合还没有可计算的市值。");
        }

        var slices = valuation.ByAssetClass.ToDictionary(s => s.Key, s => s, StringComparer.Ordinal);

        // ---- sell plan: total unchanged, each slice moved to target × current total ----------
        var sellPlan = new List<RebalanceAction>();
        foreach (var (key, target) in targets)
        {
            var (label, current) = SliceOf(slices, key);
            var deltaAmount = (target - current) * total;
            if (deltaAmount >= 0m)
            {
                continue;
            }

            sellPlan.Add(new RebalanceAction
            {
                Key = key,
                Label = label,
                Verb = "卖出",
                Amount = Money.Of(-deltaAmount, valuation.BaseCurrency).Round(),
                CurrentWeight = current,
                TargetWeight = target,
            });
        }

        // ---- new-money plan: fresh money changes the denominator, so the amounts come from
        // the smallest total that can hold every existing position inside its target share
        // (P1-02). With 80/20 at total 100 and targets 50/50, that total is 160: the 80 keeps
        // 50% and the 20 is topped up to 80 — not (0.5−0.2)×100 = 30, which lands at 61.5/38.5.
        var buyPlan = new List<RebalanceAction>();
        var required = 0m;
        var newTotalCandidates = new List<decimal>();
        foreach (var (key, target) in targets)
        {
            if (target <= 0m)
            {
                continue;
            }

            var (_, current) = SliceOf(slices, key);
            var currentValue = current * total;
            if (currentValue > 0m)
            {
                // The new total must be at least large enough for this slice to fit its share.
                newTotalCandidates.Add(currentValue / target);
            }
        }

        var newTotal = newTotalCandidates.Count > 0 ? newTotalCandidates.Max() : total;
        if (newTotal < total)
        {
            newTotal = total; // overweight-free fallback: targets already met, no new money needed
        }

        foreach (var (key, target) in targets)
        {
            var (label, current) = SliceOf(slices, key);
            var currentValue = current * total;
            var deltaAmount = target * newTotal - currentValue;
            if (deltaAmount <= 0m)
            {
                continue;
            }

            buyPlan.Add(new RebalanceAction
            {
                Key = key,
                Label = label,
                Verb = "买入",
                Amount = Money.Of(deltaAmount, valuation.BaseCurrency).Round(),
                CurrentWeight = current,
                TargetWeight = target,
            });
            required += deltaAmount;
        }

        var notes = new List<string>();

        var newMoneyFeasible = required <= availableNewCashBase;
        if (buyPlan.Count > 0)
        {
            notes.Add(newMoneyFeasible
                ? $"新增资金 {availableNewCashBase:#,0.##} 足以补齐低配（需要 {required:#,0.##}）。"
                : $"补齐低配需要 {required:#,0.##}，可用新增资金只有 {availableNewCashBase:#,0.##}。");
        }

        if (sellPlan.Count > 0 && buyPlan.Count > 0)
        {
            notes.Add(
                "新增资金方案按「能容纳现有资产的最小新总额」计算：超配品种回到目标、其余补足，" +
                "所需新增资金通常大于按现总额算出的差额。要完全不动超配持仓只能靠卖出方案。");
        }

        var parts = new List<string>();
        parts.Add(sellPlan.Count == 0
            ? "没有需要卖出的超配品种。"
            : $"卖出方案：{string.Join("；", sellPlan.Select(a => $"卖出{a.Label} {a.Amount.Amount:#,0.##}"))}。");
        parts.Add(buyPlan.Count == 0
            ? "没有低配品种需要补。"
            : $"新增资金方案：{string.Join("；", buyPlan.Select(a => $"买入{a.Label} {a.Amount.Amount:#,0.##}"))}，共需 {required:#,0.##}。");
        parts.Add("金额按目标比例推算，不是可成交报价；这是两种思路的比较，不是操作指令。");

        return new RebalanceComparison
        {
            Computable = true,
            SellPlan = sellPlan.OrderByDescending(a => a.Amount.Amount).ToArray(),
            NewMoneyPlan = buyPlan.OrderByDescending(a => a.Amount.Amount).ToArray(),
            NewMoneyRequired = Money.Of(required, valuation.BaseCurrency).Round(),
            NewMoneyFeasible = newMoneyFeasible,
            Notes = notes,
            Summary = string.Join(" ", parts),
        };
    }

    private static (string Label, decimal Weight) SliceOf(
        IReadOnlyDictionary<string, AllocationSlice> slices, string key)
    {
        if (slices.TryGetValue(key, out var slice))
        {
            return (slice.Label, slice.Weight);
        }

        return (AssetClasses.TryParse(key, out var asset) ? asset.ToDisplayName() : key, 0m);
    }

    private static RebalanceComparison Blocked(PortfolioValuation valuation, string reason) => new()
    {
        Computable = false,
        BlockReason = reason,
        SellPlan = [],
        NewMoneyPlan = [],
        NewMoneyRequired = Money.Zero(valuation.BaseCurrency),
        NewMoneyFeasible = false,
        Notes = [],
        Summary = reason,
    };
}
