using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
            or TransactionKind.Deposit or TransactionKind.Withdraw or TransactionKind.OpeningCash;

        EntrySymbol.IsEnabled = needsInstrument || kind == TransactionKind.Interest;
        EntryName.IsEnabled = EntrySymbol.IsEnabled;
        EntryQuantity.IsEnabled = needsQuantity;
        EntryPrice.IsEnabled = needsQuantity;
        EntryAmount.IsEnabled = needsAmount;
    }

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
            _model.AddAccount(name, kind, currency);
            NewAccountName.Clear();
            Notify($"已新建账户「{name}」。");
            _ = ReloadAsync();
        }
        catch (Exception ex)
        {
            Notify($"新建账户失败：{ex.Message}");
        }
    }

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

    private LedgerTransaction BuildTransaction(string accountId)
    {
        var kind = SelectedKind();
        var date = DateOnly.TryParse(EntryDate.Text.Trim(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new LedgerException($"无法解析日期「{EntryDate.Text}」，请用 yyyy-MM-dd。");
        var symbol = EntrySymbol.Text.Trim();
        var currency = (EntryCurrency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CNY";
        var quantity = ParseNumber(EntryQuantity.Text);
        var price = ParseNumber(EntryPrice.Text);
        var amount = ParseNumber(EntryAmount.Text);
        var fee = ParseNumber(EntryFee.Text);
        var tax = ParseNumber(EntryTax.Text);
        var note = EntryNote.Text.Trim();

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
            _ => LedgerTransaction.OpeningCash(accountId, date, amount, currency, note),
        };
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

    private void ClearEntry()
    {
        EntrySymbol.Clear();
        EntryName.Clear();
        EntryQuantity.Clear();
        EntryPrice.Clear();
        EntryAmount.Clear();
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

    private void DeleteTransactionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || TransactionsGrid.SelectedItem is not TransactionRow row)
        {
            Notify("请先在流水表里选中一行。");
            return;
        }

        var confirm = MessageBox.Show(
            $"删除 {row.Date} {row.Kind} {row.Symbol}？\n持仓与收益会随之重新计算。",
            "确认删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _model.RemoveTransaction(row.Id);
        Notify("已删除。");
        _ = ReloadAsync();
    }

    private void Notify(string message)
    {
        ImportStatusText.Text = message;
        Notified?.Invoke(message);
    }
}
