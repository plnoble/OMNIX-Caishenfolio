using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

public enum TransactionKind
{
    /// <summary>买入 / 场外基金申购（价格即净值）。</summary>
    Buy,
    /// <summary>卖出 / 场外基金赎回。</summary>
    Sell,
    /// <summary>现金分红。</summary>
    Dividend,
    /// <summary>送股 / 转股：份额增加，成本不变。</summary>
    StockDividend,
    /// <summary>拆股 / 合股：份额按比例变化，总成本不变。</summary>
    Split,
    /// <summary>债券票息 / 存款利息。</summary>
    Interest,
    /// <summary>外部资金转入（影响资金时间加权收益）。</summary>
    Deposit,
    /// <summary>外部资金转出。</summary>
    Withdraw,
    /// <summary>独立费用（管理费/托管费/平台费）。</summary>
    Fee,
    /// <summary>独立税费。</summary>
    Tax,
    /// <summary>换汇：一种货币出，另一种货币进。</summary>
    FxExchange,
    /// <summary>建账时的期初持仓（份额 + 平均成本，无现金腿）。</summary>
    OpeningPosition,
    /// <summary>建账时的期初现金。</summary>
    OpeningCash,
}

/// <summary>Raised when a transaction or a sequence of transactions cannot be a real ledger fact.</summary>
public sealed class LedgerException : Exception
{
    public LedgerException(string message) : base(message)
    {
    }
}

