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
        AsOfText.Text = $"估值日 {_model.AsOfText}  ·  本位币 {_model.BaseCurrency}";
        RefreshButton.IsEnabled = !_model.IsBusy;
        WarningBanner.Visibility = _model.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RefreshAsync().ConfigureAwait(true);
        }
    }
}
