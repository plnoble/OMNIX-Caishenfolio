using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

public class IpoSubscriptionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_ipo", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Applied = new(2026, 3, 1);
    private static readonly DateOnly Paid = new(2026, 3, 4);
    private static readonly DateOnly Listed = new(2026, 3, 12);

    private static IpoSubscription New(decimal issuePrice = 20m) =>
        IpoSubscription.Create("acct", "SSE:601000", Applied, 1000m, issuePrice, "CNY", "某某股份");

    [Fact]
    public void ALifecycleFromApplicationToSale()
    {
        var ipo = New()
            .WithAllotment(500m)
            .WithPayment(Paid, Listed)
            .WithSale(Listed, 32m, fee: 20m);

        Assert.Equal(IpoStatus.Sold, ipo.Status);
        Assert.True(ipo.IsAllotted);
        Assert.Equal(10_000m, ipo.Cost.Amount);
        // 500 × (32 - 20) - 20 fee
        Assert.Equal(5_980m, ipo.RealizedProfit!.Value.Amount);
    }

    [Fact]
    public void AFailedDrawIsARecordedOutcomeNotAMissingRow()
    {
        var ipo = New().WithAllotment(0m);

        Assert.Equal(IpoStatus.NotAllotted, ipo.Status);
        Assert.False(ipo.IsAllotted);
        Assert.True(ipo.IsClosed);
        Assert.True(ipo.Cost.IsZero);
        Assert.Null(ipo.RealizedProfit);
    }

    [Fact]
    public void ProfitIsUnknownUntilSoldRatherThanZero()
    {
        Assert.Null(New().RealizedProfit);
        Assert.Null(New().WithAllotment(500m).RealizedProfit);
        Assert.Null(New().WithAllotment(500m).WithPayment(Paid).RealizedProfit);
    }

    [Fact]
    public void AnAbandonedAllotmentCostsNothing()
    {
        var ipo = New().WithAllotment(500m).WithAbandonment();

        Assert.Equal(IpoStatus.Abandoned, ipo.Status);
        Assert.True(ipo.Cost.IsZero);
        Assert.True(ipo.IsClosed);
    }

    [Fact]
    public void ImpossibleTransitionsAreRefused()
    {
        Assert.Throws<LedgerException>(() => New().WithAllotment(2000m));
        Assert.Throws<LedgerException>(() => New().WithAllotment(-1m));
        Assert.Throws<LedgerException>(() => New().WithAllotment(0m).WithPayment(Paid));
        Assert.Throws<LedgerException>(() => New().WithAllotment(0m).WithAbandonment());
        // Selling something never paid for.
        Assert.Throws<LedgerException>(() => New().WithAllotment(500m).WithSale(Listed, 30m));
    }

    [Fact]
    public void RejectsAnImpossibleApplication()
    {
        Assert.Throws<LedgerException>(() =>
            IpoSubscription.Create("acct", "SSE:601000", Applied, 0m, 20m, "CNY"));
        Assert.Throws<LedgerException>(() =>
            IpoSubscription.Create("acct", "SSE:601000", Applied, 1000m, 0m, "CNY"));
        Assert.Throws<LedgerException>(() =>
            IpoSubscription.Create("acct", "601000", Applied, 1000m, 20m, "CNY"));
    }

    [Fact]
    public void AnAllotmentBecomesRealLedgerTransactions()
    {
        var paid = New().WithAllotment(500m).WithPayment(Paid, Listed);
        var buy = Assert.Single(paid.ToLedgerTransactions());

        Assert.Equal(TransactionKind.Buy, buy.Kind);
        Assert.Equal("SSE:601000", buy.Symbol);
        Assert.Equal(500m, buy.Quantity);
        Assert.Equal(20m, buy.Price);
        Assert.Equal(Paid, buy.TradeDate);

        var sold = paid.WithSale(Listed, 32m, 20m);
        var transactions = sold.ToLedgerTransactions();
        Assert.Equal(2, transactions.Count);
        Assert.Equal(TransactionKind.Sell, transactions[1].Kind);
        Assert.Equal(32m, transactions[1].Price);
    }

    [Fact]
    public void NothingIsMirroredBeforeThereIsAHolding()
    {
        Assert.Empty(New().ToLedgerTransactions());
        Assert.Empty(New().WithAllotment(0m).ToLedgerTransactions());
        Assert.Empty(New().WithAllotment(500m).ToLedgerTransactions());
        Assert.Empty(New().WithAllotment(500m).WithAbandonment().ToLedgerTransactions());
    }

    [Fact]
    public void StatisticsCountTheFailuresToo()
    {
        IpoSubscription[] history =
        [
            New().WithAllotment(0m),
            New().WithAllotment(0m),
            New().WithAllotment(0m),
            New().WithAllotment(500m).WithPayment(Paid).WithSale(Listed, 32m, 20m),
        ];

        var stats = IpoStatistics.From(history, "CNY");

        Assert.Equal(4, stats.Subscriptions);
        Assert.Equal(1, stats.Allotments);
        // Remembering only the win would report a 100% hit rate.
        Assert.Equal(0.25m, stats.HitRate);
        Assert.Equal(5_980m, stats.RealizedProfit.Amount);
        Assert.Equal(5_980m, stats.AveragePerAllotment!.Value.Amount);
    }

    [Fact]
    public void PendingDrawsAreNotCountedAsMisses()
    {
        IpoSubscription[] history = [New(), New(), New().WithAllotment(500m)];

        var stats = IpoStatistics.From(history, "CNY");

        // Only the one resolved draw counts toward the rate.
        Assert.Equal(1m, stats.HitRate);
        Assert.Equal(3, stats.Subscriptions);
    }

    [Fact]
    public void NoResolvedDrawMeansNoHitRateRatherThanZero()
    {
        var stats = IpoStatistics.From([New()], "CNY");

        Assert.Null(stats.HitRate);
        Assert.Null(stats.AveragePerAllotment);
        Assert.True(stats.RealizedProfit.IsZero);
    }

    [Fact]
    public void StoreRoundTripsAndMirrorsIntoTheLedger()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var ipo = New().WithAllotment(500m).WithPayment(Paid, Listed);
        store.SaveIpoSubscription(ipo);

        var loaded = Assert.Single(store.ListIpoSubscriptions());
        Assert.Equal(IpoStatus.Paid, loaded.Status);
        Assert.Equal(500m, loaded.AllottedQuantity);
        Assert.Equal(Listed, loaded.ListingDate);

        // The allotment is a real position, not a number in a parallel table.
        var position = Assert.Single(store.LoadState().Positions);
        Assert.Equal("SSE:601000", position.Symbol);
        Assert.Equal(500m, position.Quantity);
    }

    [Fact]
    public void SavingAgainAfterASaleUpdatesRatherThanDuplicates()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var ipo = New().WithAllotment(500m).WithPayment(Paid, Listed);
        store.SaveIpoSubscription(ipo);
        store.SaveIpoSubscription(ipo.WithSale(Listed, 32m, 20m));

        Assert.Single(store.ListIpoSubscriptions());
        Assert.Equal(2, store.ListTransactions().Count);

        var position = Assert.Single(store.LoadState().Positions);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(5_980m, position.RealizedPnl.Amount);
    }

    [Fact]
    public void RemovingARecordRemovesItsPositionToo()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var ipo = New().WithAllotment(500m).WithPayment(Paid, Listed);
        store.SaveIpoSubscription(ipo);

        Assert.True(store.RemoveIpoSubscription(ipo.Id));
        Assert.Empty(store.ListIpoSubscriptions());
        // A holding that outlived its record would quietly inflate the portfolio.
        Assert.Empty(store.ListTransactions());
    }

    [Fact]
    public void FiltersByAccount()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveIpoSubscription(New());
        store.SaveIpoSubscription(
            IpoSubscription.Create("acct_b", "SZSE:301000", Applied, 500m, 15m, "CNY"));

        Assert.Equal(2, store.ListIpoSubscriptions().Count);
        Assert.Single(store.ListIpoSubscriptions("acct_b"));
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
