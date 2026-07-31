using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

public class PortfolioStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_portfolio_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    [Fact]
    public void RoundTripsAccounts()
    {
        using var store = NewStore();
        var account = store.SaveAccount(Account.Create("华泰证券", AccountKind.Securities, "CNY", broker: "华泰"));

        var listed = Assert.Single(store.ListAccounts());
        Assert.Equal(account.Id, listed.Id);
        Assert.Equal("华泰证券", listed.Name);
        Assert.Equal(AccountKind.Securities, listed.Kind);
        Assert.Equal("CNY", listed.MainCurrency);
        Assert.False(listed.Archived);

        store.SaveAccount(listed with { Name = "华泰-A股", Archived = true });
        Assert.Empty(store.ListAccounts());

        var archived = Assert.Single(store.ListAccounts(includeArchived: true));
        Assert.Equal("华泰-A股", archived.Name);
        Assert.True(archived.Archived);
    }

    [Fact]
    public void RoundTripsInstrumentsAcrossEveryAssetClassInScope()
    {
        using var store = NewStore();
        Instrument[] universe =
        [
            Instrument.FromSymbol("SSE:600000", "浦发银行", AssetClass.Equity, lotSize: 100m),
            Instrument.FromSymbol("SSE:510300", "沪深300ETF", AssetClass.Etf, lotSize: 100m),
            Instrument.FromSymbol("FUND:110022", "易方达消费行业", AssetClass.MutualFund, lotSize: 0.01m),
            Instrument.FromSymbol("SSE:113050", "南银转债", AssetClass.ConvertibleBond, faceValue: 100m),
            Instrument.FromSymbol("HKEX:00700", "腾讯控股", AssetClass.Equity, lotSize: 100m),
            Instrument.FromSymbol("NASDAQ:AAPL", "Apple", AssetClass.Equity),
            Instrument.FromSymbol("TSE:7203", "トヨタ自動車", AssetClass.Equity, lotSize: 100m),
            Instrument.FromSymbol("FX:USDCNY", "美元/人民币", AssetClass.Fx),
        ];

        foreach (var item in universe)
        {
            store.SaveInstrument(item);
        }

        var stored = store.ListInstruments();
        Assert.Equal(universe.Length, stored.Count);

        var jp = store.GetInstrument("TSE:7203")!;
        Assert.Equal(MarketRegion.Jp, jp.Region);
        Assert.Equal("JPY", jp.Currency);

        var fx = store.GetInstrument("FX:USDCNY")!;
        Assert.Equal("CNY", fx.Currency);
        Assert.Equal(AssetClass.Fx, fx.AssetClass);

        var fund = store.GetInstrument("FUND:110022")!;
        Assert.Equal(AssetClass.MutualFund, fund.AssetClass);
        Assert.Equal(0.01m, fund.LotSize);

        var bond = store.GetInstrument("SSE:113050")!;
        Assert.Equal(100m, bond.FaceValue);
    }

    [Fact]
    public void UpsertsInstrumentInsteadOfDuplicating()
    {
        using var store = NewStore();
        store.SaveInstrument(Instrument.FromSymbol("SSE:600000", "浦发", AssetClass.Equity));
        store.SaveInstrument(Instrument.FromSymbol("SSE:600000", "浦发银行", AssetClass.Equity, lotSize: 100m));

        var stored = Assert.Single(store.ListInstruments());
        Assert.Equal("浦发银行", stored.Name);
        Assert.Equal(100m, stored.LotSize);
    }

    [Fact]
    public void PersistsDecimalsWithoutBinaryFloatDrift()
    {
        using var store = NewStore();
        var txn = LedgerTransaction.Buy("acct", "FUND:110022", new DateOnly(2026, 3, 1),
            quantity: 1234.5678m, price: 3.1415m, currency: "CNY", fee: 0.01m);
        store.AddTransaction(txn);

        var loaded = Assert.Single(store.ListTransactions());
        Assert.Equal(1234.5678m, loaded.Quantity);
        Assert.Equal(3.1415m, loaded.Price);
        Assert.Equal(0.01m, loaded.Fee);
        Assert.Equal(1234.5678m * 3.1415m, loaded.GrossAmount.Amount);
    }

    [Fact]
    public void FiltersTransactionsByAccountSymbolAndDate()
    {
        using var store = NewStore();
        store.AddTransactions([
            LedgerTransaction.Buy("acct_a", "SSE:600000", new DateOnly(2026, 1, 5), 100m, 10m, "CNY"),
            LedgerTransaction.Buy("acct_a", "NASDAQ:AAPL", new DateOnly(2026, 2, 5), 10m, 180m, "USD"),
            LedgerTransaction.Buy("acct_b", "SSE:600000", new DateOnly(2026, 3, 5), 200m, 11m, "CNY"),
        ]);

        Assert.Equal(3, store.ListTransactions().Count);
        Assert.Equal(2, store.ListTransactions(accountId: "acct_a").Count);
        Assert.Equal(2, store.ListTransactions(symbol: "SSE:600000").Count);
        // Venue aliases resolve to the stored identity.
        Assert.Equal(2, store.ListTransactions(symbol: "SH:600000").Count);
        Assert.Equal(2, store.ListTransactions(from: new DateOnly(2026, 2, 1)).Count);
        Assert.Single(store.ListTransactions(
            from: new DateOnly(2026, 2, 1), to: new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void LoadStateReplaysTheStoredLedger()
    {
        using var store = NewStore();
        store.AddTransactions([
            LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 50_000m, "CNY"),
            LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2026, 1, 5), 1000m, 10m, "CNY", fee: 5m),
            LedgerTransaction.Sell("acct", "SSE:600000", new DateOnly(2026, 2, 5), 400m, 12m, "CNY", fee: 4m),
        ]);

        var state = store.LoadState();
        var position = Assert.Single(state.Positions);

        Assert.Equal(600m, position.Quantity);
        Assert.Equal(10.005m, position.AverageCost.Amount);
        // Proceeds 4 800 - 4 fee = 4 796; released cost 10.005 × 400 = 4 002.
        Assert.Equal(794m, position.RealizedPnl.Amount);
        Assert.Equal(44_791m, Assert.Single(state.CashBalances).Amount);
    }

    [Fact]
    public void ReopeningKeepsDataAndDoesNotRerunMigrations()
    {
        var accountId = "";
        using (var store = NewStore())
        {
            accountId = store.SaveAccount(Account.Create("盈透", AccountKind.Securities, "USD")).Id;
            store.AddTransaction(LedgerTransaction.Deposit(accountId, new DateOnly(2026, 1, 2), 1000m, "USD"));
        }

        using (var reopened = NewStore())
        {
            Assert.Single(reopened.ListAccounts());
            Assert.Single(reopened.ListTransactions());
            Assert.Equal(accountId, reopened.ListAccounts()[0].Id);
        }

        Assert.Equal(PortfolioStore.SchemaVersion, ReadUserVersion(Path.Combine(_root, "portfolio.db")));
    }

    [Fact]
    public void BatchInsertIsAtomic()
    {
        using var store = NewStore();
        var good = LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2026, 1, 5), 100m, 10m, "CNY");
        // Same id twice violates the primary key, so nothing from the batch may land.
        var clash = good with { TradeDate = new DateOnly(2026, 1, 6) };

        Assert.ThrowsAny<SqliteException>(() => store.AddTransactions([good, clash]));
        Assert.Empty(store.ListTransactions());
    }

    [Fact]
    public void RemovingAnAccountRemovesItsTransactions()
    {
        using var store = NewStore();
        var account = store.SaveAccount(Account.Create("测试", AccountKind.Cash, "CNY"));
        store.AddTransaction(LedgerTransaction.Deposit(account.Id, new DateOnly(2026, 1, 2), 100m, "CNY"));
        store.AddTransaction(LedgerTransaction.Deposit("other", new DateOnly(2026, 1, 2), 100m, "CNY"));

        Assert.True(store.RemoveAccount(account.Id));
        Assert.Empty(store.ListAccounts());
        Assert.Single(store.ListTransactions());
    }

    [Fact]
    public void RemovesASingleTransaction()
    {
        using var store = NewStore();
        var txn = store.AddTransaction(
            LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 2), 100m, "CNY"));

        Assert.True(store.RemoveTransaction(txn.Id));
        Assert.False(store.RemoveTransaction(txn.Id));
        Assert.Empty(store.ListTransactions());
    }

    private static int ReadUserVersion(string databasePath)
    {
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
