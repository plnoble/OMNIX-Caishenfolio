using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One row of the contribution table, shaped for reading.</summary>
public sealed record ContributionRowView(
    string Symbol,
    string Unrealized,
    string Share,
    string LocalEffect,
    string FxEffect,
    string TrueUnrealized,
    string Note)
{
    public static ContributionRowView From(ContributionRow row, string baseCcy) =>
        new(
            row.Symbol,
            row.UnrealizedBase is { } u ? Signed(u.Amount, baseCcy) : "—",
            row.ShareOfUnrealized is { } s ? (s * 100m).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—",
            row.LocalEffectBase is { } l ? Signed(l.Amount, baseCcy) : "—",
            row.FxEffectBase is { } f ? Signed(f.Amount, baseCcy) : "未知",
            row.TrueUnrealizedBase is { } t ? Signed(t.Amount, baseCcy) : "—",
            row.UnknownReason ?? "");
    private static string Signed(decimal value, string ccy) =>
        value.ToString("+#,0.##;-#,0.##;0", CultureInfo.InvariantCulture) + " " + ccy;
}

public partial class RiskLabView : UserControl
{
    private readonly ObservableCollection<ContributionRowView> _rows = [];
    private PortfolioViewModel? _model;

    public RiskLabView()
    {
        InitializeComponent();
        ContributionGrid.ItemsSource = _rows;
    }

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
        RenderContributions();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => RenderContributions();

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Recomputes everything from the current snapshot; called on page entry.</summary>
    public void Render() => RenderContributions();

    /// <summary>Recomputes the contribution table from the latest snapshot. Unknowns stay unknown.</summary>
    private void RenderContributions()
    {
        var snapshot = _model?.Snapshot;
        if (snapshot is null)
        {
            return;
        }

        var baseCcy = snapshot.BaseCurrency;
        var rates = _model!.Workspace.Store.ListFxRates();
        var report = PortfolioContributionAnalyzer.Analyze(
            snapshot.Valuation, snapshot.Transactions, rates, baseCcy);

        _rows.Clear();
        foreach (var row in report.Rows)
        {
            _rows.Add(ContributionRowView.From(row, baseCcy));
        }
    }

