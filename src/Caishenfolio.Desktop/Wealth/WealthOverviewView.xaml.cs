using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Caishenfolio.Desktop.Wealth;

public partial class WealthOverviewView : UserControl
{
    private PortfolioViewModel? _model;

    public WealthOverviewView()
    {
        InitializeComponent();
        XirrCaption.ToolTip = TermGlossary.Tooltip("xirr");
        TwrCaption.ToolTip = TermGlossary.Tooltip("twr");
        DrawdownCaption.ToolTip = TermGlossary.Tooltip("drawdown");
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
        AllocationGrid.ItemsSource = model.Allocation;
        WarningList.ItemsSource = model.Warnings;
        AlertList.ItemsSource = model.Alerts;
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
        TotalValueText.Text = _model.TotalValueText;
        TotalPnlText.Text = _model.TotalPnlText;
        CostText.Text = _model.CostText;
        CashText.Text = _model.CashText;
        XirrText.Text = _model.XirrText;
        TwrText.Text = _model.TwrText;
        DrawdownText.Text = _model.DrawdownText;
        RiskSummaryText.Text = _model.RiskSummaryText;
        AsOfText.Text = $"估值日 {_model.AsOfText}  ·  本位币 {_model.BaseCurrency}";
        RefreshButton.IsEnabled = !_model.IsBusy;
        WarningBanner.Visibility = _model.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Incomplete coverage is a property of the risk conclusions themselves, so it marks the
        // alert block too — not only the valuation banner further up the page.
        CoverageStateText.Text = _model.RiskCoverageIsUnknown
            ? "估值不可用：全部持仓未定价或缺汇率，集中度与收益均为未知，没有“正常”结论。"
            : "估值不完整：以下风险结论仅基于已定价部分，不是全组合结论。";
        CoverageStateText.Visibility = _model.RiskCoverageIncomplete ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RefreshAsync().ConfigureAwait(true);
        }
    }

    private async void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        var dialog = _model.CreateSettingsWindow();
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();

        // Base currency, thresholds and targets all change what a valuation says, so re-run it.
        if (dialog.Saved)
        {
            await _model.RefreshAsync().ConfigureAwait(true);
        }
    }
}
