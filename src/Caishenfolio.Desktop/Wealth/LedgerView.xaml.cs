using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Caishenfolio.Host.Portfolio;
using Microsoft.Win32;

namespace Caishenfolio.Desktop.Wealth;

public partial class LedgerView : UserControl
{
    private PortfolioViewModel? _model;
    private CsvImportPreview? _pendingImport;

    public LedgerView()
    {
        InitializeComponent();
        EntryDate.Text = DateTime.Today.ToString("yyyy-MM-dd");
    }

    public event Action<string>? Notified;

    public string? ExportDirectory { get; set; }

    public void Bind(PortfolioViewModel model)
    {
        if (ReferenceEquals(_model, model))
        {
            return;
        }

        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        _model.PropertyChanged += OnModelChanged;
        AccountsGrid.ItemsSource = model.Accounts;
        TransactionsGrid.ItemsSource = model.Transactions;
        AccountSummaryGrid.ItemsSource = model.AccountSummaries;
        Render();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_model is null)
        {
            return;
        }

        StatusText.Text = _model.StatusText;
        RefreshButton.IsEnabled = !_model.IsBusy;
        ExportButton.IsEnabled = _model.Snapshot is not null;

        if (AccountsGrid.SelectedItem is null && AccountsGrid.Items.Count > 0)
        {
            AccountsGrid.SelectedIndex = 0;
        }

