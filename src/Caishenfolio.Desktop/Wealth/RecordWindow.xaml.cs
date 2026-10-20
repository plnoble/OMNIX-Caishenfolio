using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>
/// WP-01 的「新增记录」窗口：账户级现金流的四条操作。
///
/// The point is that the ordinary act — money in, money out — is 日期 + 操作 + 金额 + 备注 and
/// nothing else. Fields that only one operation needs appear with that operation, and the
/// cross-currency form asks for the two receipt amounts rather than a rate the user would have to
/// compute (AGENTS: 换汇按回单两边金额入账).
///
/// It only ever builds records through the <see cref="LedgerTransaction"/> factories, so the
/// rules (positive amounts, same-account transfer refused, balance assertions non-negative) hold
/// here exactly as they do everywhere else. <see cref="BuildTransaction"/> is internal so tests
/// can drive the real thing on an unshown window.
/// </summary>
public partial class RecordWindow : Window
{
    private const string DefaultCurrency = "CNY";

    private readonly PortfolioWorkspace _workspace;
    private readonly IReadOnlyList<Account> _accounts;

    public RecordWindow(
        PortfolioWorkspace workspace,
        IReadOnlyList<Account> accounts,
        Account? preselected = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

        InitializeComponent();
        DateBox.Text = DateTime.Today.ToString("yyyy-MM-dd");

        AccountCombo.ItemsSource = _accounts;
        var first = preselected is not null && _accounts.Any(a => a.Id == preselected.Id)
            ? preselected
            : _accounts.FirstOrDefault();
        AccountCombo.SelectedItem = first;
        RefreshCounterAccounts();
        UpdateModeAvailability();
        UpdateFieldVisibility();
    }

    /// <summary>True once a record was accepted by the ledger.</summary>
    public bool Saved { get; private set; }

    /// <summary>The row that was recorded, for the caller to report.</summary>
    public LedgerTransaction? Recorded { get; private set; }

    private Account? SelectedAccount => AccountCombo.SelectedItem as Account;

    private Account? SelectedCounterAccount => CounterAccountCombo.SelectedItem as Account;

    private string SelectedCurrency =>
        (CurrencyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? DefaultCurrency;

    private string SelectedCounterCurrency => SelectedCounterAccount?.MainCurrency ?? DefaultCurrency;

    private RecordOperation SelectedOperation
    {
        get
        {
            var tag = (OperationCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return Enum.TryParse<RecordOperation>(tag, out var parsed) ? parsed : RecordOperation.Deposit;
        }
    }

    /// <summary>
    /// Builds the transaction the current form describes. Throws <see cref="LedgerException"/> with
    /// a message meant for the user when the form cannot describe a real ledger fact.
    /// </summary>
    internal LedgerTransaction BuildTransaction()
    {
        var account = SelectedAccount
            ?? throw new LedgerException("请先选择账户。");
        if (!DateOnly.TryParse(DateBox.Text.Trim(), CultureInfo.InvariantCulture, out var date))
        {
            throw new LedgerException($"无法解析日期「{DateBox.Text.Trim()}」。请用 yyyy-MM-dd，例如 2025-06-30。");
        }

        var currency = SelectedCurrency;
        var amount = ParseAmount(AmountBox.Text, "金额");
        var note = NoteBox.Text.Trim();

        switch (SelectedOperation)
        {
            case RecordOperation.Deposit:
                return LedgerTransaction.Deposit(account.Id, date, amount, currency, note);

            case RecordOperation.Withdraw:
                return LedgerTransaction.Withdraw(account.Id, date, amount, currency, note);

            case RecordOperation.BalanceAssertion:
                RequireBalanceMode(account);
                return LedgerTransaction.BalanceAssertion(account.Id, date, amount, currency, note);

            case RecordOperation.Move:
                var target = SelectedCounterAccount
                    ?? throw new LedgerException("请选择转入账户。");
                if (string.Equals(target.Id, account.Id, StringComparison.Ordinal))
                {
                    throw new LedgerException("转入账户不能和转出账户相同。");
                }

                if (string.Equals(currency, SelectedCounterCurrency, StringComparison.Ordinal))
                {
                    // Defence in depth: the field is cleared when it goes off screen, so a value
                    // here means something survived that would otherwise be dropped without a word.
                    if (CounterAmountBox.Text.Trim().Length > 0)
                    {
                        throw new LedgerException(
                            $"两边都是 {currency}，对方金额没有意义，也不会入账；请清空对方金额，" +
                            "或把币种改成与转入账户不同的币种。");
                    }

                    return LedgerTransaction.Transfer(account.Id, target.Id, date, amount, currency, note);
                }

                // Cross-currency: both receipt amounts, no rate to type. The implied rate is
                // stored for display only; the balances come from the two amounts (D4).
                var counterAmount = ParseAmount(CounterAmountBox.Text, "对方金额");
                return LedgerTransaction.FxExchange(
                    account.Id, date, amount, currency, counterAmount, SelectedCounterCurrency,
                    0m, note, target.Id);

            default:
                throw new LedgerException("未知的操作类型。");
        }
    }

    private void RequireBalanceMode(Account account)
    {
        if (account.Mode != AccountValuationMode.Balance)
        {
            throw new LedgerException(
                $"账户「{account.Name}」是明细模式，不能报「当天资产金额」——那是余额模式账户的操作。" +
                "请改为记录投入/转出，或新建一个余额模式账户。");
        }
    }

    private static decimal ParseAmount(string text, string label)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            throw new LedgerException($"{label}不能为空。");
        }

        if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new LedgerException($"无法解析数字「{trimmed}」（{label}），请只填数字，例如 120000 或 120000.00。");
        }

