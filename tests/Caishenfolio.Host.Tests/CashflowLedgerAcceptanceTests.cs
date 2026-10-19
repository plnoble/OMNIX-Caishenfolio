using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-01 acceptance vectors A1–A9, written before the implementation so every one of them starts
/// red and turns green only when the behaviour is actually there.
///
/// Amounts are synthetic — this repository is public, so no figure from a real ledger appears
/// here — but they are shaped like real records: <c>yyyy-MM-dd</c> dates, the existing
/// <c>acct_…</c> id prefix, six-figure CNY and seven-figure JPY magnitudes. The A1 scenario keeps
/// the task book's *relationships* (期初 + 净投入 + 盈亏 = 期末) with substituted amounts, because
/// the literal figures in the task book are a real user's and must not enter the repo.
/// </summary>
public class CashflowLedgerAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_cashflow_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    // --- A1/A2: balance-mode account ------------------------------------------------

    /// <summary>
    /// A1. 期初 0，投入三笔共 180,000，转出一笔 12,000，最后报当天资产金额 195,000。
    /// 净投入 168,000；累计收益 27,000；期末 = 期初 + 净投入 + 盈亏。
    /// </summary>
    [Fact]
    public async Task A1_BalanceModeAccountReportsNetInvestmentAndCumulativePnl()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var account = workspace.AddAccount(
            "长期账户", AccountKind.Securities, "CNY", mode: AccountValuationMode.Balance);

        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2022, 9, 13), 120_000.00m, "CNY"));
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2023, 3, 6), 45_500.00m, "CNY"));
        workspace.Record(LedgerTransaction.Withdraw(account.Id, new DateOnly(2023, 11, 8), 12_000.00m, "CNY"));
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2024, 2, 19), 14_500.00m, "CNY"));
        workspace.Record(LedgerTransaction.BalanceAssertion(
            account.Id, new DateOnly(2025, 6, 30), 195_000.00m, "CNY"));

        var asOf = new DateOnly(2025, 12, 31);
        var snapshot = await workspace.RefreshAsync(asOf);
        var summary = Assert.Single(snapshot.AccountSummaries, s => s.AccountId == account.Id);

        Assert.True(summary.ValueKnown);
        Assert.Equal(195_000.00m, summary.TotalValueBase!.Value.Amount);
        Assert.Equal(168_000.00m, summary.NetInvestedBase!.Value.Amount);
        Assert.Equal(27_000.00m, summary.CumulativePnlBase!.Value.Amount);

        // 期末 = 期初 + 净投入 + 盈亏, with 期初 = 0.
        Assert.Equal(
            summary.NetInvestedBase.Value.Amount + summary.CumulativePnlBase.Value.Amount,
            summary.TotalValueBase.Value.Amount);
    }

    /// <summary>
    /// A2. The account-level money-weighted return has a value and equals a direct
    /// <see cref="ReturnMetrics.Xirr"/> over the same flows and closing value.
    /// </summary>
    [Fact]
    public async Task A2_MoneyWeightedReturnMatchesReturnMetricsDirectly()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var account = workspace.AddAccount(
            "长期账户", AccountKind.Securities, "CNY", mode: AccountValuationMode.Balance);
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2022, 9, 13), 120_000.00m, "CNY"));
        workspace.Record(LedgerTransaction.Withdraw(account.Id, new DateOnly(2023, 11, 8), 12_000.00m, "CNY"));
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2024, 2, 19), 60_000.00m, "CNY"));
        workspace.Record(LedgerTransaction.BalanceAssertion(
            account.Id, new DateOnly(2025, 6, 30), 195_000.00m, "CNY"));

        var asOf = new DateOnly(2025, 12, 31);
        var snapshot = await workspace.RefreshAsync(asOf);
        var summary = Assert.Single(snapshot.AccountSummaries, s => s.AccountId == account.Id);
        var state = PositionCalculator.Replay(store.ListTransactions());

        Assert.NotNull(summary.MoneyWeightedReturn.Value);
        Assert.True(summary.MoneyWeightedReturn.IsComplete);
        var expected = ReturnMetrics.Xirr(state.ExternalFlows, Money.Of(195_000m, "CNY"), asOf);
        Assert.NotNull(expected);
        Assert.Equal(expected!.Value, summary.MoneyWeightedReturn.Value!.Value, 9);

        // The portfolio-level figure is the same series for a single-account ledger.
        Assert.Equal(expected.Value, snapshot.Xirr!.Value, 9);
        Assert.True(snapshot.XirrStatus.IsComplete);
    }

    // --- A3: balance assertions -----------------------------------------------------

    /// <summary>
    /// A3. Two assertions on different dates: the later one replaces the earlier one, and neither
    /// touches the cash balance or the external flows (R1 — it is not a cash movement).
    /// </summary>
    [Fact]
    public async Task A3_LatestBalanceAssertionWinsAndNeverBecomesCash()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var account = workspace.AddAccount(
            "长期账户", AccountKind.Securities, "CNY", mode: AccountValuationMode.Balance);
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2024, 1, 2), 50_000m, "CNY"));
        workspace.Record(LedgerTransaction.BalanceAssertion(account.Id, new DateOnly(2025, 1, 31), 60_000m, "CNY"));
        var latest = workspace.Record(
            LedgerTransaction.BalanceAssertion(account.Id, new DateOnly(2025, 6, 30), 70_000m, "CNY"));

        var state = PositionCalculator.Replay(store.ListTransactions());
        var cash = Assert.Single(state.CashBalances);
        Assert.Equal(50_000m, cash.Amount);
        var flow = Assert.Single(state.ExternalFlows);
        Assert.Equal(TransactionKind.Deposit, flow.Kind);
        Assert.DoesNotContain(state.AccountFlows, f => f.Kind == TransactionKind.BalanceAssertion);

        var snapshot = await workspace.RefreshAsync(new DateOnly(2025, 12, 31));
        var summary = Assert.Single(snapshot.AccountSummaries, s => s.AccountId == account.Id);

        // 70,000 replaces 60,000 — it is not added to it, and the deposit is inside the total.
        Assert.Equal(70_000m, summary.TotalValueBase!.Value.Amount);
        Assert.NotEqual(130_000m, summary.TotalValueBase.Value.Amount);
        Assert.Equal(latest.Id, state.BalanceAssertions[account.Id].TransactionId);
    }

    // --- A4/A5: transfers ------------------------------------------------------------

    /// <summary>
    /// A4. Same-currency account transfer: visible from both accounts, paired through one row,
    /// excluded from the portfolio's external flows, and it does not change the portfolio total.
    /// </summary>
    [Fact]
    public async Task A4_TransferIsPairedAndInternalToThePortfolio()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var from = workspace.AddAccount("账户甲", AccountKind.Securities, "CNY");
        var to = workspace.AddAccount("账户乙", AccountKind.Bank, "CNY");
        workspace.Record(LedgerTransaction.Deposit(from.Id, new DateOnly(2025, 1, 2), 100_000m, "CNY"));
        workspace.Record(LedgerTransaction.Deposit(to.Id, new DateOnly(2025, 1, 2), 5_000m, "CNY"));

        var before = await workspace.RefreshAsync(new DateOnly(2025, 1, 31));
        var transfer = workspace.Record(
            LedgerTransaction.Transfer(from.Id, to.Id, new DateOnly(2025, 2, 3), 10_000m, "CNY", note: "调拨"));
        var after = await workspace.RefreshAsync(new DateOnly(2025, 2, 28));

        Assert.Equal(before.Valuation.TotalValue.Amount, after.Valuation.TotalValue.Amount);
        Assert.Equal(105_000m, after.Valuation.TotalValue.Amount);

        var state = PositionCalculator.Replay(store.ListTransactions());
        Assert.DoesNotContain(state.ExternalFlows, f => f.Kind == TransactionKind.Transfer);
        Assert.Equal(2, state.ExternalFlows.Count);

        // Account level: the money left 甲 and arrived in 乙, each side signed for its own account.
        var outLeg = Assert.Single(state.FlowsForAccount(from.Id), f => f.Kind == TransactionKind.Transfer);
        Assert.Equal(-10_000m, outLeg.Amount.Amount);
        var inLeg = Assert.Single(state.FlowsForAccount(to.Id), f => f.Kind == TransactionKind.Transfer);
        Assert.Equal(10_000m, inLeg.Amount.Amount);
        Assert.False(outLeg.CrossesPortfolioBoundary);

        // Paired and traceable from both accounts' own listings.
        Assert.Equal(to.Id, transfer.CounterAccountId);
        Assert.Equal(transfer.Id, Assert.Single(store.ListTransactions(accountId: from.Id), t => t.Id == transfer.Id).Id);
        Assert.Equal(transfer.Id, Assert.Single(store.ListTransactions(accountId: to.Id), t => t.Id == transfer.Id).Id);

        // Cash follows the transfer, not a deposit/withdrawal pair.
        Assert.Equal(90_000m, state.CashBalances.Single(b => b.AccountId == from.Id).Amount);
        Assert.Equal(15_000m, state.CashBalances.Single(b => b.AccountId == to.Id).Amount);
    }

    /// <summary>
    /// A5. Cross-currency (CNY out, JPY in) is an exchange with no rate typed: the implied rate is
    /// derived and shown, the portfolio does not treat either leg as external money (R4), and the
    /// account-level legs still move.
    /// </summary>
    [Fact]
    public async Task A5_CrossCurrencyMoveNeedsNoRateAndStaysInsideThePortfolio()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var bank = workspace.AddAccount("银行账户", AccountKind.Bank, "CNY");
        var yen = workspace.AddAccount("日元账户", AccountKind.Bank, "JPY");
        workspace.Record(LedgerTransaction.Deposit(bank.Id, new DateOnly(2025, 1, 2), 200_000m, "CNY"));

        var fx = workspace.Record(LedgerTransaction.FxExchange(
            bank.Id, new DateOnly(2025, 1, 6), 50_000m, "CNY", 1_050_000m, "JPY",
            note: "按回单两边金额", counterAccountId: yen.Id));

        // Derived for display only — the booked amounts are the two receipt amounts.
        Assert.Equal(21m, fx.FxRate);
        Assert.Equal(50_000m, fx.CashAmount);
        Assert.Equal(1_050_000m, fx.CounterAmount);
        Assert.Equal("JPY", fx.CounterCurrency);

        var state = PositionCalculator.Replay(store.ListTransactions());
        Assert.DoesNotContain(state.ExternalFlows, f => f.Kind == TransactionKind.FxExchange);
        Assert.Single(state.ExternalFlows);

        var outLeg = Assert.Single(state.FlowsForAccount(bank.Id), f => f.Kind == TransactionKind.FxExchange);
        Assert.Equal(-50_000m, outLeg.Amount.Amount);
        var inLeg = Assert.Single(state.FlowsForAccount(yen.Id), f => f.Kind == TransactionKind.FxExchange);
        Assert.Equal(1_050_000m, inLeg.Amount.Amount);
    }

    // --- A6/A7: multi-currency returns (R5) ------------------------------------------

    /// <summary>
    /// A6. Base CNY, one JPY deposit: the return must still be computable, with the flow folded in
    /// at <em>its own</em> day's rate — not today's.
    /// </summary>
    [Fact]
    public async Task A6_ForeignFlowIsConvertedAtItsOwnDayRate()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var account = workspace.AddAccount("日元账户", AccountKind.Bank, "JPY");
        var flowDate = new DateOnly(2025, 1, 2);
        var asOf = new DateOnly(2025, 12, 31);
        store.SaveFxRate(FxRate.Of("JPY", "CNY", 0.0482m, flowDate, "fixture"));
        store.SaveFxRate(FxRate.Of("JPY", "CNY", 0.0485m, asOf, "fixture"));

        workspace.Record(LedgerTransaction.Deposit(account.Id, flowDate, 1_000_000m, "JPY"));

        var snapshot = await workspace.RefreshAsync(asOf);

        Assert.True(snapshot.XirrStatus.IsComplete);
        Assert.NotNull(snapshot.Xirr);

        // 1,000,000 × 0.0482 = 48,200 in; 1,000,000 × 0.0485 = 48,500 out.
        var expected = ReturnMetrics.Xirr([
            new DatedAmount(flowDate, -48_200m),
            new DatedAmount(asOf, 48_500m),
        ]);
        Assert.NotNull(expected);
        Assert.Equal(expected!.Value, snapshot.Xirr!.Value, 9);

        // Today's rate would give a different answer; this asserts it was not substituted.
        var withTodaysRate = ReturnMetrics.Xirr([
            new DatedAmount(flowDate, -48_500m),
            new DatedAmount(asOf, 48_500m),
        ]);
        Assert.NotNull(withTodaysRate);
        Assert.NotEqual(withTodaysRate!.Value, snapshot.Xirr.Value);
        Assert.Equal(48_500m, snapshot.Valuation.TotalValue.Amount);
    }

    /// <summary>
    /// A7. Same as A6 but the flow's own day has no stored rate: the result is reported as
    /// incomplete with the missing date named. It is neither computed from today's rate (which
    /// would print a confident 0.00%) nor hidden behind a silent null.
    /// </summary>
    [Fact]
    public async Task A7_MissingFlowDayRateIsNamedInsteadOfSilentlySkipped()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var account = workspace.AddAccount("日元账户", AccountKind.Bank, "JPY");
        var flowDate = new DateOnly(2025, 1, 2);
        var asOf = new DateOnly(2025, 12, 31);
        // Only the valuation day was ever observed; the flow day is a genuine gap.
        store.SaveFxRate(FxRate.Of("JPY", "CNY", 0.0485m, asOf, "fixture"));

        workspace.Record(LedgerTransaction.Deposit(account.Id, flowDate, 1_000_000m, "JPY"));

        var snapshot = await workspace.RefreshAsync(asOf);

        Assert.Null(snapshot.Xirr);
        Assert.False(snapshot.XirrStatus.IsComplete);
        Assert.Contains(snapshot.XirrStatus.MissingInputs, item => item.Contains("2025-01-02", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.XirrStatus.Reason));
        // Today's rate would have produced exactly 0.00% here; a null with a reason is the honest answer.
        Assert.Contains(snapshot.Warnings, w => w.Contains("2025-01-02", StringComparison.Ordinal));
        Assert.Equal(48_500m, snapshot.Valuation.TotalValue.Amount);

        // And the per-account totals must be unknown too, not built from whatever happened to
        // convert. A 0 净投入 against a 48 500 市值 would print a confident +48 500 累计收益 for an
        // account that has only ever been funded.
        var summary = snapshot.AccountSummaries.Single(s => s.AccountId == account.Id);
        Assert.Equal(48_500m, summary.TotalValueBase!.Value.Amount);
        Assert.Null(summary.NetInvestedBase);
        Assert.Null(summary.CumulativePnlBase);
        Assert.Null(summary.Annualized);
        Assert.Contains(summary.Warnings, w => w.Contains("2025-01-02", StringComparison.Ordinal));
    }

    // --- A8: valuation modes (R2) -----------------------------------------------------

    /// <summary>
    /// A8. A balance-mode account refuses position records (D1) and its own total replaces — never
    /// adds to — the cash-plus-holdings figure. A detail-mode account refuses a balance assertion (D5).
    /// </summary>
    [Fact]
    public async Task A8_ValuationModesNeverDoubleCountAndRefuseEachOthersRecords()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var balance = workspace.AddAccount(
            "余额账户", AccountKind.Bank, "CNY", mode: AccountValuationMode.Balance);

        var position = Assert.Throws<LedgerException>(() => workspace.Record(
            LedgerTransaction.Buy(balance.Id, "SSE:600000", new DateOnly(2025, 1, 2), 100m, 10m, "CNY")));
        Assert.Contains("余额", position.Message);

        workspace.Record(LedgerTransaction.Deposit(balance.Id, new DateOnly(2025, 1, 2), 50_000m, "CNY"));
        workspace.Record(LedgerTransaction.BalanceAssertion(balance.Id, new DateOnly(2025, 6, 30), 60_000m, "CNY"));

        var snapshot = await workspace.RefreshAsync(new DateOnly(2025, 12, 31));
        var summary = Assert.Single(snapshot.AccountSummaries, s => s.AccountId == balance.Id);

        Assert.Equal(60_000m, summary.TotalValueBase!.Value.Amount);
        Assert.Equal(60_000m, snapshot.Valuation.TotalValue.Amount);
        Assert.Equal(0m, snapshot.Valuation.CashValue.Amount);

        var detail = workspace.AddAccount("明细账户", AccountKind.Securities, "CNY");
        var assertion = Assert.Throws<LedgerException>(() => workspace.Record(
            LedgerTransaction.BalanceAssertion(detail.Id, new DateOnly(2025, 6, 30), 1_000m, "CNY")));
        Assert.Contains("余额", assertion.Message);
    }

    // --- A9: reversals (R7) -----------------------------------------------------------

    /// <summary>
    /// A9. Reversing an assertion or a transfer removes its effect on replay while both rows stay
    /// visible in the ledger.
    /// </summary>
    [Fact]
    public async Task A9_ReversalCancelsAnAssertionOrATransferWithoutDeletingEither()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var account = workspace.AddAccount(
            "长期账户", AccountKind.Bank, "CNY", mode: AccountValuationMode.Balance);
        var other = workspace.AddAccount("账户乙", AccountKind.Bank, "CNY");
        workspace.Record(LedgerTransaction.Deposit(account.Id, new DateOnly(2025, 1, 2), 50_000m, "CNY"));
        workspace.Record(LedgerTransaction.BalanceAssertion(account.Id, new DateOnly(2025, 3, 31), 60_000m, "CNY"));
        var late = workspace.Record(
            LedgerTransaction.BalanceAssertion(account.Id, new DateOnly(2025, 6, 30), 70_000m, "CNY"));
        var transfer = workspace.Record(
            LedgerTransaction.Transfer(account.Id, other.Id, new DateOnly(2025, 5, 5), 8_000m, "CNY"));

        // Replaying as if both corrections happened.
        var beforeReverse = PositionCalculator.Replay(store.ListTransactions());
        Assert.Equal(70_000m, beforeReverse.BalanceAssertions[account.Id].Value.Amount);
        Assert.Contains(beforeReverse.AccountFlows, f => f.Kind == TransactionKind.Transfer);

        workspace.ReverseTransaction(late.Id, "金额记错");
        workspace.ReverseTransaction(transfer.Id, "转错账户");

        var after = PositionCalculator.Replay(store.ListTransactions());
        Assert.Equal(60_000m, after.BalanceAssertions[account.Id].Value.Amount);
        Assert.DoesNotContain(after.AccountFlows, f => f.Kind == TransactionKind.Transfer);
        Assert.Equal(50_000m, after.CashBalances.Single(b => b.AccountId == account.Id).Amount);

        var rows = store.ListTransactions();
        Assert.Contains(rows, t => t.Id == late.Id);
        Assert.Contains(rows, t => t.Id == transfer.Id);
        Assert.Contains(rows, t => t.ReversesTransactionId == late.Id);
        Assert.Contains(rows, t => t.ReversesTransactionId == transfer.Id);

        var snapshot = await workspace.RefreshAsync(new DateOnly(2025, 12, 31));
        var summary = Assert.Single(snapshot.AccountSummaries, s => s.AccountId == account.Id);
        Assert.Equal(60_000m, summary.TotalValueBase!.Value.Amount);
    }

    // --- A11: the existing trade path is untouched -------------------------------------

    /// <summary>
    /// A11. The stock buy/sell path keeps its existing semantics: cost averaging, fees into cost,
    /// realized P&amp;L on the way out. The full existing suite is the primary evidence; this pins
    /// the one behaviour WP-01 could plausibly have disturbed.
    /// </summary>
    [Fact]
    public void A11_StockTradeSemanticsAreUnchanged()
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.Deposit("acct", new DateOnly(2025, 1, 2), 100_000m, "CNY"),
            LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2025, 1, 3), 1_000m, 10m, "CNY", fee: 5m),
            LedgerTransaction.Buy("acct", "SSE:600000", new DateOnly(2025, 2, 3), 1_000m, 12m, "CNY", fee: 6m),
            LedgerTransaction.Sell("acct", "SSE:600000", new DateOnly(2025, 3, 3), 500m, 15m, "CNY", fee: 4m, tax: 3m),
        ]);

        var position = Assert.Single(ledger.Positions);
        Assert.Equal(1_500m, position.Quantity);
        // Moving weighted average: (10,005 + 12,006) / 2,000 = 11.0055 per unit.
        Assert.Equal(11.0055m, position.AverageCost.Amount);
        // Released cost 500 × 11.0055 = 5,502.75; proceeds 7,500 − 7 = 7,493; realized 1,990.25;
        // 22,011 − 5,502.75 = 16,508.25 stays in the position.
        Assert.Equal(1_990.25m, position.RealizedPnl.Amount);
        Assert.Equal(16_508.25m, position.CostBasis.Amount);
    }

    // --- fixture shape (hard requirement) ---------------------------------------------

    /// <summary>
    /// The fixtures must look like real records (dates, id prefix, magnitudes) and include one row
    /// with an empty note and one with an empty counter account — without copying anyone's numbers.
    /// </summary>
    [Fact]
    public async Task FixtureRowsAreShapedLikeRealRecordsAndSurviveAStoreRoundTrip()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store, baseCurrency: "CNY");
        var cny = workspace.AddAccount("证券账户", AccountKind.Securities, "CNY", mode: AccountValuationMode.Balance);
        var yen = workspace.AddAccount("日元账户", AccountKind.Bank, "JPY", mode: AccountValuationMode.Balance);
        var plain = workspace.AddAccount("银行账户", AccountKind.Bank, "CNY");

        Assert.StartsWith("acct_", cny.Id, StringComparison.Ordinal);
        Assert.StartsWith("acct_", yen.Id, StringComparison.Ordinal);

        var sixFigures = workspace.Record(
            LedgerTransaction.Deposit(cny.Id, new DateOnly(2022, 9, 13), 180_000.00m, "CNY"));
        var sevenFigures = workspace.Record(
            LedgerTransaction.Deposit(yen.Id, new DateOnly(2023, 4, 17), 1_050_000m, "JPY"));
        var noNote = workspace.Record(
            LedgerTransaction.BalanceAssertion(cny.Id, new DateOnly(2025, 6, 30), 195_000.00m, "CNY"));
        var noCounterAccount = workspace.Record(
            LedgerTransaction.FxExchange(plain.Id, new DateOnly(2025, 1, 6), 50_000m, "CNY", 1_050_000m, "JPY"));

        var stored = store.ListTransactions()
            .ToDictionary(t => t.Id, StringComparer.Ordinal);

        Assert.Equal("2022-09-13", stored[sixFigures.Id].TradeDate.ToString("yyyy-MM-dd"));
        Assert.Equal(180_000.00m, stored[sixFigures.Id].CashAmount);
        Assert.Equal(1_050_000m, stored[sevenFigures.Id].CashAmount);
        Assert.Equal("JPY", stored[sevenFigures.Id].Currency);
        Assert.Equal("", stored[noNote.Id].Note);
        Assert.True(string.IsNullOrEmpty(stored[noCounterAccount.Id].CounterAccountId)
                    || stored[noCounterAccount.Id].CounterAccountId == plain.Id);

        var snapshot = await workspace.RefreshAsync(new DateOnly(2025, 12, 31));
        Assert.Equal(3, snapshot.AccountSummaries.Count);

        // D3: a balance-mode account that never reported a total is unknown, not zero.
        var yenSummary = Assert.Single(snapshot.AccountSummaries, s => s.AccountId == yen.Id);
        Assert.False(yenSummary.ValueKnown);
        Assert.Null(yenSummary.TotalValueBase);
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