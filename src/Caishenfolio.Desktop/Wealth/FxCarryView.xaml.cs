using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One currency pair, with the carry expressed as money rather than a bare percentage.</summary>
public sealed record CarryRow
{
    public required string Pair { get; init; }
    public required string RateText { get; init; }
    public required string BaseRateText { get; init; }
    public required string QuoteRateText { get; init; }
    public required string CarryText { get; init; }
    public required string CarryOnMillionText { get; init; }
    public required bool CarryIsPositive { get; init; }
    public required string PercentileText { get; init; }
    public required string RangeText { get; init; }

    public static CarryRow From(CarryLegDto leg, string baseCurrency)
    {
        var carry = leg.Carry;
        return new CarryRow
        {
            Pair = leg.Pair,
            RateText = Number(leg.Rate),
            BaseRateText = Percent(leg.BaseRate),
            QuoteRateText = Percent(leg.QuoteRate),
            CarryText = carry is null
                ? "—"
                : (carry.Value * 100m).ToString("+0.##;-0.##", CultureInfo.InvariantCulture) + "%",
            // A percentage is abstract; the same number as money on a million is not.
            CarryOnMillionText = carry is null
                ? "—"
                : (carry.Value * 1_000_000m).ToString("+#,0;-#,0", CultureInfo.InvariantCulture)
                  + " " + baseCurrency,
            CarryIsPositive = carry is > 0m,
            PercentileText = leg.Percentile is { } p
                ? p.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : "数据不足",
            RangeText = leg.Low is { } low && leg.High is { } high
                ? $"{Number(low)} ~ {Number(high)}"
                : "—",
        };
    }

    private static string Number(decimal? value) =>
        value is null ? "—" : value.Value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Percent(decimal? value) =>
        value is null ? "—" : (value.Value * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}

public partial class FxCarryView : UserControl
{
    private readonly ObservableCollection<CarryRow> _rows = [];
    private readonly ObservableCollection<string> _notes = [];
    private Func<AnalyticsCoreClient?>? _clientAccessor;
    private Func<string>? _baseCurrencyAccessor;

    public FxCarryView()
    {
        InitializeComponent();
        LegGrid.ItemsSource = _rows;
        NoteList.ItemsSource = _notes;
    }

    public void Bind(Func<AnalyticsCoreClient?> clientAccessor, Func<string> baseCurrencyAccessor)
    {
        _clientAccessor = clientAccessor;
        _baseCurrencyAccessor = baseCurrencyAccessor;
    }

    private async void LoadButton_OnClick(object sender, RoutedEventArgs e)
    {
        var client = _clientAccessor?.Invoke();
        if (client is null)
        {
            StatusText.Text = "分析核心尚未就绪，无法取汇率数据。可到「系统」页启动核心。";
            return;
        }

        LoadButton.IsEnabled = false;
        StatusText.Text = "正在取汇率与利差…";
        try
        {
            var baseCurrency = _baseCurrencyAccessor?.Invoke() ?? "CNY";
            string[] others = ["USD", "HKD", "JPY", "EUR"];
            var response = await client
                .GetFxCarryAsync(baseCurrency, others.Where(c => c != baseCurrency))
                .ConfigureAwait(true);

            _rows.Clear();
            _notes.Clear();
            foreach (var leg in response.Legs)
            {
                _rows.Add(CarryRow.From(leg, baseCurrency));
            }

            foreach (var note in response.Notes)
            {
                _notes.Add("· " + note);
            }

            DisclaimerText.Text = response.Disclaimer;
            StatusText.Text = _rows.Count == 0
                ? "没有取到任何货币对。"
                : $"本位币 {baseCurrency}，共 {_rows.Count} 个货币对。「持有方利率」指持有该货币能拿到的利率。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"查询失败：{ex.Message}";
        }
        finally
        {
            LoadButton.IsEnabled = true;
        }
    }
}
