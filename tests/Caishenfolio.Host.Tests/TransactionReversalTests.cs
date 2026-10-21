using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// Corrections in an append-only ledger: the original row survives, a mirror row cancels it,
/// and every derived number returns to its pre-mistake state exactly.
/// </summary>
public class TransactionReversalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_reversal_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    [Fact]
    public void ReversalCancelsCashIncomeAndExternalFlowsExactly()
    {
        using var store = NewStore();
        var deposit = LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 1000m, "CNY");
        var buy = LedgerTransaction.Buy(
            "acct", "SSE:600000", new DateOnly(2026, 1, 5), 100m, 10m, "CNY", fee: 5m);
        store.AddTransactions([deposit, buy]);

        var baseline = store.LoadState();
        var cashBefore = Assert.Single(baseline.CashBalances).Amount; // 1000 - 1005 = -5
        Assert.Equal(-5m, cashBefore);

        var reversal = store.AddTransaction(LedgerTransaction.Reversal(buy, "价格录错"));
        var state = store.LoadState();

        Assert.Single(state.CashBalances);
        Assert.Equal(1000m, state.CashBalances[0].Amount);
        Assert.Empty(state.Positions);
        // Cash-back exactness also means the buy never joined the external flows it would have had.
        Assert.DoesNotContain(state.Positions, p => p.Symbol == "SSE:600000");

        // The pair is still in the log — nothing vanished.
        Assert.Equal(3, store.ListTransactions().Count);
        Assert.Equal(buy.Id, reversal.ReversesTransactionId);
        Assert.True(reversal.IsReversal);
    }

    [Fact]
    public void SellReversalRestoresThePositionAndItsCost()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 2), 10_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", new DateOnly(2026, 1, 2), 1000m, 10m, "CNY"),
            LedgerTransaction.Sell("acct", "SSE:600000", new DateOnly(2026, 2, 5), 400m, 12m, "CNY", fee: 4m),
        ]);

        var mid = store.LoadState();
        Assert.Equal(600m, mid.Positions[0].Quantity);
        // Proceeds 4 800 - 4 fee; released cost 10 × 400 = 4 000 (opening position, no buy fee).
        Assert.Equal(796m, mid.Positions[0].RealizedPnl.Amount);

        var sell = store.ListTransactions(symbol: "SSE:600000").Single(t => t.Kind == TransactionKind.Sell);
        store.AddTransaction(LedgerTransaction.Reversal(sell, "卖出录入错误"));

        var state = store.LoadState();
        var position = Assert.Single(state.Positions);
        Assert.Equal(1000m, position.Quantity);
        Assert.Equal(10_000m, position.CostBasis.Amount);
        Assert.Equal(0m, position.RealizedPnl.Amount);
        // Cash moved by the sell (4800 - 4) also comes back exactly.
        Assert.Equal(10_000m, Assert.Single(state.CashBalances).Amount);
    }

    [Fact]
    public void ReversingAnFxExchangeRestoresBothCurrencies()
    {
        using var store = NewStore();
        var exchange = LedgerTransaction.FxExchange(
            "acct", new DateOnly(2026, 3, 1), fromAmount: 1000m, "CNY", toAmount: 155_000m, "JPY", fee: 2m);
        // WP-03 R1: the account must hold the CNY first (amount + fee); the reversal still
        // clears both sides.
        store.AddTransaction(LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 2, 28), 2_000m, "CNY", "建账余额"));
        store.AddTransaction(exchange);

        var mid = store.LoadState();
        // 2,000 − 1,000 − 2 fee = 998 CNY left next to the 155,000 JPY it bought.
        Assert.Equal(2, mid.CashBalances.Count);

        store.AddTransaction(LedgerTransaction.Reversal(exchange, "回单金额录错"));

        var state = store.LoadState();
        // The reversal restores the opening CNY balance and removes the JPY it bought.
        Assert.Equal("CNY", Assert.Single(state.CashBalances).Currency);
        Assert.Equal(2_000m, state.CashBalances[0].Amount);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void ReversalIdsSurviveTheRoundTripThroughTheStore()
    {
        using var store = NewStore();
        var deposit = LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 100m, "CNY");
        store.AddTransaction(deposit);
        store.AddTransaction(LedgerTransaction.Reversal(deposit, "金额录错"));

        var rows = store.ListTransactions();
        var reversal = Assert.Single(rows, r => r.IsReversal);
        Assert.Equal(deposit.Id, reversal.ReversesTransactionId);
        Assert.Equal("金额录错", reversal.Note);

        Assert.Contains(deposit.Id, store.ReversedTransactionIds());
        Assert.DoesNotContain(reversal.Id, store.ReversedTransactionIds());
    }

    [Fact]
    public void StoreRejectsBrokenCorrectionChains()
    {
        using var store = NewStore();
        var deposit = LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 100m, "CNY");
        store.AddTransaction(deposit);
        var reversal = LedgerTransaction.Reversal(deposit, "first");
        store.AddTransaction(reversal);

        // Reversing a row that already carries a reversal.
        var ex = Assert.Throws<LedgerException>(() =>
            store.AddTransaction(LedgerTransaction.Reversal(deposit, "again")));
        Assert.Contains("已有一条冲正记录", ex.Message);

        // A forged row pointing at an already-reversed original is refused at insert too —
        // the store cannot be bypassed by skipping the Reversal factory.
        var second = reversal with { Id = "txn_rev_second" };
        var ex2 = Assert.Throws<LedgerException>(() => store.AddTransaction(second));
        Assert.Contains("已有一条冲正记录", ex2.Message);

        // A dangling reference is refused at insert.
        var dangling = LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 3), 1m, "CNY") with
        {
            Id = "txn_rev_dangling",
            ReversesTransactionId = "txn_missing",
        };
        var ex3 = Assert.Throws<LedgerException>(() => store.AddTransaction(dangling));
        Assert.Contains("不存在", ex3.Message);

        Assert.Equal(2, store.ListTransactions().Count);
    }

    [Fact]
    public void CorrectingAfterMoreActivityOnlyCancelsTheTargetedPair()
    {
        using var store = NewStore();
        var buyA = LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2026, 1, 5), 100m, 10m, "CNY");
        var buyB = LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2026, 1, 6), 100m, 11m, "CNY");
        store.AddTransactions([buyA, buyB]);
        store.AddTransaction(LedgerTransaction.Reversal(buyA, "撤单的成交"));

        var state = store.LoadState();
        var position = Assert.Single(state.Positions);
        Assert.Equal(100m, position.Quantity);
        Assert.Equal(11m, position.AverageCost.Amount);
        Assert.Single(store.ReversedTransactionIds());
    }

    [Fact]
    public void OldDatabasesMigrateUpAndKeepEveryRow()
    {
        var dbPath = Path.Combine(_root, "portfolio.db");
        Directory.CreateDirectory(_root);

        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            connection.Open();

            // Schema v5 as the previous release wrote it. The accounts table is part of that
            // schema: every real v5 file has it, because the v1 step creates it and later steps
            // never drop it. WP-01's v10 step alters accounts, so a fixture that claimed v5
            // without the table was not a shape any release could have produced.
            using var create = connection.CreateCommand();
            create.CommandText = """
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

                INSERT INTO accounts (id, name, kind, main_currency, broker, note, archived, created_at) VALUES
                    ('acct', '旧账户', 'Securities', 'CNY', '', '', 0, '2026-01-01T00:00:00Z');

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
                    recorded_at TEXT NOT NULL
                );
                INSERT INTO transactions VALUES (
                    'txn_legacy_1', 'acct', 'Deposit', '2026-01-02', '', '0', '0', 'CNY',
                    '0', '0', '100', '', '0', '0', '建账', '', '2026-01-02T00:00:00Z');
                PRAGMA user_version = 5;
                """;
            create.ExecuteNonQuery();
        }

        using (var store = new PortfolioStore(dbPath))
        {
            Assert.Equal(PortfolioStore.SchemaVersion, ReadUserVersion(dbPath));

            var rows = store.ListTransactions();
            var legacy = Assert.Single(rows);
            Assert.Equal("", legacy.ReversesTransactionId);
            Assert.Equal(100m, Assert.Single(store.LoadState().CashBalances).Amount);

            // The migrated column is live: a reversal on the legacy row works end to end.
            store.AddTransaction(LedgerTransaction.Reversal(legacy, "冲正旧流水"));
            Assert.Empty(store.LoadState().CashBalances);
        }

        Assert.True(File.Exists(dbPath + ".bak"), "升级前的备份应与账本放在一起。");
    }


    /// <summary>P2-R01 反例 #17：两个独立 Store 实例，同一流水只能有一条冲正。</summary>
    [Fact]
    public void TwoStoreInstancesCannotBothReverseTheSameTransaction()
    {
        var dbPath = Path.Combine(_root, "shared.db");
        Directory.CreateDirectory(_root);
        string txnId;
        using (var first = new PortfolioStore(dbPath))
        {
            var deposit = LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 100m, "CNY");
            first.AddTransaction(deposit);
            txnId = deposit.Id;
        }

        // Instance A writes its reversal and closes.
        using (var a = new PortfolioStore(dbPath))
        {
            a.AddTransaction(LedgerTransaction.Reversal(
                a.ListTransactions().Single(t => t.Id == txnId), "instance A"));
        }

        // Instance B — a separate process shape — tries to reverse the same row.
        using var b = new PortfolioStore(dbPath);
        var original = b.ListTransactions().Single(t => t.Id == txnId);
        var ex = Assert.ThrowsAny<Exception>(() =>
            b.AddTransaction(LedgerTransaction.Reversal(original, "instance B")));
        // Either the app-level check or the unique index stops it; both are LedgerException
        // shapes the UI understands, never a raw SQLite error leaking through.
        Assert.Contains("冲正", ex.Message);
        Assert.Equal(1, b.ListTransactions().Count(t => t.IsReversal));
    }

    private int ReadUserVersion(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
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
            // Best-effort cleanup: a lingering file handle must not fail the suite.
        }
    }
}
