using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>Where the money for a simulated buy comes from. Two very different questions.</summary>
public enum FundingSource
{
    /// <summary>
    /// Paid from cash already inside the ledger: the portfolio total does not change, the cash
    /// line falls. This is "shuffle money I already have into this asset".
    /// </summary>
    ExistingCash,
    /// <summary>
    /// New money arriving from outside and invested immediately: the total rises by the amount,
    /// the existing cash line is untouched. This is "add fresh money and buy".
    /// </summary>
    ExternalContribution,
}

/// <summary>What one simulated trade would do to the portfolio's weights.</summary>
public sealed record WhatIfResult
{
    public required string Symbol { get; init; }
    public required Money Amount { get; init; }
    public required FundingSource Funding { get; init; }
    /// <summary>False when the simulation cannot be computed honestly (unpriced, missing currency, incomplete valuation).</summary>
    public required bool Feasible { get; init; }
    /// <summary>Why the simulation stopped. Null when feasible.</summary>
    public string? BlockReason { get; init; }
    public required decimal CurrentSingleWeight { get; init; }
    public required decimal NewSingleWeight { get; init; }
    public required decimal CurrentCurrencyWeight { get; init; }
    public required decimal NewCurrencyWeight { get; init; }
    public required decimal NewCashWeight { get; init; }
    /// <summary>Thresholds the simulated portfolio would cross. Empty when nothing is exceeded.</summary>
    public required IReadOnlyList<string> Breaches { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required string Summary { get; init; }
}

/// <summary>
/// Answers "if I put this much into this symbol, what happens to my weights" — before anything
/// is recorded. The funding source is explicit because the two readings differ in every number:
/// existing cash keeps the total as denominator, external money grows it. Mixing them
/// understates concentration — the exact mistake this analyzer exists to prevent.
///
/// It never writes the ledger, never fetches prices, and never rounds reality in the user's
/// favour: an unpriced holding, an unknown currency, or an incomplete valuation stops the
/// simulation with a stated reason instead of a guess or a green "all clear".
/// </summary>
public static class TradeWhatIfAnalyzer
{
    public static WhatIfResult Simulate(
        PortfolioValuation valuation,
        RiskThresholds thresholds,
        string symbol,
        decimal amountBase,
        string? currency = null,
        FundingSource funding = FundingSource.ExistingCash)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (amountBase <= 0m)
        {
            throw new LedgerException($"模拟金额必须大于 0（收到 {amountBase}）。");
        }

        var baseCcy = valuation.BaseCurrency;
        var amount = Money.Of(amountBase, baseCcy);
        var normalized = SymbolId.TryParse(symbol, out var parsed)
            ? parsed.Normalized().Value
            : symbol.Trim();

        // Fail-closed: with part of the portfolio unpriced, the weights this simulation would
        // report stand on a partial denominator — a green "nothing breached" would be fabricated.
        if (!valuation.IsComplete)
        {
            return Blocked(valuation, normalized, amount, funding,
                $"当前估值不完整（{valuation.Warnings.Count} 项未定价或缺汇率），模拟无法给出可靠结论；请先让估值完整。");
        }

        var existing = valuation.Positions.FirstOrDefault(p =>
            p.Position.IsOpen && string.Equals(p.Position.Symbol, normalized, StringComparison.OrdinalIgnoreCase));

        string currencyExposure;
        if (existing is not null)
        {
            currencyExposure = existing.Position.Currency;
        }
        else if (!string.IsNullOrWhiteSpace(currency))
        {
            currencyExposure = Currencies.Normalize(currency!);
        }
        else
        {
            return Blocked(valuation, normalized, amount, funding,
                "账本里没有这个标的，模拟需要知道它的计价货币（如 CNY / USD / JPY）。");
        }

        if (existing is { Priced: false })
        {
            return Blocked(valuation, normalized, amount, funding,
                "该持仓当前未定价，现有市值未知，模拟无法计算（不会按 0 估计）。");
        }

        var totalNow = valuation.TotalValue.Amount;

        // The funding source decides the denominator — the whole point of P1-01.
        var totalAfter = funding == FundingSource.ExistingCash ? totalNow : totalNow + amountBase;
        var cashAfter = funding == FundingSource.ExistingCash
            ? valuation.CashValue.Amount - amountBase
            : valuation.CashValue.Amount;

        var currentSingle = existing?.Weight ?? 0m;
        var newSingleAmount = (existing?.MarketValueBase?.Amount ?? 0m) + amountBase;

        // Currency exposure tracks the two sides of the trade separately (P1-R01):
        // the target currency gains the asset's value, the funding side gives it up.
        // Mixing the two into one slice arithmetic cancelled cross-currency buys entirely.
        var currencyNow = CurrencySliceAmount(valuation, currencyExposure);
        var isSameCurrency = string.Equals(currencyExposure, baseCcy, StringComparison.OrdinalIgnoreCase);
        var currencyAfter = currencyNow + amountBase;
        if (funding == FundingSource.ExistingCash && isSameCurrency)
        {
            // Cash and asset live in the same slice: +amount asset − amount cash nets to zero.
            currencyAfter = currencyNow;
        }

