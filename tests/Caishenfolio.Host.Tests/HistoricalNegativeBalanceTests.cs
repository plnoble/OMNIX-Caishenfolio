using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 A5 (D2), the most dangerous vector in this package: a ledger that ALREADY contains a
/// negative balance — exactly the shape WP-01/WP-02's own fixtures had — must still open.
/// History is annotated, never rewritten and never a reason to refuse the file; the constraint
/// only applies to what is written from now on.
/// </summary>
public class HistoricalNegativeBalanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_legacy_balance_tests", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly Day2 = new(2026, 1, 6);
    private static readonly DateOnly AsOf = new(2026, 1, 7);

    private PortfolioStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return PortfolioStore.UnderStateRoot(_root);
    }

    /// <summary>
    /// Writes rows the way the OLD version would: straight into the table, no balance check.
    /// This is test-only on purpose — production can never reach it.
    /// </summary>
    private void InsertLegacyRows(PortfolioStore store, params LedgerTransaction[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO transactions (
                    id, account_id, kind, trade_date, symbol, quantity, price, currency,
                    fee, tax, cash_amount, counter_currency, counter_amount, fx_rate,
                    reverses_transaction_id, counter_account_id, note, import_batch_id, recorded_at)
                VALUES (
                    $id, $account, $kind, $date, $symbol, $qty, $price, $currency,
                    $fee, $tax, $cash, $counterCcy, $counterAmt, $rate,
                    $reverses, $counterAccount, $note, '', $recorded);
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$account", row.AccountId);
            command.Parameters.AddWithValue("$kind", row.Kind.ToString());
            command.Parameters.AddWithValue("$date", row.TradeDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$symbol", row.Symbol);
            command.Parameters.AddWithValue("$qty", row.Quantity);
            command.Parameters.AddWithValue("$price", row.Price);
            command.Parameters.AddWithValue("$currency", row.Currency);
            command.Parameters.AddWithValue("$fee", row.Fee);
            command.Parameters.AddWithValue("$tax", row.Tax);
            command.Parameters.AddWithValue("$cash", row.CashAmount);
            command.Parameters.AddWithValue("$counterCcy", row.CounterCurrency);
            command.Parameters.AddWithValue("$counterAmt", row.CounterAmount);
            command.Parameters.AddWithValue("$rate", row.FxRate);
            command.Parameters.AddWithValue("$reverses", row.ReversesTransactionId);
            command.Parameters.AddWithValue("$counterAccount", row.CounterAccountId);
            command.Parameters.AddWithValue("$note", row.Note);
            command.Parameters.AddWithValue(
                "$recorded",
                (row.RecordedAt == default ? DateTimeOffset.UtcNow : row.RecordedAt).ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task A5_ALedgerWithAnHistoricalNegativeBalanceStillOpensAndSaysSo()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("换汇账户", AccountKind.Bank, "CNY");

        // Exactly the old shape: an exchange with no funding deposit before it.
        InsertLegacyRows(
            store,
            LedgerTransaction.FxExchange(account.Id, Day1, 100_000m, "CNY", 2_000_000m, "JPY"));

        // Opening must not throw, and must not rewrite or drop the row.
        var snapshot = await workspace.RefreshAsync(AsOf);

        Assert.Equal(-100_000m, snapshot.CashBalances.Single(b => b.Currency == "CNY").Amount);
        Assert.Equal(2_000_000m, snapshot.CashBalances.Single(b => b.Currency == "JPY").Amount);
        Assert.Single(store.ListTransactions());

        // Annotated in plain words, in the same warnings the UI already shows.
        Assert.Contains(
            snapshot.Valuation.Warnings,
            warning => warning.Contains("历史负余额", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A5_TheLegacyLedgerStillAcceptsValidNewWrites()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("换汇账户", AccountKind.Bank, "CNY");
        InsertLegacyRows(
            store,
            LedgerTransaction.FxExchange(account.Id, Day1, 100_000m, "CNY", 2_000_000m, "JPY"));

        // The constraint applies to NEW writes only (D2): topping the account up and then
        // converting what is actually there must work on a ledger that is already negative.
        var snapshot0 = await workspace.RefreshAsync(AsOf);
        Assert.Equal(-100_000m, snapshot0.CashBalances.Single(b => b.Currency == "CNY").Amount);

        workspace.Record(LedgerTransaction.Deposit(account.Id, Day2, 150_000m, "CNY"));
        workspace.Record(LedgerTransaction.FxExchange(account.Id, Day2, 50_000m, "CNY", 1_000_000m, "JPY"));

        var snapshot = await workspace.RefreshAsync(AsOf);
        Assert.Equal(0m, snapshot.CashBalances.Where(b => b.Currency == "CNY").Sum(b => b.Amount));
    }

    [Fact]
    public async Task A5_TheHistoricalShortfallIsNotSilentlyTurnedIntoZero()
    {
        // The annotation has to name the amount, otherwise "历史负余额" is a word without a number.
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("换汇账户", AccountKind.Bank, "CNY");
        InsertLegacyRows(
            store,
            LedgerTransaction.FxExchange(account.Id, Day1, 100_000m, "CNY", 2_000_000m, "JPY"));

        var snapshot = await workspace.RefreshAsync(AsOf);

        var warning = Assert.Single(
            snapshot.Valuation.Warnings.Where(w => w.Contains("历史负余额", StringComparison.Ordinal)));
        Assert.Contains("100,000", warning, StringComparison.Ordinal);
        Assert.Contains("JPY", warning, StringComparison.Ordinal);
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
        }
    }
}
