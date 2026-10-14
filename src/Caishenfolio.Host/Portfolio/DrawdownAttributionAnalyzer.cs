using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>One holding's (or the cash line's) part of the worst drawdown.</summary>
public sealed record DrawdownAttributionRow
{
    public required string Label { get; init; }
    /// <summary>Base-currency change from the peak to the trough. Negative = dragged the portfolio down.</summary>
    public required Money ChangeBase { get; init; }
    /// <summary>The price-move part of that change, valued at trough-date rates.</summary>
    public Money? LocalPart { get; init; }
    /// <summary>The currency part: peak local value × (trough rate − peak rate).</summary>
    public Money? FxPart { get; init; }
    /// <summary>Change over the total change, signed. Null when the total change is zero.</summary>
    public decimal? ShareOfChange { get; init; }
    public string? Note { get; init; }
}

public sealed record DrawdownAttributionReport
{
    public DateOnly? Peak { get; init; }
    public DateOnly? Trough { get; init; }
    /// <summary>Peak-to-trough depth as a positive fraction; null when the curve has no drawdown.</summary>
    public decimal? Depth { get; init; }
    public required IReadOnlyList<DrawdownAttributionRow> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required string Summary { get; init; }
}

/// <summary>
/// Explains the worst drawdown in the stored curve: which holdings fell, how much of each fall
/// was the asset versus its currency, and how much the cash line moved (the residual — it
/// absorbs flows in and out during the window).
///
/// Exactness rules: an identity links the three numbers, so a row either has both parts or
/// neither (missing cost-window rate, or an endpoint that was unpriced). Positions absent from
/// one endpoint count as zero there — they opened or closed inside the window, and the cash
/// residual absorbs the corresponding money. The whole result is description of stored history,
/// not a prediction.
/// </summary>
public static class DrawdownAttributionAnalyzer
{
    public const string Disclaimer = "回撤归因基于历史快照，是对已发生波动的解释，不是对未来回撤的预测。";

