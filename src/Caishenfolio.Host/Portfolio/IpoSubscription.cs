using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

public enum IpoStatus
{
    /// <summary>申购已提交，等待摇号结果。</summary>
    Subscribed,
    /// <summary>未中签，本次结束。</summary>
    NotAllotted,
    /// <summary>已中签，等待缴款。</summary>
    Allotted,
    /// <summary>已缴款，持有待上市。</summary>
    Paid,
    /// <summary>已卖出。</summary>
    Sold,
    /// <summary>中签后放弃缴款。</summary>
    Abandoned,
}

/// <summary>
/// One IPO subscription from application through to sale.
///
/// This is bookkeeping, not prediction: the outcome is a lottery the app cannot forecast. What
/// it can do is record every application and tell you honestly what the whole activity earned —
/// including the applications that came to nothing, which is where casual tracking flatters
/// itself by only remembering the wins.
/// </summary>
public sealed record IpoSubscription
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public string Name { get; init; } = "";
    public required DateOnly SubscribeDate { get; init; }
    public required IpoStatus Status { get; init; }
    public required string Currency { get; init; }

    /// <summary>Units applied for.</summary>
    public decimal SubscribedQuantity { get; init; }
    /// <summary>Units actually allotted; 0 until the draw, and stays 0 when unsuccessful.</summary>
    public decimal AllottedQuantity { get; init; }
    /// <summary>Price per unit set by the issue.</summary>
    public decimal IssuePrice { get; init; }

    public DateOnly? PaymentDate { get; init; }
    public DateOnly? ListingDate { get; init; }
    public DateOnly? SoldDate { get; init; }
    public decimal SoldPrice { get; init; }
    public decimal Fee { get; init; }
    public string Note { get; init; } = "";

    public bool IsAllotted => AllottedQuantity > 0m;
    public bool IsClosed => Status is IpoStatus.NotAllotted or IpoStatus.Sold or IpoStatus.Abandoned;

    /// <summary>Cash paid at subscription; zero unless the allotment was actually paid for.</summary>
    public Money Cost => Money.Of(
        Status is IpoStatus.Paid or IpoStatus.Sold ? AllottedQuantity * IssuePrice : 0m,
        Currency);

    /// <summary>Realized profit, or null while the position is still open or never existed.</summary>
    public Money? RealizedProfit =>
        Status != IpoStatus.Sold
            ? null
            : Money.Of(AllottedQuantity * (SoldPrice - IssuePrice) - Fee, Currency);

    public static IpoSubscription Create(
        string accountId,
        string symbol,
        DateOnly subscribeDate,
        decimal subscribedQuantity,
        decimal issuePrice,
        string currency,
        string name = "",
        string note = "",
        string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (!SymbolId.TryParse(symbol, out var parsed))
        {
            throw new LedgerException($"标的代码 '{symbol}' 不是 交易所:代码 形式。");
        }

        if (subscribedQuantity <= 0m)
        {
            throw new LedgerException($"申购数量必须大于 0（收到 {subscribedQuantity}）。");
        }

        if (issuePrice <= 0m)
        {
            throw new LedgerException($"发行价必须大于 0（收到 {issuePrice}）。");
        }

        return new IpoSubscription
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"ipo_{Guid.NewGuid():N}" : id!.Trim(),
            AccountId = accountId.Trim(),
            Symbol = parsed.Normalized().Value,
            Name = name.Trim(),
            SubscribeDate = subscribeDate,
            Status = IpoStatus.Subscribed,
            Currency = Currencies.Normalize(currency),
            SubscribedQuantity = subscribedQuantity,
            IssuePrice = issuePrice,
            Note = note.Trim(),
        };
    }

    /// <summary>Records the draw result. Zero allotted is a valid, and common, outcome.</summary>
    public IpoSubscription WithAllotment(decimal allottedQuantity)
    {
        if (allottedQuantity < 0m)
        {
            throw new LedgerException($"中签数量不能为负（收到 {allottedQuantity}）。");
        }

        if (allottedQuantity > SubscribedQuantity)
        {
            throw new LedgerException(
                $"中签数量 {allottedQuantity} 不能超过申购数量 {SubscribedQuantity}。");
        }

        return this with
        {
            AllottedQuantity = allottedQuantity,
            Status = allottedQuantity > 0m ? IpoStatus.Allotted : IpoStatus.NotAllotted,
        };
    }

    public IpoSubscription WithPayment(DateOnly paymentDate, DateOnly? listingDate = null)
    {
        if (!IsAllotted)
        {
            throw new LedgerException("未中签的申购无需缴款。");
        }

        return this with
        {
            Status = IpoStatus.Paid,
            PaymentDate = paymentDate,
            ListingDate = listingDate ?? ListingDate,
        };
    }

    public IpoSubscription WithAbandonment() =>
        IsAllotted
            ? this with { Status = IpoStatus.Abandoned }
            : throw new LedgerException("未中签的申购无所谓放弃。");

    public IpoSubscription WithSale(DateOnly soldDate, decimal soldPrice, decimal fee = 0m)
    {
        if (Status != IpoStatus.Paid)
        {
            throw new LedgerException("只有已缴款的中签才能卖出。");
        }

        if (soldPrice < 0m || fee < 0m)
        {
            throw new LedgerException("卖出价与费用不能为负。");
        }

        return this with
        {
            Status = IpoStatus.Sold,
            SoldDate = soldDate,
            SoldPrice = soldPrice,
            Fee = fee,
        };
    }

    /// <summary>
    /// The ledger entries this subscription implies, so an allotment becomes a real holding
    /// instead of a number tracked in a second place that can drift from the portfolio.
    /// </summary>
    public IReadOnlyList<LedgerTransaction> ToLedgerTransactions()
    {
        var result = new List<LedgerTransaction>();
        if (Status is not (IpoStatus.Paid or IpoStatus.Sold) || AllottedQuantity <= 0m)
        {
            return result;
        }

        var buyDate = PaymentDate ?? SubscribeDate;
        result.Add(LedgerTransaction.Buy(
            AccountId, Symbol, buyDate, AllottedQuantity, IssuePrice, Currency,
            note: $"打新中签 {Name}".Trim()) with { Id = $"txn_ipo_buy_{Id}" });

        if (Status == IpoStatus.Sold && SoldDate is { } soldDate)
        {
            result.Add(LedgerTransaction.Sell(
                AccountId, Symbol, soldDate, AllottedQuantity, SoldPrice, Currency, Fee,
                note: $"打新卖出 {Name}".Trim()) with { Id = $"txn_ipo_sell_{Id}" });
        }

        return result;
    }
}

