using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Caishenfolio.Desktop.Wealth;

public partial class HoldingsView : UserControl
{
    private PortfolioViewModel? _model;

    public HoldingsView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when a report is written, so the shell can report where it landed.</summary>
    public event Action<string>? Exported;

    /// <summary>Directory reports are written to; the Host owns the path root.</summary>
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
        PositionsGrid.ItemsSource = model.Positions;
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
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RefreshAsync().ConfigureAwait(true);
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
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var positions = Path.Combine(ExportDirectory, $"持仓_{stamp}.csv");
            var allocation = Path.Combine(ExportDirectory, $"资产配置_{stamp}.csv");

            File.WriteAllText(positions, _model.ExportPositionsCsv(), System.Text.Encoding.UTF8);
            File.WriteAllText(allocation, _model.ExportAllocationCsv(), System.Text.Encoding.UTF8);
            Exported?.Invoke($"已导出：{positions}；{allocation}");
        }
        catch (Exception ex)
        {
            Exported?.Invoke($"导出失败：{ex.Message}");
        }
    }
}
