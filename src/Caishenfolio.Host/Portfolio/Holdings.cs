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

/// <summary>Money crossing the portfolio boundary — the cash-flow series that return metrics consume.</summary>
public sealed record ExternalFlow
{
    public required DateOnly Date { get; init; }
    /// <summary>Positive when money enters the portfolio.</summary>
    public required Money Amount { get; init; }
    public required TransactionKind Kind { get; init; }
    public string AccountId { get; init; } = "";
}

/// <summary>The full result of replaying a ledger.</summary>
public sealed record LedgerState
{
    public required IReadOnlyList<Position> Positions { get; init; }
    public required IReadOnlyList<CashBalance> CashBalances { get; init; }
    public required IReadOnlyList<ExternalFlow> ExternalFlows { get; init; }

    public IEnumerable<Position> OpenPositions => Positions.Where(p => p.IsOpen);

    public static LedgerState Empty { get; } = new()
    {
        Positions = [],
        CashBalances = [],
        ExternalFlows = [],
    };
}
