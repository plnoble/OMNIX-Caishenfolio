using Caishenfolio.Host.MarketData;

namespace Caishenfolio.Host.Portfolio;

public enum AlertKind
{
    /// <summary>Price fell to or below a buy level you planned.</summary>
    PlannedBuy,
    /// <summary>Price rose to or above a sell level you planned.</summary>
    PlannedSell,
    /// <summary>A concentration ceiling you set was crossed.</summary>
    Concentration,
    /// <summary>The newest price is older than expected — the valuation may be behind.</summary>
    StalePrice,
    /// <summary>Data sources disagree on the price, so the market value may be wrong.</summary>
    PriceDisagreement,
    /// <summary>A holding could not be priced at all, so it is missing from the totals.</summary>
    Unpriced,
    /// <summary>An IPO needs an action by a date — paying for an allotment, or a listing arriving.</summary>
    IpoDeadline,
}

public enum AlertSeverity
{
    Info,
    Warning,
}

public sealed record PortfolioAlert
{
    public required AlertKind Kind { get; init; }
    public required AlertSeverity Severity { get; init; }
    public string Symbol { get; init; } = "";
    public required string Title { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Turns a fresh valuation into the short list of things worth telling you about.
///
/// Buy/sell alerts reuse the planned price levels already recorded on the research side, so a
/// level you drew on the chart shows up here once you actually hold the instrument. Nothing here
/// recommends a trade — an alert only reports that a level *you* set has been reached.
/// </summary>
public static class PortfolioAlertEvaluator
{
    public static IReadOnlyList<PortfolioAlert> Evaluate(
        PortfolioValuation valuation,
        IReadOnlyList<PlannedPriceLevel>? plannedLevels = null,
        PortfolioRiskReport? risk = null,
        int stalePriceDays = 5,
        DateOnly? asOf = null,
        decimal priceTolerancePercent = 2m,
        IReadOnlyList<IpoSubscription>? ipoSubscriptions = null)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        var today = asOf ?? valuation.AsOf;
        var alerts = new List<PortfolioAlert>();

        var priced = valuation.Positions
            .Where(p => p.Position.IsOpen && p.Quote is not null)
            .ToDictionary(p => p.Position.Symbol, StringComparer.Ordinal);

        foreach (var level in plannedLevels ?? [])
        {
            if (!level.Active || !priced.TryGetValue(NormalizeSymbol(level.Symbol), out var position))
            {
                continue;
            }

            var price = position.Quote!.Price;
            var target = (decimal)level.Price;
            var isBuy = string.Equals(level.Side, "buy", StringComparison.OrdinalIgnoreCase);

            if (isBuy && price <= target)
            {
                alerts.Add(new PortfolioAlert
                {
                    Kind = AlertKind.PlannedBuy,
                    Severity = AlertSeverity.Info,
                    Symbol = position.Position.Symbol,
                    Title = "触及计划买入价",
                    Message = $"{position.Position.Symbol} 现价 {price:#,0.####}，已到你设定的买入价 {target:#,0.####}。"
                              + Suffix(level.Note),
                });
            }
            else if (!isBuy && price >= target)
            {
                alerts.Add(new PortfolioAlert
                {
                    Kind = AlertKind.PlannedSell,
                    Severity = AlertSeverity.Info,
                    Symbol = position.Position.Symbol,
                    Title = "触及计划卖出价",
                    Message = $"{position.Position.Symbol} 现价 {price:#,0.####}，已到你设定的卖出价 {target:#,0.####}。"
                              + Suffix(level.Note),
                });
            }
        }

        foreach (var position in valuation.Positions.Where(p => p.Position.IsOpen && !p.Priced))
        {
            alerts.Add(new PortfolioAlert
            {
                Kind = AlertKind.Unpriced,
                Severity = AlertSeverity.Warning,
                Symbol = position.Position.Symbol,
                Title = "缺价格",
                Message = $"{position.Position.Symbol} 取不到价格或汇率，未计入总资产（不会按 0 计算）。",
            });
        }

        foreach (var position in priced.Values)
        {
            var quote = position.Quote!;
            if (quote.SourceCount < 2 || quote.SpreadPercent <= priceTolerancePercent)
            {
                continue;
            }

            // Naming the deviating source is the actionable part: it tells you which feed to
            // distrust, rather than only that something is off.
            var blame = string.IsNullOrEmpty(quote.Outliers)
                ? ""
                : $"偏离最大的是 {quote.Outliers}。";

            alerts.Add(new PortfolioAlert
            {
                Kind = AlertKind.PriceDisagreement,
                Severity = AlertSeverity.Warning,
                Symbol = position.Position.Symbol,
                Title = "数据源价格不一致",
                Message = $"{position.Position.Symbol} 的 {quote.SourceCount} 个数据源相差 " +
                          $"{quote.SpreadPercent:0.##}%（{quote.Sources}）。{blame}" +
                          $"已取中位数 {quote.Price:#,0.####}，市值可能不准。",
            });
        }

        if (stalePriceDays > 0)
        {
            foreach (var position in priced.Values)
            {
                var age = today.DayNumber - position.Quote!.AsOf.DayNumber;
                if (age <= stalePriceDays)
                {
                    continue;
                }

                alerts.Add(new PortfolioAlert
                {
                    Kind = AlertKind.StalePrice,
                    Severity = AlertSeverity.Warning,
                    Symbol = position.Position.Symbol,
                    Title = "价格偏旧",
                    Message = $"{position.Position.Symbol} 最新价来自 {position.Quote.AsOf:yyyy-MM-dd}（{age} 天前），估值可能滞后。",
                });
            }
        }

        alerts.AddRange(IpoDeadlines(ipoSubscriptions ?? [], today));

        foreach (var finding in risk?.Findings ?? [])
        {
            alerts.Add(new PortfolioAlert
            {
                Kind = AlertKind.Concentration,
                Severity = finding.Level == RiskLevel.Warning ? AlertSeverity.Warning : AlertSeverity.Info,
                Symbol = finding.Dimension == "持仓" ? finding.Label : "",
                Title = $"集中度：{finding.Label}",
                Message = finding.Message,
            });
        }

        return alerts
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Symbol, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Deadlines an IPO record implies. Missing a payment date forfeits the allotment outright
    /// and, on A-shares, bars further applications for months — so an unpaid allotment is the
    /// loudest thing this app can tell you about.
    ///
    /// Public and independent of any valuation on purpose: these come from dates in the ledger,
    /// not from prices, so the headless notifier can check them with no market data and no
    /// analytics core running.
    /// </summary>
    public static IReadOnlyList<PortfolioAlert> IpoDeadlines(
        IReadOnlyList<IpoSubscription> subscriptions, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        return IpoAlerts(subscriptions, today).ToArray();
    }

    private static IEnumerable<PortfolioAlert> IpoAlerts(
        IReadOnlyList<IpoSubscription> subscriptions, DateOnly today)
    {
        foreach (var ipo in subscriptions)
        {
            var label = string.IsNullOrWhiteSpace(ipo.Name) ? ipo.Symbol : $"{ipo.Name}（{ipo.Symbol}）";

            if (ipo.Status == IpoStatus.Allotted)
            {
                yield return new PortfolioAlert
                {
                    Kind = AlertKind.IpoDeadline,
                    Severity = AlertSeverity.Warning,
                    Symbol = ipo.Symbol,
                    Title = "中签待缴款",
                    Message = $"{label} 已中签 {ipo.AllottedQuantity:#,0} 股，还没登记缴款。" +
                              "逾期未缴会作废，并影响后续申购资格。",
                };
                continue;
            }

            if (ipo.Status != IpoStatus.Paid || ipo.ListingDate is not { } listing)
            {
                continue;
            }

            var days = listing.DayNumber - today.DayNumber;
            if (days > 7)
            {
                continue;
            }

            yield return new PortfolioAlert
            {
                Kind = AlertKind.IpoDeadline,
                Severity = AlertSeverity.Info,
                Symbol = ipo.Symbol,
                Title = days switch
                {
                    > 0 => "即将上市",
                    0 => "今日上市",
                    _ => "已上市待卖出",
                },
                Message = days switch
                {
                    > 0 => $"{label} 将于 {listing:yyyy-MM-dd}（{days} 天后）上市，可以准备卖出计划了。",
                    0 => $"{label} 今日上市，可以卖出。",
                    _ => $"{label} 已于 {listing:yyyy-MM-dd} 上市（{-days} 天前），还没登记卖出。",
                },
            };
        }
    }

    private static string NormalizeSymbol(string symbol) =>
        Data.SymbolId.TryParse(symbol, out var parsed) ? parsed.Normalized().Value : symbol;

    private static string Suffix(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "" : $"（备注：{note.Trim()}）";
}
