using Caishenfolio.Host.Data;
using Caishenfolio.Host.MarketData;

namespace Caishenfolio.Host.Portfolio;

/// <summary>Outcome of pulling the pre-ledger fill journal into the portfolio ledger.</summary>
public sealed record LegacyImportResult
{
    public required int Imported { get; init; }
    public required int Skipped { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Moves the P4.3 fill journal (<see cref="ActualFill"/>, stored as JSON with double amounts)
/// into the ledger. Re-running is a no-op: each imported row keeps a deterministic id derived
/// from the original fill.
/// </summary>
public static class LegacyFillImporter
{
    public const string BatchId = "legacy_price_plan_fills";

    public static LegacyImportResult Import(
        PricePlanStore source,
        PortfolioStore target,
        string accountId,
        string? fallbackCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var existing = target.ListTransactions(accountId)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);

        var warnings = new List<string>();
        var pending = new List<LedgerTransaction>();
        var skipped = 0;

        foreach (var fill in source.Load().Fills)
        {
            var id = $"txn_legacy_{fill.Id}";
            if (existing.Contains(id))
            {
                skipped++;
                continue;
            }

            if (!SymbolId.TryParse(fill.Symbol, out var symbol))
            {
                warnings.Add($"跳过成交 {fill.Id}：标的 '{fill.Symbol}' 不是 交易所:代码 形式。");
                skipped++;
                continue;
            }

            symbol = symbol.Normalized();
            if (!ExchangeRegistry.TryGetQuoteCurrency(symbol, out var currency))
            {
                if (string.IsNullOrWhiteSpace(fallbackCurrency))
                {
                    warnings.Add($"跳过成交 {fill.Id}：无法确定 {symbol.Value} 的计价货币。");
                    skipped++;
                    continue;
                }

                currency = fallbackCurrency;
            }

            var tradeDate = ParseTradeDate(fill.Ts);
            var isBuy = string.Equals(fill.Side, "buy", StringComparison.OrdinalIgnoreCase);

            // The legacy journal stored money as double; the cast rounds off accumulated float noise.
            var price = (decimal)fill.Price;
            var quantity = (decimal)fill.Qty;
            var fee = (decimal)Math.Max(0, fill.Fee);

            LedgerTransaction txn;
            try
            {
                txn = isBuy
                    ? LedgerTransaction.Buy(accountId, symbol.Value, tradeDate, quantity, price, currency, fee, note: fill.Note)
                    : LedgerTransaction.Sell(accountId, symbol.Value, tradeDate, quantity, price, currency, fee, note: fill.Note);
            }
            catch (LedgerException ex)
            {
                warnings.Add($"跳过成交 {fill.Id}：{ex.Message}");
                skipped++;
                continue;
            }

            pending.Add(txn with
            {
                Id = id,
                ImportBatchId = BatchId,
                RecordedAt = ParseRecordedAt(fill.Ts),
            });
        }

        if (pending.Count > 0)
        {
            target.AddTransactions(pending);
        }

        return new LegacyImportResult
        {
            Imported = pending.Count,
            Skipped = skipped,
            Warnings = warnings,
        };
    }

    private static DateOnly ParseTradeDate(string? timestamp) =>
        DateOnly.FromDateTime(ParseRecordedAt(timestamp).ToLocalTime().DateTime);

    private static DateTimeOffset ParseRecordedAt(string? timestamp) =>
        DateTimeOffset.TryParse(timestamp, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
}
