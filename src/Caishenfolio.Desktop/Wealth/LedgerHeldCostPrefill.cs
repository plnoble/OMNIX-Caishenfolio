using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>What the FX page should prefill for the held-currency pot (recheck P1-04).</summary>
public readonly record struct HeldCostPrefill(
    string AmountText,
    string CostText,
    string StatusText)
{
    /// <summary>Cost stays BLANK when no honest basis exists — never a partial or guessed number.</summary>
    public bool HasCost => CostText.Length > 0;
}

/// <summary>
/// The prefill decision, extracted from the click handler so it is testable without a
/// window: amount from the live balance, cost from the MOVING-WEIGHTED pool (the same
/// calculator the background alerts use), and the three-way honesty rule — full cost,
/// no cost basis at all, or part of the pot with unknown origin (blank + explanation).
/// </summary>
public static class LedgerHeldCostPrefill
{
    public static HeldCostPrefill Compute(
        IReadOnlyList<LedgerTransaction> transactions,
        decimal heldTotal,
        string baseCurrency,
        string heldCurrency)
    {
        var cost = HeldCurrencyCostCalculator.CostOf(transactions, baseCurrency, heldCurrency, heldTotal);
        var totalText = heldTotal.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        if (cost.BlendedCostInBase is { } blended)
        {
            return new HeldCostPrefill(
                totalText,
                blended.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                $"已带出：{heldCurrency} 现金 {totalText}；按账本移动加权成本（多批次混合、消耗按均价释放）" +
                $"折算成本 {blended:0.##} {baseCurrency}。");
        }

        if (cost.PricedAmount == 0m && cost.UnpricedAmount == 0m)
        {
            return new HeldCostPrefill(
                totalText,
                "",
                $"已带出 {heldCurrency} 现金 {totalText}；账本里没有「{baseCurrency}→{heldCurrency}」的换汇记录，请按当时回单手填实付金额。");
        }

        return new HeldCostPrefill(
            totalText,
            "",
            $"已带出 {heldCurrency} 现金 {totalText}；其中 {cost.PricedAmount:0.##} {heldCurrency} 的成本是 " +
            $"{cost.PricedCostInBase:0.##} {baseCurrency}，另有 {cost.UnpricedAmount:0.##} {heldCurrency} 来源不明（无成本依据）。" +
            "整体成本未知——按回单手填，或先把来源不明的部分记清楚。");
    }
}
