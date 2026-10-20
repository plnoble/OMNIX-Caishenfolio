using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// WP-03 A1–A4/A6: a ledger must refuse money it does not have (R1/D1/D4) while leaving the
/// two legitimate ways of never having had it alone — opening balances and margin (D3).
/// The constraint lives in the write path, so CSV import is covered by the same rule (A6).
/// </summary>
public class BalanceConstraintTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_balance_tests", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly Day2 = new(2026, 1, 6);

    private PortfolioStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return PortfolioStore.UnderStateRoot(_root);
    }

    /// <summary>Zero balances are absent from the list rather than present with 0.</summary>
    private static decimal AmountOf(LedgerState state, string currency) =>
        state.CashBalances.Where(b => b.Currency == currency).Sum(b => b.Amount);

    private static LedgerTransaction Fx(string accountId, DateOnly date, decimal cash, string from, decimal got, string to) =>
        LedgerTransaction.FxExchange(accountId, date, cash, from, got, to);

    [Fact]
    public void A1_AnExchangeFromAnEmptyLedgerIsRefusedAndSaysHowFarShortItIs()
    {
        using var store = NewStore();
        var account = new PortfolioWorkspace(store).AddAccount("换汇账户", AccountKind.Bank, "CNY");

        var error = Assert.Throws<LedgerException>(() =>
            store.AddTransaction(Fx(account.Id, Day1, 100_000m, "CNY", 2_000_000m, "JPY")));

        // D4: how much there is, how much it needs, how far short. All three, in the message.
        Assert.Contains("0", error.Message, StringComparison.Ordinal);
        Assert.Contains("100,000", error.Message, StringComparison.Ordinal);
        Assert.Contains("差", error.Message, StringComparison.Ordinal);
        // A1: nothing at all may be written.
        Assert.Empty(store.ListTransactions());
    }

    [Fact]
    public void A2_AnExchangeWithinTheDepositedAmountGoesThroughAndLandsExactlyOnZero()
    {
        using var store = NewStore();
        var account = new PortfolioWorkspace(store).AddAccount("换汇账户", AccountKind.Bank, "CNY");
        store.AddTransaction(LedgerTransaction.Deposit(account.Id, Day1, 100_000m, "CNY"));

        store.AddTransaction(Fx(account.Id, Day2, 100_000m, "CNY", 2_000_000m, "JPY"));

        var state = PositionCalculator.Replay(store.ListTransactions());
        Assert.Equal(0m, AmountOf(state, "CNY"));
        Assert.Equal(2_000_000m, AmountOf(state, "JPY"));
    }

    [Fact]
    public void A3_OneYuanOverIsRefusedAndTheGapIsNamed()
    {
        using var store = NewStore();
        var account = new PortfolioWorkspace(store).AddAccount("换汇账户", AccountKind.Bank, "CNY");
        store.AddTransaction(LedgerTransaction.Deposit(account.Id, Day1, 100_000m, "CNY"));

        var error = Assert.Throws<LedgerException>(() =>
            store.AddTransaction(Fx(account.Id, Day2, 100_001m, "CNY", 2_000_020m, "JPY")));

        Assert.Contains("1", error.Message, StringComparison.Ordinal);
        Assert.Contains("差", error.Message, StringComparison.Ordinal);
        // The refused row did not sneak in: only the deposit is there.
        Assert.Single(store.ListTransactions());
    }

    [Fact]
    public void A4_OpeningCashEstablishesTheBalanceAndTheExchangeIsThenFine()
    {
        using var store = NewStore();
        var account = new PortfolioWorkspace(store).AddAccount("老账户", AccountKind.Bank, "CNY");
        store.AddTransaction(LedgerTransaction.OpeningCash(account.Id, Day1, 100_000m, "CNY", "建账时的余额"));

        store.AddTransaction(Fx(account.Id, Day2, 100_000m, "CNY", 2_000_000m, "JPY"));

        var state = PositionCalculator.Replay(store.ListTransactions());
        Assert.Equal(0m, AmountOf(state, "CNY"));
    }

    [Fact]
    public void A6_AnOverspendingCsvRowIsRejectedAndTheRestStillImports()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        var account = workspace.AddAccount("换汇账户", AccountKind.Bank, "CNY");

        // Built from the importer's own template header, so the column positions cannot drift:
        // a 50,000 CNY deposit, then an exchange that tries to spend 90,000 CNY.
        var header = string.Join(",", TransactionCsvImporter.TemplateHeader);
        var deposit = string.Join(",", "2026-01-05", "换汇账户", "入金", "", "", "", "", "CNY", "", "", "50000", "", "", "", "");
        var overspend = string.Join(",", "2026-01-06", "换汇账户", "换汇", "", "", "", "", "CNY", "", "", "90000", "USD", "12500", "", "花超的一行");
        var csv = header + Environment.NewLine + deposit + Environment.NewLine + overspend;

        var preview = workspace.PreviewImport(csv, account.Id);
        // A6: the bad row is rejected with a visible reason, the good row lands.
        var written = workspace.CommitImport(preview, skipInvalidRows: true);

        Assert.Equal(1, written);
        Assert.Single(store.ListTransactions());
        Assert.Contains(preview.Rows, row => row.Error is not null && row.Error.Contains("差", StringComparison.Ordinal));
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
