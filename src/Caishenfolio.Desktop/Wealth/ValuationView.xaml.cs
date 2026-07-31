using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One indicator, shaped for reading rather than for calculation.</summary>
public sealed record ValuationRow
{
    public required string Name { get; init; }
    public required string CurrentText { get; init; }
    public required string PercentileText { get; init; }
    public required double PercentileValue { get; init; }
    public required string BandLabel { get; init; }
    public required string BandDescription { get; init; }
    public required Brush BandBrush { get; init; }
    public required string WhatItIs { get; init; }
    public required string HowToRead { get; init; }
    public required string Caveat { get; init; }
    public required IReadOnlyList<string> Outcomes { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public static ValuationRow From(MetricReadingDto reading)
    {
        var percentile = reading.Percentile;
        return new ValuationRow
        {
            Name = reading.Name,
            CurrentText = reading.Current is { } value
                ? value.ToString("0.####", CultureInfo.InvariantCulture)
                : "—",
            PercentileText = percentile is { } p
                ? $"历史分位 {p.ToString("0.#", CultureInfo.InvariantCulture)}%"
                : "历史分位 —",
            PercentileValue = (double)(percentile ?? 0m),
            BandLabel = reading.Band?.Label ?? "数据不足",
            BandDescription = reading.Band?.Description ?? "历史样本不够，无法判断当前处于什么位置。",
            BandBrush = BrushFor(percentile),
            WhatItIs = Prefixed("这是什么：", reading.Explanation, "what"),
            HowToRead = Prefixed("怎么看：", reading.Explanation, "read"),
            Caveat = Prefixed("注意：", reading.Explanation, "caveat"),
            Outcomes = reading.OutcomeSummaries.Count > 0
                ? reading.OutcomeSummaries
                : ["没有可比的历史样本。"],
            Notes = reading.Notes,
        };
    }

    /// <summary>
    /// Colour tracks position in the range, low to high. It marks where a number sits, which is
    /// why the same green is used for a low PE and a high dividend yield only by coincidence —
    /// the page never claims a colour means "buy".
    /// </summary>
    private static Brush BrushFor(decimal? percentile) => percentile switch
    {
        null => new SolidColorBrush(Color.FromRgb(0xB4, 0xC0, 0xD0)),
        <= 20m => new SolidColorBrush(Color.FromRgb(0x5E, 0xE4, 0xA8)),
        <= 40m => new SolidColorBrush(Color.FromRgb(0x9A, 0xDF, 0xB0)),
        <= 60m => new SolidColorBrush(Color.FromRgb(0xD9, 0xD4, 0x8A)),
        <= 80m => new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x7A)),
        _ => new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x88)),
    };

    private static string Prefixed(string prefix, IReadOnlyDictionary<string, string> source, string key) =>
        source.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)
            ? prefix + text
            : "";
}

public partial class ValuationView : UserControl
{
    private readonly ObservableCollection<ValuationRow> _rows = [];
    private Func<AnalyticsCoreClient?>? _clientAccessor;

    public ValuationView()
    {
        InitializeComponent();
        ReadingList.ItemsSource = _rows;
    }

    /// <summary>The client arrives once the Analytics Core is up, so it is fetched on demand.</summary>
    public void Bind(Func<AnalyticsCoreClient?> clientAccessor) => _clientAccessor = clientAccessor;

    /// <summary>Loads a symbol from elsewhere in the app, e.g. a click on a holding.</summary>
    public async Task ShowAsync(string symbol)
    {
        SymbolBox.Text = symbol;
        await LoadAsync().ConfigureAwait(true);
    }

    private async void LoadButton_OnClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var client = _clientAccessor?.Invoke();
        if (client is null)
        {
            StatusText.Text = "分析核心尚未就绪，无法取估值数据。可到「系统」页启动核心。";
            return;
        }

        var symbol = SymbolBox.Text.Trim();
        if (symbol.Length == 0)
        {
            StatusText.Text = "请输入标的代码，例如 SSE:600000。";
            return;
        }

        LoadButton.IsEnabled = false;
        StatusText.Text = $"正在取 {symbol} 的估值历史…";
        try
        {
            var years = int.TryParse((YearsCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var y) ? y : 10;
            var response = await client.GetValuationAsync(symbol, years).ConfigureAwait(true);

            _rows.Clear();
            if (!response.Ok)
            {
                StatusText.Text = $"取不到估值数据：{response.Error}";
                return;
            }

            foreach (var reading in response.Readings)
            {
                _rows.Add(ValuationRow.From(reading));
            }

            StatusText.Text = _rows.Count == 0
                ? "该标的没有可用的估值指标。"
                : $"{symbol} · 回溯 {years} 年 · 数据源 {response.Provider}。以下为历史位置，不是买卖依据。";
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
