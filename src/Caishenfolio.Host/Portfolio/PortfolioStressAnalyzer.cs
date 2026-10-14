using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// A what-if market move the user dials in themselves. Percentages are entered as 0–100 and
/// converted to factors internally; a negative move means the currency loses value against the
/// base currency. Every field is a user assumption, never a forecast.
/// </summary>
public sealed record StressScenario
{
    /// <summary>Stock-market fall applied to equity and ETF holdings; 0–100.</summary>
    public decimal EquityDropPercent { get; init; }
    /// <summary>USD move against the base currency; −100..100.</summary>
    public decimal UsdMovePercent { get; init; }
    /// <summary>JPY move against the base currency; −100..100.</summary>
    public decimal JpyMovePercent { get; init; }
    /// <summary>Extra fall applied to the single largest holding on top of the equity shock; 0–100.</summary>
    public decimal LargestHoldingDropPercent { get; init; }
    /// <summary>
    /// Move the user assumes for bond-like holdings (bonds, convertibles, mutual funds). This is
    /// a direct user assumption, not a duration model — the plan gates duration/credit modelling
    /// on data quality the ledger does not have. −100..100.
    /// </summary>
    public decimal FixedIncomeMovePercent { get; init; }

    public bool IsNoOp =>
        EquityDropPercent == 0m && UsdMovePercent == 0m
        && JpyMovePercent == 0m && LargestHoldingDropPercent == 0m
        && FixedIncomeMovePercent == 0m;
}

public sealed record StressResult
{
    public required Money BaselineTotal { get; init; }
    public required Money StressedTotal { get; init; }
    /// <summary>Stressed versus baseline, as a signed fraction.</summary>
    public required decimal ChangePercent { get; init; }
    /// <summary>Every shock actually applied, in words — the reproducible input set.</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }
    /// <summary>What the scenario could not see (unpriced holdings, currency-rate gaps).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public const string Disclaimer = "假设分析：结果由你填入的情景推出，不是对未来的预测，也不构成建议。";
}

/// <summary>
/// Re-prices the current valuation under user-chosen shocks. Everything is arithmetic on prices
/// the app already has: equity/ETF holdings take the equity fall, the largest holding takes an
/// extra fall, USD/JPY positions and cash move with the currency shocks. Unpriced holdings are
/// excluded and counted — a scenario that silently valued them at zero would be a fabrication.
///
/// Bonds, convertible bonds and mutual funds are deliberately left unshocked in this first
/// batch: their rate and credit behaviour is not a linear equity move, and guessing a beta would
/// be exactly the kind of invented number this app refuses to produce.
/// </summary>
public static class PortfolioStressAnalyzer
{
    public static StressResult Run(
        PortfolioValuation valuation,
        IReadOnlyList<CashBalance> cashBalances,
        StressScenario scenario,
        IReadOnlyDictionary<string, Instrument>? instruments = null)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        ArgumentNullException.ThrowIfNull(cashBalances);

        var baseCcy = valuation.BaseCurrency;
        var warnings = new List<string>();
        var assumptions = new List<string>();

        var equityFactor = 1m - scenario.EquityDropPercent / 100m;
        var usdFactor = 1m + scenario.UsdMovePercent / 100m;
        var jpyFactor = 1m + scenario.JpyMovePercent / 100m;

        if (scenario.EquityDropPercent > 0m)
        {
            assumptions.Add(
                scenario.FixedIncomeMovePercent == 0m
                    ? $"股票与 ETF 下跌 {scenario.EquityDropPercent:0.#}%（债券、可转债、场外基金不套用此跌幅）。"
                    : $"股票与 ETF 下跌 {scenario.EquityDropPercent:0.#}%。");
        }

        if (scenario.FixedIncomeMovePercent != 0m)
        {
            assumptions.Add(
                $"债券/可转债/场外基金按你的假设变动 {scenario.FixedIncomeMovePercent:+0.#;-0.#}%"
                + "（直接假设，未建模久期）。");
        }

        if (scenario.UsdMovePercent != 0m)
        {
            assumptions.Add($"USD 兑 {baseCcy} 变动 {scenario.UsdMovePercent:+0.#;-0.#}%（含美元持仓与美元现金）。");
        }

        if (scenario.JpyMovePercent != 0m)
        {
            assumptions.Add($"JPY 兑 {baseCcy} 变动 {scenario.JpyMovePercent:+0.#;-0.#}%（含日元持仓与日元现金）。");
        }

