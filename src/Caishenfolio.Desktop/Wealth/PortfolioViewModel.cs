using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>
/// Binding surface for the wealth pages. All ledger logic lives in <see cref="PortfolioWorkspace"/>;
/// this type only shapes it for display.
/// </summary>
public sealed class PortfolioViewModel : INotifyPropertyChanged
{
    private readonly PortfolioWorkspace _workspace;
    private string _statusText = "尚未加载账本。";
    private string _totalValueText = "—";
    private string _totalPnlText = "—";
    private string _costText = "—";
    private string _cashText = "—";
    private string _xirrText = "—";
    private string _asOfText = "—";
    private string _drawdownText = "—";
    private string _riskSummaryText = "";
    private bool _isComplete = true;
    private bool _isBusy;

    public PortfolioViewModel(PortfolioWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PositionRow> Positions { get; } = [];
    public ObservableCollection<AllocationRow> Allocation { get; } = [];
    public ObservableCollection<TransactionRow> Transactions { get; } = [];
    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<AlertRow> Alerts { get; } = [];

    public string BaseCurrency => _workspace.BaseCurrency;
    public WorkspaceSnapshot? Snapshot { get; private set; }

    /// <summary>Attaches (or clears) the price feed once the Analytics Core comes up.</summary>
    public void UsePricingSource(IMarketPricingSource? source) => _workspace.PricingSource = source;

    /// <summary>Preference dialog, bound to the same workspace so a save takes effect immediately.</summary>
    public PortfolioSettingsWindow CreateSettingsWindow() =>
        new(_workspace, _workspace.PlanStore);

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string TotalValueText { get => _totalValueText; private set => Set(ref _totalValueText, value); }
    public string TotalPnlText { get => _totalPnlText; private set => Set(ref _totalPnlText, value); }
    public string CostText { get => _costText; private set => Set(ref _costText, value); }
    public string CashText { get => _cashText; private set => Set(ref _cashText, value); }
    public string XirrText { get => _xirrText; private set => Set(ref _xirrText, value); }
    public string AsOfText { get => _asOfText; private set => Set(ref _asOfText, value); }
    public string DrawdownText { get => _drawdownText; private set => Set(ref _drawdownText, value); }
    public string RiskSummaryText { get => _riskSummaryText; private set => Set(ref _riskSummaryText, value); }
    public bool IsComplete { get => _isComplete; private set => Set(ref _isComplete, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在取价并估值…";
        try
        {
            var snapshot = await _workspace.RefreshAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            StatusText = $"刷新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Account AddAccount(string name, AccountKind kind, string currency, string broker = "") =>
        _workspace.AddAccount(name, kind, currency, broker);

    public void Record(LedgerTransaction transaction, string? instrumentName = null) =>
        _workspace.Record(transaction, instrumentName);

    public CsvImportPreview PreviewImport(string csvText, string accountId) =>
        _workspace.PreviewImport(csvText, accountId);

    public int CommitImport(CsvImportPreview preview, bool skipInvalidRows) =>
        _workspace.CommitImport(preview, skipInvalidRows);

    public bool RemoveTransaction(string id) => _workspace.Store.RemoveTransaction(id);

    public string ExportPositionsCsv() =>
        Snapshot is null ? "" : _workspace.ExportPositionsCsv(Snapshot);

    public string ExportAllocationCsv() =>
        Snapshot is null ? "" : _workspace.ExportAllocationCsv(Snapshot);

    public string ExportTransactionsCsv() =>
        Snapshot is null ? "" : _workspace.ExportTransactionsCsv(Snapshot);

    public static string ImportTemplate() => TransactionCsvImporter.BuildTemplate();

    private void Apply(WorkspaceSnapshot snapshot)
    {
        Snapshot = snapshot;
        var valuation = snapshot.Valuation;

        Positions.Clear();
        foreach (var item in valuation.Positions.Where(p => p.Position.IsOpen))
        {
            Positions.Add(PositionRow.From(item, snapshot));
        }

        Allocation.Clear();
        AddSlices("品种", valuation.ByAssetClass);
        AddSlices("市场", valuation.ByRegion);
        AddSlices("货币", valuation.ByCurrency);
        AddSlices("账户", valuation.ByAccount);

        Transactions.Clear();
        var accountNames = snapshot.Accounts.ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        foreach (var txn in snapshot.Transactions.OrderByDescending(t => t.TradeDate).ThenByDescending(t => t.RecordedAt))
        {
            Transactions.Add(TransactionRow.From(txn, accountNames));
        }

        Accounts.Clear();
        foreach (var account in snapshot.Accounts)
        {
            var cash = snapshot.CashBalances
                .Where(b => b.AccountId == account.Id)
                .Select(b => $"{Format(b.Amount)} {b.Currency}")
                .ToArray();
            Accounts.Add(new AccountRow(
                account.Id,
                account.Name,
                KindLabel(account.Kind),
                account.MainCurrency,
                cash.Length == 0 ? "—" : string.Join("  |  ", cash),
                account.Archived ? "已归档" : "启用"));
        }

        Warnings.Clear();
        foreach (var warning in snapshot.Warnings)
        {
            Warnings.Add(warning);
        }

        Alerts.Clear();
        foreach (var alert in snapshot.Alerts)
        {
            Alerts.Add(new AlertRow(
                alert.Title,
                alert.Message,
                alert.Severity == AlertSeverity.Warning));
        }
        foreach (var drift in snapshot.Risk.Drift)
        {
            Alerts.Add(new AlertRow($"配置偏离：{drift.Label}", drift.Message, false));
        }

        DrawdownText = snapshot.Risk.MaxDrawdown is { } drawdown
            ? (drawdown * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";
        RiskSummaryText = snapshot.Risk.Summary;

        var ccy = snapshot.BaseCurrency;
        TotalValueText = $"{Format(valuation.TotalValue.Amount)} {ccy}";
        CostText = $"{Format(valuation.CostBasis.Amount)} {ccy}";
        CashText = $"{Format(valuation.CashValue.Amount)} {ccy}";
        TotalPnlText = FormatSigned(valuation.TotalPnl.Amount, ccy);
        XirrText = snapshot.Xirr is { } xirr
            ? (xirr * 100).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%"
            : "—";
        AsOfText = snapshot.AsOf.ToString("yyyy-MM-dd");
        IsComplete = valuation.IsComplete;

        StatusText = snapshot.IsEmpty
            ? "账本还是空的。先在「账本」页新建账户，然后手工录入或导入 CSV。"
            : valuation.IsComplete
                ? $"已估值 {Positions.Count} 个持仓，{snapshot.Accounts.Count} 个账户。"
                : $"估值不完整：{snapshot.Warnings.Count} 项未计入合计（见下方提示）。";
    }

    private void AddSlices(string dimension, IReadOnlyList<AllocationSlice> slices)
    {
        foreach (var slice in slices)
        {
            Allocation.Add(new AllocationRow(
                dimension,
                slice.Label,
                Format(slice.Value.Amount),
                (slice.Weight * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                (double)Math.Clamp(slice.Weight, 0m, 1m)));
        }
    }

    internal static string Format(decimal value) =>
        value.ToString("#,0.##", CultureInfo.InvariantCulture);

    internal static string FormatSigned(decimal value, string currency) =>
        value.ToString("+#,0.##;-#,0.##;0", CultureInfo.InvariantCulture) + " " + currency;

    private static string KindLabel(AccountKind kind) => kind switch
    {
        AccountKind.Securities => "证券",
        AccountKind.FundPlatform => "基金平台",
        AccountKind.Bank => "银行",
        AccountKind.Cash => "现金",
        _ => "其他",
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record PositionRow(
    string Symbol,
    string Name,
    string Market,
    string AssetClass,
    string Currency,
    string Quantity,
    string AverageCost,
    string LastPrice,
    string MarketValue,
    string MarketValueBase,
    string UnrealizedPnl,
    string ReturnPct,
    string Weight,
    string Status,
    bool IsGain,
    bool Priced)
{
    public static PositionRow From(PositionValuation item, WorkspaceSnapshot snapshot)
    {
        var position = item.Position;
        var instrument = snapshot.Instruments.FirstOrDefault(i => i.Symbol == position.Symbol);
        var pnl = item.UnrealizedPnlBase?.Amount;
        var cost = item.CostBasisBase.Amount;

        return new PositionRow(
            position.Symbol,
            instrument?.Name ?? position.Symbol,
            instrument?.Region.ToDisplayName() ?? MarketLabels.FromSymbol(position.Symbol),
            instrument?.AssetClass.ToDisplayName() ?? "",
            position.Currency,
            PortfolioViewModel.Format(position.Quantity),
            PortfolioViewModel.Format(position.AverageCost.Amount),
            item.Quote is null ? "—" : PortfolioViewModel.Format(item.Quote.Price),
            item.MarketValue is null ? "—" : PortfolioViewModel.Format(item.MarketValue.Value.Amount),
            item.MarketValueBase is null ? "—" : PortfolioViewModel.Format(item.MarketValueBase.Value.Amount),
            pnl is null ? "—" : PortfolioViewModel.FormatSigned(pnl.Value, snapshot.BaseCurrency),
            pnl is null || cost == 0m
                ? "—"
                : (pnl.Value / cost * 100m).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%",
            item.Weight is null ? "—" : (item.Weight.Value * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%",
            item.Priced ? "正常" : "缺价格",
            pnl >= 0m,
            item.Priced);
    }
}

public sealed record AllocationRow(
    string Dimension, string Label, string Value, string Weight, double WeightFraction);

public sealed record AlertRow(string Title, string Message, bool IsWarning);

public sealed record TransactionRow(
    string Id,
    string Date,
    string Account,
    string Kind,
    string Symbol,
    string Quantity,
    string Price,
    string Currency,
    string Fee,
    string Amount,
    string Note)
{
    public static TransactionRow From(LedgerTransaction txn, IReadOnlyDictionary<string, string> accountNames) =>
        new(
            txn.Id,
            txn.TradeDate.ToString("yyyy-MM-dd"),
            accountNames.TryGetValue(txn.AccountId, out var name) ? name : txn.AccountId,
            KindLabel(txn.Kind),
            txn.Symbol,
            txn.Quantity == 0m ? "" : PortfolioViewModel.Format(txn.Quantity),
            txn.Price == 0m ? "" : PortfolioViewModel.Format(txn.Price),
            txn.Currency,
            txn.Fee + txn.Tax == 0m ? "" : PortfolioViewModel.Format(txn.Fee + txn.Tax),
            txn.CashAmount == 0m ? "" : PortfolioViewModel.Format(txn.CashAmount),
            txn.Note);

    private static string KindLabel(TransactionKind kind) => kind switch
    {
        TransactionKind.Buy => "买入",
        TransactionKind.Sell => "卖出",
        TransactionKind.Dividend => "分红",
        TransactionKind.StockDividend => "送股",
        TransactionKind.Split => "拆股",
        TransactionKind.Interest => "利息",
        TransactionKind.Deposit => "入金",
        TransactionKind.Withdraw => "出金",
        TransactionKind.Fee => "费用",
        TransactionKind.Tax => "税",
        TransactionKind.FxExchange => "换汇",
        TransactionKind.OpeningPosition => "期初持仓",
        _ => "期初现金",
    };
}

public sealed record AccountRow(
    string Id, string Name, string Kind, string MainCurrency, string Cash, string Status);
