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
            // Money leaving a currency draws the batches down whatever the reason (WP-02 R2:
            // the batch is a projection of the cash, not of the exchange list). A withdrawal or
            // a purchase takes the cost with it; leaving the batch untouched would let later
            // money inherit a cost that already left the account.
            var fundedBy = new List<FxLotConsumption>();
            foreach (var (accountId, currency, amount) in CashOut(txn))
            {
                var draw = Consume(states, accountId, currency, amount, txn.TradeDate, warnings);
                if (draw is not { } taken)
                {
                    continue;
                }

                spends.Add(new FxLotSpend
                {
                    TransactionId = txn.Id,
                    Date = txn.TradeDate,
                    AccountId = accountId,
                    Currency = currency,
                    Amount = amount,
                    Lots = taken.Taken,
                    UncoveredAmount = taken.Uncovered,
                });

                // The new batch was paid by this very outflow: that is its provenance chain.
                if (txn.Kind == TransactionKind.FxExchange
                    && string.Equals(accountId, txn.AccountId, StringComparison.Ordinal))
                {
                    fundedBy.AddRange(taken.Taken);
                }
            }

            if (txn.Kind != TransactionKind.FxExchange)
            {
                continue;
            }

            var spendCurrency = Currencies.Normalize(txn.Currency);
            var spendAmount = txn.CashAmount + txn.Fee;

            var gotCurrency = Currencies.Normalize(txn.CounterCurrency);
            if (gotCurrency.Length == 0 || txn.CounterAmount <= 0m)
            {
                continue;
            }

            states.Add(new LotState(new FxLot
            {
                Id = $"lot_{txn.Id}",
                AcquiredOn = txn.TradeDate,
                // A cross-account move delivers the acquired currency to the counter account;
                // the batch belongs where the money now is (WP-01 books both legs that way).
                AccountId = string.IsNullOrEmpty(txn.CounterAccountId)
                    ? txn.AccountId
                    : txn.CounterAccountId,
                Currency = gotCurrency,
                OriginalAmount = txn.CounterAmount,
                RemainingAmount = txn.CounterAmount,
                // R4: this segment's own payment, never the money further up the chain.
                CostCurrency = spendCurrency,
                CostAmount = spendAmount,
                RemainingCostAmount = spendAmount,
                SourceTransactionId = txn.Id,
                Note = txn.Note,
                FundedBy = fundedBy,
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
    /// Cash leaving an account, per currency, whatever the reason: the batch view follows the
    /// money, so every outflow draws it down (fees included, the ledger's own convention).
    /// Inflows are deliberately absent — money that arrived without a receipt creates no batch.
    /// </summary>
    private static IEnumerable<(string AccountId, string Currency, decimal Amount)> CashOut(
        LedgerTransaction txn)
    {
        var currency = Currencies.Normalize(txn.Currency);
        switch (txn.Kind)
        {
            case TransactionKind.FxExchange when currency.Length > 0 && txn.CashAmount + txn.Fee > 0m:
                yield return (txn.AccountId, currency, txn.CashAmount + txn.Fee);
                break;

            case TransactionKind.Withdraw when currency.Length > 0 && txn.CashAmount + txn.Fee > 0m:
                yield return (txn.AccountId, currency, txn.CashAmount + txn.Fee);
                break;

            case TransactionKind.Buy when currency.Length > 0
                                          && txn.Quantity * txn.Price + txn.Fee + txn.Tax > 0m:
                yield return (txn.AccountId, currency, txn.Quantity * txn.Price + txn.Fee + txn.Tax);
                break;

            case TransactionKind.Transfer when currency.Length > 0 && txn.CashAmount + txn.Fee > 0m:
                yield return (txn.AccountId, currency, txn.CashAmount + txn.Fee);
                break;
        }
    }

    /// <summary>
    /// FIFO draw-down of one currency in ONE account: oldest lot first, never more than is left.
    /// Null when that account has no batches of the currency at all — spending fresh base money is
    /// how a batch gets created, not a batch consumption, so it must not produce a spend row.
    /// </summary>
    private static (IReadOnlyList<FxLotConsumption> Taken, decimal Uncovered)? Consume(
        List<LotState> lots, string accountId, string currency, decimal amount, DateOnly date,
        List<string> warnings)
    {
        // Batches are per account: spending in B must never consume A's lot, which would hand
        // B's unknown-origin cash A's historical cost.
        var tracked = lots
            .Where(s => string.Equals(s.Lot.AccountId, accountId, StringComparison.Ordinal)
                        && string.Equals(s.Lot.Currency, currency, StringComparison.OrdinalIgnoreCase))
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
                     // `Lot.IsOpen` is the record's original amount; what matters here is what is
                     // left NOW, or an exhausted batch would add a zero-amount row to every later
                     // spend's trail.
                     .Where(s => s.Remaining > 0m)
                     .OrderBy(s => s.Lot.AcquiredOn)
                     .ThenBy(s => s.Lot.Id, StringComparer.Ordinal)
                     .ToArray())
        {
            if (need <= 0m)
            {
                break;
            }

            var take = Math.Min(state.Remaining, need);
            // Exhausting a batch takes its whole remaining cost, so the cost is conserved exactly
            // instead of leaving an un-allocatable crumb behind (review #13).
            var costShare = take >= state.Remaining
                ? state.RemainingCost
                : state.RemainingCost * take / state.Remaining;
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
            // Only the AMOUNT snaps to zero on a rounding ghost. The cost is never zeroed on its
            // own: a batch exhausted to its last unit hands its whole remaining cost to that
            // consumption (see Consume), so cost is conserved exactly instead of leaking a crumb
            // no later spend could ever claim (review #13).
            if (Math.Abs(Remaining) < 0.000_000_1m)
            {
                Remaining = 0m;
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
        // Breadth-first up the chain. Each hop carries a FACTOR: the share of the ancestor batch
        // this branch actually consumed. Without it a deep chain would attribute the ancestor's
        // whole purchase to a sliver of it (review #5), and additive costs would double-count.
        var queue = new Queue<(FxLotConsumption Step, decimal Factor, int Depth)>(
            lot.FundedBy.Select(step => (step, 1m, 0)));
        while (queue.Count > 0)
        {
            var (step, factor, depth) = queue.Dequeue();

            // Depth is the cycle guard: every hop goes forward in time, so a legitimate chain is
            // short, and a malformed projection must answer instead of looping. Repeated
            // ancestors are NOT skipped — two batches may legitimately share one source.
            if (depth > 32 || step.Amount <= 0m)
            {
                continue;
            }

            var ancestor = byId.GetValueOrDefault(step.LotId);
            if (ancestor is null || ancestor.OriginalAmount <= 0m)
            {
                // The ancestor is gone (its exchange was reversed): the share is still part of the
                // story, recorded under the currency the consumption itself named.
                totals[step.CostCurrency] = totals.GetValueOrDefault(step.CostCurrency)
                                            + factor * step.CostAmount;
                continue;
            }

            // What left the ancestor for this branch, in the ancestor's own paying currency.
            totals[ancestor.CostCurrency] = totals.GetValueOrDefault(ancestor.CostCurrency)
                                            + factor * step.CostAmount;

            var share = step.Amount / ancestor.OriginalAmount;
            foreach (var next in ancestor.FundedBy)
            {
                queue.Enqueue((next, factor * share, depth + 1));
            }
        }

        return totals;
    }
}