        var largest = valuation.Positions
            .Where(p => p.Position.IsOpen && p.Priced && p.MarketValueBase is not null)
            .OrderByDescending(p => p.MarketValueBase!.Value.Amount)
            .FirstOrDefault();
        var largestSymbol = largest?.Position.Symbol ?? "";
        if (scenario.LargestHoldingDropPercent > 0m && largest is not null)
        {
            assumptions.Add($"最大单一持仓 {largestSymbol} 额外下跌 {scenario.LargestHoldingDropPercent:0.#}%。");
        }

        var unpriced = 0;
        decimal stressed = 0m;

        foreach (var item in valuation.Positions.Where(p => p.Position.IsOpen))
        {
            if (!item.Priced || item.MarketValueBase is null)
            {
                unpriced++;
                continue;
            }

            var factor = 1m;
            var assetClass = ClassOf(item.Position.Symbol, instruments);
            if (assetClass is AssetClass.Equity or AssetClass.Etf)
            {
                factor *= equityFactor;
            }
            else if (assetClass is AssetClass.Bond or AssetClass.ConvertibleBond or AssetClass.MutualFund)
            {
                factor *= 1m + scenario.FixedIncomeMovePercent / 100m;
            }

            if (largest is not null
                && string.Equals(item.Position.Symbol, largestSymbol, StringComparison.OrdinalIgnoreCase))
            {
                factor *= 1m - scenario.LargestHoldingDropPercent / 100m;
            }

            factor *= CurrencyFactor(item.Position.Currency, usdFactor, jpyFactor);
            stressed += item.MarketValueBase.Value.Amount * factor;
        }

        // Aggregate by currency FIRST (P1-08): per-balance iteration with a per-currency
        // slice lookup counted the same slice once per account holding that currency.
        foreach (var group in cashBalances
                     .Where(b => b.Amount != 0m)
                     .GroupBy(b => Currencies.Normalize(b.Currency), StringComparer.Ordinal))
        {
            var currency = group.Key;
            var cashBase = CashBaseIn(valuation, currency);
            if (cashBase is null)
            {
                warnings.Add($"{currency} 现金未出现在当前估值的货币切片中，未参与压力测试。");
                continue;
            }

            stressed += cashBase.Value * CurrencyFactor(currency, usdFactor, jpyFactor);
        }

        if (unpriced > 0)
        {
            warnings.Add($"{unpriced} 个未定价持仓未参与压力测试（市值未知，不按 0 估计）。");
        }

        if (scenario.IsNoOp)
        {
            warnings.Add("所有情景参数都是 0，结果等于当前估值。");
        }

        var baseline = valuation.TotalValue.Amount;
        var change = baseline != 0m ? (stressed - baseline) / baseline : 0m;

        assumptions.Add(assumptions.Count == 0
            ? "（未应用任何冲击。）"
            : $"基准：当前估值 {valuation.TotalValue.Amount:#,0.##} {baseCcy}（不含未定价项）。");

        return new StressResult
        {
            BaselineTotal = valuation.TotalValue,
            StressedTotal = Money.Of(stressed, baseCcy),
            ChangePercent = change,
            Assumptions = assumptions,
            Warnings = warnings,
        };
    }

    private static decimal CurrencyFactor(string currency, decimal usdFactor, decimal jpyFactor) =>
        string.Equals(currency, Currencies.Usd, StringComparison.OrdinalIgnoreCase) ? usdFactor
        : string.Equals(currency, Currencies.Jpy, StringComparison.OrdinalIgnoreCase) ? jpyFactor
        : 1m;

    /// <summary>
    /// The base-currency value of ALL cash in one currency, recovered from the valuation's own
    /// numbers: currency slice value − holdings value in that currency. Exact by construction,
    /// because the slice was built from exactly those two parts at the same rates. The loop
    /// over balances must therefore touch each currency exactly once.
    /// </summary>
    private static decimal? CashBaseIn(PortfolioValuation valuation, string currency)
    {
        var slice = valuation.ByCurrency.FirstOrDefault(s =>
            string.Equals(s.Key, currency, StringComparison.OrdinalIgnoreCase));
        if (slice is null)
        {
            return null;
        }

        var holdingsInCurrency = valuation.Positions
            .Where(p => p.Position.IsOpen && p.Priced
                        && string.Equals(p.Position.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.MarketValueBase?.Amount ?? 0m);

        return slice.Value.Amount - holdingsInCurrency;
    }

    private static AssetClass ClassOf(string symbol, IReadOnlyDictionary<string, Instrument>? instruments)
    {
        if (instruments is not null && instruments.TryGetValue(symbol, out var instrument))
        {
            return instrument.AssetClass;
        }

        return SymbolId.TryParse(symbol, out var parsed) && ExchangeRegistry.TryGet(parsed.Exchange, out var venue)
            ? venue.DefaultAssetClass
            : AssetClass.Equity;
    }
}
