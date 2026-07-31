using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>One parsed CSV line: either a transaction, or the reason it could not become one.</summary>
public sealed record CsvImportRow
{
    public required int LineNumber { get; init; }
    public LedgerTransaction? Transaction { get; init; }
    public string? Error { get; init; }
    /// <summary>True when this exact row is already in the ledger.</summary>
    public bool Duplicate { get; init; }

    public bool Usable => Transaction is not null && Error is null && !Duplicate;
}

/// <summary>A dry run: what would be imported, what would be skipped, and why.</summary>
public sealed record CsvImportPreview
{
    public required string BatchId { get; init; }
    public required IReadOnlyList<CsvImportRow> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public int Importable => Rows.Count(r => r.Usable);
    public int Duplicates => Rows.Count(r => r.Duplicate);
    public int Invalid => Rows.Count(r => r.Error is not null);
    public bool HasErrors => Invalid > 0;

    public IReadOnlyList<LedgerTransaction> Transactions =>
        Rows.Where(r => r.Usable).Select(r => r.Transaction!).ToArray();
}

/// <summary>
/// Imports a transaction statement from CSV/TSV.
///
/// Every row is validated before anything is written, so a malformed line at the bottom of a
/// statement cannot leave half an import behind. Row ids are derived from the row's own content,
/// which makes re-importing the same file a no-op instead of a duplicated position.
/// </summary>
public static class TransactionCsvImporter
{
    /// <summary>Header the export template writes and the importer always understands.</summary>
    public static IReadOnlyList<string> TemplateHeader { get; } =
    [
        "日期", "账户", "类型", "标的", "名称", "数量", "价格", "货币", "手续费", "税费", "金额", "备注",
    ];