        RefreshCounterAccountChoices();
    }

    /// <summary>Keeps the cross-account selector in step with the account list; the blank
    /// first item means "same account".</summary>
    private void RefreshCounterAccountChoices()
    {
        if (_model is null)
        {
            return;
        }

        var selected = (EntryCounterAccount.SelectedItem as AccountRow)?.Id;
        var previous = new[] { new AccountRow("", "（同一账户）", "", "", "", "") }
            .Concat(_model.Accounts);
        EntryCounterAccount.ItemsSource = previous.ToList();
        EntryCounterAccount.SelectedItem = previous.FirstOrDefault(a => a.Id == selected)
            ?? previous.First();
    }

    private string? SelectedAccountId =>
        (AccountsGrid.SelectedItem as AccountRow)?.Id;

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        if (_model is not null)
        {
            await _model.RefreshAsync().ConfigureAwait(true);
        }
    }

    private void AccountsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateImportButtonState();

    private void EntryKind_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Cash-only events have no instrument or quantity; grey those inputs out rather than
        // letting a user fill in fields the ledger will ignore.
        if (!IsLoaded)
        {
            return;
        }

        var kind = SelectedKind();
        var needsInstrument = kind is TransactionKind.Buy or TransactionKind.Sell
            or TransactionKind.Dividend or TransactionKind.OpeningPosition;
        var needsQuantity = kind is TransactionKind.Buy or TransactionKind.Sell or TransactionKind.OpeningPosition;
        var needsAmount = kind is TransactionKind.Dividend or TransactionKind.Interest
            or TransactionKind.Deposit or TransactionKind.Withdraw or TransactionKind.OpeningCash
            or TransactionKind.FxExchange;
        var isFx = kind == TransactionKind.FxExchange;

        EntrySymbol.IsEnabled = needsInstrument || kind == TransactionKind.Interest;
        if (!EntrySymbol.IsEnabled)
        {
            EntrySymbol.Text = "";
            EntryName.Text = "";
        }
        EntryName.IsEnabled = EntrySymbol.IsEnabled;
        EntryQuantity.IsEnabled = needsQuantity;
        EntryPrice.IsEnabled = needsQuantity;
        if (!needsQuantity)
        {
            EntryQuantity.Text = "";
            EntryPrice.Text = "";
        }
        EntryAmount.IsEnabled = needsAmount;
        EntryCounterAmount.IsEnabled = isFx;
        EntryCounterCurrency.IsEnabled = isFx;
        EntryCounterAccount.IsEnabled = isFx && _model is not null && _model.Accounts.Count > 1;
        EntryCounterAmountField.Visibility = isFx ? Visibility.Visible : Visibility.Collapsed;
        EntryCounterAccountField.Visibility = isFx ? Visibility.Visible : Visibility.Collapsed;
        EntryCounterCurrencyField.Visibility = isFx ? Visibility.Visible : Visibility.Collapsed;
        EntryCurrencyLabel.Text = isFx ? "换出币种" : "币种";
        EntrySymbolField.Visibility = EntrySymbol.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        EntryNameField.Visibility = EntryName.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        EntryQuantityField.Visibility = needsQuantity ? Visibility.Visible : Visibility.Collapsed;
        EntryPriceField.Visibility = needsQuantity ? Visibility.Visible : Visibility.Collapsed;
        FxRateHint.Visibility = isFx ? Visibility.Visible : Visibility.Collapsed;
        if (isFx)
        {
            UpdateFxRateHint();
        }
    }

    /// <summary>
    /// Live display of the rate implied by the two receipt amounts. Display only — the
    /// ledger books both amounts exactly as entered, never a rate-derived amount (an implied
    /// rate like 1/7.2 has no exact decimal form and would leave residue in the balance).
    /// </summary>
    private void UpdateFxRateHint()
    {
        if (FxRateHint is null)
        {
            return;
        }

        var from = (EntryCurrency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var to = (EntryCounterCurrency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var fromOk = decimal.TryParse(EntryAmount.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var fromAmount);
        var toOk = decimal.TryParse(EntryCounterAmount.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var toAmount);

        if (!fromOk || !toOk || fromAmount <= 0m || toAmount <= 0m || from == to)
        {
            FxRateHint.Text = "按回单如实填写两边的实际金额；成交汇率由软件计算并仅用于展示。";
            return;
        }

        FxRateHint.Text =
            $"成交汇率（按两边金额计算，仅展示，不用于入账）：1 {from} = " +
            $"{(toAmount / fromAmount).ToString("0.######", CultureInfo.InvariantCulture)} {to}" +
            $"　·　手续费按换出币种 {from} 记。";
    }

    private void FxField_OnChanged(object sender, RoutedEventArgs e) => UpdateFxRateHint();

    private TransactionKind SelectedKind() =>
        Enum.TryParse<TransactionKind>((EntryKind.SelectedItem as ComboBoxItem)?.Tag as string, out var kind)
            ? kind
            : TransactionKind.Buy;

    private void AddAccountButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        var name = NewAccountName.Text.Trim();
        if (name.Length == 0)
        {
            Notify("请输入账户名称。");
            return;
        }

        try
        {
            var kind = Enum.TryParse<AccountKind>((NewAccountKind.SelectedItem as ComboBoxItem)?.Tag as string, out var k)
                ? k
                : AccountKind.Securities;
            var currency = (NewAccountCurrency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CNY";
            var mode = NewAccountBalanceMode.IsChecked == true
                ? AccountValuationMode.Balance
                : AccountValuationMode.Detail;
            _model.AddAccount(name, kind, currency, mode: mode);
            NewAccountName.Clear();
            Notify(mode == AccountValuationMode.Balance
                ? $"已新建余额模式账户「{name}」：它只记投入/转出，市值靠报「当天资产金额」。"
                : $"已新建账户「{name}」。");
            _ = ReloadAsync();
        }
        catch (Exception ex)
        {
            Notify($"新建账户失败：{ex.Message}");
        }
    }

    /// <summary>
    /// WP-01's main entry: the account-level cash-flow dialog. The old 「记一笔」 form below stays
    /// reachable for the trade path — this button is the easy road, not a replacement.
    /// </summary>
    private void NewRecordButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        var accounts = _model.Snapshot?.Accounts ?? (IReadOnlyList<Account>)[];
        if (accounts.Count == 0)
        {
            Notify("先新建一个账户，再新增记录。");
            return;
        }

        var preselected = accounts.FirstOrDefault(a => a.Id == SelectedAccountId);
        var window = new RecordWindow(_model.Workspace, accounts, preselected)
        {
            Owner = Window.GetWindow(this),
        };
        _ = window.ShowDialog();

        if (window.Saved && window.Recorded is { } record)
        {
            Notify($"已记录：{record.TradeDate:yyyy-MM-dd} {AccountRecordLabel(record)}。");
            _ = ReloadAsync();
        }
    }

    private static string AccountRecordLabel(LedgerTransaction record) => record.Kind switch
    {
        TransactionKind.Deposit => $"投入 {PortfolioViewModel.Format(record.CashAmount)} {record.Currency}",
        TransactionKind.Withdraw => $"转出 {PortfolioViewModel.Format(record.CashAmount)} {record.Currency}",
        TransactionKind.BalanceAssertion => $"当天资产金额 {PortfolioViewModel.Format(record.CashAmount)} {record.Currency}",
        TransactionKind.Transfer => $"转到另一个账户 {PortfolioViewModel.Format(record.CashAmount)} {record.Currency}",
        TransactionKind.FxExchange => $"转到另一个账户 {PortfolioViewModel.Format(record.CashAmount)} {record.Currency}"
            + $" → {PortfolioViewModel.Format(record.CounterAmount)} {record.CounterCurrency}",
        _ => record.Kind.ToString(),
    };

    private void RecordButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        var accountId = SelectedAccountId;
        if (string.IsNullOrEmpty(accountId))
        {
            Notify("请先选择（或新建）一个账户。");
            return;
        }

        try
        {
            var transaction = BuildTransaction(accountId);
            _model.Record(transaction, EntryName.Text.Trim());
            Notify("已记账。");
            ClearEntry();
            _ = ReloadAsync();
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException or FormatException)
        {
            Notify($"记账失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Builds the ledger fact from the current form state. Internal so the type-boundary
    /// tests drive the real construction path (InternalsVisibleTo) — the view instance is
    /// never shown and never bound to a ledger.
    /// </summary>
    internal LedgerTransaction BuildTransaction(string accountId)
    {
        var kind = SelectedKind();
        var date = DateOnly.TryParse(EntryDate.Text.Trim(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new LedgerException($"无法解析日期「{EntryDate.Text}」，请用 yyyy-MM-dd。");
        var currency = (EntryCurrency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CNY";
        var note = EntryNote.Text.Trim();
        var needsInstrument = kind is TransactionKind.Buy or TransactionKind.Sell
            or TransactionKind.Dividend or TransactionKind.OpeningPosition or TransactionKind.Interest;
        var needsQuantity = kind is TransactionKind.Buy or TransactionKind.Sell or TransactionKind.OpeningPosition;
        var needsAmount = kind is TransactionKind.Dividend or TransactionKind.Interest
            or TransactionKind.Deposit or TransactionKind.Withdraw or TransactionKind.OpeningCash
            or TransactionKind.FxExchange;
        var needsFee = kind is TransactionKind.Buy or TransactionKind.Sell or TransactionKind.FxExchange;
        var needsTax = kind is TransactionKind.Buy or TransactionKind.Sell
            or TransactionKind.Dividend or TransactionKind.Interest;
        var symbol = needsInstrument ? ResolveSymbolForRecord() : "";
        var quantity = needsQuantity ? ParseNumber(EntryQuantity.Text) : 0m;
        var price = needsQuantity ? ParseNumber(EntryPrice.Text) : 0m;
        var amount = needsAmount ? ParseNumber(EntryAmount.Text) : 0m;
        var fee = needsFee ? ParseNumber(EntryFee.Text) : 0m;
        var tax = needsTax ? ParseNumber(EntryTax.Text) : 0m;

        return kind switch
        {
            TransactionKind.Buy =>
                LedgerTransaction.Buy(accountId, symbol, date, quantity, price, currency, fee, tax, note),
            TransactionKind.Sell =>
                LedgerTransaction.Sell(accountId, symbol, date, quantity, price, currency, fee, tax, note),
            TransactionKind.OpeningPosition =>
                LedgerTransaction.OpeningPosition(accountId, symbol, date, quantity, price, currency, note),
            TransactionKind.Dividend =>
                LedgerTransaction.Dividend(accountId, symbol, date, amount, currency, tax, note),
            TransactionKind.Interest =>
                LedgerTransaction.Interest(accountId, date, amount, currency, symbol, tax, note),
            TransactionKind.Deposit =>
                LedgerTransaction.Deposit(accountId, date, amount, currency, note),
            TransactionKind.Withdraw =>
                LedgerTransaction.Withdraw(accountId, date, amount, currency, note),
            TransactionKind.FxExchange => BuildFxExchange(accountId, date, amount, currency, fee, note),
            TransactionKind.OpeningCash =>
                LedgerTransaction.OpeningCash(accountId, date, amount, currency, note),
            // No silent fallback: a kind this form does not offer must say so instead of being
            // recorded as something else (the same rule the export and the row labels follow).
            _ => throw new LedgerException(
                $"这个表单不支持「{kind}」类型的记录，请用「新增记录」对话框或 CSV 导入。"),
        };
    }

    /// <summary>
    /// An exchange is booked from the two amounts on the receipt — both currencies and both
    /// amounts exactly as they happened, in the selected (multi-currency) account.
    /// </summary>
    private LedgerTransaction BuildFxExchange(
        string accountId, DateOnly date, decimal fromAmount, string fromCurrency, decimal fee, string note)
    {
        var counterAmount = ParseNumber(EntryCounterAmount.Text);
        var counterCurrency = (EntryCounterCurrency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var counterAccountId = (EntryCounterAccount.SelectedItem as AccountRow)?.Id;
        if (string.Equals(counterAccountId, accountId, StringComparison.Ordinal))
        {
            counterAccountId = null; // same account — keep the row shape identical to before
        }

        if (fromAmount <= 0m)
        {
            throw new LedgerException("换汇的换出金额必须大于 0（按回单填写）。");
        }

        if (counterAmount <= 0m)
        {
            throw new LedgerException("换汇的到账金额必须大于 0（按回单填写，不要用汇率反推）。");
        }

        return LedgerTransaction.FxExchange(
            accountId, date, fromAmount, fromCurrency, counterAmount, counterCurrency, fee, note,
            counterAccountId);
    }

    private static decimal ParseNumber(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0
            ? 0m
            : decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new LedgerException($"无法解析数字「{text}」。");
    }

    // --- symbol resolution (N1: novices never type the internal EXCHANGE:CODE form) ---

    private IReadOnlyList<Instrument> KnownInstruments =>
        _model?.Snapshot?.Instruments ?? Array.Empty<Instrument>();

    /// <summary>
    /// Resolves what the user actually typed into the internal symbol form. Unresolved or
    /// ambiguous input refuses to record — the ledger must never receive a guessed instrument.
    /// </summary>
    private string ResolveSymbolForRecord()
    {
        var input = EntrySymbol.Text.Trim();
        if (input.Length == 0)
        {
            return "";
        }

        var resolution = SymbolResolver.Resolve(input, KnownInstruments);
        if (resolution.Resolved is null)
        {
            throw new LedgerException($"标的「{input}」无法识别。{resolution.Reason}");
        }

        if (resolution.IsAmbiguous)
        {
            throw new LedgerException(
                $"标的「{input}」有 {resolution.Ambiguities.Count} 种解读（{string.Join("、", resolution.Ambiguities)}）。" +
                "请点击下方提示里的完整代码，明确是哪一个。");
        }

        return resolution.Resolved;
    }

    private void EntrySymbol_OnTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateSymbolHint();

    /// <summary>
    /// Live feedback under the symbol box: resolved (green check + why), ambiguous (clickable
    /// candidates), or unresolved (how to try again). This is the novice's error message
    /// *before* the mistake is recorded, not after.
    /// </summary>
    private void UpdateSymbolHint()
    {
        if (EntrySymbolHint is null)
        {
            return;
        }

        EntrySymbolHint.Inlines.Clear();
        var input = EntrySymbol.Text.Trim();
        if (input.Length == 0)
        {
            return;
        }

        var resolution = SymbolResolver.Resolve(input, KnownInstruments);
        var accent = TryFindResource("BrushAccent") as Brush ?? Brushes.White;
        var muted = TryFindResource("BrushFgMuted") as Brush ?? Brushes.Gray;
        var danger = TryFindResource("BrushDanger") as Brush ?? Brushes.OrangeRed;

        if (resolution.Resolved is null)
        {
            EntrySymbolHint.Inlines.Add(new Run($"✗ {resolution.Reason}") { Foreground = danger });
            return;
        }

        if (resolution.IsAmbiguous)
        {
            EntrySymbolHint.Inlines.Add(new Run(
                $"「{input}」有 {resolution.Ambiguities.Count} 种解读，记账前请点击选择：")
            {
                Foreground = danger,
            });
            for (var i = 0; i < resolution.Ambiguities.Count; i++)
            {
                if (i > 0)
                {
                    EntrySymbolHint.Inlines.Add(new Run("  ") { Foreground = muted });
                }

                var candidate = resolution.Ambiguities[i];
                var link = new Hyperlink(new Run(candidate))
                {
                    Foreground = accent,
                    FontWeight = FontWeights.SemiBold,
                    TextDecorations = TextDecorations.Underline,
                };
                link.Click += (_, _) => EntrySymbol.Text = candidate;
                EntrySymbolHint.Inlines.Add(link);
            }

            return;
        }

        EntrySymbolHint.Inlines.Add(new Run($"✓ 已识别：{resolution.Resolved}")
        {
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
        });
        EntrySymbolHint.Inlines.Add(new Run($"（{resolution.Reason}）") { Foreground = muted });
    }

    private void ClearEntry()
    {
        EntrySymbol.Clear();
        EntryName.Clear();
        EntryQuantity.Clear();
        EntryPrice.Clear();
        EntryAmount.Clear();
        EntryCounterAmount.Clear();
        EntryFee.Clear();
        EntryTax.Clear();
        EntryNote.Clear();
    }

    private void ChooseFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        var accountId = SelectedAccountId;
        if (string.IsNullOrEmpty(accountId))
        {
            Notify("请先选择（或新建）一个账户，作为文件中未指定账户时的归属。");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择交易流水 CSV",
            Filter = "CSV / TSV 文件|*.csv;*.tsv;*.txt|所有文件|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            _pendingImport = _model.PreviewImport(text, accountId);
            ShowPreview(Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            _pendingImport = null;
            ImportStatusText.Text = $"读取失败：{ex.Message}";
            UpdateImportButtonState();
        }
    }

    private void ShowPreview(string fileName)
    {
        if (_pendingImport is null)
        {
            return;
        }

        var lines = new List<string>
        {
            $"{fileName}：可导入 {_pendingImport.Importable} 行；重复 {_pendingImport.Duplicates} 行；" +
            $"错误 {_pendingImport.Invalid} 行。",
        };
        lines.AddRange(_pendingImport.Warnings);
        lines.AddRange(_pendingImport.Rows
            .Where(r => r.Error is not null)
            .Take(8)
            .Select(r => $"第 {r.LineNumber} 行：{r.Error}"));

        if (_pendingImport.Invalid > 8)
        {
            lines.Add($"…另有 {_pendingImport.Invalid - 8} 行错误未列出。");
        }

        ImportStatusText.Text = string.Join(Environment.NewLine, lines);
        UpdateImportButtonState();
    }

    private void UpdateImportButtonState() =>
        CommitImportButton.IsEnabled = _pendingImport is not null && _pendingImport.Importable > 0;

    private void CommitImportButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || _pendingImport is null)
        {
            return;
        }

        try
        {
            var written = _model.CommitImport(_pendingImport, SkipInvalidCheck.IsChecked == true);
            ImportStatusText.Text = $"已导入 {written} 条流水。重复行已自动跳过。";
            _pendingImport = null;
            UpdateImportButtonState();
            _ = ReloadAsync();
        }
        catch (LedgerException ex)
        {
            ImportStatusText.Text = $"{ex.Message}（勾选「跳过错误行」可只导入有效行）";
        }
    }

    private void SaveTemplateButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存导入模板",
            FileName = "OMNIX_交易导入模板.csv",
            Filter = "CSV 文件|*.csv",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, PortfolioViewModel.ImportTemplate(), Encoding.UTF8);
            Notify($"模板已保存：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Notify($"保存模板失败：{ex.Message}");
        }
    }

    private void ExportButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model?.Snapshot is null || string.IsNullOrEmpty(ExportDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ExportDirectory);
            var path = Path.Combine(
                ExportDirectory, $"交易流水_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, _model.ExportTransactionsCsv(), Encoding.UTF8);
            Notify($"已导出：{path}");
        }
        catch (Exception ex)
        {
            Notify($"导出失败：{ex.Message}");
        }
    }

    private void ReverseTransactionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || TransactionsGrid.SelectedItem is not TransactionRow row)
        {
            Notify("请先在流水表里选中一行。");
            return;
        }

        if (!row.CanReverse)
        {
            Notify(row.IsReversal
                ? "这条是冲正记录，本身不能再次冲正。"
                : "这条流水已冲正过，不能重复冲正。");
            return;
        }

        var confirm = MessageBox.Show(
            $"冲正 {row.Date} {row.Kind} {row.Symbol}？\n" +
            "原流水会保留，并新增一条反向冲正记录；持仓、现金与收益会回到这笔入账之前。\n" +
            "已入账的流水不能删除，冲正是唯一的更正方式。",
            "确认冲正",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var reason = $"冲正 {row.Date} {row.Kind} {row.Symbol}（用户在账本页确认）";
            _model.ReverseTransaction(row.Id, reason);
            Notify("已冲正：原流水与冲正记录都保留在账本里。");
            _ = ReloadAsync();
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException)
        {
            Notify($"冲正失败：{ex.Message}");
        }
    }

    private void Notify(string message)
    {
        ImportStatusText.Text = message;
        Notified?.Invoke(message);
    }
}
