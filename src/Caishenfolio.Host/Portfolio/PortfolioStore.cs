using Caishenfolio.Host.Data;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// Durable ledger under the Host State path root: accounts, instruments, and the append-only
/// transaction log. Decimals are stored as TEXT — Microsoft.Data.Sqlite maps them that way to
/// avoid the precision loss REAL would cause, which matters because these rows are money.
/// </summary>
public sealed class PortfolioStore : IDisposable
{
    /// <summary>Bump when the schema changes and add the matching step in <see cref="Migrate"/>.</summary>
    public const int SchemaVersion = 5;

    private readonly string _connectionString;
    private readonly object _gate = new();

    public PortfolioStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var full = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        DatabasePath = full;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        lock (_gate)
        {
            using var connection = Open();
            Migrate(connection);
        }
    }

    public string DatabasePath { get; }

    public static PortfolioStore UnderStateRoot(string stateRootDirectory, string fileName = "portfolio.db")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootDirectory);
        return new PortfolioStore(Path.Combine(Path.GetFullPath(stateRootDirectory), fileName));
    }

    // --- accounts ------------------------------------------------------------------

    public Account SaveAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts (id, name, kind, main_currency, broker, note, archived, created_at)
                VALUES ($id, $name, $kind, $currency, $broker, $note, $archived, $created)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    kind = excluded.kind,
                    main_currency = excluded.main_currency,
                    broker = excluded.broker,
                    note = excluded.note,
                    archived = excluded.archived;
                """;
            command.Parameters.AddWithValue("$id", account.Id);
            command.Parameters.AddWithValue("$name", account.Name);
            command.Parameters.AddWithValue("$kind", account.Kind.ToString());
            command.Parameters.AddWithValue("$currency", account.MainCurrency);
            command.Parameters.AddWithValue("$broker", account.Broker);
            command.Parameters.AddWithValue("$note", account.Note);
            command.Parameters.AddWithValue("$archived", account.Archived ? 1 : 0);
            command.Parameters.AddWithValue("$created", account.CreatedAt.ToString("O"));
            command.ExecuteNonQuery();
        }

        return account;
    }

    public IReadOnlyList<Account> ListAccounts(bool includeArchived = false)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, kind, main_currency, broker, note, archived, created_at
                FROM accounts
                WHERE ($all = 1 OR archived = 0)
                ORDER BY created_at, id;
                """;
            command.Parameters.AddWithValue("$all", includeArchived ? 1 : 0);
            using var reader = command.ExecuteReader();
            var results = new List<Account>();
            while (reader.Read())
            {
                results.Add(new Account
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Kind = Enum.TryParse<AccountKind>(reader.GetString(2), out var kind) ? kind : AccountKind.Other,
                    MainCurrency = reader.GetString(3),
                    Broker = reader.GetString(4),
                    Note = reader.GetString(5),
                    Archived = reader.GetInt32(6) != 0,
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
                });
            }

            return results;
        }
    }

    public bool RemoveAccount(string accountId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM transactions WHERE account_id = $id; DELETE FROM accounts WHERE id = $id;";
            command.Parameters.AddWithValue("$id", accountId);
            var affected = command.ExecuteNonQuery();
            tx.Commit();
            return affected > 0;
        }
    }

    // --- instruments ---------------------------------------------------------------

    public Instrument SaveInstrument(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO instruments (symbol, name, asset_class, region, currency, lot_size, face_value, note)
                VALUES ($symbol, $name, $asset, $region, $currency, $lot, $face, $note)
                ON CONFLICT(symbol) DO UPDATE SET
                    name = excluded.name,
                    asset_class = excluded.asset_class,
                    region = excluded.region,
                    currency = excluded.currency,
                    lot_size = excluded.lot_size,
                    face_value = excluded.face_value,
                    note = excluded.note;
                """;
            command.Parameters.AddWithValue("$symbol", instrument.Symbol);
            command.Parameters.AddWithValue("$name", instrument.Name);
            command.Parameters.AddWithValue("$asset", instrument.AssetClass.ToCode());
            command.Parameters.AddWithValue("$region", instrument.Region.ToCode());
            command.Parameters.AddWithValue("$currency", instrument.Currency);
            command.Parameters.AddWithValue("$lot", instrument.LotSize);
            command.Parameters.AddWithValue("$face", instrument.FaceValue);
            command.Parameters.AddWithValue("$note", instrument.Note);
            command.ExecuteNonQuery();
        }

        return instrument;
    }

    public IReadOnlyList<Instrument> ListInstruments()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT symbol, name, asset_class, region, currency, lot_size, face_value, note
                FROM instruments ORDER BY symbol;
                """;
            using var reader = command.ExecuteReader();
            var results = new List<Instrument>();
            while (reader.Read())
            {
                AssetClasses.TryParse(reader.GetString(2), out var asset);
                MarketRegions.TryParse(reader.GetString(3), out var region);
                results.Add(new Instrument
                {
                    Symbol = reader.GetString(0),
                    Name = reader.GetString(1),
                    AssetClass = asset,
                    Region = region,
                    Currency = reader.GetString(4),
                    LotSize = reader.GetDecimal(5),
                    FaceValue = reader.GetDecimal(6),
                    Note = reader.GetString(7),
                });
            }

            return results;
        }
    }

    public Instrument? GetInstrument(string symbol) =>
        ListInstruments().FirstOrDefault(i =>
            string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

    // --- transactions --------------------------------------------------------------

    public LedgerTransaction AddTransaction(LedgerTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        AddTransactions([transaction]);
        return transaction;
    }

    /// <summary>Inserts a batch atomically — a partially imported statement is worse than none.</summary>
    public int AddTransactions(IReadOnlyCollection<LedgerTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        if (transactions.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var inserted = 0;
            foreach (var item in transactions)
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO transactions (
                        id, account_id, kind, trade_date, symbol, quantity, price, currency,
                        fee, tax, cash_amount, counter_currency, counter_amount, fx_rate,
                        note, import_batch_id, recorded_at)
                    VALUES (
                        $id, $account, $kind, $date, $symbol, $qty, $price, $currency,
                        $fee, $tax, $cash, $counterCcy, $counterAmt, $rate,
                        $note, $batch, $recorded);
                    """;
                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$account", item.AccountId);
                command.Parameters.AddWithValue("$kind", item.Kind.ToString());
                command.Parameters.AddWithValue("$date", item.TradeDate.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("$symbol", item.Symbol);
                command.Parameters.AddWithValue("$qty", item.Quantity);
                command.Parameters.AddWithValue("$price", item.Price);
                command.Parameters.AddWithValue("$currency", item.Currency);
                command.Parameters.AddWithValue("$fee", item.Fee);
                command.Parameters.AddWithValue("$tax", item.Tax);
                command.Parameters.AddWithValue("$cash", item.CashAmount);
                command.Parameters.AddWithValue("$counterCcy", item.CounterCurrency);
                command.Parameters.AddWithValue("$counterAmt", item.CounterAmount);
                command.Parameters.AddWithValue("$rate", item.FxRate);
                command.Parameters.AddWithValue("$note", item.Note);
                command.Parameters.AddWithValue("$batch", item.ImportBatchId);
                command.Parameters.AddWithValue("$recorded", item.RecordedAt.ToString("O"));
                inserted += command.ExecuteNonQuery();
            }

            tx.Commit();
            return inserted;
        }
    }

    public IReadOnlyList<LedgerTransaction> ListTransactions(
        string? accountId = null,
        string? symbol = null,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, kind, trade_date, symbol, quantity, price, currency,
                       fee, tax, cash_amount, counter_currency, counter_amount, fx_rate,
                       note, import_batch_id, recorded_at
                FROM transactions
                WHERE ($account IS NULL OR account_id = $account)
                  AND ($symbol IS NULL OR symbol = $symbol)
                  AND ($from IS NULL OR trade_date >= $from)
                  AND ($to IS NULL OR trade_date <= $to)
                ORDER BY trade_date, recorded_at, id;
                """;
            command.Parameters.AddWithValue("$account", (object?)accountId ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbol", (object?)NormalizeSymbolFilter(symbol) ?? DBNull.Value);
            command.Parameters.AddWithValue("$from", (object?)from?.ToString("yyyy-MM-dd") ?? DBNull.Value);
            command.Parameters.AddWithValue("$to", (object?)to?.ToString("yyyy-MM-dd") ?? DBNull.Value);

            using var reader = command.ExecuteReader();
            var results = new List<LedgerTransaction>();
            while (reader.Read())
            {
                results.Add(new LedgerTransaction
                {
                    Id = reader.GetString(0),
                    AccountId = reader.GetString(1),
                    Kind = Enum.Parse<TransactionKind>(reader.GetString(2)),
                    TradeDate = DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd"),
                    Symbol = reader.GetString(4),
                    Quantity = reader.GetDecimal(5),
                    Price = reader.GetDecimal(6),
                    Currency = reader.GetString(7),
                    Fee = reader.GetDecimal(8),
                    Tax = reader.GetDecimal(9),
                    CashAmount = reader.GetDecimal(10),
                    CounterCurrency = reader.GetString(11),
                    CounterAmount = reader.GetDecimal(12),
                    FxRate = reader.GetDecimal(13),
                    Note = reader.GetString(14),
                    ImportBatchId = reader.GetString(15),
                    RecordedAt = DateTimeOffset.Parse(reader.GetString(16)),
                });
            }

            return results;
        }
    }

    public bool RemoveTransaction(string transactionId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM transactions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", transactionId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // --- fx rates ------------------------------------------------------------------

    public int SaveFxRates(IReadOnlyCollection<FxRate> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (rates.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var affected = 0;
            foreach (var rate in rates)
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO fx_rates (base_currency, quote_currency, as_of, rate, provider)
                    VALUES ($base, $quote, $asOf, $rate, $provider)
                    ON CONFLICT(base_currency, quote_currency, as_of) DO UPDATE SET
                        rate = excluded.rate,
                        provider = excluded.provider;
                    """;
                command.Parameters.AddWithValue("$base", rate.BaseCurrency);
                command.Parameters.AddWithValue("$quote", rate.QuoteCurrency);
                command.Parameters.AddWithValue("$asOf", rate.AsOf.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("$rate", rate.Rate);
                command.Parameters.AddWithValue("$provider", rate.Provider);
                affected += command.ExecuteNonQuery();
            }

            tx.Commit();
            return affected;
        }
    }

    public FxRate SaveFxRate(FxRate rate)
    {
        SaveFxRates([rate]);
        return rate;
    }

    /// <summary>Rates observed on or before <paramref name="asOf"/>, oldest first.</summary>
    public IReadOnlyList<FxRate> ListFxRates(DateOnly? asOf = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT base_currency, quote_currency, as_of, rate, provider
                FROM fx_rates
                WHERE ($asOf IS NULL OR as_of <= $asOf)
                ORDER BY as_of, base_currency, quote_currency;
                """;
            command.Parameters.AddWithValue("$asOf", (object?)asOf?.ToString("yyyy-MM-dd") ?? DBNull.Value);

            using var reader = command.ExecuteReader();
            var results = new List<FxRate>();
            while (reader.Read())
            {
                results.Add(new FxRate
                {
                    BaseCurrency = reader.GetString(0),
                    QuoteCurrency = reader.GetString(1),
                    AsOf = DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd"),
                    Rate = reader.GetDecimal(3),
                    Provider = reader.GetString(4),
                });
            }

            return results;
        }
    }

    /// <summary>Converter built from the freshest rate per pair at <paramref name="asOf"/>.</summary>
    public FxConverter CreateFxConverter(DateOnly? asOf = null, string pivot = Currencies.Usd) =>
        new(ListFxRates(asOf), pivot);

    // --- IPO subscriptions ---------------------------------------------------------

    /// <summary>
    /// Saves the subscription and, once it becomes a real holding, mirrors it into the ledger so
    /// positions live in one place. The mirrored rows use deterministic ids, so re-saving after
    /// each status change updates them rather than duplicating the position.
    /// </summary>
    public IpoSubscription SaveIpoSubscription(IpoSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO ipo_subscriptions (
                        id, account_id, symbol, name, subscribe_date, status, currency,
                        subscribed_quantity, allotted_quantity, issue_price,
                        payment_date, listing_date, sold_date, sold_price, fee, note)
                    VALUES (
                        $id, $account, $symbol, $name, $subscribed, $status, $currency,
                        $subQty, $allotQty, $price,
                        $paid, $listed, $sold, $soldPrice, $fee, $note)
                    ON CONFLICT(id) DO UPDATE SET
                        status = excluded.status,
                        name = excluded.name,
                        allotted_quantity = excluded.allotted_quantity,
                        payment_date = excluded.payment_date,
                        listing_date = excluded.listing_date,
                        sold_date = excluded.sold_date,
                        sold_price = excluded.sold_price,
                        fee = excluded.fee,
                        note = excluded.note;
                    """;
                command.Parameters.AddWithValue("$id", subscription.Id);
                command.Parameters.AddWithValue("$account", subscription.AccountId);
                command.Parameters.AddWithValue("$symbol", subscription.Symbol);
                command.Parameters.AddWithValue("$name", subscription.Name);
                command.Parameters.AddWithValue("$subscribed", subscription.SubscribeDate.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("$status", subscription.Status.ToString());
                command.Parameters.AddWithValue("$currency", subscription.Currency);
                command.Parameters.AddWithValue("$subQty", subscription.SubscribedQuantity);
                command.Parameters.AddWithValue("$allotQty", subscription.AllottedQuantity);
                command.Parameters.AddWithValue("$price", subscription.IssuePrice);
                command.Parameters.AddWithValue("$paid", DateOrNull(subscription.PaymentDate));
                command.Parameters.AddWithValue("$listed", DateOrNull(subscription.ListingDate));
                command.Parameters.AddWithValue("$sold", DateOrNull(subscription.SoldDate));
                command.Parameters.AddWithValue("$soldPrice", subscription.SoldPrice);
                command.Parameters.AddWithValue("$fee", subscription.Fee);
                command.Parameters.AddWithValue("$note", subscription.Note);
                command.ExecuteNonQuery();
            }

            foreach (var transaction in subscription.ToLedgerTransactions())
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO transactions (
                        id, account_id, kind, trade_date, symbol, quantity, price, currency,
                        fee, tax, cash_amount, counter_currency, counter_amount, fx_rate,
                        note, import_batch_id, recorded_at)
                    VALUES (
                        $id, $account, $kind, $date, $symbol, $qty, $price, $currency,
                        $fee, 0, 0, '', 0, 0, $note, $batch, $recorded)
                    ON CONFLICT(id) DO UPDATE SET
                        trade_date = excluded.trade_date,
                        quantity = excluded.quantity,
                        price = excluded.price,
                        fee = excluded.fee,
                        note = excluded.note;
                    """;
                command.Parameters.AddWithValue("$id", transaction.Id);
                command.Parameters.AddWithValue("$account", transaction.AccountId);
                command.Parameters.AddWithValue("$kind", transaction.Kind.ToString());
                command.Parameters.AddWithValue("$date", transaction.TradeDate.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("$symbol", transaction.Symbol);
                command.Parameters.AddWithValue("$qty", transaction.Quantity);
                command.Parameters.AddWithValue("$price", transaction.Price);
                command.Parameters.AddWithValue("$currency", transaction.Currency);
                command.Parameters.AddWithValue("$fee", transaction.Fee);
                command.Parameters.AddWithValue("$note", transaction.Note);
                command.Parameters.AddWithValue("$batch", IpoBatchId);
                command.Parameters.AddWithValue("$recorded", transaction.RecordedAt.ToString("O"));
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return subscription;
    }

    /// <summary>Import batch tag on ledger rows that came from an IPO record.</summary>
    public const string IpoBatchId = "ipo";

    public IReadOnlyList<IpoSubscription> ListIpoSubscriptions(string? accountId = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, symbol, name, subscribe_date, status, currency,
                       subscribed_quantity, allotted_quantity, issue_price,
                       payment_date, listing_date, sold_date, sold_price, fee, note
                FROM ipo_subscriptions
                WHERE ($account IS NULL OR account_id = $account)
                ORDER BY subscribe_date DESC, id;
                """;
            command.Parameters.AddWithValue("$account", (object?)accountId ?? DBNull.Value);

            using var reader = command.ExecuteReader();
            var results = new List<IpoSubscription>();
            while (reader.Read())
            {
                results.Add(new IpoSubscription
                {
                    Id = reader.GetString(0),
                    AccountId = reader.GetString(1),
                    Symbol = reader.GetString(2),
                    Name = reader.GetString(3),
                    SubscribeDate = DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd"),
                    Status = Enum.Parse<IpoStatus>(reader.GetString(5)),
                    Currency = reader.GetString(6),
                    SubscribedQuantity = reader.GetDecimal(7),
                    AllottedQuantity = reader.GetDecimal(8),
                    IssuePrice = reader.GetDecimal(9),
                    PaymentDate = ReadDate(reader, 10),
                    ListingDate = ReadDate(reader, 11),
                    SoldDate = ReadDate(reader, 12),
                    SoldPrice = reader.GetDecimal(13),
                    Fee = reader.GetDecimal(14),
                    Note = reader.GetString(15),
                });
            }

            return results;
        }
    }

    public bool RemoveIpoSubscription(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            // The mirrored ledger rows go with it, or the position would outlive its record.
            command.CommandText = """
                DELETE FROM transactions WHERE id = $buy OR id = $sell;
                DELETE FROM ipo_subscriptions WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$buy", $"txn_ipo_buy_{id}");
            command.Parameters.AddWithValue("$sell", $"txn_ipo_sell_{id}");
            var affected = command.ExecuteNonQuery();
            tx.Commit();
            return affected > 0;
        }
    }

    private static object DateOrNull(DateOnly? value) =>
        value is null ? DBNull.Value : value.Value.ToString("yyyy-MM-dd");

    private static DateOnly? ReadDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateOnly.ParseExact(reader.GetString(index), "yyyy-MM-dd");

    // --- settings ------------------------------------------------------------------

    /// <summary>Stored preferences, falling back to defaults for anything never set.</summary>
    public PortfolioSettings LoadSettings()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM settings;";
            using var reader = command.ExecuteReader();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }

            return PortfolioSettings.FromKeyValues(values);
        }
    }

    /// <summary>
    /// Validates and stores preferences. Target rows are replaced wholesale, so removing an
    /// asset class from the target mix actually removes it instead of leaving a stale weight.
    /// </summary>
    public PortfolioSettings SaveSettings(PortfolioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validated = settings.Validated();

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM settings WHERE key LIKE $prefix;";
                clear.Parameters.AddWithValue("$prefix", PortfolioSettings.TargetPrefix + "%");
                clear.ExecuteNonQuery();
            }

            foreach (var (key, value) in validated.ToKeyValues())
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO settings (key, value) VALUES ($key, $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return validated;
    }

    // --- valuation history ---------------------------------------------------------

    /// <summary>
    /// Records the portfolio value for a date. One row per date: refreshing repeatedly on the
    /// same day corrects that day rather than piling up duplicate points on the equity curve.
    /// An incomplete valuation is still stored but flagged, so drawdown can ignore it.
    /// </summary>
    public void SaveValuationSnapshot(PortfolioValuation valuation)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO valuation_snapshots
                    (as_of, base_currency, total_value, holdings_value, cash_value, cost_basis, complete)
                VALUES ($asOf, $currency, $total, $holdings, $cash, $cost, $complete)
                ON CONFLICT(as_of, base_currency) DO UPDATE SET
                    total_value = excluded.total_value,
                    holdings_value = excluded.holdings_value,
                    cash_value = excluded.cash_value,
                    cost_basis = excluded.cost_basis,
                    complete = excluded.complete;
                """;
            command.Parameters.AddWithValue("$asOf", valuation.AsOf.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$currency", valuation.BaseCurrency);
            command.Parameters.AddWithValue("$total", valuation.TotalValue.Amount);
            command.Parameters.AddWithValue("$holdings", valuation.HoldingsValue.Amount);
            command.Parameters.AddWithValue("$cash", valuation.CashValue.Amount);
            command.Parameters.AddWithValue("$cost", valuation.CostBasis.Amount);
            command.Parameters.AddWithValue("$complete", valuation.IsComplete ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Equity curve in <paramref name="baseCurrency"/>, oldest first.</summary>
    public IReadOnlyList<ValuationPoint> ListValuationHistory(
        string baseCurrency, bool completeOnly = true)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT as_of, total_value
                FROM valuation_snapshots
                WHERE base_currency = $currency AND ($all = 1 OR complete = 1)
                ORDER BY as_of;
                """;
            command.Parameters.AddWithValue("$currency", Currencies.Normalize(baseCurrency));
            command.Parameters.AddWithValue("$all", completeOnly ? 0 : 1);

            using var reader = command.ExecuteReader();
            var results = new List<ValuationPoint>();
            while (reader.Read())
            {
                results.Add(new ValuationPoint(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd"),
                    reader.GetDecimal(1)));
            }

            return results;
        }
    }

    /// <summary>Replays the stored ledger into positions, cash, and external flows.</summary>
    public LedgerState LoadState(string? accountId = null) =>
        PositionCalculator.Replay(ListTransactions(accountId));

    public void Dispose() => SqliteConnection.ClearAllPools();

    // --- schema --------------------------------------------------------------------

    private static string? NormalizeSymbolFilter(string? symbol) =>
        string.IsNullOrWhiteSpace(symbol)
            ? null
            : SymbolId.TryParse(symbol, out var parsed) ? parsed.Normalized().Value : symbol.Trim();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Migrate(SqliteConnection connection)
    {
        var current = ReadUserVersion(connection);
        if (current >= SchemaVersion)
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        if (current < 1)
        {
            Execute(connection, tx, """
                CREATE TABLE IF NOT EXISTS accounts (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    main_currency TEXT NOT NULL,
                    broker TEXT NOT NULL DEFAULT '',
                    note TEXT NOT NULL DEFAULT '',
                    archived INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS instruments (
                    symbol TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    asset_class TEXT NOT NULL,
                    region TEXT NOT NULL,
                    currency TEXT NOT NULL,
                    lot_size TEXT NOT NULL DEFAULT '0',
                    face_value TEXT NOT NULL DEFAULT '0',
                    note TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS transactions (
                    id TEXT PRIMARY KEY,
                    account_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    trade_date TEXT NOT NULL,
                    symbol TEXT NOT NULL DEFAULT '',
                    quantity TEXT NOT NULL DEFAULT '0',
                    price TEXT NOT NULL DEFAULT '0',
                    currency TEXT NOT NULL,
                    fee TEXT NOT NULL DEFAULT '0',
                    tax TEXT NOT NULL DEFAULT '0',
                    cash_amount TEXT NOT NULL DEFAULT '0',
                    counter_currency TEXT NOT NULL DEFAULT '',
                    counter_amount TEXT NOT NULL DEFAULT '0',
                    fx_rate TEXT NOT NULL DEFAULT '0',
                    note TEXT NOT NULL DEFAULT '',
                    import_batch_id TEXT NOT NULL DEFAULT '',
                    recorded_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_transactions_account_date
                    ON transactions(account_id, trade_date);
                CREATE INDEX IF NOT EXISTS ix_transactions_symbol ON transactions(symbol);
                CREATE INDEX IF NOT EXISTS ix_transactions_batch ON transactions(import_batch_id);
                """);
        }

        if (current < 2)
        {
            // Rates are snapshotted so a portfolio can still be valued when the provider is down.
            Execute(connection, tx, """
                CREATE TABLE IF NOT EXISTS fx_rates (
                    base_currency TEXT NOT NULL,
                    quote_currency TEXT NOT NULL,
                    as_of TEXT NOT NULL,
                    rate TEXT NOT NULL,
                    provider TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (base_currency, quote_currency, as_of)
                );

                CREATE INDEX IF NOT EXISTS ix_fx_rates_as_of ON fx_rates(as_of);
                """);
        }

        if (current < 3)
        {
            // One row per valuation date. Without a history there is no equity curve, and
            // without an equity curve drawdown cannot be computed at all.
            Execute(connection, tx, """
                CREATE TABLE IF NOT EXISTS valuation_snapshots (
                    as_of TEXT NOT NULL,
                    base_currency TEXT NOT NULL,
                    total_value TEXT NOT NULL,
                    holdings_value TEXT NOT NULL DEFAULT '0',
                    cash_value TEXT NOT NULL DEFAULT '0',
                    cost_basis TEXT NOT NULL DEFAULT '0',
                    complete INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (as_of, base_currency)
                );
                """);
        }

        if (current < 4)
        {
            // Key/value rather than columns: preferences grow (new thresholds, new asset classes
            // in the target mix) and a schema migration per preference is not worth it.
            Execute(connection, tx, """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """);
        }

        if (current < 5)
        {
            Execute(connection, tx, """
                CREATE TABLE IF NOT EXISTS ipo_subscriptions (
                    id TEXT PRIMARY KEY,
                    account_id TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    name TEXT NOT NULL DEFAULT '',
                    subscribe_date TEXT NOT NULL,
                    status TEXT NOT NULL,
                    currency TEXT NOT NULL,
                    subscribed_quantity TEXT NOT NULL DEFAULT '0',
                    allotted_quantity TEXT NOT NULL DEFAULT '0',
                    issue_price TEXT NOT NULL DEFAULT '0',
                    payment_date TEXT,
                    listing_date TEXT,
                    sold_date TEXT,
                    sold_price TEXT NOT NULL DEFAULT '0',
                    fee TEXT NOT NULL DEFAULT '0',
                    note TEXT NOT NULL DEFAULT ''
                );

                CREATE INDEX IF NOT EXISTS ix_ipo_account ON ipo_subscriptions(account_id);
                """);
        }

        Execute(connection, tx, $"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