/// <summary>What the whole activity actually earned, applications that failed included.</summary>
public sealed record IpoStatistics
{
    public required int Subscriptions { get; init; }
    public required int Allotments { get; init; }
    public required int Sold { get; init; }
    public required string Currency { get; init; }
    public required Money RealizedProfit { get; init; }
    public required Money Cost { get; init; }

    /// <summary>Share of applications that were allotted anything. Null before any draw resolved.</summary>
    public decimal? HitRate { get; init; }
    /// <summary>Average realized profit per sold allotment.</summary>
    public Money? AveragePerAllotment { get; init; }

    public static IpoStatistics From(IEnumerable<IpoSubscription> subscriptions, string currency)
    {
        var items = subscriptions
            .Where(s => string.Equals(s.Currency, currency, StringComparison.Ordinal))
            .ToArray();

        // Only resolved draws count toward a hit rate; pending ones are not misses yet.
        var resolved = items.Where(s => s.Status != IpoStatus.Subscribed).ToArray();
        var allotted = items.Count(s => s.IsAllotted);
        var sold = items.Where(s => s.Status == IpoStatus.Sold).ToArray();

        var profit = Money.Sum(sold.Select(s => s.RealizedProfit!.Value), currency);

        return new IpoStatistics
        {
            Subscriptions = items.Length,
            Allotments = allotted,
            Sold = sold.Length,
            Currency = currency,
            RealizedProfit = profit,
            Cost = Money.Sum(items.Select(s => s.Cost), currency),
            HitRate = resolved.Length == 0 ? null : (decimal)allotted / resolved.Length,
            AveragePerAllotment = sold.Length == 0 ? null : profit / sold.Length,
        };
    }
}
