using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

public class PortfolioWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_workspace_tests", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Day1 = new(2025, 1, 2);
    private static readonly DateOnly AsOf = new(2026, 1, 2);

    private sealed class StubPricing : IMarketPricingSource
    {
        public Dictionary<string, decimal> Prices { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string, string), decimal> Rates { get; } = new();

        public Task<PriceQuote?> TryGetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(Prices.TryGetValue(symbol, out var price)
                ? PriceQuote.Of(symbol, price, CurrencyOf(symbol), AsOf, "stub")
                : null);

        public Task<FxRate?> TryGetFxRateAsync(
            string baseCurrency, string quoteCurrency, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rates.TryGetValue((baseCurrency, quoteCurrency), out var rate)
                ? FxRate.Of(baseCurrency, quoteCurrency, rate, AsOf, "stub")
                : null);

        private static string CurrencyOf(string symbol) =>
            ExchangeRegistry.TryGetQuoteCurrency(SymbolId.Parse(symbol), out var currency) ? currency : "CNY";
    }

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    [Fact]
    public async Task EmptyLedgerRefreshesToAnEmptySnapshot()
    {
        using var store = NewStore();
        var snapshot = await new PortfolioWorkspace(store).RefreshAsync(AsOf);

        Assert.True(snapshot.IsEmpty);
        Assert.True(snapshot.Valuation.TotalValue.IsZero);
        Assert.Empty(snapshot.Warnings);
        Assert.Null(snapshot.Xirr);
        Assert.Equal("CNY", snapshot.BaseCurrency);
    }

    [Fact]
    public async Task RefreshValuesAMultiMarketLedgerAndComputesXirr()
    {
        using var store = NewStore();
        var pricing = new StubPricing();
        pricing.Prices["SSE:600000"] = 12m;
        pricing.Prices["NASDAQ:AAPL"] = 200m;
        pricing.Rates[("USD", "CNY")] = 7.2m;

        var workspace = new PortfolioWorkspace(store, pricing);
        var account = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");
        workspace.Record(LedgerTransaction.Deposit(account.Id, Day1, 100_000m, "CNY"));
        workspace.Record(
            LedgerTransaction.Buy(account.Id, "SSE:600000", Day1, 1000m, 10m, "CNY"), "浦发银行");
        workspace.Record(
            LedgerTransaction.Buy(account.Id, "NASDAQ:AAPL", Day1, 10m, 180m, "USD"), "Apple");

        var snapshot = await workspace.RefreshAsync(AsOf);

        Assert.True(snapshot.Valuation.IsComplete);
        // 12 000 CNY + 2 000 USD × 7.2 = 26 400 in holdings.
        Assert.Equal(26_400m, snapshot.Valuation.HoldingsValue.Amount);
        Assert.Equal(2, snapshot.Valuation.Positions.Count);
        Assert.Single(snapshot.Accounts);
        Assert.NotNull(snapshot.Xirr);
    }

    [Fact]
    public async Task RecordingATradeRemembersTheInstrument()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        workspace.Record(LedgerTransaction.Buy("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"), "トヨタ自動車");

        var instrument = Assert.Single(store.ListInstruments());
        Assert.Equal("TSE:7203", instrument.Symbol);
        Assert.Equal("トヨタ自動車", instrument.Name);
        Assert.Equal(MarketRegion.Jp, instrument.Region);
        Assert.Equal("JPY", instrument.Currency);

        var snapshot = await workspace.RefreshAsync(AsOf);
        Assert.Single(snapshot.Instruments);
    }

    [Fact]
    public async Task WithoutAPricingSourceHoldingsAreUnpricedNotZeroed()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        workspace.Record(LedgerTransaction.Buy("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"));
        workspace.Record(LedgerTransaction.Deposit("acct", Day1, 50_000m, "CNY"));

        var snapshot = await workspace.RefreshAsync(AsOf);

        Assert.False(snapshot.Valuation.IsComplete);
        Assert.Contains(snapshot.Warnings, w => w.Contains("SSE:600000"));
        Assert.True(snapshot.Valuation.HoldingsValue.IsZero);
        // Cash is still known exactly; only the priced part is missing.
        Assert.Equal(40_000m, snapshot.Valuation.CashValue.Amount);
    }

    [Fact]
    public async Task XirrIsSkippedRatherThanWrongWhenFlowsAreMultiCurrency()
    {
        using var store = NewStore();
        var pricing = new StubPricing();
        pricing.Rates[("USD", "CNY")] = 7.2m;
        var workspace = new PortfolioWorkspace(store, pricing);
        workspace.Record(LedgerTransaction.Deposit("acct", Day1, 10_000m, "CNY"));
        workspace.Record(LedgerTransaction.Deposit("acct", Day1, 1_000m, "USD"));

        var snapshot = await workspace.RefreshAsync(AsOf);

        Assert.Null(snapshot.Xirr);
        Assert.Equal(17_200m, snapshot.Valuation.TotalValue.Amount);
    }

    [Fact]
    public async Task ImportsCsvAndBackfillsInstrumentMetadata()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");

        const string csv = """
            日期,账户,类型,标的,数量,价格,货币
            2025-01-02,华泰证券,买入,SSE:600000,1000,10,CNY
            2025-01-03,华泰证券,买入,HKEX:00700,100,320,HKD
            """;

        var preview = workspace.PreviewImport(csv, account.Id);
        Assert.Equal(2, preview.Importable);
        Assert.Equal(2, workspace.CommitImport(preview));

        // The account name in the file resolved to its id.
        Assert.All(store.ListTransactions(), t => Assert.Equal(account.Id, t.AccountId));

        var snapshot = await workspace.RefreshAsync(AsOf);
        Assert.Equal(2, snapshot.Instruments.Count);
        Assert.Equal(MarketRegion.Hk, snapshot.Instruments.Single(i => i.Symbol == "HKEX:00700").Region);
    }

    [Fact]
    public async Task ExportsRoundTripThroughTheSnapshot()
    {
        using var store = NewStore();
        var pricing = new StubPricing();
        pricing.Prices["SSE:600000"] = 12m;
        var workspace = new PortfolioWorkspace(store, pricing);
        var account = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");
        workspace.Record(LedgerTransaction.Buy(account.Id, "SSE:600000", Day1, 1000m, 10m, "CNY"), "浦发银行");

        var snapshot = await workspace.RefreshAsync(AsOf);

        Assert.Contains("浦发银行", workspace.ExportPositionsCsv(snapshot));
        Assert.Contains("华泰证券", workspace.ExportPositionsCsv(snapshot));
        Assert.Contains("品种", workspace.ExportAllocationCsv(snapshot));

        var reimported = TransactionCsvImporter.Preview(
            workspace.ExportTransactionsCsv(snapshot), account.Id);
        Assert.Equal(1, reimported.Importable);
    }

    [Fact]
    public async Task LegacyFillsCanBePulledInOnce()
    {
        var plans = new MarketData.PricePlanStore(_root);
        plans.AddFill("SSE:600000", "buy", price: 10, qty: 1000, fee: 5, ts: "2025-01-02T02:00:00Z");

        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("默认账户", AccountKind.Securities, "CNY");

        Assert.Equal(1, workspace.ImportLegacyFills(plans, account.Id).Imported);
        Assert.Equal(0, workspace.ImportLegacyFills(plans, account.Id).Imported);

        var snapshot = await workspace.RefreshAsync(AsOf);
        Assert.Equal(1000m, snapshot.Valuation.Positions.Single().Position.Quantity);
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
