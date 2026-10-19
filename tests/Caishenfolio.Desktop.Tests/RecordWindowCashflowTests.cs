using System.IO;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// WP-01 目标 4: 「新增记录」的通路 — 投入 / 转出 / 转到我的另一个账户 / 当天资产金额。
///
/// These drive the real <see cref="RecordWindow"/> (never shown, so no message pump): the same
/// ComboBox/TextBox objects a person clicks, the same <see cref="RecordWindow.BuildTransaction"/>
/// the save button calls. The store is a temp-dir ledger, never the installed one.
/// </summary>
public class RecordWindowCashflowTests : IDisposable
{
    private static readonly DateOnly Day = new(2025, 6, 30);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_record_window_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    /// <summary>
    /// WPF registers even an unshown <see cref="Window"/> with <see cref="Application"/>, and the
    /// isolation suite pins that the resource host leaves nothing behind — so every window this
    /// suite opens is closed before the dispatcher body returns.
    /// </summary>
    private void Run(Func<Task> body) => WpfTestHost.Run(async () =>
    {
        try
        {
            await body();
        }
        finally
        {
            foreach (var window in _opened)
            {
                window.Close();
            }

            _opened.Clear();
        }
    });

    private readonly List<RecordWindow> _opened = [];

    [Fact]
    public void DepositAndWithdrawAreThreeFieldsOnTheAccountsOwnCurrency()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var account = workspace.AddAccount("日常账户", AccountKind.Bank, "CNY");
            var window = NewWindow(workspace, account);

            Set(window, "DateBox", "2025-06-30");
            Set(window, "AmountBox", "45000");
            Set(window, "NoteBox", "工资");

            var deposit = window.BuildTransaction();
            Assert.Equal(TransactionKind.Deposit, deposit.Kind);
            Assert.Equal(account.Id, deposit.AccountId);
            Assert.Equal(Day, deposit.TradeDate);
            Assert.Equal(45_000m, deposit.CashAmount);
            Assert.Equal("CNY", deposit.Currency);
            Assert.Equal("工资", deposit.Note);

            SelectOperation(window, "Withdraw");
            Set(window, "AmountBox", "12000");
            var withdraw = window.BuildTransaction();
            Assert.Equal(TransactionKind.Withdraw, withdraw.Kind);
            Assert.Equal(12_000m, withdraw.CashAmount);
            Assert.Equal("CNY", withdraw.Currency);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void MovingBetweenAccountsOfTheSameCurrencyIsATransferAndCrossCurrencyIsAnExchange()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var from = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");
            var toCny = workspace.AddAccount("招行", AccountKind.Bank, "CNY");
            var toJpy = workspace.AddAccount("日本账户", AccountKind.Bank, "JPY");

            var window = NewWindow(workspace, from);
            Set(window, "DateBox", "2025-06-30");
            SelectOperation(window, "Move");

            // Same currency on both ends: one amount, and the ledger sees a Transfer.
            SelectCounter(window, toCny.Id);
            Set(window, "AmountBox", "30000");
            var transfer = window.BuildTransaction();
            Assert.Equal(TransactionKind.Transfer, transfer.Kind);
            Assert.Equal(from.Id, transfer.AccountId);
            Assert.Equal(toCny.Id, transfer.CounterAccountId);
            Assert.Equal(30_000m, transfer.CashAmount);
            Assert.Equal(30_000m, transfer.CounterAmount);
            Assert.Equal("CNY", transfer.Currency);

            // Different currencies: both receipt amounts, no rate typed by hand (D4).
            SelectCounter(window, toJpy.Id);
            Set(window, "AmountBox", "10000");
            Set(window, "CounterAmountBox", "210000");
            var exchange = window.BuildTransaction();
            Assert.Equal(TransactionKind.FxExchange, exchange.Kind);
            Assert.Equal(from.Id, exchange.AccountId);
            Assert.Equal(toJpy.Id, exchange.CounterAccountId);
            Assert.Equal(10_000m, exchange.CashAmount);
            Assert.Equal("CNY", exchange.Currency);
            Assert.Equal(210_000m, exchange.CounterAmount);
            Assert.Equal("JPY", exchange.CounterCurrency);
            Assert.Equal(21m, exchange.FxRate); // shown for reference; the two amounts are the fact
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void BalanceAssertionIsOfferedOnBalanceAccountsAndRefusedOnDetailAccounts()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var detail = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");
            var balance = workspace.AddAccount(
                "券商总览", AccountKind.Securities, "CNY", mode: AccountValuationMode.Balance);

