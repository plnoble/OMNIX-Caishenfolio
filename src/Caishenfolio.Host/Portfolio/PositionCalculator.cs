using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// Replays ledger transactions into positions, cash balances, and external cash flows.
///
/// Cost method is moving weighted average (移动加权平均) — what CN brokers report and what
/// keeps 送股/拆股 arithmetic simple. Selling more than is held fails closed rather than
/// producing a negative position: use <see cref="TransactionKind.OpeningPosition"/> to start
/// a ledger from an existing holding instead of back-filling years of history.
/// </summary>
public static class PositionCalculator
{
    public static LedgerState Replay(IEnumerable<LedgerTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        var positions = new Dictionary<(string Account, string Symbol), PositionAccumulator>();
        var cash = new Dictionary<(string Account, string Currency), decimal>();
        var flows = new List<ExternalFlow>();

        foreach (var txn in Ordered(transactions))
        {
            switch (txn.Kind)
            {
                case TransactionKind.Buy:
                case TransactionKind.OpeningPosition:
                    ApplyBuy(positions, txn);
                    if (txn.Kind == TransactionKind.Buy)
                    {
                        AddCash(cash, txn.AccountId, txn.Currency, -(txn.Quantity * txn.Price + txn.Fee + txn.Tax));
                    }

                    break;

                case TransactionKind.Sell:
                    ApplySell(positions, txn);
                    AddCash(cash, txn.AccountId, txn.Currency, txn.Quantity * txn.Price - txn.Fee - txn.Tax);
                    break;

                case TransactionKind.Dividend:
                    Accumulator(positions, txn).Dividends += txn.CashAmount - txn.Tax;
                    Accumulator(positions, txn).Taxes += txn.Tax;
                    AddCash(cash, txn.AccountId, txn.Currency, txn.CashAmount - txn.Tax);
                    break;

                case TransactionKind.StockDividend:
                    ApplyStockDividend(positions, txn);
                    break;

                case TransactionKind.Split:
                    ApplySplit(positions, txn);
                    break;

                case TransactionKind.Interest:
                    if (!string.IsNullOrEmpty(txn.Symbol))
                    {
                        Accumulator(positions, txn).Dividends += txn.CashAmount - txn.Tax;
                        Accumulator(positions, txn).Taxes += txn.Tax;
                    }

                    AddCash(cash, txn.AccountId, txn.Currency, txn.CashAmount - txn.Tax);
                    break;

                case TransactionKind.Deposit:
                case TransactionKind.OpeningCash:
                    AddCash(cash, txn.AccountId, txn.Currency, txn.CashAmount);
                    break;

                case TransactionKind.Withdraw:
                    AddCash(cash, txn.AccountId, txn.Currency, -txn.CashAmount);
                    break;

                case TransactionKind.Fee:
                case TransactionKind.Tax:
                    ApplyCharge(positions, txn);
                    AddCash(cash, txn.AccountId, txn.Currency, -txn.CashAmount);
                    break;

                case TransactionKind.FxExchange:
                    AddCash(cash, txn.AccountId, txn.Currency, -(txn.CashAmount + txn.Fee));
                    AddCash(cash, txn.AccountId, txn.CounterCurrency, txn.CounterAmount);
                    break;

                default:
                    throw new LedgerException($"未支持的流水类型 {txn.Kind}。");
            }

            if (txn.IsExternalFlow)
            {
                flows.Add(ExternalFlowOf(txn));
            }
        }

        return new LedgerState
        {
            Positions = positions.Values
                .Select(item => item.ToPosition())
                .OrderBy(p => p.AccountId, StringComparer.Ordinal)
                .ThenBy(p => p.Symbol, StringComparer.Ordinal)
                .ToArray(),
            CashBalances = cash
                .Where(pair => pair.Value != 0m)
                .Select(pair => new CashBalance
                {
                    AccountId = pair.Key.Account,
                    Currency = pair.Key.Currency,
                    Amount = pair.Value,
                })
                .OrderBy(b => b.AccountId, StringComparer.Ordinal)
                .ThenBy(b => b.Currency, StringComparer.Ordinal)
                .ToArray(),
            ExternalFlows = flows
                .OrderBy(f => f.Date)
                .ToArray(),
        };
    }

    /// <summary>Deterministic order: trade date, then the order the rows were recorded, then id.</summary>
    public static IEnumerable<LedgerTransaction> Ordered(IEnumerable<LedgerTransaction> transactions) =>
        transactions
            .OrderBy(t => t.TradeDate)
            .ThenBy(t => t.RecordedAt)
            .ThenBy(t => t.Id, StringComparer.Ordinal);

    private static ExternalFlow ExternalFlowOf(LedgerTransaction txn)
    {
        // Opening balances are the money already inside on day one, so they count as inflows.
        var signed = txn.Kind switch
        {
            TransactionKind.Withdraw => -txn.CashAmount,
            TransactionKind.OpeningPosition => txn.Quantity * txn.Price,
            _ => txn.CashAmount,
        };

        return new ExternalFlow
        {
            Date = txn.TradeDate,
            Amount = Money.Of(signed, txn.Currency),
            Kind = txn.Kind,
            AccountId = txn.AccountId,
        };
    }