    private static readonly Dictionary<string, string> ColumnAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["日期"] = "date", ["交易日期"] = "date", ["成交日期"] = "date", ["date"] = "date", ["trade_date"] = "date",
        ["账户"] = "account", ["账户名称"] = "account", ["account"] = "account", ["account_id"] = "account",
        ["类型"] = "kind", ["交易类型"] = "kind", ["方向"] = "kind", ["操作"] = "kind",
        ["kind"] = "kind", ["type"] = "kind", ["side"] = "kind", ["action"] = "kind",
        ["标的"] = "symbol", ["代码"] = "symbol", ["证券代码"] = "symbol", ["symbol"] = "symbol", ["code"] = "symbol",
        ["名称"] = "name", ["证券名称"] = "name", ["name"] = "name",
        ["数量"] = "quantity", ["份额"] = "quantity", ["股数"] = "quantity",
        ["quantity"] = "quantity", ["qty"] = "quantity", ["shares"] = "quantity", ["units"] = "quantity",
        ["价格"] = "price", ["成交价"] = "price", ["单价"] = "price", ["净值"] = "price",
        ["price"] = "price", ["nav"] = "price",
        ["货币"] = "currency", ["币种"] = "currency", ["currency"] = "currency", ["ccy"] = "currency",
        ["手续费"] = "fee", ["佣金"] = "fee", ["费用"] = "fee", ["fee"] = "fee", ["commission"] = "fee",
        ["税费"] = "tax", ["印花税"] = "tax", ["tax"] = "tax",
        ["金额"] = "amount", ["发生额"] = "amount", ["现金"] = "amount", ["amount"] = "amount", ["cash"] = "amount",
        ["备注"] = "note", ["摘要"] = "note", ["note"] = "note", ["memo"] = "note", ["remark"] = "note",
        ["对方货币"] = "counter_currency", ["counter_currency"] = "counter_currency",
        ["对方金额"] = "counter_amount", ["counter_amount"] = "counter_amount",
    };

    private static readonly Dictionary<string, TransactionKind> KindAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["买入"] = TransactionKind.Buy, ["买"] = TransactionKind.Buy, ["证券买入"] = TransactionKind.Buy,
        ["申购"] = TransactionKind.Buy, ["认购"] = TransactionKind.Buy, ["定投"] = TransactionKind.Buy,
        ["buy"] = TransactionKind.Buy, ["b"] = TransactionKind.Buy, ["purchase"] = TransactionKind.Buy,
        ["卖出"] = TransactionKind.Sell, ["卖"] = TransactionKind.Sell, ["证券卖出"] = TransactionKind.Sell,
        ["赎回"] = TransactionKind.Sell, ["sell"] = TransactionKind.Sell, ["s"] = TransactionKind.Sell,
        ["分红"] = TransactionKind.Dividend, ["现金分红"] = TransactionKind.Dividend,
        ["股息"] = TransactionKind.Dividend, ["dividend"] = TransactionKind.Dividend,
        ["送股"] = TransactionKind.StockDividend, ["转股"] = TransactionKind.StockDividend,
        ["stock_dividend"] = TransactionKind.StockDividend,
        ["拆股"] = TransactionKind.Split, ["split"] = TransactionKind.Split,
        ["利息"] = TransactionKind.Interest, ["票息"] = TransactionKind.Interest,
        ["interest"] = TransactionKind.Interest, ["coupon"] = TransactionKind.Interest,
        ["入金"] = TransactionKind.Deposit, ["转入"] = TransactionKind.Deposit,
        ["银证转入"] = TransactionKind.Deposit, ["deposit"] = TransactionKind.Deposit,
        ["出金"] = TransactionKind.Withdraw, ["转出"] = TransactionKind.Withdraw,
        ["银证转出"] = TransactionKind.Withdraw, ["withdraw"] = TransactionKind.Withdraw,
        ["费用"] = TransactionKind.Fee, ["管理费"] = TransactionKind.Fee, ["fee"] = TransactionKind.Fee,
        ["税"] = TransactionKind.Tax, ["税费"] = TransactionKind.Tax, ["tax"] = TransactionKind.Tax,
        ["换汇"] = TransactionKind.FxExchange, ["结汇"] = TransactionKind.FxExchange,
        ["购汇"] = TransactionKind.FxExchange, ["fx"] = TransactionKind.FxExchange,
        ["期初持仓"] = TransactionKind.OpeningPosition, ["opening_position"] = TransactionKind.OpeningPosition,
        ["期初现金"] = TransactionKind.OpeningCash, ["opening_cash"] = TransactionKind.OpeningCash,
    };

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/M/d", "yyyy.MM.dd", "yyyyMMdd", "d/M/yyyy", "MM/dd/yyyy",
    ];

    /// <summary>Blank template with the header and one example row per common kind.</summary>
    public static string BuildTemplate() => DelimitedText.Write(
    [
        TemplateHeader,
        ["2026-01-05", "华泰证券", "买入", "SSE:600000", "浦发银行", "1000", "10.05", "CNY", "5", "0", "", "首建仓"],
        ["2026-02-09", "华泰证券", "卖出", "SSE:600000", "浦发银行", "400", "12.00", "CNY", "4", "6", "", ""],
        ["2026-03-10", "华泰证券", "分红", "SSE:600000", "浦发银行", "", "", "CNY", "", "20", "320", "每10股3元"],
        ["2026-01-02", "华泰证券", "入金", "", "", "", "", "CNY", "", "", "50000", "银证转入"],
        ["2026-01-05", "天天基金", "买入", "FUND:110022", "易方达消费行业", "1000.25", "3.5012", "CNY", "1.5", "", "", "定投"],
        ["2026-01-06", "盈透证券", "期初持仓", "NASDAQ:AAPL", "Apple", "10", "180", "USD", "", "", "", "建账前持有"],
    ]);

    /// <summary>
    /// Parses and validates without writing. <paramref name="store"/> is only read, to mark rows
    /// that are already present.
    /// </summary>
    public static CsvImportPreview Preview(
        string csvText,
        string defaultAccountId,
        PortfolioStore? store = null,
        string? defaultCurrency = null,
        IReadOnlyDictionary<string, string>? accountNameToId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultAccountId);

        var batchId = $"csv_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];
        var warnings = new List<string>();
        var rows = DelimitedText.Parse(csvText);
        if (rows.Count == 0)
        {
            return new CsvImportPreview { BatchId = batchId, Rows = [], Warnings = ["文件为空。"] };
        }

        var columns = MapColumns(rows[0], warnings);
        if (!columns.ContainsKey("date") || !columns.ContainsKey("kind"))
        {
            return new CsvImportPreview
            {
                BatchId = batchId,
                Rows = [],
                Warnings = [$"表头缺少必需列（日期、类型）。已识别：{string.Join("、", columns.Keys)}。" +
                            $"模板表头：{string.Join("、", TemplateHeader)}"],
            };
        }

        var existingIds = store?.ListTransactions().Select(t => t.Id).ToHashSet(StringComparer.Ordinal)
                          ?? new HashSet<string>(StringComparer.Ordinal);
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<CsvImportRow>();

        for (var i = 1; i < rows.Count; i++)
        {
            var cells = rows[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var lineNumber = i + 1;
            try
            {
                var transaction = BuildTransaction(
                    cells, columns, defaultAccountId, defaultCurrency, accountNameToId, batchId);
                var duplicate = !existingIds.Add(transaction.Id) || !seenInFile.Add(transaction.Id);
                parsed.Add(new CsvImportRow
                {
                    LineNumber = lineNumber,
                    Transaction = transaction,
                    Duplicate = duplicate,
                });
            }
            catch (Exception ex) when (ex is LedgerException or ArgumentException or FormatException)
            {
                parsed.Add(new CsvImportRow { LineNumber = lineNumber, Error = ex.Message });
            }
        }

        return new CsvImportPreview { BatchId = batchId, Rows = parsed, Warnings = warnings };
    }

    /// <summary>
    /// Writes the importable rows. Refuses a preview containing errors unless
    /// <paramref name="skipInvalidRows"/> is set, so a partial import is always a deliberate choice.
    /// </summary>
    public static int Commit(CsvImportPreview preview, PortfolioStore store, bool skipInvalidRows = false)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(store);

        if (preview.HasErrors && !skipInvalidRows)
        {
            throw new LedgerException(
                $"有 {preview.Invalid} 行无法解析。请修正后重试，或显式选择跳过错误行。");
        }

        var transactions = preview.Transactions;
        return transactions.Count == 0 ? 0 : store.AddTransactions(transactions);
    }

    private static Dictionary<string, int> MapColumns(IReadOnlyList<string> header, List<string> warnings)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var raw = header[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            if (ColumnAliases.TryGetValue(raw, out var canonical))
            {
                columns.TryAdd(canonical, i);
            }
            else
            {
                warnings.Add($"忽略未知列「{raw}」。");
            }
        }

        return columns;
    }

    private static LedgerTransaction BuildTransaction(
        string[] cells,
        Dictionary<string, int> columns,
        string defaultAccountId,
        string? defaultCurrency,
        IReadOnlyDictionary<string, string>? accountNameToId,
        string batchId)
    {
        var kindText = Cell(cells, columns, "kind");
        if (!KindAliases.TryGetValue(kindText.Trim(), out var kind))
        {
            throw new LedgerException($"无法识别的交易类型「{kindText}」。");
        }

        var date = ParseDate(Cell(cells, columns, "date"));
        var accountId = ResolveAccount(Cell(cells, columns, "account"), defaultAccountId, accountNameToId);
        var symbol = Cell(cells, columns, "symbol").Trim();
        var currency = Cell(cells, columns, "currency").Trim();
        if (currency.Length == 0)
        {
            currency = defaultCurrency ?? InferCurrency(symbol)
                ?? throw new LedgerException("缺少货币列，且无法从标的推断计价货币。");
        }

        var quantity = ParseDecimal(Cell(cells, columns, "quantity"));
        var price = ParseDecimal(Cell(cells, columns, "price"));
        var fee = ParseDecimal(Cell(cells, columns, "fee"));
        var tax = ParseDecimal(Cell(cells, columns, "tax"));
        var amount = ParseDecimal(Cell(cells, columns, "amount"));
        var note = Cell(cells, columns, "note").Trim();

        var transaction = kind switch
        {
            TransactionKind.Buy =>
                LedgerTransaction.Buy(accountId, symbol, date, quantity, ResolvePrice(price, amount, quantity), currency, fee, tax, note),
            TransactionKind.Sell =>
                LedgerTransaction.Sell(accountId, symbol, date, quantity, ResolvePrice(price, amount, quantity), currency, fee, tax, note),
            TransactionKind.OpeningPosition =>
                LedgerTransaction.OpeningPosition(accountId, symbol, date, quantity, ResolvePrice(price, amount, quantity), currency, note),
            TransactionKind.Dividend =>
                LedgerTransaction.Dividend(accountId, symbol, date, RequireAmount(amount, kind), currency, tax, note),
            TransactionKind.Interest =>
                LedgerTransaction.Interest(accountId, date, RequireAmount(amount, kind), currency, symbol, tax, note),
            TransactionKind.Deposit =>
                LedgerTransaction.Deposit(accountId, date, RequireAmount(amount, kind), currency, note),
            TransactionKind.Withdraw =>
                LedgerTransaction.Withdraw(accountId, date, RequireAmount(amount, kind), currency, note),
            TransactionKind.OpeningCash =>
                LedgerTransaction.OpeningCash(accountId, date, RequireAmount(amount, kind), currency, note),
            TransactionKind.StockDividend =>
                LedgerTransaction.StockDividend(accountId, symbol, date, quantity, currency, note),
            TransactionKind.Split =>
                LedgerTransaction.Split(accountId, symbol, date, quantity, currency, note),
            TransactionKind.Fee or TransactionKind.Tax =>
                LedgerTransaction.Charge(kind, accountId, date, RequireAmount(amount != 0m ? amount : fee + tax, kind), currency, symbol, note),
            TransactionKind.FxExchange =>
                LedgerTransaction.FxExchange(
                    accountId, date, RequireAmount(amount, kind), currency,
                    ParseDecimal(Cell(cells, columns, "counter_amount")),
                    Cell(cells, columns, "counter_currency"), fee, note),
            _ => throw new LedgerException($"暂不支持导入 {kind} 类型。"),
        };

        return transaction with
        {
            Id = DeterministicId(transaction),
            ImportBatchId = batchId,
            // Ordering must not depend on when the import happened to run.
            RecordedAt = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        };
    }

    /// <summary>Price per unit, derived from the total when a statement only records the amount.</summary>
    private static decimal ResolvePrice(decimal price, decimal amount, decimal quantity) =>
        price > 0m || quantity == 0m ? price : amount / quantity;

    private static decimal RequireAmount(decimal amount, TransactionKind kind) =>
        amount > 0m ? amount : throw new LedgerException($"{kind} 需要金额列且必须大于 0。");

    private static string ResolveAccount(
        string cell, string defaultAccountId, IReadOnlyDictionary<string, string>? accountNameToId)
    {
        var value = cell.Trim();
        if (value.Length == 0)
        {
            return defaultAccountId;
        }

        return accountNameToId is not null && accountNameToId.TryGetValue(value, out var id) ? id : value;
    }

    private static string? InferCurrency(string symbol) =>
        SymbolId.TryParse(symbol, out var parsed)
        && ExchangeRegistry.TryGetQuoteCurrency(parsed.Normalized(), out var currency)
            ? currency
            : null;

    private static string Cell(string[] cells, Dictionary<string, int> columns, string key) =>
        columns.TryGetValue(key, out var index) && index < cells.Length ? cells[index] : "";

    private static DateOnly ParseDate(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            throw new LedgerException("缺少日期。");
        }

        // Statements sometimes carry a time component; the ledger only needs the trade date.
        var datePart = text.Split(' ', 'T')[0];
        if (DateOnly.TryParseExact(datePart, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        if (DateOnly.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
        {
            return loose;
        }

        throw new LedgerException($"无法解析日期「{text}」。支持 yyyy-MM-dd、yyyy/M/d、yyyyMMdd 等。");
    }

    private static decimal ParseDecimal(string value)
    {
        var text = value.Trim().Replace(",", "").Replace("，", "").Replace("¥", "").Replace("$", "");
        if (text.Length == 0 || text == "-")
        {
            return 0m;
        }

        // A statement writes an outflow as a negative number; the ledger encodes direction in the kind.
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Abs(parsed);
        }

        throw new LedgerException($"无法解析数字「{value}」。");
    }

    /// <summary>
    /// Id derived from the row's economic content, so importing the same statement twice
    /// collides on the primary key instead of duplicating the position.
    /// </summary>
    private static string DeterministicId(LedgerTransaction txn)
    {
        var seed = string.Join('|',
            txn.AccountId, txn.Kind, txn.TradeDate.ToString("yyyy-MM-dd"), txn.Symbol,
            txn.Quantity.ToString(CultureInfo.InvariantCulture),
            txn.Price.ToString(CultureInfo.InvariantCulture),
            txn.Currency,
            txn.Fee.ToString(CultureInfo.InvariantCulture),
            txn.Tax.ToString(CultureInfo.InvariantCulture),
            txn.CashAmount.ToString(CultureInfo.InvariantCulture),
            txn.CounterCurrency,
            txn.CounterAmount.ToString(CultureInfo.InvariantCulture),
            txn.Note);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "txn_csv_" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }
}
