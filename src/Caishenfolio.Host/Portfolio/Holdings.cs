using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// An open holding after replaying the ledger. Cost basis includes buy-side fees and taxes,
/// which is what a broker statement shows as 摊薄成本.
/// </summary>
public sealed record Position
{
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public required string Currency { get; init; }
    public required decimal Quantity { get; init; }
    public required Money CostBasis { get; init; }
    public required Money RealizedPnl { get; init; }
    public required Money Dividends { get; init; }
    public required Money Fees { get; init; }
    public required Money Taxes { get; init; }

    /// <summary>Average cost per unit; zero once the position is fully closed.</summary>
    public Money AverageCost =>
        Quantity == 0m ? Money.Zero(Currency) : CostBasis / Quantity;

    public bool IsOpen => Quantity != 0m;
}

/// <summary>Cash sitting in one account in one currency.</summary>
public sealed record CashBalance
{
    public required string AccountId { get; init; }
    public required string Currency { get; init; }
    public required decimal Amount { get; init; }

    public Money Money => Data.Money.Of(Amount, Currency);
}

/// <summary>Money crossing a boundary — the cash-flow series that return metrics consume.</summary>
public sealed record ExternalFlow
{
    public required DateOnly Date { get; init; }
    /// <summary>Positive when money enters the boundary being measured.</summary>
    public required Money Amount { get; init; }
    public required TransactionKind Kind { get; init; }
    /// <summary>The account this leg belongs to (source account for an internal move).</summary>
    public string AccountId { get; init; } = "";
    /// <summary>Receiving account of an internal move; empty when there is only one account.</summary>
    public string CounterAccountId { get; init; } = "";
    /// <summary>
    /// False for moves that stay inside the portfolio (账户间转移 / 换汇). Such a flow is real at
    /// the account level — money did leave one account and enter another — but it must never
    /// reach the portfolio XIRR, where it would look like a fake contribution and withdrawal.
    /// </summary>
    public bool CrossesPortfolioBoundary { get; init; } = true;
}

/// <summary>
/// The latest "当天资产金额" of an account: what a balance-mode account is worth, and from when.
/// The amount is in the account's own currency; conversion to the base currency is the
/// valuation's job, because that depends on the date being valued.
/// </summary>
public sealed record BalanceAssertionPoint
{
    public required DateOnly Date { get; init; }
    public required Money Value { get; init; }
    public required string TransactionId { get; init; }
}

/// <summary>The full result of replaying a ledger.</summary>
public sealed record LedgerState
{
    public required IReadOnlyList<Position> Positions { get; init; }
    public required IReadOnlyList<CashBalance> CashBalances { get; init; }
    /// <summary>Portfolio-boundary flows only. Drives the portfolio XIRR.</summary>
    public required IReadOnlyList<ExternalFlow> ExternalFlows { get; init; }

    /// <summary>
    /// Every flow that crosses an account's own boundary, including the two legs of an internal
    /// move. Drives per-account return metrics, where "a transfer out" is money leaving that
    /// account.
    /// </summary>
    public IReadOnlyList<ExternalFlow> AccountFlows { get; init; } = [];

    /// <summary>Latest assertion per account, keyed by account id.</summary>
    public IReadOnlyDictionary<string, BalanceAssertionPoint> BalanceAssertions { get; init; } =
        new Dictionary<string, BalanceAssertionPoint>(StringComparer.Ordinal);

    public IEnumerable<Position> OpenPositions => Positions.Where(p => p.IsOpen);

    /// <summary>Flows that cross <paramref name="accountId"/>'s boundary, in date order.</summary>
    public IReadOnlyList<ExternalFlow> FlowsForAccount(string accountId) =>
        string.IsNullOrEmpty(accountId)
            ? []
            : AccountFlows
                .Where(f => string.Equals(f.AccountId, accountId, StringComparison.Ordinal))
                .OrderBy(f => f.Date)
                .ToArray();

    public static LedgerState Empty { get; } = new()
    {
        Positions = [],
        CashBalances = [],
        ExternalFlows = [],
    };
}
