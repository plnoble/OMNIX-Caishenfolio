using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// One booked currency exchange, as the receipt stated it: two amounts, one date.
/// The implied rate is derived for display only — the ledger stores both amounts precisely
/// because the rate has no exact decimal form (R1 rule: book the receipt, never the rate).
/// </summary>
public sealed record FxExchangeRecord
{
    public required DateOnly Date { get; init; }
    public required string AccountId { get; init; }
    public required string FromCurrency { get; init; }
    public required decimal FromAmount { get; init; }
    public required string ToCurrency { get; init; }
    public required decimal ToAmount { get; init; }
    public decimal Fee { get; init; }
    public string Note { get; init; } = "";

    /// <summary>Units of <see cref="ToCurrency"/> per 1 unit of <see cref="FromCurrency"/> — display only.</summary>
    public decimal ImpliedRate => FromAmount == 0m ? 0m : ToAmount / FromAmount;
}

/// <summary>
/// Reads the user's actual exchange history out of the ledger (N2: the "historical cost"
/// side of the path comparison). Pure function over transactions, so the FX page and the
/// tests share one source of truth.
/// </summary>
public static class FxExchangeHistory
{
    /// <summary>
    /// All effective exchanges, oldest first. Reversal pairs are excluded — a corrected
    /// exchange never happened, and showing it as a real cost basis would be wrong.
    /// </summary>
    public static IReadOnlyList<FxExchangeRecord> Records(IEnumerable<LedgerTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        var cancelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var txn in transactions)
        {
            if (txn.IsReversal)
            {
                cancelled.Add(txn.ReversesTransactionId);
                cancelled.Add(txn.Id);
            }
        }

        return transactions
            .Where(t => t.Kind == TransactionKind.FxExchange && !cancelled.Contains(t.Id))
            .OrderBy(t => t.TradeDate)
            .ThenBy(t => t.RecordedAt)
            .Select(t => new FxExchangeRecord
            {
                Date = t.TradeDate,
                AccountId = t.AccountId,
                FromCurrency = t.Currency,
                FromAmount = t.CashAmount,
                ToCurrency = t.CounterCurrency,
                ToAmount = t.CounterAmount,
                Fee = t.Fee,
                Note = t.Note,
            })
            .ToArray();
    }

    /// <summary>
    /// The latest exchange that acquired <paramref name="heldCurrency"/> with
    /// <paramref name="baseCurrency"/> — i.e. how the user actually got that foreign cash.
    /// Null when there is no such record: the cost basis is then genuinely unknown and the
    /// caller must ask the user rather than guess.
    /// </summary>
    public static FxExchangeRecord? MostRecentAcquisition(
        IReadOnlyList<FxExchangeRecord> records,
        string baseCurrency,
        string heldCurrency)
    {
        var baseCode = (baseCurrency ?? "").Trim().ToUpperInvariant();
        var heldCode = (heldCurrency ?? "").Trim().ToUpperInvariant();
        FxExchangeRecord? best = null;
        foreach (var record in records)
        {
            if (!string.Equals(record.FromCurrency, baseCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.ToCurrency, heldCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || record.Date >= best.Date)
            {
                best = record;
            }
        }

        return best;
    }
}

/// <summary>
/// The moving-weighted cost of a foreign-currency pot (P1-07): every acquisition adds its
/// amount at its actual cost, every outward exchange (spending or converting the currency
/// away) releases the average cost of what leaves. Batches with different rates therefore
/// blend instead of "the last purchase speaking for the whole pot".
///
/// Non-exchange arrivals (interest, external deposits in the currency) have no known cost:
/// they are tracked and reported as an explicit unknown share rather than priced at zero or
/// at the last rate. Releases that exceed the priced layers leave the cost unknown too.
/// </summary>
public sealed record HeldCurrencyCost
{
    /// <summary>Priced quantity: amount and what it actually cost, blended.</summary>
    public required decimal PricedAmount { get; init; }
    public required decimal PricedCostInBase { get; init; }
    /// <summary>Arrived by a path with no cost basis (interest, external transfer).</summary>
    public required decimal UnpricedAmount { get; init; }
    /// <summary>Null when some of the pot has no honest cost basis.</summary>
    public decimal? BlendedCostInBase => UnpricedAmount == 0m && PricedAmount > 0m
        ? PricedCostInBase
        : null;
    /// <summary>Null unless the whole balance has a single honest cost.</summary>
    public decimal? BlendedRateHeldPerBase => BlendedCostInBase is { } cost
        ? PricedAmount / cost
        : null;
}

public static class HeldCurrencyCostCalculator
{
    /// <summary>
    /// Repeated proportional division can leave sub-unit residue; a pool that is exhausted
    /// to a rounding ghost is empty, and cost follows it to zero.
    /// </summary>
    private static (decimal Amount, decimal Cost) Normalize(decimal amount, decimal cost)
    {
        if (Math.Abs(amount) < 0.000_000_1m)
        {
            return (0m, 0m);
        }

        return (amount, cost);
    }

    /// <summary>
    /// Blends every exchange touching <paramref name="heldCurrency"/> (either direction)
    /// into the cost of the CURRENT balance of that currency across all accounts.
    /// </summary>
    public static HeldCurrencyCost CostOf(
        IReadOnlyList<LedgerTransaction> transactions,
        string baseCurrency,
        string heldCurrency,
        decimal currentBalance)
    {
        var baseCode = Currencies.Normalize(baseCurrency);
        var heldCode = Currencies.Normalize(heldCurrency);

        var cancelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var txn in transactions)
        {
            if (txn.IsReversal)
            {
                cancelled.Add(txn.ReversesTransactionId);
                cancelled.Add(txn.Id);
            }
        }