            // Detail account: the option is disabled, and forcing it is refused with a reason.
            var detailWindow = NewWindow(workspace, detail);
            Assert.False(OptionEnabled(detailWindow, "BalanceAssertion"));
            SelectOperation(detailWindow, "BalanceAssertion");
            Set(detailWindow, "AmountBox", "195000");
            var refusal = Assert.Throws<LedgerException>(() => detailWindow.BuildTransaction());
            Assert.Contains("明细模式", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("当天资产金额", refusal.Message, StringComparison.Ordinal);

            // Balance account: the option works, and the amount is the whole account.
            var balanceWindow = NewWindow(workspace, balance);
            Assert.True(OptionEnabled(balanceWindow, "BalanceAssertion"));
            SelectOperation(balanceWindow, "BalanceAssertion");
            Set(balanceWindow, "AmountBox", "195000");
            Set(balanceWindow, "DateBox", "2025-06-30");
            var assertion = balanceWindow.BuildTransaction();
            Assert.Equal(TransactionKind.BalanceAssertion, assertion.Kind);
            Assert.Equal(balance.Id, assertion.AccountId);
            Assert.Equal(195_000m, assertion.CashAmount);
            Assert.Equal("CNY", assertion.Currency);
            Assert.Equal(0m, assertion.Quantity); // never a position, never a cash movement
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void FieldsFollowTheOperationInsteadOfAskingForEverythingAtOnce()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var from = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");
            var toCny = workspace.AddAccount("招行", AccountKind.Bank, "CNY");
            var toJpy = workspace.AddAccount("日本账户", AccountKind.Bank, "JPY");

            var window = NewWindow(workspace, from);

            // 投入/转出: date + operation + amount + note. Nothing else is on screen.
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "CounterAccountRow"));
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "CounterAmountRow"));
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "FxRateText"));

            // Moving to a same-currency account: pick the destination, still one amount.
            SelectOperation(window, "Move");
            SelectCounter(window, toCny.Id);
            Assert.Equal(Visibility.Visible, VisibilityOf(window, "CounterAccountRow"));
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "CounterAmountRow"));

            // Moving across currencies: the second amount and the implied rate appear.
            SelectCounter(window, toJpy.Id);
            Set(window, "AmountBox", "10000");
            Set(window, "CounterAmountBox", "210000");
            Assert.Equal(Visibility.Visible, VisibilityOf(window, "CounterAmountRow"));
            var rate = (TextBlock)Element(window, "FxRateText");
            Assert.Equal(Visibility.Visible, rate.Visibility);
            Assert.Contains("21", rate.Text, StringComparison.Ordinal);
            Assert.Contains("CNY", rate.Text, StringComparison.Ordinal);
            Assert.Contains("JPY", rate.Text, StringComparison.Ordinal);

            // Leaving the cross-currency form takes it away again.
            SelectOperation(window, "Deposit");
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "CounterAmountRow"));
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "FxRateText"));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void OpeningTheDialogWritesNothingAndSavingWritesExactlyOneRow()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var account = workspace.AddAccount("日常账户", AccountKind.Bank, "CNY");

            var window = NewWindow(workspace, account);
            Set(window, "DateBox", "2025-06-30");
            Set(window, "AmountBox", "1000");
            Assert.Empty(store.ListTransactions()); // opening a form is not a ledger fact

            Assert.True(window.TryRecord());
            Assert.True(window.Saved);
            var row = Assert.Single(store.ListTransactions());
            Assert.Equal(TransactionKind.Deposit, row.Kind);
            Assert.Equal(1000m, row.CashAmount);
            Assert.Equal("CNY", row.Currency);

            // A rejected form leaves the ledger untouched and says why, on the dialog itself.
            var broken = NewWindow(workspace, account);
            Set(broken, "DateBox", "2025-06-30");
            Set(broken, "AmountBox", "not-a-number");
            Assert.False(broken.TryRecord());
            Assert.False(broken.Saved);
            Assert.Single(store.ListTransactions());
            Assert.Contains("无法解析数字", ((TextBlock)Element(broken, "ErrorText")).Text, StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void TheAccountsCurrencyIsPrefilledAndImpossibleNumbersAreRefused()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var jpy = workspace.AddAccount("日本账户", AccountKind.Bank, "JPY");
            var window = NewWindow(workspace, jpy);

            Assert.Equal("JPY", SelectedCurrency(window));

            Set(window, "AmountBox", "");
            var empty = Assert.Throws<LedgerException>(() => window.BuildTransaction());
            Assert.Contains("不能为空", empty.Message, StringComparison.Ordinal);

            // "2025/06/30" would parse — the refusal has to be about a genuinely unusable date.
            Set(window, "DateBox", "not-a-date");
            var date = Assert.Throws<LedgerException>(() => window.BuildTransaction());
            Assert.Contains("无法解析日期", date.Message, StringComparison.Ordinal);

            Set(window, "DateBox", "2025-06-30");
            Set(window, "AmountBox", "0");
            var zero = Assert.Throws<LedgerException>(() => window.BuildTransaction());
            Assert.Contains("必须大于 0", zero.Message, StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// 目标 3, seen from the UI: the four numbers per account, and an account nobody has valued
    /// reads 未知 rather than 0.00 (D3).
    /// </summary>
    [Fact]
    public void AccountSummariesShowTheFourNumbersAndNeverInventAZero()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var detail = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");
            var balance = workspace.AddAccount(
                "券商总览", AccountKind.Securities, "CNY", mode: AccountValuationMode.Balance);
            workspace.Record(LedgerTransaction.Deposit(detail.Id, new DateOnly(2025, 6, 2), 100_000m, "CNY"));
            workspace.Record(LedgerTransaction.Withdraw(balance.Id, new DateOnly(2025, 6, 3), 20_000m, "CNY"));

            var model = new PortfolioViewModel(workspace);
            await model.RefreshAsync().ConfigureAwait(true);
            var view = new LedgerView();
            view.Bind(model);

            var grid = (DataGrid)view.FindName("AccountSummaryGrid");
            Assert.Same(model.AccountSummaries, grid.ItemsSource);
            Assert.Equal(2, model.AccountSummaries.Count);

            var detailRow = model.AccountSummaries.Single(r => r.Id == detail.Id);
            Assert.Equal("明细模式", detailRow.Mode);
            Assert.Equal("100,000 CNY", detailRow.TotalValue);
            Assert.Equal("0 CNY", detailRow.CumulativePnl);
            Assert.NotEqual("—", detailRow.MoneyWeightedReturn);
            Assert.Equal("", detailRow.Note); // nothing is missing, so nothing is explained away

            // The balance-mode account has real money out but no reported total: unknown, not 0.
            var balanceRow = model.AccountSummaries.Single(r => r.Id == balance.Id);
            Assert.Equal("余额模式", balanceRow.Mode);
            Assert.Equal("未知", balanceRow.TotalValue);
            Assert.Equal("未知", balanceRow.CumulativePnl);
            Assert.Equal("—", balanceRow.MoneyWeightedReturn);
            Assert.Equal("—", balanceRow.Annualized);
            Assert.Contains("市值", balanceRow.Note, StringComparison.Ordinal);
            Assert.DoesNotContain("0 CNY", balanceRow.TotalValue, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The currency decides whether a move needs a second amount, so it must refresh the form like
    /// the account and operation combos do — and a number that stops meaning anything must not sit
    /// on screen pretending it will be recorded.
    /// </summary>
    [Fact]
    public void ChangingTheCurrencyRefreshesTheFormAndNeverDropsTheCounterAmountSilently()
    {
        Run(async () =>
        {
            using var store = NewStore();
            var workspace = new PortfolioWorkspace(store);
            var from = workspace.AddAccount("华泰证券", AccountKind.Securities, "CNY");
            var toJpy = workspace.AddAccount("日本账户", AccountKind.Bank, "JPY");

            var window = NewWindow(workspace, from);
            SelectOperation(window, "Move");
            SelectCounter(window, toJpy.Id);
            Assert.Equal("CNY", SelectedCurrency(window));
            Assert.Equal(Visibility.Visible, VisibilityOf(window, "CounterAmountRow"));
            // The counter currency is on screen; the user copies both amounts off the receipt.
            Assert.Equal("JPY", ((TextBlock)Element(window, "CounterCurrencyText")).Text);

            Set(window, "AmountBox", "10000");
            Set(window, "CounterAmountBox", "210000");

            // Switching the outgoing currency to the destination's makes it a same-currency move.
            SelectCurrency(window, "JPY");
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "CounterAmountRow"));
            Assert.Equal(Visibility.Collapsed, VisibilityOf(window, "FxRateText"));
            Assert.Equal("", ((TextBox)Element(window, "CounterAmountBox")).Text);
            Assert.Contains("同币种", ((TextBlock)Element(window, "CounterCurrencyText")).Text, StringComparison.Ordinal);

            // And back: the cross-currency form returns, empty rather than stale.
            SelectCurrency(window, "CNY");
            Assert.Equal(Visibility.Visible, VisibilityOf(window, "CounterAmountRow"));
            Assert.Equal("JPY", ((TextBlock)Element(window, "CounterCurrencyText")).Text);

            // Whatever happens, a counter amount that cannot be recorded is refused, not ignored.
            SelectCurrency(window, "JPY");
            Set(window, "CounterAmountBox", "210000");
            Set(window, "DateBox", "2025-06-30");
            Assert.False(window.TryRecord());
            Assert.Contains("对方金额", ((TextBlock)Element(window, "ErrorText")).Text, StringComparison.Ordinal);
            Assert.Empty(store.ListTransactions());
            await Task.CompletedTask;
        });
    }

    private RecordWindow NewWindow(PortfolioWorkspace workspace, Account? preselected = null)
    {
        var window = new RecordWindow(workspace, workspace.Store.ListAccounts(), preselected);
        _opened.Add(window);
        return window;
    }

    private static void SelectOperation(RecordWindow window, string tag)
    {
        var combo = (ComboBox)Element(window, "OperationCombo");
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == tag);
    }

    private static bool OptionEnabled(RecordWindow window, string tag) =>
        ((ComboBox)Element(window, "OperationCombo")).Items.Cast<ComboBoxItem>()
            .Single(item => (string?)item.Tag == tag).IsEnabled;

    private static void SelectCounter(RecordWindow window, string accountId)
    {
        var combo = (ComboBox)Element(window, "CounterAccountCombo");
        combo.SelectedItem = combo.Items.Cast<Account>().Single(a => a.Id == accountId);
    }

    private static string SelectedCurrency(RecordWindow window) =>
        ((ComboBoxItem)((ComboBox)Element(window, "CurrencyCombo")).SelectedItem).Content?.ToString() ?? "";

    private static void SelectCurrency(RecordWindow window, string currency)
    {
        var combo = (ComboBox)Element(window, "CurrencyCombo");
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .Single(item => (string?)item.Content == currency);
    }

    private static void Set(RecordWindow window, string name, string value) =>
        ((TextBox)Element(window, name)).Text = value;

    private static FrameworkElement Element(RecordWindow window, string name) =>
        (FrameworkElement)(window.FindName(name)
            ?? throw new InvalidOperationException($"找不到控件 {name}。"));

    private static Visibility VisibilityOf(RecordWindow window, string name) => Element(window, name).Visibility;

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