        return value;
    }

    private void AccountCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AmountRow is null)
        {
            return;
        }

        RefreshCounterAccounts();
        ApplyAccountCurrency();
        UpdateModeAvailability();
        UpdateFieldVisibility();
    }

    private void OperationCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AmountRow is null)
        {
            return;
        }

        UpdateFieldVisibility();
    }

    private void CounterAccountCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AmountRow is null)
        {
            return;
        }

        UpdateFieldVisibility();
    }

    /// <summary>
    /// The currency is what decides whether a move needs a second amount at all, so it has to
    /// refresh the form like the account and operation combos do.
    /// </summary>
    private void CurrencyCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AmountRow is null)
        {
            return;
        }

        UpdateFieldVisibility();
    }

    private void CounterAmountBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (AmountRow is null)
        {
            return;
        }

        UpdateDerivedRate();
    }

    /// <summary>The account's own currency is the sensible default for its cash movements.</summary>
    private void ApplyAccountCurrency()
    {
        var currency = Currencies.Normalize(SelectedAccount?.MainCurrency ?? DefaultCurrency);
        foreach (var item in CurrencyCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), currency, StringComparison.Ordinal))
            {
                CurrencyCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void RefreshCounterAccounts()
    {
        var others = _accounts
            .Where(a => !string.Equals(a.Id, SelectedAccount?.Id, StringComparison.Ordinal))
            .ToArray();
        var previous = SelectedCounterAccount?.Id;
        CounterAccountCombo.ItemsSource = others;
        CounterAccountCombo.SelectedItem = others.FirstOrDefault(a => a.Id == previous)
            ?? others.FirstOrDefault();
    }

    /// <summary>
    /// 「当天资产金额」belongs to a balance-mode account (R2/D5);「转到我的另一个账户」needs a
    /// second account. Options that cannot work are disabled here rather than failing on save,
    /// and the ledger refuses them again at write time regardless of what this window does.
    /// </summary>
    private void UpdateModeAvailability()
    {
        var account = SelectedAccount;
        var isBalance = account?.Mode == AccountValuationMode.Balance;
        foreach (var item in OperationCombo.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString();
            item.IsEnabled = tag switch
            {
                nameof(RecordOperation.BalanceAssertion) => isBalance,
                nameof(RecordOperation.Move) => _accounts.Count > 1,
                _ => true,
            };
        }

        if (SelectedOperation == RecordOperation.BalanceAssertion && !isBalance)
        {
            SelectOperation(RecordOperation.Deposit);
        }

        if (SelectedOperation == RecordOperation.Move && _accounts.Count <= 1)
        {
            SelectOperation(RecordOperation.Deposit);
        }

        ModeHintText.Text = account is null
            ? "先建一个账户，再记录。"
            : isBalance
                ? $"「{account.Name}」是余额模式：平时只记投入/转出，隔一段时间报一次「当天资产金额」。它的市值就是最后一次报的数。"
                : $"「{account.Name}」是明细模式：按实际交易记账，市值由现金和持仓算出。要改成「只报一个总数」，请新建余额模式账户。";
    }

    private void SelectOperation(RecordOperation operation)
    {
        foreach (var item in OperationCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), operation.ToString(), StringComparison.Ordinal))
            {
                OperationCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateFieldVisibility()
    {
        // The currency combo is the first control in the form, so its SelectionChanged fires while
        // the rest of the BAML is still being parsed: everything below must tolerate the fields
        // that do not exist yet.
        if (CounterAccountRow is null || CounterAmountRow is null || FxRateText is null || AmountRow is null)
        {
            return;
        }

        var operation = SelectedOperation;
        var move = operation == RecordOperation.Move;
        var crossCurrency = move &&
            !string.Equals(SelectedCurrency, SelectedCounterCurrency, StringComparison.Ordinal);

        // Changing the currency can turn a cross-currency move into a same-currency one, and the
        // counter amount stops meaning anything. Clear it as it goes off screen: a number the user
        // typed must never be dropped silently at save time.
        if (!crossCurrency && CounterAmountBox is not null && CounterAmountBox.Text.Length > 0)
        {
            CounterAmountBox.Text = "";
        }

        CounterAccountRow.Visibility = move ? Visibility.Visible : Visibility.Collapsed;
        CounterAmountRow.Visibility = crossCurrency ? Visibility.Visible : Visibility.Collapsed;
        FxRateText.Visibility = crossCurrency ? Visibility.Visible : Visibility.Collapsed;
        if (CounterCurrencyText is not null)
        {
            CounterCurrencyText.Text = crossCurrency
                ? SelectedCounterCurrency
                : move
                    ? $"{SelectedCurrency}（两边同币种）"
                    : "—";
        }

        UpdateDerivedRate();
    }

    /// <summary>Display only: what the two receipt amounts imply. The rate is never typed in.</summary>
    private void UpdateDerivedRate()
    {
        if (FxRateText is null || AmountBox is null || CounterAmountBox is null)
        {
            return;
        }

        if (FxRateText.Visibility != Visibility.Visible)
        {
            FxRateText.Text = "汇率：—";
            return;
        }

        var from = decimal.TryParse(AmountBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
            ? a
            : 0m;
        var to = decimal.TryParse(CounterAmountBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var b)
            ? b
            : 0m;
        FxRateText.Text = from > 0m && to > 0m
            ? $"汇率：{SelectedCurrency} → {SelectedCounterCurrency} ≈ {(to / from).ToString("0.####", CultureInfo.InvariantCulture)}（只用于显示，入账按两边金额）"
            : "汇率：填好两边金额后自动显示（只用于显示，入账按两边金额）";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (TryRecord())
        {
            Dismiss(true);
        }
    }

    /// <summary>
    /// The save path without the window lifecycle: build, let the ledger judge, report. Kept
    /// separate so a test drives the real thing on a window that was never shown, and so the
    /// button cannot quietly do anything else.
    /// </summary>
    internal bool TryRecord()
    {
        try
        {
            var transaction = BuildTransaction();
            Recorded = _workspace.Record(transaction);
            Saved = true;
            ErrorText.Text = "";
            return true;
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException or FormatException)
        {
            ErrorText.Text = ex.Message;
            return false;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Dismiss(false);

    /// <summary>
    /// A window opened with <c>ShowDialog</c> wants a <see cref="Window.DialogResult"/>; an unshown
    /// one (a test driving the real form) must not set it, because WPF refuses that before the
    /// window has ever been displayed.
    /// </summary>
    private void Dismiss(bool result)
    {
        if (!IsLoaded)
        {
            return;
        }

        DialogResult = result;
        Close();
    }

    private enum RecordOperation
    {
        Deposit,
        Withdraw,
        Move,
        BalanceAssertion,
    }
}