    private static void ApplyBuy(
        Dictionary<(string, string), PositionAccumulator> positions, LedgerTransaction txn)
    {
        var acc = Accumulator(positions, txn);
        acc.Quantity += txn.Quantity;
        acc.CostBasis += txn.Quantity * txn.Price + txn.Fee + txn.Tax;
        acc.Fees += txn.Fee;
        acc.Taxes += txn.Tax;
    }

    private static void ApplySell(
        Dictionary<(string, string), PositionAccumulator> positions, LedgerTransaction txn)
    {
        var acc = Accumulator(positions, txn);
        if (txn.Quantity > acc.Quantity)
        {
            throw new LedgerException(
                $"{txn.TradeDate:yyyy-MM-dd} 卖出 {txn.Symbol} {txn.Quantity} 份，超过当时持仓 {acc.Quantity} 份。" +
                "若这是建账前就持有的份额，请先补一条期初持仓。");
        }

        var averageCost = acc.Quantity == 0m ? 0m : acc.CostBasis / acc.Quantity;
        var releasedCost = averageCost * txn.Quantity;
        var proceeds = txn.Quantity * txn.Price - txn.Fee - txn.Tax;

        acc.RealizedPnl += proceeds - releasedCost;
        acc.CostBasis -= releasedCost;
        acc.Quantity -= txn.Quantity;
        acc.Fees += txn.Fee;
        acc.Taxes += txn.Tax;

        if (acc.Quantity == 0m)
        {
            // Guard against residue from repeated division.
            acc.CostBasis = 0m;
        }
    }

    private static void ApplyStockDividend(
        Dictionary<(string, string), PositionAccumulator> positions, LedgerTransaction txn)
    {
        var acc = Accumulator(positions, txn);
        if (acc.Quantity <= 0m)
        {
            throw new LedgerException($"{txn.TradeDate:yyyy-MM-dd} {txn.Symbol} 无持仓，无法登记送股。");
        }

        // Units go up, total cost stays put, so average cost falls.
        acc.Quantity += txn.Quantity;
    }

    private static void ApplySplit(
        Dictionary<(string, string), PositionAccumulator> positions, LedgerTransaction txn)
    {
        var acc = Accumulator(positions, txn);
        if (acc.Quantity <= 0m)
        {
            throw new LedgerException($"{txn.TradeDate:yyyy-MM-dd} {txn.Symbol} 无持仓，无法登记拆股。");
        }

        acc.Quantity *= txn.Quantity;
    }

    private static void ApplyCharge(
        Dictionary<(string, string), PositionAccumulator> positions, LedgerTransaction txn)
    {
        if (string.IsNullOrEmpty(txn.Symbol))
        {
            return;
        }

        var acc = Accumulator(positions, txn);
        if (txn.Kind == TransactionKind.Fee)
        {
            acc.Fees += txn.CashAmount;
        }
        else
        {
            acc.Taxes += txn.CashAmount;
        }

        acc.RealizedPnl -= txn.CashAmount;
    }

    private static PositionAccumulator Accumulator(
        Dictionary<(string, string), PositionAccumulator> positions, LedgerTransaction txn)
    {
        if (string.IsNullOrEmpty(txn.Symbol))
        {
            throw new LedgerException($"{txn.Kind} 流水缺少标的代码。");
        }

        var key = (txn.AccountId, txn.Symbol);
        if (!positions.TryGetValue(key, out var acc))
        {
            acc = new PositionAccumulator(txn.AccountId, txn.Symbol, txn.Currency);
            positions[key] = acc;
        }
        else if (!string.Equals(acc.Currency, txn.Currency, StringComparison.Ordinal))
        {
            throw new LedgerException(
                $"{txn.Symbol} 在同一账户下出现了两种计价货币（{acc.Currency} 与 {txn.Currency}），请检查流水。");
        }

        return acc;
    }

    private static void AddCash(
        Dictionary<(string Account, string Currency), decimal> cash,
        string accountId,
        string currency,
        decimal delta)
    {
        var key = (accountId, Currencies.Normalize(currency));
        cash[key] = cash.TryGetValue(key, out var current) ? current + delta : delta;
    }

    private sealed class PositionAccumulator(string accountId, string symbol, string currency)
    {
        public string AccountId { get; } = accountId;
        public string Symbol { get; } = symbol;
        public string Currency { get; } = currency;
        public decimal Quantity { get; set; }
        public decimal CostBasis { get; set; }
        public decimal RealizedPnl { get; set; }
        public decimal Dividends { get; set; }
        public decimal Fees { get; set; }
        public decimal Taxes { get; set; }

        public Position ToPosition() => new()
        {
            AccountId = AccountId,
            Symbol = Symbol,
            Currency = Currency,
            Quantity = Quantity,
            CostBasis = Money.Of(CostBasis, Currency),
            RealizedPnl = Money.Of(RealizedPnl, Currency),
            Dividends = Money.Of(Dividends, Currency),
            Fees = Money.Of(Fees, Currency),
            Taxes = Money.Of(Taxes, Currency),
        };
    }
}
