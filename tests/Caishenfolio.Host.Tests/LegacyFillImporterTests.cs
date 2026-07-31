using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

public class LegacyFillImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_legacy_import_tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ImportsFillsAndIsSafeToRunTwice()
    {
        var plans = new PricePlanStore(_root);
        plans.AddFill("SSE:600000", "buy", price: 10.5, qty: 1000, fee: 5, note: "首建仓", ts: "2026-01-05T02:00:00Z");
        plans.AddFill("SSE:600000", "sell", price: 12.0, qty: 400, fee: 4, ts: "2026-02-05T02:00:00Z");
        plans.AddFill("HKEX:00700", "buy", price: 320, qty: 100, fee: 30, ts: "2026-02-06T02:00:00Z");

        using var store = PortfolioStore.UnderStateRoot(_root);
        var account = store.SaveAccount(Account.Create("默认账户", AccountKind.Securities, "CNY"));

        var first = LegacyFillImporter.Import(plans, store, account.Id);
        Assert.Equal(3, first.Imported);
        Assert.Equal(0, first.Skipped);
        Assert.Empty(first.Warnings);

        var second = LegacyFillImporter.Import(plans, store, account.Id);
        Assert.Equal(0, second.Imported);
        Assert.Equal(3, second.Skipped);
        Assert.Equal(3, store.ListTransactions().Count);

        var state = store.LoadState();
        var pufa = state.Positions.Single(p => p.Symbol == "SSE:600000");
        Assert.Equal(600m, pufa.Quantity);
        Assert.Equal(10.505m, pufa.AverageCost.Amount);

        // Currency comes from the venue, which the legacy journal never recorded.
        var tencent = state.Positions.Single(p => p.Symbol == "HKEX:00700");
        Assert.Equal("HKD", tencent.Currency);
        Assert.Equal("CNY", pufa.Currency);
    }

    [Fact]
    public void SkipsUnusableRowsInsteadOfAbortingTheImport()
    {
        var plans = new PricePlanStore(_root);
        plans.AddFill("SSE:600000", "buy", price: 10, qty: 100, ts: "2026-01-05T02:00:00Z");
        plans.AddFill("LSE:VOD", "buy", price: 1.2, qty: 500, ts: "2026-01-06T02:00:00Z");

        using var store = PortfolioStore.UnderStateRoot(_root);
        var result = LegacyFillImporter.Import(plans, store, "acct");

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(result.Warnings, w => w.Contains("LSE:VOD") && w.Contains("计价货币"));
        Assert.Single(store.ListTransactions());
    }

    [Fact]
    public void FallbackCurrencyRescuesUnknownVenues()
    {
        var plans = new PricePlanStore(_root);
        plans.AddFill("LSE:VOD", "buy", price: 1.2, qty: 500, ts: "2026-01-06T02:00:00Z");

        using var store = PortfolioStore.UnderStateRoot(_root);
        var result = LegacyFillImporter.Import(plans, store, "acct", fallbackCurrency: "GBP");

        Assert.Equal(1, result.Imported);
        Assert.Equal("GBP", Assert.Single(store.ListTransactions()).Currency);
    }

    [Fact]
    public void TagsImportedRowsWithTheirBatch()
    {
        var plans = new PricePlanStore(_root);
        plans.AddFill("SSE:600000", "buy", price: 10, qty: 100, ts: "2026-01-05T02:00:00Z");

        using var store = PortfolioStore.UnderStateRoot(_root);
        LegacyFillImporter.Import(plans, store, "acct");

        var txn = Assert.Single(store.ListTransactions());
        Assert.Equal(LegacyFillImporter.BatchId, txn.ImportBatchId);
        Assert.StartsWith("txn_legacy_fill_", txn.Id);
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
