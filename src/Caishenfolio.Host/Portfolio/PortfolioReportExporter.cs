using System.Globalization;
using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// CSV exports of a valuation and of the raw ledger.
///
/// Unpriced holdings are exported with empty market-value cells rather than zeros — a
/// spreadsheet that sums the column must not silently include a holding nobody could price.
/// </summary>
public static class PortfolioReportExporter
{
    public static string PositionsCsv(
        PortfolioValuation valuation,
        IReadOnlyDictionary<string, Instrument>? instruments = null,
        IReadOnlyDictionary<string, Account>? accounts = null)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        var rows = new List<IEnumerable<string>>
        {
            new[]
            {
                "账户", "标的", "名称", "品种", "市场", "货币", "数量", "成本单价",
                $"成本({valuation.BaseCurrency})", "最新价", "市值(原币)", $"市值({valuation.BaseCurrency})",
                $"浮动盈亏({valuation.BaseCurrency})", $"已实现({valuation.BaseCurrency})",
                $"分红({valuation.BaseCurrency})", "占比", "价格日期", "状态",
            },
        };

        foreach (var item in valuation.Positions)
        {
            var position = item.Position;
            var meta = instruments is not null && instruments.TryGetValue(position.Symbol, out var found)
                ? found
                : null;
            var accountName = accounts is not null && accounts.TryGetValue(position.AccountId, out var account)
                ? account.Name
                : position.AccountId;

            rows.Add(new[]
            {
                accountName,
                position.Symbol,
                meta?.Name ?? "",
                meta?.AssetClass.ToDisplayName() ?? "",
                meta?.Region.ToDisplayName() ?? RegionOf(position.Symbol),
                position.Currency,
                Number(position.Quantity),
                Number(position.AverageCost.Amount),
                Number(item.CostBasisBase.Amount),
                item.Quote is null ? "" : Number(item.Quote.Price),
                item.MarketValue is null ? "" : Number(item.MarketValue.Value.Amount),
                item.MarketValueBase is null ? "" : Number(item.MarketValueBase.Value.Amount),
                item.UnrealizedPnlBase is null ? "" : Number(item.UnrealizedPnlBase.Value.Amount),
                Number(item.RealizedPnlBase.Amount),
                Number(item.DividendsBase.Amount),
                item.Weight is null ? "" : Percent(item.Weight.Value),
                item.Quote?.AsOf.ToString("yyyy-MM-dd") ?? "",
                item.Priced ? (position.IsOpen ? "持仓" : "已清仓") : "缺价格",
            });
        }

        rows.Add(Array.Empty<string>());
        rows.Add(new[] { "现金合计", "", "", "", "", valuation.BaseCurrency, "", "", "", "", "", Number(valuation.CashValue.Amount) });
        rows.Add(new[] { "持仓合计", "", "", "", "", valuation.BaseCurrency, "", "", Number(valuation.CostBasis.Amount), "", "", Number(valuation.HoldingsValue.Amount), Number(valuation.UnrealizedPnl.Amount), Number(valuation.RealizedPnl.Amount), Number(valuation.Dividends.Amount) });
        rows.Add(new[] { "总资产", "", "", "", "", valuation.BaseCurrency, "", "", "", "", "", Number(valuation.TotalValue.Amount) });

        if (!valuation.IsComplete)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "估值不完整，以下项目未计入合计：" });
            foreach (var warning in valuation.Warnings)
            {
                rows.Add(new[] { warning });
            }
        }

        rows.Add(Array.Empty<string>());
        rows.Add(new[] { ProductInfo.ResearchDisclaimer });
        return DelimitedText.Write(rows);
    }

    public static string AllocationCsv(PortfolioValuation valuation)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        var rows = new List<IEnumerable<string>>
        {
            new[] { "维度", "分类", $"市值({valuation.BaseCurrency})", "占比" },
        };

        AppendSlices(rows, "品种", valuation.ByAssetClass);
        AppendSlices(rows, "市场", valuation.ByRegion);
        AppendSlices(rows, "货币", valuation.ByCurrency);
        AppendSlices(rows, "账户", valuation.ByAccount);

        rows.Add(Array.Empty<string>());
        rows.Add(new[] { ProductInfo.ResearchDisclaimer });
        return DelimitedText.Write(rows);
    }

    /// <summary>Round-trips through <see cref="TransactionCsvImporter"/>: the export is a valid import.</summary>
    public static string TransactionsCsv(
        IEnumerable<LedgerTransaction> transactions,
        IReadOnlyDictionary<string, Account>? accounts = null,
        IReadOnlyDictionary<string, Instrument>? instruments = null)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        var rows = new List<IEnumerable<string>> { TransactionCsvImporter.TemplateHeader };
        foreach (var txn in PositionCalculator.Ordered(transactions))
        {
            var accountName = accounts is not null && accounts.TryGetValue(txn.AccountId, out var account)
                ? account.Name
                : txn.AccountId;
            var name = instruments is not null && instruments.TryGetValue(txn.Symbol, out var instrument)
                ? instrument.Name
                : "";

            rows.Add(new[]
            {
                txn.TradeDate.ToString("yyyy-MM-dd"),
                accountName,
                KindLabel(txn.Kind),
                txn.Symbol,
                name,
                txn.Quantity == 0m ? "" : Number(txn.Quantity),
                txn.Price == 0m ? "" : Number(txn.Price),
                txn.Currency,
                txn.Fee == 0m ? "" : Number(txn.Fee),
                txn.Tax == 0m ? "" : Number(txn.Tax),
                txn.CashAmount == 0m ? "" : Number(txn.CashAmount),
                txn.Note,
            });
        }

        return DelimitedText.Write(rows);
    }

    private static void AppendSlices(
        List<IEnumerable<string>> rows, string dimension, IReadOnlyList<AllocationSlice> slices)
    {
        foreach (var slice in slices)
        {
            rows.Add(new[] { dimension, slice.Label, Number(slice.Value.Amount), Percent(slice.Weight) });
        }
    }

    private static string KindLabel(TransactionKind kind) => kind switch
    {
        TransactionKind.Buy => "买入",
        TransactionKind.Sell => "卖出",
        TransactionKind.Dividend => "分红",
        TransactionKind.StockDividend => "送股",
        TransactionKind.Split => "拆股",
        TransactionKind.Interest => "利息",
        TransactionKind.Deposit => "入金",
        TransactionKind.Withdraw => "出金",
        TransactionKind.Fee => "费用",
        TransactionKind.Tax => "税",
        TransactionKind.FxExchange => "换汇",
        TransactionKind.OpeningPosition => "期初持仓",
        _ => "期初现金",
    };

    private static string RegionOf(string symbol) =>
        SymbolId.TryParse(symbol, out var parsed) && ExchangeRegistry.TryGetRegion(parsed.Exchange, out var region)
            ? region.ToDisplayName()
            : "";

    private static string Number(decimal value) =>
        value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Percent(decimal weight) =>
        (weight * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
