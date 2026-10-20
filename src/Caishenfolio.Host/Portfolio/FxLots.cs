using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// How much of one lot a later exchange consumed, and what that share had cost.
/// A projection of the ledger, not a second book of facts: it is rebuilt on every read.
/// </summary>
public sealed record FxLotConsumption
{
    public required string LotId { get; init; }

    /// <summary>The currency of the lot that was consumed (e.g. JPY).</summary>
    public required string Currency { get; init; }

    /// <summary>Units of <see cref="Currency"/> taken from that lot.</summary>
    public required decimal Amount { get; init; }

    /// <summary>What that share cost, in <see cref="CostCurrency"/>.</summary>
    public required decimal CostAmount { get; init; }

    public required string CostCurrency { get; init; }
}

/// <summary>
/// One purchase of a foreign currency: when it was acquired, how much of it is left, and what
/// that purchase cost <em>in the currency actually paid for it</em> (WP-02 R4 — for a CNY→JPY→USD
/// chain the USD lot costs JPY, never the original CNY; the original investment is a separate
/// story told by <see cref="FxLotLedger.UpstreamCosts"/>).
/// </summary>
public sealed record FxLot
{
    /// <summary>Stable identity derived from the ledger row, so a re-read yields the same id.</summary>
    public required string Id { get; init; }

    public required DateOnly AcquiredOn { get; init; }

    public required string AccountId { get; init; }

    /// <summary>The currency acquired by this batch (e.g. JPY).</summary>
    public required string Currency { get; init; }

    public required decimal OriginalAmount { get; init; }

    public required decimal RemainingAmount { get; init; }

    /// <summary>The currency paid in this segment (R4: the segment's cost currency).</summary>
    public required string CostCurrency { get; init; }

    /// <summary>What the whole batch cost, in <see cref="CostCurrency"/>.</summary>
    public required decimal CostAmount { get; init; }

    /// <summary>The share of <see cref="CostAmount"/> still attributable to <see cref="RemainingAmount"/>.</summary>
    public required decimal RemainingCostAmount { get; init; }

    public string SourceTransactionId { get; init; } = "";

    public string Note { get; init; } = "";

    /// <summary>
    /// Where the <see cref="CostCurrency"/> that paid for this lot came from — the previous
    /// segment's lots. Empty for a lot bought with money that has no batch (fresh base cash).
    /// </summary>
    public IReadOnlyList<FxLotConsumption> FundedBy { get; init; } = [];

    public bool IsOpen => RemainingAmount > 0m;
}

/// <summary>One exchange that consumed batches: which lots were used, in FIFO order.</summary>
public sealed record FxLotSpend
{
    public required string TransactionId { get; init; }

    public required DateOnly Date { get; init; }

    public required string AccountId { get; init; }

    /// <summary>The currency that was spent (e.g. JPY when converting JPY → USD).</summary>
    public required string Currency { get; init; }

    /// <summary>How much left the account, fee included (the ledger's own convention).</summary>
    public required decimal Amount { get; init; }

    /// <summary>The lots consumed, oldest first — the visible FIFO trail (R3/D1).</summary>
    public IReadOnlyList<FxLotConsumption> Lots { get; init; } = [];

    /// <summary>
    /// Spent above what the batches can explain (external deposits, interest). Reported, never
    /// silently assigned to a batch — an unexplained release would corrupt every remaining cost.
    /// </summary>
    public decimal UncoveredAmount { get; init; }
}

/// <summary>
/// The batch view of the ledger's exchange history (WP-02 R2/R3/R4). A pure projection: rebuild
/// it from the transactions and you get the same batches, so a reversed exchange takes its lot
/// with it and nobody can edit a batch directly.
/// </summary>
public sealed record FxLotLedger
{
    public required IReadOnlyList<FxLot> Lots { get; init; }

