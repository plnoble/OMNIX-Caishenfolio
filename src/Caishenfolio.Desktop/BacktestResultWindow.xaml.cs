using System.Text;
using System.Text.Json;
using System.Windows;
using Caishenfolio.Host;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop;

public partial class BacktestResultWindow : Window
{
    private readonly AnalyticsCoreClient _client;
    private readonly string _artifactRoot;
    private readonly string _symbol;
    private readonly string _start;
    private readonly string _end;
    private JsonElement _result;
    private List<(string Time, double Equity, double Close)> _points = new();

    public BacktestResultWindow(
        AnalyticsCoreClient client,
        string artifactRoot,
        string symbol,
        string start,
        string end,
        JsonElement result)
    {
        InitializeComponent();
        _client = client;
        _artifactRoot = artifactRoot;
        _symbol = symbol;
        _start = start;
        _end = end;
        _result = result;
        LoadResult(result);
    }

    /// <summary>
    /// Shows the numbers that decide whether a rule is worth anything, and the out-of-sample
    /// verdict. A headline return alone flatters every rule; this panel is what can say no.
    /// </summary>
    private void RenderVerdict(JsonElement result)
    {
        var findings = new List<string>();

        if (result.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            var lines = new StringBuilder();
            lines.AppendLine(
                $"胜率={Ratio(metrics, "win_rate")}  盈亏比={Number(metrics, "profit_factor")}  " +
                $"最长连亏={Integer(metrics, "max_consecutive_losses")} 笔");
            lines.AppendLine(
                $"年化={Ratio(metrics, "annualized_return")}  波动率={Ratio(metrics, "volatility")}  " +
                $"回撤持续={Integer(metrics, "max_drawdown_bars")} 根K线");
            lines.Append(
                $"扣成本后相对买入持有={Ratio(metrics, "excess_over_buy_hold")}  " +
                $"单位回撤收益={Number(metrics, "return_over_max_drawdown")}");
            MetricsText.Text = lines.ToString();
        }

        var hasReport = result.TryGetProperty("out_of_sample", out var oos)
                        && oos.ValueKind == JsonValueKind.Object;
        if (!hasReport)
        {
            VerdictBadgeText.Text = "数据不足";
            VerdictBadge.Background = Brush(0xB4, 0xC0, 0xD0);
            findings.Add("样本区间太短，无法留出样本外数据检验。至少约 200 根K线才有意义。");
        }
        else
        {
            var survives = oos.TryGetProperty("survives_out_of_sample", out var s) && s.GetBoolean();
            VerdictBadgeText.Text = survives ? "样本外仍成立" : "样本外未通过";
            VerdictBadge.Background = survives ? Brush(0x5E, 0xE4, 0xA8) : Brush(0xFF, 0x7A, 0x88);

            if (oos.TryGetProperty("findings", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                findings.AddRange(
                    list.EnumerateArray().Select(f => "· " + (f.GetString() ?? "")));
            }

            if (oos.TryGetProperty("split_date", out var split))
            {
                findings.Add($"· 切分点：{split.GetString()} 之后的数据规则从未见过。");
            }
        }

        FindingList.ItemsSource = findings;
        VerdictPanel.Visibility = Visibility.Visible;
    }

    private static System.Windows.Media.SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(System.Windows.Media.Color.FromRgb(r, g, b));

    private static string Ratio(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble().ToString("P2")
            : "—";

    private static string Number(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble().ToString("0.##")
            : "—";

    private static string Integer(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32().ToString()
            : "—";

    private void LoadResult(JsonElement result)
    {
        _points = new List<(string, double, double)>();
        if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            TitleBlock.Text = "回测失败";
            SummaryText.Text = result.TryGetProperty("error", out var err) ? err.GetString() : "未知错误";
            return;
        }

        var strategy = result.TryGetProperty("strategy", out var st) ? st.GetString() : "ma_cross";
        var total = GetDouble(result, "total_return");
        var bh = GetDouble(result, "buy_hold_return");
        var dd = GetDouble(result, "max_drawdown");
        var trades = result.TryGetProperty("trades", out var t) ? t.GetInt32() : 0;
        var skipped = result.TryGetProperty("skipped_signals", out var sk) ? sk.GetInt32() : 0;

        TitleBlock.Text = $"回测权益曲线 · {MarketLabels.FromSymbol(_symbol)} {_symbol} · {strategy}";
        var sb = new StringBuilder();
        sb.AppendLine($"区间 {_start} ~ {_end}");
        sb.AppendLine($"策略收益={total:P2}  买入持有={bh:P2}  最大回撤={dd:P2}");
        sb.AppendLine($"成交次数={trades}  跳过信号(涨跌停等)={skipped}");
        if (result.TryGetProperty("cost_model", out var cm) && cm.ValueKind == JsonValueKind.Object)
        {
            sb.Append("成本: ");
            sb.Append($"佣金={GetDouble(cm, "commission_rate"):P2} ");
            sb.Append($"印花税={GetDouble(cm, "stamp_duty_rate"):P2} ");
            sb.Append($"滑点={GetDouble(cm, "slippage_rate"):P2} ");
            sb.Append($"涨停={GetDouble(cm, "limit_up_pct"):P0}/跌停={GetDouble(cm, "limit_down_pct"):P0}");
            if (cm.TryGetProperty("enforce_limit", out var el))
            {
                sb.Append(el.GetBoolean() ? "  涨跌停约束=开" : "  涨跌停约束=关");
            }
        }

        SummaryText.Text = sb.ToString();
        RenderVerdict(result);

        if (result.TryGetProperty("equity_curve", out var curve) && curve.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in curve.EnumerateArray())
            {
                var time = p.TryGetProperty("timestamp_utc", out var ts) ? ts.GetString() ?? "" : "";
                var eq = GetDouble(p, "equity");
                var close = GetDouble(p, "close");
                _points.Add((time, eq, close));
            }
        }

        Redraw();
    }

    private static double GetDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;

    private void ChartCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (ChartCanvas.ActualWidth < 10 || ChartCanvas.ActualHeight < 10)
        {
            return;
        }

        EquityChartPainter.Draw(ChartCanvas, _points);
    }

    private async void ExportReport_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var sections = new List<object>
            {
                new
                {
                    heading = "回测摘要",
                    body = SummaryText.Text,
                },
                new
                {
                    heading = "标的与区间",
                    body = new Dictionary<string, string>
                    {
                        ["symbol"] = _symbol,
                        ["market"] = MarketLabels.FromSymbol(_symbol),
                        ["start"] = _start,
                        ["end"] = _end,
                    },
                },
                new
                {
                    heading = "说明",
                    body = new List<string>
                    {
                        "权益曲线见软件内窗口；本报告保存文字摘要。",
                        ProductInfo.ResearchDisclaimer,
                    },
                },
            };
            var result = await _client
                .ExportReportAsync(
                    _artifactRoot,
                    $"回测报告_{_symbol.Replace(':', '_')}",
                    _symbol,
                    sections)
                .ConfigureAwait(true);
            if (result.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                var path = result.TryGetProperty("markdown_path", out var p) ? p.GetString() : _artifactRoot;
                MessageBox.Show(this, $"报告已导出：\n{path}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
