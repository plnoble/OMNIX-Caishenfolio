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
    public void ReversingASpendGivesTheConsumedBatchesBack()
    {
        // A3 from the other side: the task book says 剩余量回到冲正前. Reversing the SPEND must
        // put back what it consumed and drop the batch it created.
        var buy = TwoPurchases()[0];
        var spend = LedgerTransaction.FxExchange(Account, Day3, 300_000m, "JPY", 2_000m, "USD", 0m, "");

        var before = FxLotLedger.FromTransactions([buy, spend]);
        Assert.Equal(300_000m, before.Lots.Single(l => l.Currency == "JPY").RemainingAmount);
        Assert.Single(before.Lots, l => l.Currency == "USD");
        Assert.Single(before.Spends);

        var after = FxLotLedger.FromTransactions([buy, spend, LedgerTransaction.Reversal(spend, "换汇输错")]);

        // Back to the state before the spend: full JPY batch, no USD batch, no consumption.
        var jpy = Assert.Single(after.Lots);
        Assert.Equal("JPY", jpy.Currency);
        Assert.Equal(600_000m, jpy.RemainingAmount);
        Assert.Equal(30_000m, jpy.RemainingCostAmount);
        Assert.Empty(after.Spends);
    }

    [Fact]
    public void MoneyThatLeavesTakesItsCostWithIt()
    {
        // WP-02 (review #3): the batch follows the cash, not the exchange list. Withdraw the whole
        // pot, put back cash without a receipt, then spend it: the new lot must NOT inherit the
        // cost that already left the account.
        var buy = LedgerTransaction.FxExchange(Account, Day1, 30_000m, "CNY", 600_000m, "JPY");
        var withdraw = LedgerTransaction.Withdraw(Account, Day2, 600_000m, "JPY", "取现");
        var redeposit = LedgerTransaction.Deposit(Account, Day3, 300_000m, "JPY", "柜台存回");
        var spend = LedgerTransaction.FxExchange(Account, Day4, 100_000m, "JPY", 660m, "USD", 0m, "");

        var ledger = FxLotLedger.FromTransactions([buy, withdraw, redeposit, spend]);

        // The withdrawal drew the batch to zero…
        var jpy = ledger.Lots.Single(lot => lot.Currency == "JPY");
        Assert.Equal(0m, jpy.RemainingAmount);
        Assert.Equal(0m, jpy.RemainingCostAmount);
        // …and the withdrawal itself is on the trail.
        Assert.Single(ledger.Spends, spend => spend.TransactionId == withdraw.Id);

        // The USD lot was paid with money that has no receipt: no upstream cost is invented.
        var usd = ledger.Lots.Single(lot => lot.Currency == "USD");
        Assert.Equal(100_000m, usd.CostAmount);
        Assert.Empty(usd.FundedBy);
        Assert.Empty(FxLotLedger.UpstreamCosts(ledger, usd));
    }

    [Fact]
    public void AnotherAccountsBatchIsNeverConsumed()
    {
        // Review #4: spending in B must not inherit A's historical cost.
        const string Other = "acct_other";
        var buy = LedgerTransaction.FxExchange(Account, Day1, 30_000m, "CNY", 600_000m, "JPY");
        var deposit = LedgerTransaction.Deposit(Other, Day2, 300_000m, "JPY", "别人转入");
        var spend = LedgerTransaction.FxExchange(Other, Day3, 300_000m, "JPY", 2_000m, "USD", 0m, "");

        var ledger = FxLotLedger.FromTransactions([buy, deposit, spend]);

        // A's batch is untouched by B's spending…
        var aLot = ledger.Lots.Single(lot => lot.AccountId == Account && lot.Currency == "JPY");
        Assert.Equal(600_000m, aLot.RemainingAmount);
        // …and B's USD lot has no cost chain at all.
        var usd = Assert.Single(ledger.Lots, lot => lot.AccountId == Other && lot.Currency == "USD");
        Assert.Empty(usd.FundedBy);
    }

    [Fact]
    public void ACrossAccountExchangePutsTheBatchInTheReceivingAccount()
    {
        const string Receiver = "acct_receiver";
        var move = LedgerTransaction.FxExchange(
            Account, Day1, 30_000m, "CNY", 600_000m, "JPY", 0m, "", counterAccountId: Receiver);

        var lot = Assert.Single(FxLotLedger.FromTransactions([move]).Lots);

        Assert.Equal(Receiver, lot.AccountId);
        Assert.Equal(30_000m, lot.CostAmount);
    }

    [Fact]
    public void APotWithoutAReceiptStillHasAQuantityAndAnUnknownCost()
    {
        // Review #8/A8 at the wiring seam: the alert layer used to bail out when the moving
        // average had no cost, which silently disabled net-arrival alerts for money that arrived
        // without a receipt. The pot must survive with cost = null (unknown, not zero).
        Directory.CreateDirectory(_root);
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveAccount(Portfolio.Account.Create("换汇账户", AccountKind.Bank, "CNY"));
        store.AddTransaction(LedgerTransaction.Deposit("acct", Day1, 1_500m, "JPY", "境外亲戚转入"));

        var held = Notifications.BackgroundExtendedCheck.HeldPositionOf(store, "CNY", "JPY");

        Assert.NotNull(held);
        Assert.Equal(1_500m, held!.Value.amount);
        Assert.Null(held.Value.cost);
    }

    [Fact]
    public void TheHeldCostIsTheFifoRemainderNotTheMovingAverage()
    {
        // Review #2, the smallest discriminating input: 100 CNY → 2,000 JPY, then
        // 200 CNY → 2,000 JPY, then 2,000 JPY out. FIFO leaves the SECOND batch (200 CNY),
        // the moving average would say 150 CNY.
        Directory.CreateDirectory(_root);
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveAccount(Portfolio.Account.Create("换汇账户", AccountKind.Bank, "CNY"));
        // WP-03 R1: the account is funded first; the cost figures this test pins are unchanged.
        store.AddTransaction(LedgerTransaction.OpeningCash("acct", Day1, 1_000m, "CNY", "建账余额"));
        store.AddTransaction(LedgerTransaction.FxExchange("acct", Day1, 100m, "CNY", 2_000m, "JPY"));
        store.AddTransaction(LedgerTransaction.FxExchange("acct", Day2, 200m, "CNY", 2_000m, "JPY"));
        store.AddTransaction(LedgerTransaction.FxExchange("acct", Day3, 2_000m, "JPY", 13.33m, "USD"));

        var held = Notifications.BackgroundExtendedCheck.HeldPositionOf(store, "CNY", "JPY");

        Assert.NotNull(held);
        Assert.Equal(2_000m, held!.Value.amount);
        Assert.Equal(200m, held.Value.cost);
    }

    [Fact]
    public void ADeepChainIsAttributedByShareNotByWhatTheAncestorPaidInTotal()
    {
        // Review #5, repro one: 100 CNY → 2,000 JPY; 1,000 JPY → 10 USD; 5 USD → 4 EUR.
        // The EUR batch used HALF of the USD batch, so it inherits half of that batch's cost:
        // 500 JPY for this segment, and a quarter of the original 100 CNY = 25 CNY behind it.
        var a = LedgerTransaction.FxExchange(Account, Day1, 100m, "CNY", 2_000m, "JPY");
        var b = LedgerTransaction.FxExchange(Account, Day2, 1_000m, "JPY", 10m, "USD", 0m, "");
        var c = LedgerTransaction.FxExchange(Account, Day3, 5m, "USD", 4m, "EUR", 0m, "");

        var ledger = FxLotLedger.FromTransactions([a, b, c]);
        var eur = ledger.Lots.Single(lot => lot.Currency == "EUR");
        var upstream = FxLotLedger.UpstreamCosts(ledger, eur);

        Assert.Equal(500m, upstream["JPY"]);
        Assert.Equal(25m, upstream["CNY"]);
    }

    [Fact]
    public void TwoBranchesFromOneSourceBothCount()
    {
        // Review #5, repro two: one JPY batch funds two USD batches which then merge into EUR.
        // The shared source must appear once per real contribution, not once in total.
        var a = LedgerTransaction.FxExchange(Account, Day1, 100m, "CNY", 2_000m, "JPY");
        var b1 = LedgerTransaction.FxExchange(Account, Day2, 1_000m, "JPY", 10m, "USD", 0m, "");
        var b2 = LedgerTransaction.FxExchange(Account, Day3, 1_000m, "JPY", 10m, "USD", 0m, "");
        var c = LedgerTransaction.FxExchange(Account, Day4, 12m, "USD", 11m, "EUR", 0m, "");

        var ledger = FxLotLedger.FromTransactions([a, b1, b2, c]);
        var eur = ledger.Lots.Single(lot => lot.Currency == "EUR");
        var upstream = FxLotLedger.UpstreamCosts(ledger, eur);

        // 12 USD out of 20: all 10 of the first batch (1,000 JPY / 50 CNY) plus 2 of the second
        // (200 JPY / 10 CNY). The shared JPY source is counted on both branches — the old
        // once-per-lot guard dropped one of them and said 50 CNY.
        Assert.Equal(1_200m, upstream["JPY"]);
        Assert.Equal(60m, upstream["CNY"]);
    }

    [Fact]
    public void CostIsConservedToTheLastUnit()
    {
        // Review #13: 0.05 CNY → 1 JPY, then spend 0.999999 of it. The crumb used to be zeroed,
        // so the rest of the pot silently became free.
        var buy = LedgerTransaction.FxExchange(Account, Day1, 0.05m, "CNY", 1m, "JPY");
        var spend = LedgerTransaction.FxExchange(Account, Day2, 0.999999m, "JPY", 0.006m, "USD", 0m, "");
        var rest = LedgerTransaction.FxExchange(Account, Day3, 0.000001m, "JPY", 0.00000001m, "USD", 0m, "");

        var ledger = FxLotLedger.FromTransactions([buy, spend, rest]);
        var jpy = ledger.Lots.Single(lot => lot.Currency == "JPY");
        var consumed = ledger.Spends
            .Where(s => s.Currency == "JPY")
            .SelectMany(s => s.Lots)
            .Sum(c => c.CostAmount);

        // Everything that left carries its share; nothing evaporates at the last unit.
        Assert.Equal(0m, jpy.RemainingAmount);
        Assert.Equal(0m, jpy.RemainingCostAmount);
        Assert.Equal(0.05m, consumed + jpy.RemainingCostAmount, 10);
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
    public void AnExhaustedBatchNeverAppearsInALaterSpend()
    {
        // Spend the whole first batch, then spend again: the second trail must name only what it
        // actually took, not the empty batch with an amount of 0.
        var transactions = new[]
        {
            TwoPurchases()[0],                                                  // 600,000 JPY
            TwoPurchases()[1],                                                  // 430,000 JPY
            LedgerTransaction.FxExchange(Account, Day3, 600_000m, "JPY", 4_000m, "USD", 0m, ""),
            LedgerTransaction.FxExchange(Account, Day4, 100_000m, "JPY", 660m, "USD", 0m, ""),
        };

        var ledger = FxLotLedger.FromTransactions(transactions);

        var second = ledger.Spends.Single(spend => spend.Date == Day4);
        var consumed = Assert.Single(second.Lots);
        Assert.Equal(100_000m, consumed.Amount);
        Assert.Equal(Day2, ledger.Lots.Single(l => l.Id == consumed.LotId).AcquiredOn);
        // No zero-amount ghosts, and nothing left unexplained.
        Assert.DoesNotContain(second.Lots, lot => lot.Amount <= 0m);
        Assert.Equal(0m, second.UncoveredAmount);
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

    /// <summary>
    /// **Rewritten on purpose (WP-03 D8).** This test used to characterise the old behaviour — an
    /// exchange silently leaving a negative balance. That rule now exists, so the assertions state
    /// the opposite: the write is REFUSED and says by how much. Nothing is loosened; the pure
    /// projection still tolerates such a row, because that is how a pre-upgrade ledger replays
    /// (D2 — history is annotated, never rewritten).
    /// </summary>
    [Fact]
    public void AnOverspendExchangeIsNowRefusedInsteadOfLeavingANegativeBalance()
    {
        Directory.CreateDirectory(_root);
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveAccount(Portfolio.Account.Create("换汇账户", AccountKind.Bank, "CNY", id: Account));
        var exchange = LedgerTransaction.FxExchange(Account, Day1, 100_000m, "CNY", 2_000_000m, "JPY", 0m, "");

        var error = Assert.Throws<LedgerException>(() => store.AddTransaction(exchange));

        Assert.Contains("100,000", error.Message, StringComparison.Ordinal);
        Assert.Contains("差", error.Message, StringComparison.Ordinal);
        Assert.Empty(store.ListTransactions());

        // Reading is unchanged: replaying such a row (as an old ledger does) still yields the
        // negative balance, and the batch still follows the receipt.
        var state = PositionCalculator.Replay([exchange]);
        Assert.Equal(-100_000m, state.CashBalances.Single(balance => balance.Currency == "CNY").Amount);
        var lot = Assert.Single(FxLotLedger.FromTransactions([exchange]).Lots);
        Assert.Equal(2_000_000m, lot.RemainingAmount);
        Assert.Equal(100_000m, lot.CostAmount);
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
        // WP-03 R1: the two purchases need 50,000 CNY of real money behind them.
        store.AddTransaction(LedgerTransaction.OpeningCash(Account, Day1, 50_000m, "CNY", "建账余额"));
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