    public required IReadOnlyList<FxLotSpend> Spends { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Projects the batches out of the ledger's exchange rows, oldest acquisition first.</summary>
    public static FxLotLedger FromTransactions(IEnumerable<LedgerTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var all = transactions as IReadOnlyList<LedgerTransaction> ?? transactions.ToArray();

        // A corrected exchange never happened, so neither did its batch (R2). Same rule the
        // receipt reader uses: drop the reversal row and the row it reverses.
        var cancelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var txn in all)
        {
            if (txn.IsReversal)
            {
                cancelled.Add(txn.ReversesTransactionId);
                cancelled.Add(txn.Id);
            }
        }

        var states = new List<LotState>();
        var spends = new List<FxLotSpend>();
        var warnings = new List<string>();

        // The ledger's own stable order (date → recorded at → id): FIFO must not depend on the
        // order a caller happens to hand the rows in.
        foreach (var txn in PositionCalculator.Ordered(all.Where(t => !cancelled.Contains(t.Id))))
        {
            if (txn.Kind != TransactionKind.FxExchange)
            {
                continue;
            }

            // What left the account, fee included — the ledger's own convention (the fee is
            // charged in the currency being converted).
            var spendCurrency = Currencies.Normalize(txn.Currency);
            var spendAmount = txn.CashAmount + txn.Fee;
            var draw = Consume(states, spendCurrency, spendAmount, txn.TradeDate, warnings);
            if (draw is { } taken)
            {
                spends.Add(new FxLotSpend
                {
                    TransactionId = txn.Id,
                    Date = txn.TradeDate,
                    AccountId = txn.AccountId,
                    Currency = spendCurrency,
                    Amount = spendAmount,
                    Lots = taken.Taken,
                    UncoveredAmount = taken.Uncovered,
                });
            }

            var gotCurrency = Currencies.Normalize(txn.CounterCurrency);
            if (gotCurrency.Length == 0 || txn.CounterAmount <= 0m)
            {
                continue;
            }

            states.Add(new LotState(new FxLot
            {
                Id = $"lot_{txn.Id}",
                AcquiredOn = txn.TradeDate,
                AccountId = txn.AccountId,
                Currency = gotCurrency,
                OriginalAmount = txn.CounterAmount,
                RemainingAmount = txn.CounterAmount,
                // R4: this segment's own payment, never the money further up the chain.
                CostCurrency = spendCurrency,
                CostAmount = spendAmount,
                RemainingCostAmount = spendAmount,
                SourceTransactionId = txn.Id,
                Note = txn.Note,
                FundedBy = draw?.Taken ?? [],
            }));
        }

        return new FxLotLedger
        {
            Lots = states
                .Select(state => state.ToRecord())
                .OrderBy(lot => lot.AcquiredOn)
                .ThenBy(lot => lot.Id, StringComparer.Ordinal)
                .ToArray(),
            Spends = spends,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// FIFO draw-down of one currency: oldest lot first, and never more than is left.
    /// Null when this currency has no batches at all — spending fresh base money is how a batch
    /// gets created, not a batch consumption, so it must not produce a spend row or a warning.
    /// </summary>
    private static (IReadOnlyList<FxLotConsumption> Taken, decimal Uncovered)? Consume(
        List<LotState> lots, string currency, decimal amount, DateOnly date, List<string> warnings)
    {
        var tracked = lots
            .Where(s => string.Equals(s.Lot.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (tracked.Length == 0)
        {
            return null;
        }

        var taken = new List<FxLotConsumption>();
        if (amount <= 0m)
        {
            return (taken, 0m);
        }

        var need = amount;
        foreach (var state in tracked
                     .Where(s => s.Lot.IsOpen)
                     .OrderBy(s => s.Lot.AcquiredOn)
                     .ThenBy(s => s.Lot.Id, StringComparer.Ordinal)
                     .ToArray())
        {
            if (need <= 0m)
            {
                break;
            }

            var take = Math.Min(state.Remaining, need);
            var costShare = state.Remaining > 0m
                ? state.RemainingCost * take / state.Remaining
                : 0m;
            taken.Add(new FxLotConsumption
            {
                LotId = state.Lot.Id,
                Currency = state.Lot.Currency,
                Amount = take,
                CostAmount = costShare,
                CostCurrency = state.Lot.CostCurrency,
            });
            state.Remaining -= take;
            state.RemainingCost -= costShare;
            state.Normalize();
            need -= take;
        }

        if (need <= 0m)
        {
            return (taken, 0m);
        }

        // Spent money the batches cannot explain (an external deposit, interest): report it and
        // keep it out of every lot, because assigning it would corrupt their costs.
        warnings.Add(
            $"{date:yyyy-MM-dd} 有 {need:0.######} {currency} 支出未被批次覆盖" +
            "（这部分外币不是换汇买来的），已单独记下，没有摊到任何批次上。");
        return (taken, need);
    }

    /// <summary>Mutable accumulator; the public surface stays immutable records.</summary>
    private sealed class LotState(FxLot lot)
    {
        public FxLot Lot { get; } = lot;

        public decimal Remaining { get; set; } = lot.RemainingAmount;

        public decimal RemainingCost { get; set; } = lot.RemainingCostAmount;

        public void Normalize()
        {
            // Repeated proportional division leaves dust; a lot that is exhausted to a rounding
            // ghost is empty, and its cost follows it to zero.
            if (Math.Abs(Remaining) < 0.000_000_1m)
            {
                Remaining = 0m;
            }

            if (Math.Abs(RemainingCost) < 0.000_000_1m)
            {
                RemainingCost = 0m;
            }

            RemainingCost = Math.Max(RemainingCost, 0m);
            Remaining = Math.Max(Remaining, 0m);
        }

        public FxLot ToRecord() => Lot with
        {
            RemainingAmount = Remaining,
            RemainingCostAmount = RemainingCost,
        };
    }

    /// <summary>Lots of one currency that still hold something, oldest first.</summary>
    public IReadOnlyList<FxLot> OpenLots(string currency) =>
        Lots.Where(lot => lot.IsOpen
                          && string.Equals(lot.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(lot => lot.AcquiredOn)
            .ThenBy(lot => lot.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// What this batch was ultimately paid with, keyed by the currency each hop actually paid —
    /// the "original investment chain" R4 keeps apart from the segment's own cost. For a USD lot
    /// bought with JPY bought with CNY it yields CNY (the original money), while
    /// <see cref="FxLot.CostCurrency"/> on the lot itself answers JPY (this segment's payment).
    /// The two are shown separately and never added together.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> UpstreamCosts(FxLotLedger ledger, FxLot lot)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(lot);

        var byId = new Dictionary<string, FxLot>(StringComparer.Ordinal);
        foreach (var candidate in ledger.Lots)
        {
            byId[candidate.Id] = candidate;
        }

        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<FxLotConsumption>(lot.FundedBy);
        while (queue.Count > 0)
        {
            var step = queue.Dequeue();

            // One hop per lot: an ancestor cannot legitimately appear twice, and the guard keeps a
            // malformed projection from looping instead of answering.
            if (!seen.Add(step.LotId))
            {
                continue;
            }

            if (!byId.TryGetValue(step.LotId, out var ancestor))
            {
                // The ancestor is gone (its exchange was reversed): the share is still part of the
                // story, recorded under the currency the consumption itself named.
                totals[step.CostCurrency] = totals.GetValueOrDefault(step.CostCurrency) + step.CostAmount;
                continue;
            }

            totals[ancestor.CostCurrency] =
                totals.GetValueOrDefault(ancestor.CostCurrency) + step.CostAmount;
            foreach (var next in ancestor.FundedBy)
            {
                queue.Enqueue(next);
            }
        }

        return totals;
    }
}
