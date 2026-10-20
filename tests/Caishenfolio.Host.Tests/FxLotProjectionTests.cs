using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-02 A1–A4: the batch projection. Every amount here is synthetic (30,000 CNY / 600,000 JPY
/// and friends) and the account id follows the existing <c>acct_</c> prefix rule.
/// </summary>
public class FxLotProjectionTests : IDisposable
{
    private const string Account = "acct_fx_ledger";
    private static readonly DateOnly Day1 = new(2025, 1, 10);
    private static readonly DateOnly Day2 = new(2025, 3, 15);
    private static readonly DateOnly Day3 = new(2025, 4, 1);
    private static readonly DateOnly Day4 = new(2025, 5, 1);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_fx_lots", Guid.NewGuid().ToString("N"));

    /// <summary>Two purchases of JPY at different rates — the shape A1 must keep apart.</summary>
    private static LedgerTransaction[] TwoPurchases() =>
    [
        // 600,000 JPY for 30,000 CNY (20 JPY per CNY).
        LedgerTransaction.FxExchange(Account, Day1, 30_000m, "CNY", 600_000m, "JPY", 0m, ""),
        // 430,000 JPY for 20,000 CNY (21.5 JPY per CNY).
        LedgerTransaction.FxExchange(Account, Day2, 20_000m, "CNY", 430_000m, "JPY", 0m, ""),
    ];

    [Fact]
    public void A1_EachPurchaseBecomesItsOwnBatchInsteadOfOneAverage()
    {
        var ledger = FxLotLedger.FromTransactions(TwoPurchases());

        Assert.Equal(2, ledger.Lots.Count);
        var first = ledger.Lots.Single(l => l.AcquiredOn == Day1);
        var second = ledger.Lots.Single(l => l.AcquiredOn == Day2);

        // Each batch keeps its own date, its own amounts and its own derived cost.
        Assert.Equal("JPY", first.Currency);
        Assert.Equal(600_000m, first.OriginalAmount);
        Assert.Equal(600_000m, first.RemainingAmount);
        Assert.Equal("CNY", first.CostCurrency);
        Assert.Equal(30_000m, first.CostAmount);

        Assert.Equal("JPY", second.Currency);
        Assert.Equal(430_000m, second.OriginalAmount);
        Assert.Equal(20_000m, second.CostAmount);
        Assert.NotEqual(first.AcquiredOn, second.AcquiredOn);

        // "Not averaged": a blended average would give both batches the same per-unit cost.
        var firstUnitCost = first.CostAmount / first.OriginalAmount;
        var secondUnitCost = second.CostAmount / second.OriginalAmount;
        Assert.NotEqual(firstUnitCost, secondUnitCost);
    }

    [Fact]
    public void A2_PartialConversionConsumesTheOldestBatchFirstAndSaysSo()
    {
        var transactions = TwoPurchases()
            .Append(
                // Spend half of the first batch: 300,000 JPY → 2,000 USD.
                LedgerTransaction.FxExchange(Account, Day3, 300_000m, "JPY", 2_000m, "USD", 0m, ""))
            .ToArray();

        var ledger = FxLotLedger.FromTransactions(transactions);

        var spend = Assert.Single(ledger.Spends);
        Assert.Equal("JPY", spend.Currency);
        Assert.Equal(300_000m, spend.Amount);
        var consumed = Assert.Single(spend.Lots);
        Assert.Equal("JPY", consumed.Currency);
        Assert.Equal(300_000m, consumed.Amount);
        // Half of a 30,000 CNY batch: the cost follows the amount, in the lot's own currency.
        Assert.Equal(15_000m, consumed.CostAmount);
        Assert.Equal("CNY", consumed.CostCurrency);
        Assert.Equal(0m, spend.UncoveredAmount);

        // FIFO: the January batch is drawn down first; March is untouched.
        var january = ledger.Lots.Single(l => l.AcquiredOn == Day1);
        var march = ledger.Lots.Single(l => l.AcquiredOn == Day2);
        Assert.Equal(300_000m, january.RemainingAmount);
        Assert.Equal(15_000m, january.RemainingCostAmount);
        Assert.Equal(430_000m, march.RemainingAmount);
        Assert.Equal(20_000m, march.RemainingCostAmount);

        // Conservation: what was bought minus what was spent is what is left, in that currency.
        Assert.Equal(730_000m, ledger.Lots.Where(l => l.Currency == "JPY").Sum(l => l.RemainingAmount));
    }

    [Fact]
    public void A3_ReversingAnExchangeTakesItsBatchWithItAndRederivesTheConsumption()
    {
        var januaryBuy = TwoPurchases()[0];
        var marchBuy = TwoPurchases()[1];
        var spend = LedgerTransaction.FxExchange(Account, Day3, 300_000m, "JPY", 2_000m, "USD", 0m, "");

        // Baseline: the spend eats into the January batch. (The USD the spend bought is a lot
        // too, so scope these assertions to the JPY batches.)
        var before = FxLotLedger.FromTransactions([januaryBuy, marchBuy, spend]);
        Assert.Equal(2, before.Lots.Count(l => l.Currency == "JPY"));
        Assert.Equal(300_000m, before.Lots.Single(l => l.AcquiredOn == Day1).RemainingAmount);

        // Reversing the March purchase removes exactly that batch.
        var afterMarch = FxLotLedger.FromTransactions(
            [januaryBuy, marchBuy, spend, LedgerTransaction.Reversal(marchBuy, "汇率谈错了")]);
        var survivor = Assert.Single(afterMarch.Lots.Where(l => l.Currency == "JPY"));
        Assert.Equal(Day1, survivor.AcquiredOn);
        Assert.Equal(300_000m, survivor.RemainingAmount);

        // Reversing the January purchase instead must re-derive the consumption onto March:
        // a projection of the ledger cannot keep pointing at a fact the user took back.
        var afterJanuary = FxLotLedger.FromTransactions(
            [januaryBuy, marchBuy, spend, LedgerTransaction.Reversal(januaryBuy, "成交价录错")]);
        var remaining = Assert.Single(afterJanuary.Lots.Where(l => l.Currency == "JPY"));
        Assert.Equal(Day2, remaining.AcquiredOn);
        Assert.Equal(130_000m, remaining.RemainingAmount);
        var rederived = Assert.Single(afterJanuary.Spends);
        Assert.Equal(remaining.Id, Assert.Single(rederived.Lots).LotId);
        Assert.Equal(0m, rederived.UncoveredAmount);
    }

    [Fact]
    public void A4_AThirdCurrencyBatchCostsWhatThisSegmentPaidNotTheOriginalMoney()
    {
        var cnyToJpy = TwoPurchases()[0];                      // 30,000 CNY → 600,000 JPY
        var jpyToUsdOne = LedgerTransaction.FxExchange(Account, Day3, 300_000m, "JPY", 2_000m, "USD", 0m, "");
        var jpyToUsdTwo = LedgerTransaction.FxExchange(Account, Day4, 200_000m, "JPY", 1_320m, "USD", 0m, "");

        var ledger = FxLotLedger.FromTransactions([cnyToJpy, jpyToUsdOne, jpyToUsdTwo]);

        var usdLots = ledger.Lots.Where(l => l.Currency == "USD").OrderBy(l => l.AcquiredOn).ToArray();
        Assert.Equal(2, usdLots.Length);

        // The core of R4: the USD batch costs JPY (the currency this segment paid), never CNY.
        Assert.All(usdLots, lot => Assert.Equal("JPY", lot.CostCurrency));
        Assert.Equal(300_000m, usdLots[0].CostAmount);
        Assert.Equal(200_000m, usdLots[1].CostAmount);

        // The original money is a separate story, in a different currency, never added in.
        var upstream = FxLotLedger.UpstreamCosts(ledger, usdLots[0]);
        Assert.Equal(15_000m, upstream["CNY"]);
        Assert.DoesNotContain("JPY", upstream.Keys);

        var upstreamSecond = FxLotLedger.UpstreamCosts(ledger, usdLots[1]);
        Assert.Equal(10_000m, upstreamSecond["CNY"]);
    }

    [Fact]
    public void SpendingMoreThanTheBatchesCanExplainIsReportedNotAssigned()
    {
        // An external deposit of JPY has no batch; spending it must not be pinned on a batch.
        var transactions = new[]
        {
            LedgerTransaction.FxExchange(Account, Day1, 30_000m, "CNY", 600_000m, "JPY", 0m, ""),
            LedgerTransaction.Deposit(Account, Day2, 250_000m, "JPY"),
            LedgerTransaction.FxExchange(Account, Day3, 700_000m, "JPY", 4_600m, "USD", 0m, ""),
        };

        var ledger = FxLotLedger.FromTransactions(transactions);

        var spend = Assert.Single(ledger.Spends);
        // 600,000 of the 700,000 is explained by the batch; 100,000 arrived without one.
        Assert.Equal(600_000m, spend.Lots.Sum(l => l.Amount));
        Assert.Equal(100_000m, spend.UncoveredAmount);
        Assert.Contains(ledger.Warnings, w => w.Contains("未被批次覆盖", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFixtureLooksLikeARealReceiptAndSurvivesAStoreRoundTrip()
    {
        // Shape requirements: yyyy-MM-dd dates, existing id prefixes, six-digit CNY and
        // seven-digit JPY magnitudes, one empty fee and one empty note.
        var buy = TwoPurchases()[1];
        Assert.Equal("", buy.Note);
        Assert.Equal(0m, buy.Fee);
        Assert.StartsWith("acct_", Account, StringComparison.Ordinal);

        Directory.CreateDirectory(_root);
        using var store = PortfolioStore.UnderStateRoot(_root);
        // Spelled out because this class has a member called Account (the account id).
        store.SaveAccount(Portfolio.Account.Create("换汇账户", AccountKind.Bank, "CNY", id: Account));
        foreach (var transaction in TwoPurchases())
        {
            store.AddTransaction(transaction);
        }

        var ledger = FxLotLedger.FromTransactions(store.ListTransactions());
        Assert.Equal(2, ledger.Lots.Count);
        Assert.Equal(1_030_000m, ledger.Lots.Sum(l => l.RemainingAmount));
        Assert.All(ledger.Lots, lot => Assert.InRange(lot.OriginalAmount, 100_000m, 9_999_999m));
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