        // Replay transaction by transaction (remediation WP3): no same-day netting. A
        // withdraw and an unrelated deposit on one date are two facts — netting them let a
        // day's external arrival inherit the old pool's cost basis. Order is
        // TradeDate → RecordedAt → Id, the ledger's own stable order.
        decimal pricedAmount = 0m, pricedCost = 0m, unpriced = 0m;
        foreach (var txn in PositionCalculator.Ordered(
                     transactions.Where(t => !cancelled.Contains(t.Id))))
        {
            foreach (var (delta, costInBase) in HeldCurrencyDeltas(txn, heldCode, baseCode))
            {
                if (delta > 0m)
                {
                    if (costInBase is { } cost && cost > 0m)
                    {
                        pricedAmount += delta;
                        pricedCost += cost;
                    }
                    else
                    {
                        // Arrived without a base-currency cost basis (interest, third-currency
                        // exchange, external transfer): unpriced, never priced at zero or at
                        // the last rate.
                        unpriced += delta;
                    }
                }
                else if (delta < 0m)
                {
                    var released = -delta;
                    var total = pricedAmount + unpriced;
                    if (total <= 0m)
                    {
                        continue;
                    }

                    if (released > total)
                    {
                        pricedAmount = 0m;
                        pricedCost = 0m;
                        unpriced = 0m;
                        continue;
                    }

                    var pricedShare = released * pricedAmount / total;
                    var unpricedShare = released - pricedShare;
                    // Ratio first (exact when clean), never unit-cost × quantity (residue).
                    var share = pricedAmount > 0m ? pricedShare / pricedAmount : 0m;
                    pricedAmount -= pricedShare;
                    pricedCost -= pricedCost * share;
                    unpriced -= unpricedShare;
                    (pricedAmount, pricedCost) = Normalize(pricedAmount, pricedCost);
                    if (unpriced is > -0.000_000_1m and < 0.000_000_1m)
                    {
                        unpriced = 0m;
                    }
                }
            }
        }

        // The ledger's current balance is ground truth; anything the flows above cannot
        // explain arrived outside them → unpriced, never inherited from priced layers.
        var explained = pricedAmount + unpriced;
        if (currentBalance > explained)
        {
            unpriced += currentBalance - explained;
        }
        else if (currentBalance < explained - 0.000_000_1m)
        {
            // Flows claim more than the ledger holds: the missing part cannot be attributed —
            // release the whole pool proportionally so the remainder keeps a fair share.
            var kept = explained > 0m ? currentBalance / explained : 0m;
            pricedAmount *= kept;
            pricedCost *= kept;
            unpriced *= kept;
        }

        return new HeldCurrencyCost
        {
            PricedAmount = pricedAmount,
            PricedCostInBase = pricedCost,
            UnpricedAmount = unpriced,
        };
    }

    /// <summary>
    /// The change this transaction makes to the TOTAL held-currency balance, with the
    /// base-currency cost of that change when one exists (null = no basis).
    /// </summary>
    private static IEnumerable<(decimal Delta, decimal? CostInBase)> HeldCurrencyDeltas(
        LedgerTransaction txn, string heldCode, string baseCode)
    {
        switch (txn.Kind)
        {
            case TransactionKind.FxExchange when string.Equals(txn.CounterCurrency, heldCode, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(txn.Currency, baseCode, StringComparison.OrdinalIgnoreCase):
                // Bought held currency with base: the receipt amount arrives at its actual cost.
                yield return (txn.CounterAmount, txn.CashAmount + txn.Fee);
                break;

            case TransactionKind.FxExchange when string.Equals(txn.Currency, heldCode, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(txn.CounterCurrency, baseCode, StringComparison.OrdinalIgnoreCase):
                // Converted held → base: the held side loses amount + its fee (the fee is
                // charged in the held currency and leaves with the conversion).
                yield return (-(txn.CashAmount + txn.Fee), null);
                break;

            case TransactionKind.FxExchange when string.Equals(txn.Currency, heldCode, StringComparison.OrdinalIgnoreCase):
                // Held → a third currency: release only; the proceeds say nothing about cost.
                yield return (-(txn.CashAmount + txn.Fee), null);
                break;

            case TransactionKind.FxExchange when string.Equals(txn.CounterCurrency, heldCode, StringComparison.OrdinalIgnoreCase):
                // A third currency → held: arrival without a base cost basis.
                yield return (txn.CounterAmount, null);
                break;

            case TransactionKind.Withdraw when string.Equals(txn.Currency, heldCode, StringComparison.OrdinalIgnoreCase):
                yield return (-txn.CashAmount, null);
                break;

            case TransactionKind.Deposit when string.Equals(txn.Currency, heldCode, StringComparison.OrdinalIgnoreCase):
                yield return (txn.CashAmount, null);
                break;

            case TransactionKind.Fee or TransactionKind.Tax
                when string.Equals(txn.Currency, heldCode, StringComparison.OrdinalIgnoreCase):
                yield return (-txn.CashAmount, null);
                break;

            case TransactionKind.Buy or TransactionKind.Sell
                when string.Equals(txn.Currency, heldCode, StringComparison.OrdinalIgnoreCase):
                // Buying a holding priced in the held currency spends held cash; selling
                // returns it. Cost basis unchanged (the money never left the currency).
                yield return (txn.Kind == TransactionKind.Buy
                    ? -(txn.Quantity * txn.Price + txn.Fee + txn.Tax)
                    : txn.Quantity * txn.Price - txn.Fee - txn.Tax, null);
                break;

            case TransactionKind.Interest or TransactionKind.Dividend
                when string.Equals(txn.Currency, heldCode, StringComparison.OrdinalIgnoreCase):
                yield return (txn.CashAmount - txn.Tax, null);
                break;
        }
    }

}