    private void WhatIfButton_OnClick(object sender, RoutedEventArgs e)
    {
        var snapshot = _model?.Snapshot;
        if (snapshot is null)
        {
            WhatIfResultText.Text = "还没有估值快照，先在「总览」刷新一次。";
            return;
        }

        var symbol = WhatIfSymbolBox.Text.Trim();
        if (symbol.Length == 0 || !TryReadDecimal(WhatIfAmountBox.Text, "金额", out var amount))
        {
            return;
        }

        try
        {
            var currency = (WhatIfCurrencyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var result = TradeWhatIfAnalyzer.Simulate(
                snapshot.Valuation,
                _model!.Workspace.Settings.Thresholds,
                symbol,
                amount,
                currency);

            var lines = new List<string> { result.Summary };
            lines.AddRange(result.Breaches.Select(b => "⚠ " + b));
            lines.AddRange(result.Notes.Select(n => "· " + n));
            WhatIfResultText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException)
        {
            WhatIfResultText.Text = $"无法模拟：{ex.Message}";
        }
    }

    private void StressButton_OnClick(object sender, RoutedEventArgs e)
    {
        var snapshot = _model?.Snapshot;
        if (snapshot is null)
        {
            StressResultText.Text = "还没有估值快照，先在「总览」刷新一次。";
            return;
        }

        try
        {
            var scenario = new StressScenario
            {
                EquityDropPercent = ReadPercent(StressEquityBox.Text, "股票跌幅"),
                UsdMovePercent = ReadSignedPercent(StressUsdBox.Text, "USD 变动"),
                JpyMovePercent = ReadSignedPercent(StressJpyBox.Text, "JPY 变动"),
                LargestHoldingDropPercent = ReadPercent(StressLargestBox.Text, "最大持仓跌幅"),
                FixedIncomeMovePercent = ReadSignedPercent(StressFixedIncomeBox.Text, "债券类变动"),
            };

            var result = PortfolioStressAnalyzer.Run(
                snapshot.Valuation,
                snapshot.CashBalances,
                scenario,
                snapshot.Instruments.ToDictionary(i => i.Symbol, StringComparer.Ordinal));

            var lines = new List<string>
            {
                $"当前 {result.BaselineTotal.Amount:#,0.##} {result.BaselineTotal.Currency} → "
                + $"压后 {result.StressedTotal.Amount:#,0.##} {result.StressedTotal.Currency}"
                + $"（{result.ChangePercent * 100m:+0.#;-0.#;0}%）。",
            };
            lines.AddRange(result.Assumptions.Select(a => "· " + a));
            lines.AddRange(result.Warnings.Select(w => "⚠ " + w));
            lines.Add(StressResult.Disclaimer);
            StressResultText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException)
        {
            StressResultText.Text = $"无法运行：{ex.Message}";
        }
    }

    private void RebalanceButton_OnClick(object sender, RoutedEventArgs e)
    {
        var snapshot = _model?.Snapshot;
        if (snapshot is null)
        {
            RebalanceResultText.Text = "还没有估值快照，先在「总览」刷新一次。";
            return;
        }

        var available = TryReadDecimal(RebalanceCashBox.Text, "可用新增资金", out var amount) ? amount : 0m;
        var comparison = RebalanceComparisonAnalyzer.Compare(
            snapshot.Valuation,
            _model!.Workspace.Settings.TargetAssetAllocation,
            available);

        var lines = new List<string>();
        if (!comparison.Computable)
        {
            lines.Add(comparison.BlockReason ?? "无法比较。");
        }
        else
        {
            lines.Add(comparison.Summary);
            lines.AddRange(comparison.Notes.Select(n => "· " + n));
        }

        RebalanceResultText.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Attributes the worst drawdown in the stored history. Needs per-holding snapshots, which
    /// start accumulating only after this feature ships — before that, the honest answer is that
    /// the history is totals-only and cannot be explained.
    /// </summary>
    private void AttributionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        try
        {
            var store = _model.Workspace.Store;
            var report = DrawdownAttributionAnalyzer.Analyze(
                store.ListValuationHistory(_model.BaseCurrency),
                store.ListPositionValuationHistory(_model.BaseCurrency),
                store.ListFxRates(),
                _model.BaseCurrency);

            var lines = new List<string> { report.Summary };
            lines.AddRange(report.Rows
                .Where(r => r.ChangeBase.Amount != 0m)
                .Take(8)
                .Select(r =>
                {
                    var share = r.ShareOfChange is { } s
                        ? $"（占变动 {s * 100m:0.#}%）"
                        : "";
                    return $"· {r.Label}：{r.ChangeBase.Amount:+#,0.##;-#,0.##;0}{share}{(r.Note is null ? "" : "　" + r.Note)}";
                }));
            lines.AddRange(report.Warnings.Select(w => "⚠ " + w));

            AttributionResultText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException or FormatException)
        {
            AttributionResultText.Text = $"归因失败：{ex.Message}";
        }
    }

    private static bool TryReadDecimal(string text, string label, out decimal value)
    {
        if (decimal.TryParse((text ?? "").Trim().Replace(",", ""),
                NumberStyles.Any, CultureInfo.InvariantCulture, out value) && value >= 0m)
        {
            return true;
        }

        value = 0m;
        return false;
    }

    /// <summary>Reads a 0–100 percentage the user typed; blank means 0.</summary>
    private static decimal ReadPercent(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        if (TryReadDecimal(text, label, out var value) && value <= 100m)
        {
            return value;
        }

        throw new LedgerException($"{label}必须是 0–100 之间的数字（收到 {text}）。");
    }

    private static decimal ReadSignedPercent(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        if (TryReadDecimal(text.Replace("-", "").Replace("−", ""), label, out var magnitude)
            && magnitude <= 100m
            && decimal.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var signed))
        {
            return signed;
        }

        throw new LedgerException($"{label}必须是 −100 到 100 之间的数字（收到 {text}）。");
    }
}
