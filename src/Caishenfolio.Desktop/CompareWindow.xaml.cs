using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Caishenfolio.Desktop;

public partial class CompareWindow : Window
{
    private List<string> _dates = new();
    private Dictionary<string, IReadOnlyList<double>> _series = new();

    public CompareWindow()
    {
        InitializeComponent();
    }

    public void LoadFromCompareJson(JsonElement result)
    {
        _dates = new List<string>();
        _series = new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);

        if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            TitleBlock.Text = "对比失败";
            SummaryText.Text = result.TryGetProperty("error", out var err)
                ? err.GetString()
                : "未知错误";
            return;
        }

        var start = result.TryGetProperty("start", out var s) ? s.GetString() : "";
        var end = result.TryGetProperty("end", out var e) ? e.GetString() : "";
        var n = result.TryGetProperty("date_count", out var dc) ? dc.GetInt32() : 0;
        TitleBlock.Text = $"归一化收盘对比  {start} → {end}  （{n} 个共同交易日，起点=100）";

        if (result.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
        {
            var seriesAcc = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in points.EnumerateArray())
            {
                var date = p.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
                _dates.Add(date);
                if (!p.TryGetProperty("values", out var vals) || vals.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in vals.EnumerateObject())
                {
                    if (!seriesAcc.TryGetValue(prop.Name, out var list))
                    {
                        list = new List<double>();
                        seriesAcc[prop.Name] = list;
                    }

                    list.Add(prop.Value.GetDouble());
                }
            }

            foreach (var kv in seriesAcc)
            {
                _series[kv.Key] = kv.Value;
            }
        }

        var summaryLines = new List<string>();
        if (result.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in summary.EnumerateArray())
            {
                var sym = item.TryGetProperty("symbol", out var sy) ? sy.GetString() : "";
                var ret = item.TryGetProperty("return", out var r) && r.ValueKind == JsonValueKind.Number
                    ? r.GetDouble()
                    : double.NaN;
                var endN = item.TryGetProperty("normalized_end", out var ne) && ne.ValueKind == JsonValueKind.Number
                    ? ne.GetDouble()
                    : double.NaN;
                summaryLines.Add(
                    $"{MarketLabels.FromSymbol(sym)} {sym}: 区间收益={(double.IsNaN(ret) ? "n/a" : ret.ToString("P2"))}, 终点指数={endN:0.00}");
            }
        }

        if (result.TryGetProperty("fetch_errors", out var fe) && fe.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in fe.EnumerateArray())
            {
                summaryLines.Add("拉取失败: " + x.GetString());
            }
        }

        SummaryText.Text = string.Join(Environment.NewLine, summaryLines);
        Redraw();
    }

    private void ChartCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (ChartCanvas.ActualWidth < 10 || ChartCanvas.ActualHeight < 10)
        {
            return;
        }

        CompareChartPainter.Draw(ChartCanvas, _dates, _series, LegendText);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
