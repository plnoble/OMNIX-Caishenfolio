using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>One holding's share of the portfolio's profit, split into price and currency parts.</summary>
public sealed record ContributionRow
{
    public required string Symbol { get; init; }
    /// <summary>
    /// Unrealized PnL as the overview reports it — cost converted at today's rate. By construction
    /// this equals the price-move part only; the currency part lives in <see cref="FxEffectBase"/>.
    /// </summary>
    public Money? UnrealizedBase { get; init; }
    /// <summary>Share of the priced portfolio's total unrealized PnL; null when the total is zero or unknown.</summary>
    public decimal? ShareOfUnrealized { get; init; }
    /// <summary>The part that comes from the price moving, valued at today's rate. Equals <see cref="UnrealizedBase"/> when known.</summary>
    public Money? LocalEffectBase { get; init; }
    /// <summary>cost(原币) × (今日汇率 − 成本日汇率) — the currency's contribution, hidden inside the overview's converted cost.</summary>
    public Money? FxEffectBase { get; init; }
    /// <summary>PnL against the money actually invested: 涨跌部分 + 汇率部分. Null when the cost-date rate is unknown.</summary>
    public Money? TrueUnrealizedBase { get; init; }
    public decimal? RateNow { get; init; }
    public decimal? RateAtCost { get; init; }
    /// <summary>Why the split is unknown. Null when both effects were computed.</summary>
    public string? UnknownReason { get; init; }
}

public sealed record ContributionReport
{
    public required IReadOnlyList<ContributionRow> Rows { get; init; }
    public required Money TotalUnrealizedBase { get; init; }
    /// <summary>Everything that could not be attributed, stated instead of guessed away.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public const string Disclaimer = "贡献拆分基于当前估值与账本记录，是事实陈述，不是投资建议。";
}

/// <summary>
/// Explains where the profit sits: per holding, and for foreign-currency holdings how much of the
/// gain is the asset itself versus the exchange rate.
///
/// The overview's unrealized PnL converts cost at today's rate, which by construction measures
/// only the price move. The currency's contribution — cost(原币) × (r_now − r_cost) — is what
/// that conversion hides. The split is exact, never estimated: with r_now = 市值(本位币)/市值(原币)
/// and the ledger's cost-date rate r_cost,
/// <c>相对实际投入的盈亏 = (市值−成本)×r_now + 成本×(r_now−r_cost)</c> holds by construction.
/// When the stored rate history has no observation on or before the cost date, the split is
/// reported as unknown — never imputed.
/// </summary>
public static class PortfolioContributionAnalyzer
{
    public static ContributionReport Analyze(
        PortfolioValuation valuation,
        IReadOnlyList<LedgerTransaction> transactions,
        IReadOnlyList<FxRate> fxRates,
        string baseCurrency)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(fxRates);

        var baseCcy = Currencies.Normalize(baseCurrency);
        var warnings = new List<string>();
        var rows = new List<ContributionRow>();

        var total = valuation.UnrealizedPnl;
        var costDates = CostDates(transactions);

        foreach (var item in valuation.Positions.Where(p => p.Position.IsOpen))
        {
            var symbol = item.Position.Symbol;

            if (!item.Priced || item.MarketValueBase is null || item.MarketValue is null)
            {
                rows.Add(new ContributionRow
                {
                    Symbol = symbol,
                    UnknownReason = "未定价，无法计算贡献（不会按 0 估计）。",
                });
                warnings.Add($"{symbol} 未定价，未参与贡献拆分。");
                continue;
            }

            var unrealized = item.UnrealizedPnlBase ?? Money.Zero(baseCcy);
            var mvLocal = item.MarketValue.Value.Amount;
            var mvBase = item.MarketValueBase.Value.Amount;

            // Blended rate actually used by this valuation. For a CNY-based ledger holding CNY
            // assets this is exactly 1, and the split degenerates to the whole PnL being local.
            decimal? rateNow = mvLocal != 0m ? mvBase / mvLocal : null;
            var costLocal = item.Position.CostBasis.Amount;
            var costCurrency = item.Position.Currency;

            if (rateNow is null)
            {
                rows.Add(new ContributionRow
                {
                    Symbol = symbol,
                    UnrealizedBase = unrealized,
                    UnknownReason = "原币市值为 0，无法定义汇率拆分。",
                });
                continue;
            }

            var costDate = costDates.TryGetValue((item.Position.AccountId, symbol), out var date)
                ? date
                : (DateOnly?)null;
            if (costDate is null)
            {
                rows.Add(new ContributionRow
                {
                    Symbol = symbol,
                    UnrealizedBase = unrealized,
                    ShareOfUnrealized = Share(unrealized, total),
                    RateNow = rateNow,
                    UnknownReason = "账本里找不到建仓流水，无法确定成本日汇率。",
                });
                continue;
            }

            // Rates observed up to the cost date only: a later rate must not reach back.
            var history = fxRates.Where(r => r.AsOf <= costDate.Value).ToArray();
            var converter = new FxConverter(history);
            if (!converter.TryGetRate(costCurrency, baseCcy, out var rateAtCost))
            {
                rows.Add(new ContributionRow
                {
                    Symbol = symbol,
                    UnrealizedBase = unrealized,
                    ShareOfUnrealized = Share(unrealized, total),
                    RateNow = rateNow,
                    UnknownReason = $"缺少 {costDate:yyyy-MM-dd} 当日或之前的 {costCurrency} → {baseCcy} 汇率快照，无法拆分汇率贡献。",
                });
                warnings.Add($"{symbol} 汇率贡献未知：缺成本日（{costDate:yyyy-MM-dd}）汇率。");
                continue;
            }

            var fxEffect = Money.Of(costLocal * (rateNow.Value - rateAtCost), baseCcy);
            var localEffect = unrealized;
            rows.Add(new ContributionRow
            {
                Symbol = symbol,
                UnrealizedBase = unrealized,
                ShareOfUnrealized = Share(unrealized, total),
                LocalEffectBase = localEffect,
                FxEffectBase = fxEffect,
                TrueUnrealizedBase = localEffect + fxEffect,
                RateNow = rateNow,
                RateAtCost = rateAtCost,
            });
        }

        return new ContributionReport
        {
            Rows = rows
                .OrderByDescending(r => r.UnrealizedBase?.Amount ?? decimal.MinValue)
                .ThenBy(r => r.Symbol, StringComparer.Ordinal)
                .ToArray(),
            TotalUnrealizedBase = total,
            Warnings = warnings,
        };
    }

    private static decimal? Share(Money part, Money whole) =>
        whole.IsZero ? null : part.Amount / whole.Amount;

    /// <summary>Earliest build-up date per (account, symbol) — the cost date for the FX split.</summary>
    private static IReadOnlyDictionary<(string Account, string Symbol), DateOnly> CostDates(
        IReadOnlyList<LedgerTransaction> transactions)
    {
        var dates = new Dictionary<(string, string), DateOnly>();
        foreach (var txn in transactions)
        {
            if (txn.IsReversal
                || txn.Kind is not (TransactionKind.Buy or TransactionKind.OpeningPosition)
                || string.IsNullOrEmpty(txn.Symbol))
            {
                continue;
            }

            var key = (txn.AccountId, txn.Symbol);
            if (!dates.TryGetValue(key, out var current) || txn.TradeDate < current)
            {
                dates[key] = txn.TradeDate;
            }
        }

        return dates;
    }
}
