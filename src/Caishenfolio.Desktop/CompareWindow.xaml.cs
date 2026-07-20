using System.Text.Json;
using System.Windows;
using Caishenfolio.Host;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop;

public partial class CompareWindow : Window
{
    private readonly AnalyticsCoreClient? _client;
    private readonly string? _artifactRoot;
    private readonly string _rangeStart;
    private readonly string _rangeEnd;
    private List<string> _dates = new();
    private Dictionary<string, IReadOnlyList<double>> _series = new();
    private JsonElement _raw;
    private string _summaryForReport = "";

    public CompareWindow(
        AnalyticsCoreClient? client = null,
        string? artifactRoot = null,
        string rangeStart = "",
        string rangeEnd = "")
    {
        InitializeComponent();
        _client = client;
        _artifactRoot = artifactRoot;
        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;
    }

    public void LoadFromCompareJson(JsonElement result)
    {
        _raw = result;
        _dates = new List<string>();
        _series = new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);

        if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            TitleBlock.Text = "对比失败";
            SummaryText.Text = result.TryGetProperty("error", out var err)
                ? err.GetString()
                : "未知错误";
            _summaryForReport = SummaryText.Text;
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
        _summaryForReport = SummaryText.Text;
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

    private async void ExportReport_OnClick(object sender, RoutedEventArgs e)
    {
        if (_client is null || string.IsNullOrWhiteSpace(_artifactRoot))
        {
            MessageBox.Show(this, "无法导出：缺少客户端或 Artifact 路径。", "导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var symbols = string.Join(", ", _series.Keys);
            var sections = new List<object>
            {
                new
                {
                    heading = "对比区间",
                    body = new Dictionary<string, string>
                    {
                        ["start"] = _rangeStart,
                        ["end"] = _rangeEnd,
                        ["symbols"] = symbols,
                        ["method"] = "归一化收盘价（起点=100）",
                    },
                },
                new
                {
                    heading = "对比摘要",
                    body = _summaryForReport,
                },
                new
                {
                    heading = "图例说明",
                    body = LegendText.Text,
                },
                new
                {
                    heading = "说明",
                    body = new List<string>
                    {
                        "叠线图见软件对比窗口；本报告保存文字摘要。",
                        ProductInfo.ResearchDisclaimer,
                    },
                },
            };

            var result = await _client
                .ExportReportAsync(
                    _artifactRoot,
                    $"对比报告_{DateTime.Now:yyyyMMdd_HHmmss}",
                    symbols,
                    sections)
                .ConfigureAwait(true);

            if (result.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                var path = result.TryGetProperty("markdown_path", out var p) ? p.GetString() : _artifactRoot;
                MessageBox.Show(this, $"对比报告已导出：\n{path}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    this,
                    result.TryGetProperty("error", out var err) ? err.GetString() : "导出失败",
                    "导出失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
