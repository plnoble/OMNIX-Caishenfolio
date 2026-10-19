using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// A10. A real v9 file on disk — created with v9 DDL and <c>PRAGMA user_version = 9</c>, not by
/// downgrading a v10 database — must upgrade to v10 with nothing lost: rows, accounts, reversal
/// links, and the pre-upgrade <c>.bak</c> copy. Running the migration a second time must do
/// nothing at all.
/// </summary>
public class LedgerSchemaV10Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_schema_v10_tests", Guid.NewGuid().ToString("N"));

    /// <summary>The v9 schema as it existed before WP-01, plus one fixture row set.</summary>
    private const string V9Schema = """
        CREATE TABLE accounts (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            main_currency TEXT NOT NULL,
            broker TEXT NOT NULL DEFAULT '',
            note TEXT NOT NULL DEFAULT '',
            archived INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );

        CREATE TABLE transactions (
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
            recorded_at TEXT NOT NULL,
            reverses_transaction_id TEXT NOT NULL DEFAULT '',
            counter_account_id TEXT NOT NULL DEFAULT ''
        );

        CREATE UNIQUE INDEX ux_transactions_single_reversal
            ON transactions(reverses_transaction_id) WHERE reverses_transaction_id <> '';

        CREATE TABLE fx_rates (
            base_currency TEXT NOT NULL,
            quote_currency TEXT NOT NULL,
            as_of TEXT NOT NULL,
            rate TEXT NOT NULL,
            provider TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (base_currency, quote_currency, as_of)
        );

        CREATE TABLE valuation_snapshots (
            as_of TEXT NOT NULL,
            base_currency TEXT NOT NULL,
            total_value TEXT NOT NULL,
            holdings_value TEXT NOT NULL DEFAULT '0',
            cash_value TEXT NOT NULL DEFAULT '0',
            cost_basis TEXT NOT NULL DEFAULT '0',
            complete INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (as_of, base_currency)
        );

        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        INSERT INTO accounts (id, name, kind, main_currency, broker, note, archived, created_at) VALUES
            ('acct_old_one', '老账户甲', 'Securities', 'CNY', '', '', 0, '2024-01-01T00:00:00.0000000+00:00'),
            ('acct_old_two', '老账户乙', 'Bank', 'JPY', '', '日元', 0, '2024-01-02T00:00:00.0000000+00:00');

        INSERT INTO transactions (
            id, account_id, kind, trade_date, symbol, quantity, price, currency,
            fee, tax, cash_amount, counter_currency, counter_amount, fx_rate, note,
            import_batch_id, recorded_at, reverses_transaction_id, counter_account_id) VALUES
            ('txn_old_deposit', 'acct_old_one', 'Deposit', '2024-02-01', '', '0', '0', 'CNY',
             '0', '0', '50000', '', '0', '0', '', '', '2024-02-01T00:00:00.0000000+00:00', '', ''),
            ('txn_old_buy', 'acct_old_one', 'Buy', '2024-02-02', 'SSE:600000', '1000', '10', 'CNY',
             '5', '0', '0', '', '0', '0', '', '', '2024-02-02T00:00:00.0000000+00:00', '', ''),
            ('txn_old_rev', 'acct_old_one', 'Buy', '2024-02-02', 'SSE:600000', '1000', '10', 'CNY',
             '5', '0', '0', '', '0', '0', '冲正', '', '2024-02-03T00:00:00.0000000+00:00', 'txn_old_buy', '');

        PRAGMA user_version = 9;
        """;

    private string DatabasePath => Path.Combine(_root, "portfolio.db");

    private void CreateV9Database()
    {
        Directory.CreateDirectory(_root);
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = V9Schema;
        command.ExecuteNonQuery();
    }

    private static int ReadUserVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static bool HasColumn(string path, string table, string column)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void V9LedgerUpgradesToV10KeepingEveryRowAndBackingUpFirst()
    {
        CreateV9Database();
        Assert.Equal(9, ReadUserVersion(DatabasePath));
        Assert.False(HasColumn(DatabasePath, "accounts", "mode"));

        using (var store = new PortfolioStore(DatabasePath))
        {
            var accounts = store.ListAccounts(includeArchived: true);
            var transactions = store.ListTransactions();

            Assert.Equal(2, accounts.Count);
            Assert.Equal(3, transactions.Count);
            Assert.Equal("老账户甲", accounts.Single(a => a.Id == "acct_old_one").Name);
            Assert.Equal("JPY", accounts.Single(a => a.Id == "acct_old_two").MainCurrency);
            // A schema upgrade must not silently move an existing account into 余额 mode.
            Assert.All(accounts, a => Assert.Equal(AccountValuationMode.Detail, a.Mode));

            // The reversal link survives verbatim.
            var reversal = transactions.Single(t => t.Id == "txn_old_rev");
            Assert.Equal("txn_old_buy", reversal.ReversesTransactionId);
            Assert.Contains("txn_old_buy", store.ReversedTransactionIds());

            // The pre-upgrade file was kept before the schema changed.
            var backup = DatabasePath + ".bak";
            Assert.True(File.Exists(backup));
            Assert.Equal(9, ReadUserVersion(backup));
        }

        Assert.Equal(PortfolioStore.SchemaVersion, ReadUserVersion(DatabasePath));
        Assert.True(HasColumn(DatabasePath, "accounts", "mode"));
        Assert.Equal(10, PortfolioStore.SchemaVersion);
    }

    [Fact]
    public void MigratingAnAlreadyUpgradedLedgerIsANoOp()
    {
        CreateV9Database();
        using (var first = new PortfolioStore(DatabasePath))
        {
            Assert.Equal(3, first.ListTransactions().Count);
        }

        var backup = DatabasePath + ".bak";
        var backupStamp = File.GetLastWriteTimeUtc(backup);

        using (var second = new PortfolioStore(DatabasePath))
        {
            Assert.Equal(2, second.ListAccounts(includeArchived: true).Count);
            Assert.Equal(3, second.ListTransactions().Count);
            Assert.Equal("txn_old_buy", second.ListTransactions().Single(t => t.Id == "txn_old_rev").ReversesTransactionId);
        }

        // A second open must not re-run the migration: the backup is still the v9 file,
        // not a fresh copy of an already-v10 database.
        Assert.Equal(9, ReadUserVersion(backup));
        Assert.Equal(backupStamp, File.GetLastWriteTimeUtc(backup));
        Assert.Equal(PortfolioStore.SchemaVersion, ReadUserVersion(DatabasePath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}