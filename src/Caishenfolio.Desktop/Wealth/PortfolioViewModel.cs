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
    private string _twrText = "—";
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
    /// <summary>The four per-account numbers (WP-01 目标 3), one row per account.</summary>
    public ObservableCollection<AccountSummaryRow> AccountSummaries { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<AlertRow> Alerts { get; } = [];

    public string BaseCurrency => _workspace.BaseCurrency;
    public WorkspaceSnapshot? Snapshot { get; private set; }

    /// <summary>
    /// The facade behind this view model, for pages that need store-level data the snapshot does
    /// not carry (FX rate history for the contribution split, thresholds for simulations).
    /// </summary>
    public PortfolioWorkspace Workspace => _workspace;

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
    public string TwrText { get => _twrText; private set => Set(ref _twrText, value); }
    public string AsOfText { get => _asOfText; private set => Set(ref _asOfText, value); }
    public string DrawdownText { get => _drawdownText; private set => Set(ref _drawdownText, value); }
    public string RiskSummaryText { get => _riskSummaryText; private set => Set(ref _riskSummaryText, value); }
    public bool IsComplete { get => _isComplete; private set => Set(ref _isComplete, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    /// <summary>True when part of the market value is missing — risk ratios are partial.</summary>
    public bool RiskCoverageIncomplete =>
        Snapshot is { } snap && snap.Risk.Coverage.State != CoverageState.Complete;

    /// <summary>True when nothing priced at all — every ratio is unknown.</summary>
    public bool RiskCoverageIsUnknown =>
        Snapshot is { } snap && snap.Risk.Coverage.IsUnknown;

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

    public Account AddAccount(
        string name,
        AccountKind kind,
        string currency,
        string broker = "",
        AccountValuationMode mode = AccountValuationMode.Detail) =>
        _workspace.AddAccount(name, kind, currency, broker, mode);

    public void Record(LedgerTransaction transaction, string? instrumentName = null) =>
        _workspace.Record(transaction, instrumentName);

    public CsvImportPreview PreviewImport(string csvText, string accountId) =>
        _workspace.PreviewImport(csvText, accountId);

    public int CommitImport(CsvImportPreview preview, bool skipInvalidRows) =>
        _workspace.CommitImport(preview, skipInvalidRows);

    /// <summary>
    /// Marks a recorded transaction as cancelled: a reversal row is written and both rows stay.
    /// There is deliberately no remove — the ledger is append-only and corrects by pairing.
    /// </summary>
    public LedgerTransaction ReverseTransaction(string id, string reason = "") =>
        _workspace.ReverseTransaction(id, reason);

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
        var reversedIds = _workspace.Store.ReversedTransactionIds();
        foreach (var txn in snapshot.Transactions.OrderByDescending(t => t.TradeDate).ThenByDescending(t => t.RecordedAt))
        {
            Transactions.Add(TransactionRow.From(txn, accountNames, reversedIds));
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

        AccountSummaries.Clear();
        foreach (var summary in snapshot.AccountSummaries)
        {
            AccountSummaries.Add(AccountSummaryRow.From(summary, snapshot.BaseCurrency));
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
        // 累计收益 has exactly one definition now (WP-02 裁决): Σ 账户（总资产 − 净投入), the
        // same number the ledger page shows per account. `valuation.TotalPnl`
        // (未实现+已实现+分红) is a different quantity and is no longer this headline.
        TotalPnlText = snapshot.CumulativePnlBase is { } cumulative
            ? FormatSigned(cumulative.Amount, ccy)
            : "未知";
        XirrText = snapshot.Xirr is { } xirr
            ? (xirr * 100).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%"
            : "—";
        TwrText = snapshot.Twr is { } twr
            ? (twr * 100).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%"
            : "—";
        AsOfText = snapshot.AsOf.ToString("yyyy-MM-dd");
        IsComplete = valuation.IsComplete;

        StatusText = snapshot.IsEmpty
            ? "账本还是空的。先在「账本」页新建账户，然后手工录入或导入 CSV。"
            : valuation.IsComplete
                ? $"已估值 {Positions.Count} 个持仓，{snapshot.Accounts.Count} 个账户。"
                : snapshot.Risk.Coverage.IsUnknown
                    ? "估值不可用：所有持仓都未定价，集中度与收益均为未知（不会按 0 计算）。"
                    : $"估值不完整：{snapshot.Warnings.Count} 项需要处理，风险结论仅基于已确定的部分（见下方提示）。";
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

    /// <summary>Null means "no number", which shows as a dash — the reason lives in the note column.</summary>
    internal static string FormatPercent(double? value) => value is { } rate
        ? (rate * 100).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%"
        : "—";

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
    string Note,
    bool IsReversal,
    bool IsCancelled,
    bool CanReverse)
{
    public static TransactionRow From(
        LedgerTransaction txn,
        IReadOnlyDictionary<string, string> accountNames,
        IReadOnlySet<string> reversedIds) =>
        new(
            txn.Id,
            txn.TradeDate.ToString("yyyy-MM-dd"),
            // A move involves two accounts: showing only the source hides where the money went.
            CounterLabel(txn, accountNames),
            KindLabel(txn.Kind),
            txn.Symbol,
            txn.Quantity == 0m ? "" : PortfolioViewModel.Format(txn.Quantity),
            txn.Price == 0m ? "" : PortfolioViewModel.Format(txn.Price),
            txn.Currency,
            txn.Fee + txn.Tax == 0m ? "" : PortfolioViewModel.Format(txn.Fee + txn.Tax),
            AmountLabel(txn),
            txn.Note,
            txn.IsReversal,
            reversedIds.Contains(txn.Id),
            !txn.IsReversal && !reversedIds.Contains(txn.Id));

    private static string CounterLabel(
        LedgerTransaction txn, IReadOnlyDictionary<string, string> accountNames)
    {
        var own = accountNames.TryGetValue(txn.AccountId, out var name) ? name : txn.AccountId;
        if (string.IsNullOrEmpty(txn.CounterAccountId))
        {
            return own;
        }

        var other = accountNames.TryGetValue(txn.CounterAccountId, out var counter)
            ? counter
            : txn.CounterAccountId;
        return $"{own} → {other}";
    }

    /// <summary>An exchange shows both receipt amounts — either side alone is half the fact.</summary>
    private static string AmountLabel(LedgerTransaction txn) => txn.Kind == TransactionKind.FxExchange
        ? $"{PortfolioViewModel.Format(txn.CashAmount)} {txn.Currency} → {PortfolioViewModel.Format(txn.CounterAmount)} {txn.CounterCurrency}"
        : txn.CashAmount == 0m ? "" : PortfolioViewModel.Format(txn.CashAmount);

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
        TransactionKind.OpeningCash => "期初现金",
        TransactionKind.BalanceAssertion => "当天资产金额",
        TransactionKind.Transfer => "账户间转移",
        // Never borrow another kind's name; an unmapped kind shows its raw name instead.
        _ => kind.ToString(),
    };
}

public sealed record AccountRow(
    string Id, string Name, string Kind, string MainCurrency, string Cash, string Status);

/// <summary>
/// One account's 总资产 / 累计收益 / 资金加权收益率 / 年化 (WP-01 目标 3).
///
/// Unknown is shown as 「未知」 with the reason beside it, never as 0 or a bare dash: an account
/// whose value nobody has reported must not look like an account worth nothing (D3).
/// </summary>
public sealed record AccountSummaryRow(
    string Id,
    string Name,
    string Mode,
    string TotalValue,
    string CumulativePnl,
    string MoneyWeightedReturn,
    string Annualized,
    string Note)
{
    public static AccountSummaryRow From(AccountSummary summary, string ccy)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var notes = new List<string>(summary.Warnings);
        if (!string.IsNullOrEmpty(summary.MoneyWeightedReturn.Reason) && summary.MoneyWeightedReturn.Value is null)
        {
            notes.Add(summary.MoneyWeightedReturn.Reason);
        }

        return new AccountSummaryRow(
            summary.AccountId,
            summary.Name,
            summary.Mode == AccountValuationMode.Balance ? "余额模式" : "明细模式",
            summary.TotalValueBase is { } total
                ? $"{PortfolioViewModel.Format(total.Amount)} {ccy}"
                : "未知",
            summary.CumulativePnlBase is { } pnl
                ? PortfolioViewModel.FormatSigned(pnl.Amount, ccy)
                : "未知",
            PortfolioViewModel.FormatPercent(summary.MoneyWeightedReturn.Value),
            PortfolioViewModel.FormatPercent(summary.Annualized),
            string.Join("；", notes.Distinct(StringComparer.Ordinal)));
    }
}