    public static DrawdownAttributionReport Analyze(
        IReadOnlyList<ValuationPoint> curve,
        IReadOnlyList<PortfolioStore.PositionValuationSnapshot> positions,
        IReadOnlyList<FxRate> fxRates,
        string baseCurrency)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fxRates);

        var baseCcy = Currencies.Normalize(baseCurrency);
        var warnings = new List<string>();

        var window = PortfolioRiskAnalyzer.MaxDrawdown(curve);
        if (window is null)
        {
            return NoWindow("尚无足够的估值历史来计算回撤，无法归因（每次刷新会记一个点）。");
        }

        var (depth, peak, trough) = window.Value;
        var peakTotal = ValueAt(curve, peak);
        var troughTotal = ValueAt(curve, trough);
        var totalChange = troughTotal - peakTotal;

        var atPeak = AggregateBySymbol(positions.Where(p => p.AsOf == peak && p.BaseCurrency == baseCcy));
        var atTrough = AggregateBySymbol(positions.Where(p => p.AsOf == trough && p.BaseCurrency == baseCcy));
        if (atPeak.Count == 0 && atTrough.Count == 0)
        {
            warnings.Add("回撤两端都没有持仓级快照（该功能上线前的历史只有总额），无法归因。");
        }

        var symbols = atPeak.Select(p => p.Symbol)
            .Concat(atTrough.Select(p => p.Symbol))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var rows = new List<DrawdownAttributionRow>();
        var attributedChange = 0m;

        var peakBySymbol = atPeak.ToDictionary(p => p.Symbol, StringComparer.Ordinal);
        var troughBySymbol = atTrough.ToDictionary(p => p.Symbol, StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            peakBySymbol.TryGetValue(symbol, out var peakRow);
            troughBySymbol.TryGetValue(symbol, out var troughRow);

            // Absent at an endpoint = opened or closed inside the window; the money moved through
            // cash, which the residual row picks up.
            var peakBase = peakRow?.MarketValueBase ?? 0m;
            var troughBase = troughRow?.MarketValueBase ?? 0m;
            var change = troughBase - peakBase;

            string? note = null;
            Money? localPart = null;
            Money? fxPart = null;

            var attributable = !(peakRow is { Priced: false } || troughRow is { Priced: false });
            if (!attributable)
            {
                note = "回撤某一端该持仓未定价，无法诚实拆分。";
                warnings.Add($"{symbol} 在回撤端点缺价格，归因不完整。");
            }
            else
            {
                var currency = peakRow?.Currency ?? troughRow?.Currency ?? baseCcy;
                var mvLocalPeak = peakRow?.MarketValueLocal ?? 0m;
                var mvLocalTrough = troughRow?.MarketValueLocal ?? 0m;

                if (string.Equals(currency, baseCcy, StringComparison.Ordinal))
                {
                    // Same currency as the base: the whole move is the asset's.
                    localPart = Money.Of(change, baseCcy);
                    fxPart = Money.Zero(baseCcy);
                }
                else
                {
                    var peakRate = RateAt(fxRates, currency, baseCcy, peak);
                    var troughRate = RateAt(fxRates, currency, baseCcy, trough);
                    if (peakRate is null || troughRate is null)
                    {
                        note = $"缺端点汇率（{currency} → {baseCcy}），汇率部分未知。";
                        warnings.Add($"{symbol} 的汇率归因未知：缺 {peak}/{trough} 的 {currency} 汇率。");
                    }
                    else
                    {
                        // Δ(base) = Δ(local) × r_trough + local_peak × (r_trough − r_peak) — identity.
                        localPart = Money.Of((mvLocalTrough - mvLocalPeak) * troughRate.Value, baseCcy);
                        fxPart = Money.Of(change - localPart.Value.Amount, baseCcy);
                    }
                }
            }

            if (peakRow is null)
            {
                note ??= "窗口内新开仓（峰值日无此持仓）。";
            }
            else if (troughRow is null)
            {
                note ??= "窗口内清仓（谷值日无此持仓）。";
            }

            attributedChange += change;
            rows.Add(new DrawdownAttributionRow
            {
                Label = symbol,
                ChangeBase = Money.Of(change, baseCcy),
                LocalPart = localPart,
                FxPart = fxPart,
                // Negative = dragged the portfolio down; a holding that rose offsets.
                ShareOfChange = attributable && totalChange != 0m
                    ? change / Math.Abs(totalChange)
                    : null,
                Note = note,
            });
        }

        // The cash line is the residual: whatever the holdings rows cannot explain, including
        // deposits and withdrawals made inside the window.
        var cashChange = totalChange - attributedChange;
        rows.Add(new DrawdownAttributionRow
        {
            Label = "现金（含出入金）",
            ChangeBase = Money.Of(cashChange, baseCcy),
            ShareOfChange = totalChange != 0m ? cashChange / Math.Abs(totalChange) : null,
            Note = "总额残差，吸收窗口内的出入金与利息。",
        });

        rows = rows.OrderBy(r => r.ChangeBase.Amount).ToList();

        var summary = BuildSummary(rows, peak, trough, totalChange, peakTotal);
        return new DrawdownAttributionReport
        {
            Peak = peak,
            Trough = trough,
            Depth = depth,
            Rows = rows,
            Warnings = warnings,
            Summary = summary,
        };
    }

    private static DrawdownAttributionReport NoWindow(string reason) => new()
    {
        Rows = [],
        Warnings = [],
        Summary = reason + " " + Disclaimer,
    };

    /// <summary>
    /// One row per symbol, summed across accounts (P2-02): the same stock held in two
    /// accounts is one economic position — dropping the smaller account's move would let it
    /// be absorbed into the cash residual. Priced only when every account's row is priced;
    /// local and base market values add up; quantity adds up.
    /// </summary>
    private static IReadOnlyList<PortfolioStore.PositionValuationSnapshot> AggregateBySymbol(
        IEnumerable<PortfolioStore.PositionValuationSnapshot> rows) =>
        rows
            .GroupBy(p => p.Symbol, StringComparer.Ordinal)
            .Select(group =>
            {
                var all = group.ToList();
                var allPriced = all.All(p => p.Priced);
                return new PortfolioStore.PositionValuationSnapshot(
                    all[0].AsOf,
                    all[0].BaseCurrency,
                    all[0].Symbol,
                    string.Join("+", all.Select(p => p.AccountId).OrderBy(a => a, StringComparer.Ordinal)),
                    all[0].Currency,
                    all.Sum(p => p.Quantity),
                    allPriced ? all.Sum(p => p.MarketValueLocal) : null,
                    allPriced ? all.Sum(p => p.MarketValueBase) : null,
                    all.Sum(p => p.CostBasisBase),
                    allPriced);
            })
            .ToList();

    private static decimal ValueAt(IReadOnlyList<ValuationPoint> curve, DateOnly date) =>
        curve.First(p => p.Date == date).Value;

    /// <summary>Latest stored rate on or before <paramref name="asOf"/> — no forward reach.</summary>
    private static decimal? RateAt(IReadOnlyList<FxRate> rates, string from, string to, DateOnly asOf)
    {
        var converter = new FxConverter(rates.Where(r => r.AsOf <= asOf));
        return converter.TryGetRate(from, to, out var rate) ? rate : null;
    }

    private static string BuildSummary(
        IReadOnlyList<DrawdownAttributionRow> rows, DateOnly peak, DateOnly trough, decimal totalChange, decimal peakTotal)
    {
        var depth = peakTotal != 0m ? -totalChange / peakTotal : 0m;
        var parts = new List<string>
        {
            $"最大回撤出现在 {peak:yyyy-MM-dd} → {trough:yyyy-MM-dd}（−{depth * 100m:0.#}%），"
            + $"组合变动 {totalChange:+#,0.##;-#,0.##;0}。",
        };

        var drags = rows.Where(r => r.ChangeBase.Amount < 0m).Take(3)
            .Select(r =>
            {
                var text = $"{r.Label} {r.ChangeBase.Amount:+#,0.##;-#,0.##;0}";
                if (r.FxPart is { } fx && fx.Amount != 0m)
                {
                    text += $"（其中汇率 {fx.Amount:+#,0.##;-#,0.##;0}）";
                }

                return text;
            });
        if (drags.Any())
        {
            parts.Add("主要拖累：" + string.Join("、", drags) + "。");
        }

        parts.Add(Disclaimer);
        return string.Join(" ", parts);
    }
}