        var breaches = new List<string>();
        if (totalAfter > 0m && newSingleAmount / totalAfter > thresholds.SinglePosition)
        {
            breaches.Add($"单一持仓 {Percent(newSingleAmount / totalAfter)} 将超过你设定的上限 {Percent(thresholds.SinglePosition)}。");
        }

        if (totalAfter > 0m && currencyAfter / totalAfter > thresholds.Currency)
        {
            breaches.Add($"货币「{currencyExposure}」占比 {Percent(currencyAfter / totalAfter)} 将超过你设定的上限 {Percent(thresholds.Currency)}。");
        }

        // The cash check is base-currency-specific for existing-cash funding (P1-R01): paying
        // from foreign-currency cash requires converting it first, with costs this simulation
        // does not model — saying "fine, total cash covers it" would hide that.
        var baseCash = CashInCurrency(valuation, baseCcy);
        if (funding == FundingSource.ExistingCash && amountBase > baseCash)
        {
            breaches.Add(
                $"本位币现金只有 {baseCash:#,0.##} {baseCcy}，不够支付 {amountBase:#,0.##}；" +
                "外币现金需先换汇（有额外成本，本模拟未计入）。模拟不改变真实账本。");
        }

        var notes = new List<string>
        {
            funding == FundingSource.ExistingCash
                ? "资金来源：账本现有现金——总资产不变，现金减少。"
                : "资金来源：外部新增资金——总资产增加，现有现金不变。",
        };
        if (existing is null)
        {
            notes.Add($"「{normalized}」目前不在账本里，按新开仓模拟（计价货币 {currencyExposure}）。");
        }

        var summary = string.Join(" ",
        [
            $"模拟：{(funding == FundingSource.ExistingCash ? "用账本现金" : "用外部新增资金")}",
            $"{Format(amountBase)} {baseCcy} 买入 {normalized}。",
            $"单一持仓 {Percent(currentSingle)} → {Percent(totalAfter > 0m ? newSingleAmount / totalAfter : 0m)}；"
            + $"货币「{currencyExposure}」{Percent(totalNow > 0m ? currencyNow / totalNow : 0m)} → {Percent(totalAfter > 0m ? currencyAfter / totalAfter : 0m)}。",
            breaches.Count > 0
                ? $"{breaches.Count} 项将超过你设定的上限。"
                : "未超过你设定的任何上限。",
            "这只是数字推演，不构成建议，也不会写入账本。",
        ]);

        return new WhatIfResult
        {
            Symbol = normalized,
            Amount = amount,
            Funding = funding,
            Feasible = true,
            CurrentSingleWeight = currentSingle,
            NewSingleWeight = totalAfter > 0m ? newSingleAmount / totalAfter : 0m,
            CurrentCurrencyWeight = totalNow > 0m ? currencyNow / totalNow : 0m,
            NewCurrencyWeight = totalAfter > 0m ? currencyAfter / totalAfter : 0m,
            NewCashWeight = totalAfter > 0m ? cashAfter / totalAfter : 0m,
            Breaches = breaches,
            Notes = notes,
            Summary = summary,
        };
    }

    private static WhatIfResult Blocked(
        PortfolioValuation valuation, string symbol, Money amount, FundingSource funding, string reason) =>
        new()
        {
            Symbol = symbol,
            Amount = amount,
            Funding = funding,
            Feasible = false,
            BlockReason = reason,
            CurrentSingleWeight = 0m,
            NewSingleWeight = 0m,
            CurrentCurrencyWeight = 0m,
            NewCurrencyWeight = 0m,
            NewCashWeight = 0m,
            Breaches = [],
            Notes = [],
            Summary = "无法模拟：" + reason,
        };

    private static decimal CurrencySliceAmount(PortfolioValuation valuation, string currency) =>
        valuation.ByCurrency.FirstOrDefault(s => string.Equals(s.Key, currency, StringComparison.OrdinalIgnoreCase))?.Value.Amount ?? 0m;

    /// <summary>Cash only (no holdings) in one currency, derived from the currency slice.</summary>
    private static decimal CashInCurrency(PortfolioValuation valuation, string currency) =>
        CurrencySliceAmount(valuation, currency)
        - valuation.Positions
            .Where(p => p.Position.IsOpen && p.Priced
                        && string.Equals(p.Position.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.MarketValueBase?.Amount ?? 0m);

    private static string Percent(decimal weight) =>
        (weight * 100m).ToString("0.#") + "%";

    private static string Format(decimal value) =>
        value.ToString("#,0.##");
}
