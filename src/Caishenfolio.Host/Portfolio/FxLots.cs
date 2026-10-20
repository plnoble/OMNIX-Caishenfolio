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
        throw new NotImplementedException("WP-02 P1：批次投影尚未实现。");
    }

    /// <summary>Lots of one currency that still hold something, oldest first.</summary>
    public IReadOnlyList<FxLot> OpenLots(string currency) =>
        Lots.Where(lot => lot.IsOpen
                          && string.Equals(lot.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(lot => lot.AcquiredOn)
            .ThenBy(lot => lot.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Every coupon of every batch this one was ultimately paid with, keyed by currency — the
    /// "original investment chain" R4 keeps apart from the segment's own cost. For a USD lot
    /// bought with JPY bought with CNY this yields JPY (the segment's payment) and CNY (the
    /// original money), so the two can be shown separately and never added together.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> UpstreamCosts(FxLotLedger ledger, FxLot lot)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(lot);
        throw new NotImplementedException("WP-02 P2：链路成本尚未实现。");
    }
}
