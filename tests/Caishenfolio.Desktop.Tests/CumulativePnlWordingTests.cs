using System.IO;
using System.Windows.Controls;
using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// WP-02 口径统一: the overview and the ledger page must use one word and one number. Before
/// this, the overview reported 未实现+已实现+分红 while the ledger reported 总资产 − 净投入 —
/// two different answers to the same question, which the coordinator ruled a bug, not a design.
/// </summary>
public class CumulativePnlWordingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_pnl_wording", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheOverviewAndTheLedgerShowTheSameNumberUnderTheSameWords()
    {
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var workspace = new PortfolioWorkspace(store);
            var detail = workspace.AddAccount("日常", AccountKind.Bank, "CNY");
            var balance = workspace.AddAccount(
                "余额账户", AccountKind.Bank, "CNY", mode: AccountValuationMode.Balance);
            var day = new DateOnly(2025, 1, 2);
            workspace.Record(LedgerTransaction.Deposit(detail.Id, day, 100_000m, "CNY"));
            workspace.Record(LedgerTransaction.Deposit(balance.Id, day, 50_000m, "CNY"));
            workspace.Record(
                LedgerTransaction.BalanceAssertion(balance.Id, new DateOnly(2025, 6, 30), 60_000m, "CNY"));

            var model = new PortfolioViewModel(workspace);
            await model.RefreshAsync().ConfigureAwait(true);

            // The headline is the sum of the account figures, formatted the same way.
            var ledgerRow = model.AccountSummaries.Single(r => r.Id == balance.Id);
            Assert.Equal("+10,000 CNY", ledgerRow.CumulativePnl);
            Assert.Equal("+10,000 CNY", model.TotalPnlText);

            // And the two surfaces use one caption, not two names for one number.
            var overview = new WealthOverviewView();
            var novice = new NoviceHomeView();
            var overviewCaption = (TextBlock)overview.FindName("PnlCaption")!;
            var noviceCaption = (TextBlock)novice.FindName("PnlCaption")!;
            Assert.Equal("累计收益（总资产−净投入）", overviewCaption.Text);
            Assert.Equal(overviewCaption.Text, noviceCaption.Text);
        });
    }

    [Fact]
    public void AnUnknownAccountMakesTheHeadlineSayUnknownInsteadOfAPartialSum()
    {
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var workspace = new PortfolioWorkspace(store);
            var detail = workspace.AddAccount("日常", AccountKind.Bank, "CNY");
            var balance = workspace.AddAccount(
                "余额账户", AccountKind.Bank, "CNY", mode: AccountValuationMode.Balance);
            var day = new DateOnly(2025, 1, 2);
            workspace.Record(LedgerTransaction.Deposit(detail.Id, day, 100_000m, "CNY"));
            workspace.Record(LedgerTransaction.Deposit(balance.Id, day, 50_000m, "CNY"));

            var model = new PortfolioViewModel(workspace);
            await model.RefreshAsync().ConfigureAwait(true);

            Assert.Equal("未知", model.TotalPnlText);
        });
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