/// <summary>
/// One append-only fact in the ledger. Monetary fields are decimal plus an explicit
/// <see cref="Currency"/>; <see cref="Money"/> is used once values enter aggregation.
/// </summary>
public sealed record LedgerTransaction
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required TransactionKind Kind { get; init; }
    public required DateOnly TradeDate { get; init; }
    /// <summary>Empty for account-level cash movements that are not tied to an instrument.</summary>
    public string Symbol { get; init; } = "";
    /// <summary>Units for trades, ratio for <see cref="TransactionKind.Split"/>, 0 for pure cash events.</summary>
    public decimal Quantity { get; init; }
    /// <summary>Price or NAV per unit.</summary>
    public decimal Price { get; init; }
    public required string Currency { get; init; }
    public decimal Fee { get; init; }
    public decimal Tax { get; init; }
    /// <summary>Explicit cash leg for events where quantity × price does not describe the money moved.</summary>
    public decimal CashAmount { get; init; }
    /// <summary>Target currency of an <see cref="TransactionKind.FxExchange"/>.</summary>
    public string CounterCurrency { get; init; } = "";
    /// <summary>
    /// Amount actually credited in <see cref="CounterCurrency"/>. Stored rather than derived,
    /// because a rate like 1/7.2 has no exact decimal form and would leave residue in the balance.
    /// </summary>
    public decimal CounterAmount { get; init; }
    /// <summary>Units of <see cref="CounterCurrency"/> per unit of <see cref="Currency"/>; display only.</summary>
    public decimal FxRate { get; init; }
    public string Note { get; init; } = "";
    public string ImportBatchId { get; init; } = "";
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;

    public Money FeeMoney => Money.Of(Fee, Currency);
    public Money TaxMoney => Money.Of(Tax, Currency);

    /// <summary>Gross trade value before costs.</summary>
    public Money GrossAmount => Money.Of(Quantity * Price, Currency);

    /// <summary>True when the event moves money in or out of the portfolio boundary (drives XIRR).</summary>
    public bool IsExternalFlow =>
        Kind is TransactionKind.Deposit or TransactionKind.Withdraw
            or TransactionKind.OpeningCash or TransactionKind.OpeningPosition;

    // --- factories -----------------------------------------------------------------

    public static LedgerTransaction Buy(
        string accountId, string symbol, DateOnly tradeDate,
        decimal quantity, decimal price, string currency,
        decimal fee = 0m, decimal tax = 0m, string note = "") =>
        Trade(TransactionKind.Buy, accountId, symbol, tradeDate, quantity, price, currency, fee, tax, note);

    public static LedgerTransaction Sell(
        string accountId, string symbol, DateOnly tradeDate,
        decimal quantity, decimal price, string currency,
        decimal fee = 0m, decimal tax = 0m, string note = "") =>
        Trade(TransactionKind.Sell, accountId, symbol, tradeDate, quantity, price, currency, fee, tax, note);

    public static LedgerTransaction OpeningPosition(
        string accountId, string symbol, DateOnly asOf,
        decimal quantity, decimal averageCost, string currency, string note = "") =>
        Trade(TransactionKind.OpeningPosition, accountId, symbol, asOf, quantity, averageCost, currency, 0m, 0m, note);

    public static LedgerTransaction Dividend(
        string accountId, string symbol, DateOnly payDate,
        decimal amount, string currency, decimal tax = 0m, string note = "") =>
        CashEvent(TransactionKind.Dividend, accountId, symbol, payDate, amount, currency, tax, note);

    public static LedgerTransaction Interest(
        string accountId, DateOnly payDate, decimal amount, string currency,
        string symbol = "", decimal tax = 0m, string note = "") =>
        CashEvent(TransactionKind.Interest, accountId, symbol, payDate, amount, currency, tax, note);

    public static LedgerTransaction Deposit(
        string accountId, DateOnly date, decimal amount, string currency, string note = "") =>
        CashEvent(TransactionKind.Deposit, accountId, "", date, amount, currency, 0m, note);

    public static LedgerTransaction Withdraw(
        string accountId, DateOnly date, decimal amount, string currency, string note = "") =>
        CashEvent(TransactionKind.Withdraw, accountId, "", date, amount, currency, 0m, note);

    public static LedgerTransaction OpeningCash(
        string accountId, DateOnly asOf, decimal amount, string currency, string note = "") =>
        CashEvent(TransactionKind.OpeningCash, accountId, "", asOf, amount, currency, 0m, note);

    public static LedgerTransaction Charge(
        TransactionKind kind, string accountId, DateOnly date,
        decimal amount, string currency, string symbol = "", string note = "")
    {
        if (kind is not (TransactionKind.Fee or TransactionKind.Tax))
        {
            throw new LedgerException($"{kind} 不是费用类流水。");
        }

        return CashEvent(kind, accountId, symbol, date, amount, currency, 0m, note);
    }

    public static LedgerTransaction StockDividend(
        string accountId, string symbol, DateOnly date, decimal quantity, string currency, string note = "")
    {
        RequirePositive(quantity, "送股份额");
        return New(TransactionKind.StockDividend, accountId, symbol, date, currency) with
        {
            Quantity = quantity,
            Note = note.Trim(),
        };
    }

    /// <summary>Split ratio: 2 means each old unit becomes two; 0.5 means a reverse split.</summary>
    public static LedgerTransaction Split(
        string accountId, string symbol, DateOnly date, decimal ratio, string currency, string note = "")
    {
        RequirePositive(ratio, "拆股比例");
        return New(TransactionKind.Split, accountId, symbol, date, currency) with
        {
            Quantity = ratio,
            Note = note.Trim(),
        };
    }

    /// <summary>
    /// Records an exchange by the two amounts on the receipt — the precise way to book it,
    /// since the implied rate rarely has an exact decimal form.
    /// </summary>
    public static LedgerTransaction FxExchange(
        string accountId, DateOnly date,
        decimal fromAmount, string fromCurrency,
        decimal toAmount, string toCurrency,
        decimal fee = 0m, string note = "")
    {
        RequirePositive(toAmount, "换汇入账金额");
        return FxLeg(accountId, date, fromAmount, fromCurrency, toCurrency, toAmount, toAmount / fromAmount, fee, note);
    }

    /// <summary>Records an exchange by rate: units of <paramref name="toCurrency"/> per unit of <paramref name="fromCurrency"/>.</summary>
    public static LedgerTransaction FxExchangeAtRate(
        string accountId, DateOnly date,
        decimal fromAmount, string fromCurrency,
        string toCurrency, decimal rate,
        decimal fee = 0m, string note = "")
    {
        RequirePositive(rate, "换汇汇率");
        return FxLeg(accountId, date, fromAmount, fromCurrency, toCurrency, fromAmount * rate, rate, fee, note);
    }

    private static LedgerTransaction FxLeg(
        string accountId, DateOnly date,
        decimal fromAmount, string fromCurrency,
        string toCurrency, decimal toAmount, decimal rate,
        decimal fee, string note)
    {
        RequirePositive(fromAmount, "换汇金额");
        RequireNonNegative(fee, "手续费");
        var from = Currencies.Normalize(fromCurrency);
        var to = Currencies.Normalize(toCurrency);
        if (from == to)
        {
            throw new LedgerException("换汇的两种货币不能相同。");
        }

        return New(TransactionKind.FxExchange, accountId, "", date, from) with
        {
            CashAmount = fromAmount,
            CounterCurrency = to,
            CounterAmount = toAmount,
            FxRate = rate,
            Fee = fee,
            Note = note.Trim(),
        };
    }

    // --- helpers -------------------------------------------------------------------

    private static LedgerTransaction Trade(
        TransactionKind kind, string accountId, string symbol, DateOnly tradeDate,
        decimal quantity, decimal price, string currency, decimal fee, decimal tax, string note)
    {
        RequirePositive(quantity, "数量");
        RequireNonNegative(price, "价格");
        RequireNonNegative(fee, "手续费");
        RequireNonNegative(tax, "税费");
        return New(kind, accountId, symbol, tradeDate, currency) with
        {
            Quantity = quantity,
            Price = price,
            Fee = fee,
            Tax = tax,
            Note = note.Trim(),
        };
    }

    private static LedgerTransaction CashEvent(
        TransactionKind kind, string accountId, string symbol, DateOnly date,
        decimal amount, string currency, decimal tax, string note)
    {
        RequirePositive(amount, "金额");
        RequireNonNegative(tax, "税费");
        return New(kind, accountId, symbol, date, currency) with
        {
            CashAmount = amount,
            Tax = tax,
            Note = note.Trim(),
        };
    }

    private static LedgerTransaction New(
        TransactionKind kind, string accountId, string symbol, DateOnly date, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var resolvedSymbol = "";
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            if (!SymbolId.TryParse(symbol, out var parsed))
            {
                throw new LedgerException($"标的代码 '{symbol}' 不是 交易所:代码 形式。");
            }

            resolvedSymbol = parsed.Normalized().Value;
        }
        else if (RequiresSymbol(kind))
        {
            throw new LedgerException($"{kind} 流水必须指定标的。");
        }

        return new LedgerTransaction
        {
            Id = $"txn_{Guid.NewGuid():N}",
            AccountId = accountId.Trim(),
            Kind = kind,
            TradeDate = date,
            Symbol = resolvedSymbol,
            Currency = Currencies.Normalize(currency),
            RecordedAt = DateTimeOffset.UtcNow,
        };
    }

    private static bool RequiresSymbol(TransactionKind kind) =>
        kind is TransactionKind.Buy or TransactionKind.Sell or TransactionKind.Dividend
            or TransactionKind.StockDividend or TransactionKind.Split or TransactionKind.OpeningPosition;

    private static void RequirePositive(decimal value, string label)
    {
        if (value <= 0m)
        {
            throw new LedgerException($"{label}必须大于 0（收到 {value}）。");
        }
    }

    private static void RequireNonNegative(decimal value, string label)
    {
        if (value < 0m)
        {
            throw new LedgerException($"{label}不能为负数（收到 {value}）。");
        }
    }
}
